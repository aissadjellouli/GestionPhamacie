using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Notifications;
using Pharma.Domain.Securite;

namespace Pharma.Application.Achats;

// ---- Ruptures et patients en attente (lot 7, M3b, script 116) ----------------------
// Historique des ruptures chez les grossistes, noté à chaque import de catalogue (lot 6) ; file des patients en attente d'un
// produit ; alerte au comptoir quand le produit redevient disponible (stock reçu ou grossiste qui le déclare disponible) ;
// SMS « commande disponible » par le modem GSM local, seulement avec l'accord enregistré du patient. Aucun service étranger.

public enum TransitionRupture { Aucune, Ouvrir, Fermer }

/// <summary>
/// Passage disponible ⇄ indisponible d'un produit chez un grossiste, à l'import de son catalogue : indisponible sans rupture
/// ouverte → une rupture commence ; disponible avec une rupture ouverte → elle se termine (retour en stock chez le grossiste).
/// Disponibilité inconnue (colonne absente, texte non reconnu) : rien ne change.
/// </summary>
public static class RegleHistoriqueRuptures
{
    public static TransitionRupture Transition(bool ruptureOuverte, bool? disponible) => disponible switch
    {
        false when !ruptureOuverte => TransitionRupture.Ouvrir,
        true when ruptureOuverte => TransitionRupture.Fermer,
        _ => TransitionRupture.Aucune
    };
}

/// <summary>Produit en rupture chez tous les grossistes qui le connaissent (aucun ne le déclare disponible). Depuis : début de la
/// dernière rupture ouverte (moment où plus aucun grossiste ne l'avait).</summary>
public sealed record RuptureProduit(long ProduitId, string Produit, int Grossistes, DateTime Depuis, int StockOfficine, int PatientsEnAttente);

/// <summary>Période d'indisponibilité d'un produit chez un grossiste ; Fin : retour en stock (null = toujours en rupture).</summary>
public sealed record PeriodeRupture(long Id, long ProduitId, string Produit, long FournisseurId, string Grossiste, DateTime Debut, DateTime? Fin);

public interface IRupturesGrossistes
{
    Task<Page<RuptureProduit>> PartoutAsync(string? recherche, DemandePage page, CancellationToken ct = default);
    /// <param name="enCours">true : ruptures ouvertes ; false : terminées (retours en stock) ; null : toutes.</param>
    Task<Page<PeriodeRupture>> HistoriqueAsync(string? recherche, bool? enCours, DemandePage page, CancellationToken ct = default);
}

public sealed class ConsulterRuptures(IRupturesGrossistes ruptures)
{
    public Task<Page<RuptureProduit>> PartoutAsync(string? recherche, DemandePage page, CancellationToken ct = default) =>
        ruptures.PartoutAsync(Propre(recherche), page, ct);

    public Task<Page<PeriodeRupture>> HistoriqueAsync(string? recherche, string? etat, DemandePage page, CancellationToken ct = default) =>
        ruptures.HistoriqueAsync(Propre(recherche), etat switch { "en_cours" => true, "terminees" => false, _ => null }, page, ct);

    private static string? Propre(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

// ---- Patients en attente d'un produit ----------------------------------------------

public static class StatutDemandePatient
{
    public const string EnAttente = "en_attente";
    public const string Prevenu = "prevenu";
    public const string Servie = "servie";
    public const string Annulee = "annulee";
}

/// <summary>Demande « Prévenir quand disponible ». DisponibleGrossiste : au moins un grossiste le déclare disponible.</summary>
public sealed record DemandePatient(
    long Id, long PatientId, string Patient, string? Telephone, bool ConsentementSms, DateTime? ConsentementSmsLe,
    long ProduitId, string Produit, int Quantite, DateTime CreeLe, string Statut, DateTime? PrevenuLe, string? PrevenuCanal, string? SmsErreur,
    int StockOfficine, bool DisponibleGrossiste)
{
    public bool Disponible => RegleDemandesPatients.Disponible(StockOfficine, DisponibleGrossiste);
}

/// <summary>Produit dû d'une vente en attente (motif « produit manquant », script 094) : sans patient enregistré.</summary>
public sealed record ProduitDuEnAttente(long PanierId, string Libelle, long ProduitId, string Produit, int Quantite, DateTime CreeLe,
    int StockOfficine, bool DisponibleGrossiste)
{
    public bool Disponible => RegleDemandesPatients.Disponible(StockOfficine, DisponibleGrossiste);
}

/// <summary>Alerte du comptoir : demandes non encore prévenues et produits dus des ventes en attente, devenus disponibles.</summary>
public sealed record AlertesDisponibilite(IReadOnlyList<DemandePatient> Patients, IReadOnlyList<ProduitDuEnAttente> VentesEnAttente)
{
    public int Nombre => Patients.Count + VentesEnAttente.Count;
}

public interface IDemandesPatients
{
    Task<bool> PatientExisteAsync(long patientId, CancellationToken ct = default);
    Task<bool> ProduitExisteAsync(long produitId, CancellationToken ct = default);
    /// <summary>Demande encore ouverte (en attente ou prévenu) du même patient pour le même produit.</summary>
    Task<long?> DemandeOuverteAsync(long patientId, long produitId, CancellationToken ct = default);
    Task<long> CreerAsync(long patientId, long produitId, int quantite, long utilisateurId, CancellationToken ct = default);
    Task<DemandePatient?> LireAsync(long id, CancellationToken ct = default);
    Task<Page<DemandePatient>> PageAsync(string? recherche, string? statut, DemandePage page, CancellationToken ct = default);
    /// <summary>Demandes « en attente » (pas encore prévenues) dont le produit est disponible.</summary>
    Task<IReadOnlyList<DemandePatient>> DisponiblesAsync(CancellationToken ct = default);
    /// <summary>Produits dus des ventes en attente devenus disponibles.</summary>
    Task<IReadOnlyList<ProduitDuEnAttente>> ProduitsDusDisponiblesAsync(CancellationToken ct = default);
    Task MarquerPrevenuAsync(long id, string canal, long utilisateurId, DateTime quandUtc, CancellationToken ct = default);
    Task NoterEchecSmsAsync(long id, string erreur, CancellationToken ct = default);
    Task<bool> CloturerAsync(long id, string statut, long utilisateurId, DateTime quandUtc, CancellationToken ct = default);
    Task<(bool Accord, DateTime? Le)?> ConsentementAsync(long patientId, CancellationToken ct = default);
    Task EnregistrerConsentementAsync(long patientId, bool accord, DateTime quandUtc, CancellationToken ct = default);
}

public static class RegleDemandesPatients
{
    public const int QuantiteMax = 1000;

    /// <summary>Le produit redevient disponible : du stock vendable reçu à l'officine, OU un grossiste le déclare disponible.</summary>
    public static bool Disponible(int stockOfficine, bool disponibleGrossiste) => stockOfficine > 0 || disponibleGrossiste;

    /// <summary>
    /// Texte du SMS : aucune donnée de santé (ni produit, ni patient) ; nom de la pharmacie s'il est réglé. Court, sans accents
    /// (TexteSms.Preparer) : un seul SMS.
    /// </summary>
    public static string TexteSms(string? pharmacie) =>
        TexteSms_Preparer(string.IsNullOrWhiteSpace(pharmacie)
            ? "Votre commande est disponible a la pharmacie."
            : $"Votre commande est disponible a la pharmacie {pharmacie.Trim()}.");

    private static string TexteSms_Preparer(string t) => Pharma.Domain.Notifications.TexteSms.Preparer(t);

    /// <summary>SMS possible : accord du patient enregistré, numéro connu, modem configuré. Null si oui, sinon la raison.</summary>
    public static string? RefusSms(bool consentement, string? telephone, bool modemConfigure) =>
        !consentement ? "Le patient n’a pas donné son accord pour recevoir un SMS : prévenez-le au comptoir ou par téléphone, ou enregistrez son accord."
        : string.IsNullOrWhiteSpace(telephone) ? "Aucun numéro de téléphone sur la fiche du patient."
        : !modemConfigure ? "Modem GSM non configuré (Administration › Notifications) : aucun SMS possible."
        : null;
}

/// <summary>Saisie « Prévenir quand disponible » au comptoir (patient + produit).</summary>
public sealed class DemanderPrevenirPatient(IDemandesPatients demandes, IPermissionService droits, IJournal journal)
{
    public async Task<long> ExecuterAsync(long patientId, long produitId, int quantite, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendre, "noter un patient en attente d’un produit", ct);
        if (quantite is < 1 or > RegleDemandesPatients.QuantiteMax)
            throw new RegleMetierException($"Quantité entre 1 et {RegleDemandesPatients.QuantiteMax}.", "quantite");
        if (!await demandes.PatientExisteAsync(patientId, ct)) throw new RegleMetierException("Choisissez le patient.", "patient");
        if (!await demandes.ProduitExisteAsync(produitId, ct)) throw new RegleMetierException("Choisissez le produit.", "produit");
        if (await demandes.DemandeOuverteAsync(patientId, produitId, ct) is { } existante) return existante;
        var id = await demandes.CreerAsync(patientId, produitId, quantite, utilisateurId, ct);
        // Journal : identifiants seulement (ni nom ni téléphone du patient)
        await journal.EcrireAsync(utilisateurId, "demande_patient_creee", "demande_patient", id,
            new Dictionary<string, object?> { ["produit_id"] = produitId, ["quantite"] = quantite }, Permissions.CaisseVendre, ct);
        return id;
    }
}

public sealed class ConsulterDemandesPatients(IDemandesPatients demandes)
{
    public Task<Page<DemandePatient>> PageAsync(string? recherche, string? statut, DemandePage page, CancellationToken ct = default) =>
        demandes.PageAsync(string.IsNullOrWhiteSpace(recherche) ? null : recherche.Trim(), string.IsNullOrWhiteSpace(statut) ? null : statut, page, ct);

    public async Task<AlertesDisponibilite> AlertesAsync(CancellationToken ct = default) =>
        new(await demandes.DisponiblesAsync(ct), await demandes.ProduitsDusDisponiblesAsync(ct));
}

/// <summary>
/// Prévenir le patient : au comptoir (de vive voix, par téléphone) ou par SMS. Le SMS n'est envoyé qu'avec l'accord du patient
/// enregistré sur sa fiche, par le modem GSM local, et ne contient aucune donnée de santé. SMS en échec : la demande reste « en
/// attente », l'erreur est notée.
/// </summary>
public sealed class PrevenirPatient(IDemandesPatients demandes, INotificationsDepot reglages, IEnvoiSms sms, Administration.IPharmacie pharmacie,
    IPermissionService droits, IJournal journal, IHorloge horloge)
{
    public async Task<string> ExecuterAsync(long demandeId, bool parSms, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendre, "prévenir un patient", ct);
        var d = await demandes.LireAsync(demandeId, ct) ?? throw new RegleMetierException("Demande introuvable.");
        if (d.Statut is not (StatutDemandePatient.EnAttente or StatutDemandePatient.Prevenu))
            throw new RegleMetierException("Cette demande est close (servie ou annulée).");
        var canal = "comptoir";
        if (parSms)
        {
            var r = await reglages.ReglagesAsync(ct);
            if (RegleDemandesPatients.RefusSms(d.ConsentementSms, d.Telephone, r.SmsActifs) is { } refus) throw new RegleMetierException(refus);
            var texte = RegleDemandesPatients.TexteSms((await pharmacie.LireAsync(ct)).Nom);
            try
            {
                await sms.EnvoyerAsync(r.ModemPort!, d.Telephone!.Trim(), texte, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await demandes.NoterEchecSmsAsync(demandeId, ex.Message, ct);
                throw new RegleMetierException($"Le SMS n’est pas parti ({ex.Message}). Prévenez le patient par téléphone ou réessayez.");
            }
            canal = "sms";
        }
        await demandes.MarquerPrevenuAsync(demandeId, canal, utilisateurId, horloge.MaintenantUtc, ct);
        await journal.EcrireAsync(utilisateurId, "demande_patient_prevenue", "demande_patient", demandeId,
            new Dictionary<string, object?> { ["canal"] = canal }, Permissions.CaisseVendre, ct);
        return canal;
    }
}

/// <summary>Demande servie (produit remis) ou annulée : elle sort de la file.</summary>
public sealed class CloturerDemandePatient(IDemandesPatients demandes, IPermissionService droits, IJournal journal, IHorloge horloge)
{
    public async Task ExecuterAsync(long demandeId, bool servie, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendre, "clore une demande de patient", ct);
        var statut = servie ? StatutDemandePatient.Servie : StatutDemandePatient.Annulee;
        if (!await demandes.CloturerAsync(demandeId, statut, utilisateurId, horloge.MaintenantUtc, ct))
            throw new RegleMetierException("Demande introuvable ou déjà close.");
        await journal.EcrireAsync(utilisateurId, "demande_patient_close", "demande_patient", demandeId,
            new Dictionary<string, object?> { ["statut"] = statut }, Permissions.CaisseVendre, ct);
    }
}

/// <summary>Accord du patient pour le SMS « commande disponible » : case enregistrée sur sa fiche avec la date.</summary>
public sealed class ConsentementSmsPatient(IDemandesPatients demandes, IPermissionService droits, IJournal journal, IHorloge horloge)
{
    public async Task<(bool Accord, DateTime? Le)> LireAsync(long patientId, CancellationToken ct = default) =>
        await demandes.ConsentementAsync(patientId, ct) ?? throw new RegleMetierException("Patient introuvable.");

    public async Task EnregistrerAsync(long patientId, bool accord, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendre, "enregistrer l’accord SMS d’un patient", ct);
        if (await demandes.ConsentementAsync(patientId, ct) is null) throw new RegleMetierException("Patient introuvable.");
        await demandes.EnregistrerConsentementAsync(patientId, accord, horloge.MaintenantUtc, ct);
        await journal.EcrireAsync(utilisateurId, "consentement_sms_patient", "patient", patientId,
            new Dictionary<string, object?> { ["accord"] = accord }, Permissions.CaisseVendre, ct);
    }
}
