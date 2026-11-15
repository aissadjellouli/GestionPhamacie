using Pharma.Application.Securite;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Caisse;

/// <remarks>Regularisation : ticket de la dernière régularisation Chifa mise à la charge du patient (recette du 2026-11-15, D7).</remarks>
public sealed record PatientEnCredit(long Id, string Nom, string? Telephone, decimal Solde, decimal Plafond, DateTime? DerniereVente, string? Regularisation = null);

/// <summary>
/// Vente à crédit (montant net des retours). Regle et Reste : les remboursements soldent la dette la plus ancienne d'abord
/// (dette reprise de l'ancien logiciel, puis ventes à crédit dans l'ordre des dates).
/// </summary>
/// <param name="Facture">N° de la facture (non remplacée) qui reprend la part patient de la vente, sinon null (2026-11-15).</param>
public sealed record VenteACredit(string Numero, DateTime Date, decimal Montant, decimal Regle = 0, decimal Reste = 0, long Id = 0, string? Facture = null,
    string? Remise = null);

/// <summary>
/// Ligne du relevé de dette. Type : ouverture, reprise, vente_credit, remboursement, retour, annulation, correction.
/// Montant positif : la dette augmente ; négatif : elle diminue.
/// </summary>
public sealed record MouvementCredit(DateTime Date, string Type, decimal Montant, decimal SoldeApres, string? Vente, string? Employe, string? Poste,
    string? Reference, long? EmployeId = null, string? Remise = null);

/// <summary>DetteReprise : dette antérieure aux ventes à crédit du logiciel (ancien logiciel), soldée en premier.</summary>
public sealed record SituationCredit(PatientEnCredit Patient, IReadOnlyList<VenteACredit> Ventes, IReadOnlyList<MouvementCredit>? Mouvements = null,
    decimal DetteReprise = 0, int MouvementsTotal = 0);

public interface ICreditsClients
{
    Task<IReadOnlyList<PatientEnCredit>> AvecCreditAsync(string? recherche, CancellationToken ct = default);
    /// <summary>Même liste, par page (règle des listes) : plus grosses dettes d'abord.</summary>
    Task<Page<PatientEnCredit>> AvecCreditPageAsync(string? recherche, DemandePage page, CancellationToken ct = default);
    Task<SituationCredit?> SituationAsync(long patientId, CancellationToken ct = default);
    /// <summary>Une page du relevé de dette, du mouvement le plus récent au plus ancien.</summary>
    Task<Page<MouvementCredit>> MouvementsAsync(long patientId, DemandePage page, FiltreReleve? filtre = null, CancellationToken ct = default);
    /// <summary>Paiement du patient (sans vente) dans la caisse, dette diminuée ; renvoie le n° du reçu.</summary>
    /// <summary>Renvoie le numéro du reçu (RC-000001).</summary>
    /// <remarks>Ventes : ventes à crédit choisies par le patient (087), le montant leur est imputé ; le surplus solde la dette la plus ancienne.</remarks>
    Task<string> EncaisserAsync(long patientId, decimal montant, string mode, long sessionCaisseId, long utilisateurId,
        IReadOnlyList<long>? ventes = null, CancellationToken ct = default);
    /// <summary>Remplace la dette (attendue : celle lue avant, sinon refus) ; le relevé inscrit une « correction » avec son motif.</summary>
    Task CorrigerAsync(long patientId, decimal detteAttendue, decimal nouvelleDette, string motif, long utilisateurId, string? poste, CancellationToken ct = default);
}

public sealed record RecuCredit(string Numero, string Patient, decimal Montant, string Mode, decimal ResteDu);

/// <summary>Crédits clients au comptoir (droit caisse.vendre) : liste, situation, remboursement.</summary>
public sealed class ConsulterCredits(ICreditsClients credits)
{
    public Task<IReadOnlyList<PatientEnCredit>> ListerAsync(string? recherche, CancellationToken ct = default) =>
        credits.AvecCreditAsync(string.IsNullOrWhiteSpace(recherche) ? null : recherche.Trim(), ct);

    /// <summary>Patients en crédit par page ; recherche sur le nom ou le début du téléphone.</summary>
    public Task<Page<PatientEnCredit>> PageAsync(string? recherche, DemandePage page, CancellationToken ct = default) =>
        credits.AvecCreditPageAsync(string.IsNullOrWhiteSpace(recherche) ? null : recherche.Trim(), page, ct);

    public Task<Page<MouvementCredit>> MouvementsAsync(long patientId, DemandePage page, FiltreReleve? filtre = null, CancellationToken ct = default) =>
        credits.MouvementsAsync(patientId, page, filtre, ct);

    public async Task<SituationCredit> SituationAsync(long patientId, CancellationToken ct = default) =>
        await credits.SituationAsync(patientId, ct) ?? throw new RegleMetierException("Patient introuvable.");
}

/// <summary>Le patient rembourse tout ou partie de sa dette : l'argent entre dans la caisse ouverte du poste.</summary>
public sealed class EncaisserReglementCredit(ICreditsClients credits, ISessionsCaisse sessions, IJournal journal, IAvoirs? avoirs = null)
{
    public async Task<RecuCredit> ExecuterAsync(long patientId, decimal montant, string? mode, long utilisateurId, string? poste,
        IReadOnlyList<long>? ventes = null, CancellationToken ct = default)
    {
        var session = await sessions.SessionDuPosteAsync(PosteCaisse.Exiger(poste), ct)
                      ?? throw new RegleMetierException("Ouvrez la caisse du poste avant d’encaisser un règlement.");
        var s = await credits.SituationAsync(patientId, ct) ?? throw new RegleMetierException("Patient introuvable.");
        RegleReglementCredit.Verifier(montant, s.Patient.Solde, mode);
        // Dette payée avec l'avoir du patient (script 106) : au plus son solde, revérifié sous verrou du patient à l'écriture
        if (mode == RegleAvoir.Mode && (avoirs is null ? 0 : await avoirs.SoldeAsync(patientId, ct)) is var soldeAvoir && montant > soldeAvoir)
            throw RegleAvoir.Insuffisant(soldeAvoir);
        var numero = await credits.EncaisserAsync(patientId, montant, mode!, session.Id, utilisateurId, ventes, ct);
        await journal.EcrireAsync(utilisateurId, "reglement_credit", "patient", patientId,
            new Dictionary<string, object?> { ["montant"] = montant, ["mode"] = mode, ["reste_du"] = s.Patient.Solde - montant, ["recu"] = numero, ["ventes"] = ventes,
                // N° de ticket des ventes choisies : le journal les affiche à la place des identifiants (recette 2026-11-15)
                ["tickets"] = ventes is { Count: > 0 } ? s.Ventes.Where(v => ventes.Contains(v.Id)).Select(v => v.Numero).ToList() : null }, null, ct);
        return new RecuCredit(numero, s.Patient.Nom, montant, mode!, s.Patient.Solde - montant);
    }
}

public interface IDocumentsCredit
{
    /// <summary>Relevé de la dette d'un patient, à lui remettre : ventes à crédit (réglé, reste dû) et mouvements.</summary>
    /// <remarks>Avoir du patient (script 106) : section séparée, jamais confondue avec la dette.</remarks>
    byte[] ReleveCredit(SituationCredit situation, string? pharmacie, IReadOnlyList<MouvementAvoir>? avoir = null, decimal soldeAvoir = 0);
}

/// <summary>Relevé PDF de la dette d'un patient (droit caisse.vendre, comme la consultation des crédits).</summary>
public sealed class ImprimerReleveCredit(ICreditsClients credits, IDocumentsCredit documents, Administration.IPharmacie pharmacie,
    IAvoirs? avoirs = null)
{
    public async Task<(byte[] Contenu, string NomFichier)> ExecuterAsync(long patientId, CancellationToken ct = default)
    {
        var s = await credits.SituationAsync(patientId, ct) ?? throw new RegleMetierException("Patient introuvable.");
        // Le relevé imprimé porte tous les mouvements, pas seulement la première page affichée à l'écran
        var tous = new List<MouvementCredit>();
        for (var numero = 1; ; numero++)
        {
            var page = await credits.MouvementsAsync(patientId, new DemandePage(numero, DemandePage.TailleMax), null, ct);
            tous.AddRange(page.Lignes);
            if (page.Lignes.Count < DemandePage.TailleMax) break;
        }
        s = s with { Mouvements = tous, MouvementsTotal = tous.Count };
        // Avoir du patient (script 106) : tous ses mouvements, en section séparée de la dette
        var avoir = new List<MouvementAvoir>();
        var soldeAvoir = 0m;
        if (avoirs is not null)
        {
            soldeAvoir = await avoirs.SoldeAsync(patientId, ct);
            for (var numero = 1; ; numero++)
            {
                var page = await avoirs.MouvementsAsync(patientId, new DemandePage(numero, DemandePage.TailleMax), ct);
                avoir.AddRange(page.Lignes);
                if (page.Lignes.Count < DemandePage.TailleMax) break;
            }
        }
        return (documents.ReleveCredit(s, (await pharmacie.LireAsync(ct)).Nom, avoir, soldeAvoir), $"releve_credit_{patientId}_{DateTime.Now:yyyyMMdd}.pdf");
    }
}

/// <summary>
/// Correction de la dette d'un patient par le titulaire (erreur de reprise, geste commercial, créance abandonnée) : motif
/// obligatoire, inscrite au relevé de dette (type « correction ») et au journal avec la dette avant et après.
/// </summary>
public sealed class CorrigerDetteCredit(ICreditsClients credits, Securite.IPermissionService droits, IJournal journal)
{
    public const int MotifMin = 5;

    public async Task ExecuterAsync(long patientId, decimal nouvelleDette, string? motif, long utilisateurId, string? poste, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "corriger la dette d’un patient", ct);
        var texte = motif?.Trim() ?? "";
        if (texte.Length < MotifMin) throw new RegleMetierException("Indiquez le motif de la correction (5 caractères au moins).", "motif_correction");
        if (texte.Length > 300) throw new RegleMetierException("Motif : 300 caractères au plus.", "motif_correction");
        if (nouvelleDette < 0) throw new RegleMetierException("La dette ne peut pas être négative.", "nouvelle_dette");
        nouvelleDette = Arrondi.Centime(nouvelleDette);
        var s = await credits.SituationAsync(patientId, ct) ?? throw new RegleMetierException("Patient introuvable.");
        if (s.Patient.Solde == nouvelleDette) throw new RegleMetierException("La dette est déjà de ce montant : rien à corriger.", "nouvelle_dette");
        await credits.CorrigerAsync(patientId, s.Patient.Solde, nouvelleDette, texte, utilisateurId, string.IsNullOrWhiteSpace(poste) ? null : poste.Trim(), ct);
        await journal.EcrireAsync(utilisateurId, "correction_credit", "patient", patientId,
            new Dictionary<string, object?> { ["avant"] = s.Patient.Solde, ["apres"] = nouvelleDette, ["motif"] = texte }, null, ct);
    }
}
