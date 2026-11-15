using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Caisse;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Caisse;

// ---- Modèles ----------------------------------------------------------------

/// <summary>
/// Sortie ou entrée d'argent par le tiroir à écrire, dans une seule transaction avec l'événement d'ouverture du tiroir (script 096).
/// Reglement : paiement d'un grossiste (règlement en espèces pris dans le tiroir) ; null pour les autres détails.
/// </summary>
public sealed record MouvementTiroirAEcrire(long UtilisateurId, string Poste, long SessionId, string MotifCode, string MotifLibelle,
    string Sens, string Detail, decimal Montant, SaisieTiroir Saisie, long? ValidePar, Achats.ReglementAEcrire? Reglement = null);

public sealed record MouvementTiroirEcrit(long Id, string Numero);

/// <summary>Sortie ou entrée du tiroir telle qu'enregistrée (contrôle interne, bon de sortie). Corrigee : une écriture inverse existe.</summary>
public sealed record MouvementTiroir(long Id, string Numero, DateTime Le, long UtilisateurId, string Employe, string? Poste, long SessionId,
    string MotifCode, string Motif, string Sens, string Detail, decimal Montant, string? Categorie, string? Libelle, string? Beneficiaire,
    string? Reference, string? Patient, string? Raison, string? ValidePar, long? CorrigeId, string? MotifCorrection, bool Corrigee,
    long? ReglementFournisseurId = null);

public sealed record FiltreMouvementsTiroir(DateOnly? Du, DateOnly? Au, long? EmployeId, string? Motif, string? Sens);

/// <summary>Une page de sorties et d'entrées du tiroir, avec les totaux (nets des corrections) de tout le filtre.</summary>
public sealed record PageMouvementsTiroir(Page<MouvementTiroir> Page, decimal TotalSorties, decimal TotalEntrees);

/// <summary>Ligne du récapitulatif de relève ou de clôture : sorties (par motif) ou entrées du tiroir de la session, nettes des corrections.</summary>
public sealed record LigneTiroir(string Motif, string Sens, decimal Montant);

/// <summary>
/// Récapitulatif de relève ou de clôture : l'attendu (fond + espèces nettes de la session) comprend déjà les sorties et entrées du tiroir ;
/// elles sont montrées en lignes séparées, et les espèces encaissées (ventes, annulations, retours, crédits…) le sont hors tiroir :
/// fond + encaissées − sorties + entrées = attendu.
/// </summary>
public static class RecapitulatifTiroir
{
    public static decimal Sorties(IEnumerable<LigneTiroir>? lignes) => lignes?.Where(l => l.Sens == RegleTiroir.Sortie).Sum(l => l.Montant) ?? 0;
    public static decimal Entrees(IEnumerable<LigneTiroir>? lignes) => lignes?.Where(l => l.Sens == RegleTiroir.Entree).Sum(l => l.Montant) ?? 0;

    /// <param name="especesSession">Espèces nettes de la session (VentesEspeces), sorties et entrées du tiroir comprises.</param>
    public static decimal Encaissees(decimal especesSession, IReadOnlyList<LigneTiroir>? lignes) => especesSession + Sorties(lignes) - Entrees(lignes);
}

/// <summary>Bon de sortie (ou d'entrée) de caisse, imprimé au format rouleau comme le ticket.</summary>
public sealed record BonSortieCaisse(string Numero, DateTime Le, string Motif, string Sens, decimal Montant, string Employe, string? Poste,
    string? Beneficiaire, IReadOnlyList<string> Details, string? ValidePar, string? Pharmacie, string? Correction);

// ---- Ports ------------------------------------------------------------------

public interface ITiroir
{
    /// <summary>Événement d'ouverture du tiroir, mouvement du tiroir et son écriture (dépense, règlement, coffre) : une transaction.</summary>
    Task<MouvementTiroirEcrit> EnregistrerAsync(MouvementTiroirAEcrire m, CancellationToken ct = default);
    Task<MouvementTiroir?> LireAsync(long id, CancellationToken ct = default);
    Task<PageMouvementsTiroir> ListerAsync(FiltreMouvementsTiroir filtre, DemandePage page, CancellationToken ct = default);
    /// <summary>Écriture inverse (sens opposé, même montant) dans la session ouverte du poste ; refusée si le mouvement est déjà corrigé.</summary>
    Task<MouvementTiroirEcrit> CorrigerAsync(MouvementTiroir original, long sessionId, string poste, string motif, long utilisateurId, CancellationToken ct = default);
}

public interface IDocumentsBonSortie
{
    byte[] BonSortie(BonSortieCaisse bon, int largeurMm = FormatTicket.LargeurParDefaut);
}

// ---- Cas d'usage --------------------------------------------------------------

/// <summary>
/// Ouverture du tiroir avec un motif à effet (F8, règle du titulaire 2026-11-15) : montant obligatoire, détail exigé, sortie au plus des
/// espèces attendues, validation d'un ayant droit au-delà du seuil (droit caisse.cloturer, code comptoir possible), paiement d'un
/// grossiste avec le droit achats.regler. Écrit dans la même transaction l'événement, le mouvement du tiroir et l'écriture de trésorerie.
/// </summary>
public sealed class OuvrirTiroirAvecEffet(ITiroir tiroir, ISessionsCaisse sessions, IPermissionService droits, IJournal journal,
    Referentiels.MotifsActifs motifs, Achats.IReglementsFournisseurs? reglements = null, AutorisationCourante? autorisation = null, IHorloge? horloge = null)
{
    public async Task<MouvementTiroirEcrit> ExecuterAsync(Pharma.Domain.Referentiels.MotifReglable motif, string libelleMotif, SaisieTiroir? saisie,
        long utilisateurId, string? poste, CancellationToken ct = default)
    {
        var montant = RegleTiroir.VerifierMontant(motif.Effet, saisie?.Montant)!.Value;
        var s = RegleTiroir.VerifierDetail(motif.Detail, (saisie ?? new SaisieTiroir(montant)) with { Montant = montant });
        var nomPoste = PosteCaisse.Exiger(poste);
        var session = await sessions.SessionDuPosteAsync(nomPoste, ct)
                      ?? throw new RegleMetierException("Ouvrez la caisse du poste : une sortie ou une entrée d’argent est rattachée à la caisse.");
        if (motif.Effet == RegleTiroir.Sortie)
            RegleTiroir.VerifierDansTiroir(montant, session.FondCaisse + await sessions.VentesEspecesAsync(session.Id, ct));

        // Catégorie de charge : liste réglable existante (catégories actives seulement)
        if (motif.Detail == RegleTiroir.Depense
            && !(await motifs.ExecuterAsync(Pharma.Domain.Referentiels.RegleMotifs.Charge, ct)).Any(c => c.Code == s.Categorie))
            throw new RegleMetierException("Catégorie de dépense inconnue ou désactivée.", "categorie");

        // Paiement d'un grossiste : droit achats.regler, dette relue, imputé aux factures les plus anciennes
        Achats.ReglementAEcrire? reglement = null;
        string? grossiste = null;
        if (motif.Detail == RegleTiroir.Fournisseur && s.FournisseurId is { } fournisseurId)
        {
            await droits.ExigerAsync(utilisateurId, Permissions.AchatsRegler, "payer un grossiste avec l’argent du tiroir", ct);
            if (reglements is null) throw new InvalidOperationException("Service des règlements absent.");
            var situation = await reglements.SituationAsync(fournisseurId, ct) ?? throw new RegleMetierException("Grossiste introuvable.", "fournisseur");
            if (montant > Arrondi.Centime(Math.Max(situation.Solde, 0)))
                throw new RegleMetierException($"Le montant dépasse la dette envers {situation.Nom} ({situation.Solde:N2} DA).", "montant");
            // Facture désignée par son n° de BL ou de facture (si elle couvre le montant), sinon les plus anciennes
            var designee = situation.Factures.FirstOrDefault(f => f.Reste >= montant
                && (string.Equals(f.NumeroFacture, s.Reference, StringComparison.OrdinalIgnoreCase) || string.Equals(f.NumeroBl, s.Reference, StringComparison.OrdinalIgnoreCase)));
            IReadOnlyList<(long, decimal)> lignes = designee is not null
                ? [(designee.ReceptionId, montant)]
                : Pharma.Domain.Achats.RegleReglement.ImputerAuxPlusAnciennes(montant,
                    situation.Factures.Select(f => new Pharma.Domain.Achats.FactureAPayer(f.ReceptionId, f.Date, f.Reste)));
            reglement = new Achats.ReglementAEcrire(fournisseurId, horloge?.Aujourdhui ?? DateOnly.FromDateTime(DateTime.Now), montant,
                Pharma.Domain.Achats.MoyenReglement.Especes, s.Reference, null, null, montant, session.Id, utilisateurId,
                $"Payé depuis le tiroir ({nomPoste})", lignes);
            grossiste = situation.Nom;
        }

        // Au-delà du seuil : un ayant droit valide (le sien, ou le code comptoir d'un collègue qui a le droit)
        long? validePar = null;
        var parametres = await sessions.ParametresAsync(ct);
        if (RegleTiroir.ValidationRequise(motif.Effet, montant, parametres.SeuilSortieTiroir))
        {
            await droits.ExigerAsync(utilisateurId, Permissions.CaisseCloturer,
                $"valider une sortie de caisse de plus de {parametres.SeuilSortieTiroir:N2} DA", ct);
            validePar = autorisation?.AutoriseParId ?? utilisateurId;
        }

        var ecrit = await tiroir.EnregistrerAsync(new MouvementTiroirAEcrire(utilisateurId, nomPoste, session.Id, motif.Code, libelleMotif,
            motif.Effet, motif.Detail, montant, s, validePar, reglement), ct);

        await journal.EcrireAsync(utilisateurId, "tiroir_ouvert", "session_caisse", session.Id,
            new Dictionary<string, object?>
            {
                ["poste"] = nomPoste, ["motif"] = libelleMotif, ["motif_code"] = motif.Code, ["sens"] = motif.Effet, ["montant"] = montant,
                ["bon"] = ecrit.Numero, ["categorie"] = s.Categorie, ["libelle"] = s.Libelle, ["grossiste"] = grossiste, ["beneficiaire"] = s.Beneficiaire,
                ["reference"] = s.Reference, ["patient_id"] = s.PatientId, ["raison"] = s.Raison
            }, Permissions.CaisseOuvrirTiroir, ct);
        if (reglement is not null)
            await journal.EcrireAsync(utilisateurId, "reglement_grossiste", "mouvement_tiroir", ecrit.Id,
                new Dictionary<string, object?>
                {
                    ["grossiste"] = grossiste, ["montant"] = montant, ["moyen"] = "especes", ["reference"] = s.Reference, ["part_tiroir"] = montant,
                    ["factures"] = reglement.Imputations.Count, ["bon"] = ecrit.Numero
                }, Permissions.AchatsRegler, ct);
        return ecrit;
    }
}

/// <summary>
/// Correction d'une sortie ou d'une entrée du tiroir : jamais de suppression, écriture inverse journalisée (même montant, sens opposé)
/// dans la caisse ouverte du poste ; droit caisse.cloturer (code comptoir possible), motif obligatoire. Le règlement d'un grossiste
/// ne se corrige pas ici (sa dette et ses factures sont touchées) : refusé avec explication.
/// </summary>
public sealed class CorrigerMouvementTiroir(ITiroir tiroir, ISessionsCaisse sessions, IPermissionService droits, IJournal journal)
{
    public async Task<MouvementTiroirEcrit> ExecuterAsync(long id, string? motif, long utilisateurId, string? poste, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CaisseCloturer, "corriger une sortie ou une entrée du tiroir", ct);
        var m = string.IsNullOrWhiteSpace(motif) ? throw new RegleMetierException("Indiquez le motif de la correction.", "motif_correction") : motif.Trim();
        if (m.Length > RegleTiroir.TexteMax) throw new RegleMetierException($"Motif : {RegleTiroir.TexteMax} caractères au plus.", "motif_correction");
        var original = await tiroir.LireAsync(id, ct) ?? throw new RegleMetierException("Mouvement du tiroir introuvable.");
        if (original.CorrigeId is not null) throw new RegleMetierException("Une écriture inverse ne se corrige pas.");
        if (original.Corrigee) throw new RegleMetierException("Ce mouvement est déjà corrigé.");
        if (original.ReglementFournisseurId is not null)
            throw new RegleMetierException("Le paiement d’un grossiste a réglé ses factures : il ne se corrige pas depuis la caisse (voyez le titulaire, Achats › Règlements).");
        var nomPoste = PosteCaisse.Exiger(poste);
        var session = await sessions.SessionDuPosteAsync(nomPoste, ct) ?? throw new RegleMetierException("Ouvrez la caisse du poste pour corriger.");
        // L'inverse d'une entrée sort de l'argent : pas plus que le tiroir n'en contient
        if (original.Sens == RegleTiroir.Entree)
            RegleTiroir.VerifierDansTiroir(original.Montant, session.FondCaisse + await sessions.VentesEspecesAsync(session.Id, ct));
        var ecrit = await tiroir.CorrigerAsync(original, session.Id, nomPoste, m, utilisateurId, ct);
        await journal.EcrireAsync(utilisateurId, "correction_tiroir", "mouvement_tiroir", ecrit.Id,
            new Dictionary<string, object?>
            {
                ["bon_corrige"] = original.Numero, ["bon"] = ecrit.Numero, ["motif_initial"] = original.Motif, ["montant"] = original.Montant,
                ["sens_initial"] = original.Sens, ["motif"] = m, ["poste"] = nomPoste
            }, Permissions.CaisseCloturer, ct);
        return ecrit;
    }
}

/// <summary>Contrôle interne du titulaire : sorties et entrées du tiroir par motif, employé et période (paginées).</summary>
public sealed class ListerMouvementsTiroir(ITiroir tiroir, IPermissionService droits)
{
    public async Task<PageMouvementsTiroir> ExecuterAsync(FiltreMouvementsTiroir filtre, DemandePage page, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter les sorties du tiroir", ct);
        if (filtre is { Du: { } du, Au: { } au } && au < du) throw new RegleMetierException("La période est inversée.");
        if (filtre.Sens is not (null or RegleTiroir.Sortie or RegleTiroir.Entree)) throw new RegleMetierException("Sens inconnu.");
        return await tiroir.ListerAsync(filtre with { Motif = string.IsNullOrWhiteSpace(filtre.Motif) ? null : filtre.Motif.Trim() }, page, ct);
    }
}

/// <summary>Bon de sortie de caisse en PDF rouleau : celui qui l'a fait, ou un employé qui voit les écarts de caisse (titulaire compris).</summary>
public sealed class ObtenirBonSortie(ITiroir tiroir, IPermissionService droits, IDocumentsBonSortie documents, Administration.IPharmacie pharmacie)
{
    public static BonSortieCaisse Contenu(MouvementTiroir m, string? nomPharmacie)
    {
        var details = new List<string>();
        if (m.Categorie is { } c) details.Add($"Catégorie : {c}");
        if (m.Libelle is { } l) details.Add(l);
        if (m.Reference is { } r) details.Add($"BL / facture : {r}");
        if (m.Raison is { } raison) details.Add($"Raison : {raison}");
        var correction = m.CorrigeId is not null ? $"Écriture inverse : {m.MotifCorrection}" : null;
        return new BonSortieCaisse(m.Numero, m.Le, m.Motif, m.Sens, m.Montant, m.Employe, m.Poste, m.Beneficiaire ?? m.Patient, details,
            m.ValidePar, nomPharmacie, correction);
    }

    public async Task<(byte[] Contenu, string NomFichier)?> ExecuterAsync(long id, int? largeurMm, long utilisateurId, CancellationToken ct = default)
    {
        if (await tiroir.LireAsync(id, ct) is not { } m) return null;
        if (m.UtilisateurId != utilisateurId && !await droits.APermissionAsync(utilisateurId, Permissions.CaisseVoirEcart, ct)
            && !await droits.EstTitulaireAsync(utilisateurId, ct))
            throw new DroitRefuseException(Permissions.CaisseVoirEcart, "imprimer le bon de sortie d’un collègue");
        var nom = (await pharmacie.LireAsync(ct)).Nom;
        return (documents.BonSortie(Contenu(m, nom), FormatTicket.Borner(largeurMm)), $"bon_{m.Numero}.pdf");
    }
}
