using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Achats;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Achats;

/// <summary>
/// Traite (effet) remise à un grossiste. CompteId / Banque : banque choisie à la remise (débitée à l'échéance sans action) ; null :
/// payée à l'échéance sur confirmation du titulaire. PayeeLe / MoyenPaiement : paiement confirmé, ou débit à l'échéance passée
/// d'une traite remise avec une banque (moyen « debit_echeance ») ; null : pas encore payée.
/// </summary>
public sealed record TraiteAPayer(long ReglementId, string? Numero, long FournisseurId, string Grossiste, string? Reference, string? BanqueGrossiste,
    DateOnly DateRemise, DateOnly Echeance, decimal Montant, long? CompteId, string? Banque, DateOnly? PayeeLe = null, string? MoyenPaiement = null);

/// <summary>
/// Filtres de la liste des traites. Etat : a_payer (défaut : non payées, échues ou à 7 jours), echues, a_venir, payees, toutes.
/// Texte : grossiste ou n° de la traite. Aujourdhui : date du jour du serveur.
/// </summary>
public sealed record FiltreTraites(string Etat, string? Texte, DateOnly Aujourdhui)
{
    public static readonly string[] Etats = ["a_payer", "echues", "a_venir", "payees", "toutes"];

    public static FiltreTraites Lire(string? etat, string? texte, DateOnly aujourdhui) =>
        new(Etats.Contains(etat) ? etat! : "a_payer", string.IsNullOrWhiteSpace(texte) ? null : texte.Trim(), aujourdhui);
}

/// <summary>Une page de traites et, pour le titulaire seulement, le solde du coffre (proposition du complément pris dans le tiroir).</summary>
public sealed record TraitesAPayer(Page<TraiteAPayer> Traites, decimal? SoldeCoffre);

/// <summary>Paiement d'une traite. PartTiroir (espèces) : argent du tiroir de la caisse ouverte porté au coffre avant le paiement.</summary>
public sealed record PaiementTraiteAEcrire(long ReglementId, DateOnly Date, MoyenReglement Moyen, long? CompteId, string? Reference, decimal PartTiroir,
    long? SessionCaisseId, string? Poste, long UtilisateurId);

public interface ITraites
{
    /// <summary>
    /// Traites selon l'état (a_payer : non payées, échéance passée ou dans <see cref="ConsulterTraites.JoursAvantEcheance"/> jours —
    /// une traite avec banque seulement jusqu'à son échéance ; echues : non payées, échéance passée, sans banque ; a_venir : non payées,
    /// échéance à venir ; payees : paiement confirmé, ou débitée à l'échéance passée), filtrées par grossiste ou n°, paginées par le
    /// serveur, de l'échéance la plus ancienne à la plus récente (payées : la plus récente d'abord).
    /// </summary>
    Task<Page<TraiteAPayer>> ListerAsync(FiltreTraites filtre, DemandePage page, CancellationToken ct = default);
    /// <summary>Traite non payée, ou null (inconnue ou déjà payée).</summary>
    Task<TraiteAPayer?> TraiteAsync(long reglementId, CancellationToken ct = default);
    /// <summary>
    /// Paiement en une transaction : complément du tiroir porté au coffre, solde du coffre relu sous verrou (espèces), paiement noté
    /// une seule fois. Traite remise avec une banque : noté sans second débit.
    /// </summary>
    Task<long> PayerAsync(PaiementTraiteAEcrire p, CancellationToken ct = default);
}

/// <summary>Traites remises aux grossistes, filtrées et paginées (par défaut : échues ou à 7 jours, non payées) ; droit « achats.regler ».</summary>
public sealed class ConsulterTraites(ITraites traites, IPermissionService droits, IHorloge horloge, Tresorerie.ITresorerie? tresorerie = null)
{
    /// <summary>Une traite est « à payer » dès que son échéance arrive dans ce nombre de jours (ou est passée).</summary>
    public const int JoursAvantEcheance = 7;

    public async Task<TraitesAPayer> ExecuterAsync(long utilisateurId, string? etat = null, string? texte = null, DemandePage? page = null,
        CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsRegler, "consulter les traites à payer", ct);
        var liste = await traites.ListerAsync(FiltreTraites.Lire(etat, texte, horloge.Aujourdhui), page ?? new DemandePage(), ct);
        decimal? coffre = null;
        if (tresorerie is not null && await droits.EstTitulaireAsync(utilisateurId, ct))
            coffre = (await tresorerie.ComptesAsync(ct)).FirstOrDefault(c => c.Type == "coffre")?.Solde;
        return new TraitesAPayer(liste, coffre);
    }
}

/// <summary>
/// Paiement d'une traite à son échéance, confirmé par le titulaire : espèces (coffre, complété au besoin par l'argent du tiroir de la
/// caisse ouverte du poste, porté au coffre), chèque, virement ou prélèvement (banque obligatoire, débitée à la date du paiement).
/// Traite remise avec une banque : déjà débitée à l'échéance, la confirmation est notée sans second débit. Journalisé.
/// </summary>
public sealed class PayerTraite(ITraites traites, ISessionsCaisse sessions, IPermissionService droits, IHorloge horloge, IJournal journal,
    Tresorerie.ITresorerie? tresorerie = null)
{
    public async Task<long> ExecuterAsync(long reglementId, DateOnly? date, string? moyen, long? compteId, string? reference, decimal partTiroir,
        long utilisateurId, string? poste, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "confirmer le paiement d’une traite", ct);
        var t = await traites.TraiteAsync(reglementId, ct) ?? throw new RegleMetierException("Cette traite est introuvable ou déjà payée : rechargez la liste.");
        var jour = date ?? horloge.Aujourdhui;
        MoyenReglement m;
        if (t.CompteId is { } banqueRemise)
        {
            // Débitée à l'échéance sur la banque choisie à la remise : la confirmation ne débite rien de plus
            if (jour > horloge.Aujourdhui) throw new RegleMetierException("La date du paiement ne peut pas être dans le futur.", "date");
            (m, compteId, partTiroir) = (MoyenReglement.Prelevement, banqueRemise, 0);
        }
        else
        {
            m = RegleReglement.VerifierPaiementTraite(moyen, compteId, reference, partTiroir, t.Montant, jour, t.DateRemise, horloge.Aujourdhui);
            if (m != MoyenReglement.Especes) await EnregistrerReglement.VerifierBanqueAsync(tresorerie, compteId!.Value, ct);
            else compteId = null;
        }

        long? sessionId = null;
        if (partTiroir > 0)
        {
            var nom = PosteCaisse.Exiger(poste);
            var session = await sessions.SessionDuPosteAsync(nom, ct)
                          ?? throw new RegleMetierException("Ouvrez la caisse du poste pour prendre un complément dans le tiroir.", "tiroir");
            var dansTiroir = session.FondCaisse + await sessions.VentesEspecesAsync(session.Id, ct);
            if (partTiroir > dansTiroir) throw new RegleMetierException($"Le tiroir ne contient en principe que {dansTiroir:N2} DA.", "tiroir");
            sessionId = session.Id;
        }

        var id = await traites.PayerAsync(new PaiementTraiteAEcrire(reglementId, jour, m, compteId,
            string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(), partTiroir, sessionId, poste, utilisateurId), ct);
        await journal.EcrireAsync(utilisateurId, "traite_payee", "reglement_fournisseur", reglementId,
            new Dictionary<string, object?>
            {
                ["grossiste"] = t.Grossiste, ["traite"] = t.Reference, ["montant"] = t.Montant, ["echeance"] = t.Echeance, ["date"] = jour,
                ["moyen"] = m.Code(), ["compte_id"] = compteId, ["part_tiroir"] = partTiroir, ["debit_a_l_echeance"] = t.CompteId is not null
            }, null, ct);
        return id;
    }
}
