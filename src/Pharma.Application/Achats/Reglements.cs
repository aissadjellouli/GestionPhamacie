using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Achats;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Achats;

public sealed record GrossisteAReglement(long Id, string Nom, decimal Solde, decimal ResteFactures, int FacturesOuvertes);

public sealed record FactureFournisseur(long ReceptionId, string? NumeroFacture, string? NumeroBl, DateTime Date, decimal Montant, decimal Regle, decimal Reste);

public sealed record ReglementPasse(long Id, DateOnly Date, decimal Montant, string Moyen, string? Reference, DateOnly? Echeance, decimal PartTiroir, string Employe,
    long? EmployeId = null);

/// <summary>Dette du grossiste (solde, avoirs et reprise compris), factures encore à payer, derniers règlements.</summary>
/// <summary>Ligne du relevé de la dette envers un grossiste ; Montant positif : la dette augmente, négatif : elle diminue.</summary>
public sealed record MouvementGrossiste(DateTime Date, string Type, decimal Montant, decimal SoldeApres, string? Employe, string? Reference, long? EmployeId = null);

public sealed record SituationGrossiste(long Id, string Nom, decimal Solde, IReadOnlyList<FactureFournisseur> Factures, IReadOnlyList<ReglementPasse> Reglements,
    IReadOnlyList<MouvementGrossiste>? Mouvements = null, int MouvementsTotal = 0);

/// <summary>Source : prêt ou apport du titulaire qui paie hors trésorerie (moyen Financement, aucun compte débité), null sinon.</summary>
public sealed record ReglementAEcrire(long FournisseurId, DateOnly Date, decimal Montant, MoyenReglement Moyen, string? Reference, string? Banque,
    DateOnly? Echeance, decimal PartTiroir, long? SessionCaisseId, long UtilisateurId, string? Commentaire,
    IReadOnlyList<(long ReceptionId, decimal Montant)> Imputations, long? CompteId = null, Pharma.Domain.Financements.SourceFinancement? Source = null);

public interface IReglementsFournisseurs
{
    Task<IReadOnlyList<GrossisteAReglement>> GrossistesAsync(CancellationToken ct = default);
    Task<SituationGrossiste?> SituationAsync(long fournisseurId, CancellationToken ct = default);
    /// <summary>Une page du relevé de la dette envers un grossiste, du mouvement le plus récent au plus ancien.</summary>
    Task<Page<MouvementGrossiste>> MouvementsAsync(long fournisseurId, DemandePage page, FiltreReleve? filtre = null, CancellationToken ct = default);
    Task<long> EnregistrerAsync(ReglementAEcrire r, CancellationToken ct = default);
}

public sealed class ConsulterReglements(IReglementsFournisseurs reglements, IPermissionService droits)
{
    public async Task<IReadOnlyList<GrossisteAReglement>> GrossistesAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsRegler, "consulter les règlements des grossistes", ct);
        return await reglements.GrossistesAsync(ct);
    }

    public async Task<Page<MouvementGrossiste>> MouvementsAsync(long fournisseurId, DemandePage page, FiltreReleve? filtre, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsRegler, "consulter les règlements des grossistes", ct);
        return await reglements.MouvementsAsync(fournisseurId, page, filtre, ct);
    }

    public async Task<SituationGrossiste> SituationAsync(long fournisseurId, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsRegler, "consulter les règlements des grossistes", ct);
        return await reglements.SituationAsync(fournisseurId, ct) ?? throw new RegleMetierException("Grossiste introuvable.");
    }
}

/// <summary>Une facture choisie, avec la part du règlement qui lui est imputée.</summary>
public sealed record ImputationDemandee(long ReceptionId, decimal Montant);

/// <summary>
/// Règlement d'un grossiste (droit « achats.regler »), imputé facture par facture : les factures choisies, ou les plus anciennes
/// si aucune n'est choisie. La dette du grossiste diminue du montant réglé (le surplus éventuel réduit l'ancien solde, sans facture).
/// Espèces : le coffre, et le complément pris dans le tiroir de la caisse ouverte du poste quand le coffre ne suffit pas.
/// Chèque, virement, prélèvement : banque de la trésorerie obligatoire (active), débitée à la date du règlement. Traite : banque
/// facultative (avec une banque, débitée à l'échéance ; sans, payée à l'échéance sur confirmation du titulaire, « Traites à payer »).
/// Source (titulaire seul) : facture payée par un prêt ou un apport du titulaire, hors trésorerie (aucun compte débité, source tracée).
/// </summary>
public sealed class EnregistrerReglement(IReglementsFournisseurs reglements, ISessionsCaisse sessions, IPermissionService droits, IHorloge horloge, IJournal journal,
    Tresorerie.ITresorerie? tresorerie = null)
{
    /// <summary>Banque choisie : un compte de type banque, actif (contrôlé quand la trésorerie est connue).</summary>
    internal static async Task VerifierBanqueAsync(Tresorerie.ITresorerie? tresorerie, long compteId, CancellationToken ct)
    {
        if (tresorerie is null) return;
        if (!(await tresorerie.ComptesAsync(ct)).Any(c => c.Id == compteId && c.Type == "banque" && c.Actif))
            throw new RegleMetierException("La banque choisie n’existe plus ou n’est plus active : choisissez une banque de la trésorerie.", "compte");
    }

    public async Task<long> ExecuterAsync(long fournisseurId, DateOnly? date, decimal montant, string? moyen, string? reference, string? banque,
        DateOnly? echeance, decimal partTiroir, string? commentaire, IReadOnlyList<ImputationDemandee>? imputations, long utilisateurId, string? poste,
        long? compteId = null, string? source = null, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsRegler, "régler un grossiste", ct);
        // Prêt ou apport du titulaire : réservé au titulaire, hors trésorerie (aucun compte débité, pas de tiroir)
        var sourceFinancement = Pharma.Domain.Financements.SourceFinancement.Lire(source);
        if (sourceFinancement is not null)
        {
            await droits.ExigerTitulaireAsync(utilisateurId, "payer une facture de grossiste par un prêt ou un apport", ct);
            (moyen, compteId, partTiroir, echeance) = ("financement", null, 0, null);
        }
        var m = sourceFinancement is null ? RegleReglement.Moyen(moyen) : MoyenReglement.Financement;
        var jour = date ?? horloge.Aujourdhui;
        RegleReglement.Verifier(montant, m, reference, echeance, partTiroir, jour, horloge.Aujourdhui);
        // Plus de règlement « hors trésorerie » par chèque, virement ou prélèvement : la banque débitée est obligatoire
        RegleReglement.VerifierBanque(m, compteId);
        if (m is not (MoyenReglement.Especes or MoyenReglement.Financement) && compteId is > 0 and var banqueId) await VerifierBanqueAsync(tresorerie, banqueId, ct);
        var situation = await reglements.SituationAsync(fournisseurId, ct) ?? throw new RegleMetierException("Grossiste introuvable.");
        // Montant au centime près (vérifié ci-dessus) : comparaison exacte avec la dette arrondie au centime
        if (montant > Arrondi.Centime(Math.Max(situation.Solde, 0)))
            throw new RegleMetierException($"Le montant dépasse la dette envers {situation.Nom} ({situation.Solde:N2} DA).", "montant");

        var restes = situation.Factures.ToDictionary(f => f.ReceptionId, f => f.Reste);
        IReadOnlyList<(long, decimal)> lignes = imputations is { Count: > 0 }
            ? imputations.Select(i => (i.ReceptionId, i.Montant)).ToList()
            : RegleReglement.ImputerAuxPlusAnciennes(montant, situation.Factures.Select(f => new FactureAPayer(f.ReceptionId, f.Date, f.Reste)));
        RegleReglement.VerifierImputations(montant, lignes, restes);

        long? sessionId = null;
        if (partTiroir > 0)
            sessionId = (await sessions.SessionDuPosteAsync(PosteCaisse.Exiger(poste), ct)
                        ?? throw new RegleMetierException("Ouvrez la caisse du poste pour prendre un complément dans le tiroir.")).Id;

        var id = await reglements.EnregistrerAsync(new ReglementAEcrire(fournisseurId, jour, montant, m,
            string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(), string.IsNullOrWhiteSpace(banque) ? null : banque.Trim(),
            m == MoyenReglement.Effet ? echeance : null, partTiroir, sessionId, utilisateurId,
            string.IsNullOrWhiteSpace(commentaire) ? null : commentaire.Trim(), lignes,
            m is MoyenReglement.Especes or MoyenReglement.Financement || compteId is not > 0 ? null : compteId, sourceFinancement), ct);
        await journal.EcrireAsync(utilisateurId, "reglement_grossiste", "reglement_fournisseur", id,
            new Dictionary<string, object?>
            {
                ["grossiste"] = situation.Nom, ["montant"] = montant, ["moyen"] = m.Code(), ["reference"] = reference,
                ["part_tiroir"] = partTiroir, ["factures"] = lignes.Count, ["source"] = sourceFinancement?.Type, ["pret_id"] = sourceFinancement?.PretId
            }, Permissions.AchatsRegler, ct);
        return id;
    }
}
