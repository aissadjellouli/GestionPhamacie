using Pharma.Application.Commun;
using Pharma.Domain.Commun;

namespace Pharma.Application.Patients;

/// <summary>
/// Fin des droits Chifa d'un patient (script 102, 2026-11-15, décision D-4) : une date de fin dépassée (strictement avant
/// aujourd'hui) n'empêche pas la vente en tiers payant CNAS/CASNOS ; elle est seulement signalée au vendeur et journalisée.
/// Vente à crédit d'un tel patient (contrôle de fin de droits, 2026-11-15) : avertissement renforcé et journal propre.
/// </summary>
public static class DroitsChifa
{
    /// <summary>Action du journal : vente en tiers payant CNAS/CASNOS d'un patient aux droits expirés.</summary>
    public const string ActionJournal = "droits_chifa_expires";
    /// <summary>Action du journal : la même vente, mise au crédit du patient (risque de non-paiement).</summary>
    public const string ActionJournalCredit = "droits_chifa_expires_credit";

    public static bool Expires(DateOnly? dateFinDroit, DateOnly aujourdhui) => dateFinDroit is { } f && f < aujourdhui;

    /// <summary>Crédit sans tiers payant : « expires » (date dépassée), « bientot » (dans les N jours, aujourd'hui compris), sinon null.</summary>
    public static string? EtatCreditSansTiersPayant(DateOnly fin, DateOnly aujourdhui, int joursAlerte) =>
        fin < aujourdhui ? "expires" : fin.DayNumber - aujourdhui.DayNumber <= Math.Max(0, joursAlerte) ? "bientot" : null;

    public static string Message(DateOnly dateFinDroit) =>
        $"Droits Chifa expirés depuis le {dateFinDroit:dd/MM/yyyy} : vérifiez la carte du patient (vente non bloquée).";

    /// <summary>Journalise l'avertissement après l'enregistrement de la vente ; ne bloque jamais.</summary>
    /// <param name="credit">Vente mise au crédit du patient : second avertissement journalisé (risque de non-paiement).</param>
    public static async Task JournaliserSiExpiresAsync(IPatients patients, IJournal? journal, long? patientId, bool tiersPayantChifa,
        DateOnly aujourdhui, long utilisateurId, long venteId, string numero, CancellationToken ct, bool credit = false)
    {
        if (!tiersPayantChifa || patientId is not { } pid || journal is null) return;
        var fiche = await patients.FicheAsync(pid, ct);
        if (fiche?.DateFinDroit is not { } fin || !Expires(fin, aujourdhui)) return;
        var details = new Dictionary<string, object?> { ["vente"] = numero, ["patient_id"] = pid, ["date_fin_droit"] = fin };
        await journal.EcrireAsync(utilisateurId, ActionJournal, "vente", venteId, details, Pharma.Domain.Securite.Permissions.CaisseVendre, ct);
        if (credit)
            await journal.EcrireAsync(utilisateurId, ActionJournalCredit, "vente", venteId,
                new Dictionary<string, object?>(details) { ["mode"] = "credit" }, Pharma.Domain.Securite.Permissions.CaisseVendre, ct);
    }

    /// <summary>
    /// Vente à crédit SANS tiers payant Chifa (décision du titulaire, 2026-11-15) : un patient dont la date de fin de droits est
    /// dépassée ou dans les N jours (seuil « bientôt expirés ») reçoit le même avertissement renforcé (MessageCredit) ; journal
    /// droits_chifa_expires_credit (état expires ou bientot) ; jamais de blocage.
    /// </summary>
    public static async Task JournaliserCreditSansTiersPayantAsync(IPatients patients, IJournal? journal, long? patientId,
        DateOnly aujourdhui, int joursAlerte, long utilisateurId, long venteId, string numero, CancellationToken ct)
    {
        if (patientId is not { } pid || journal is null) return;
        var fiche = await patients.FicheAsync(pid, ct);
        if (fiche?.DateFinDroit is not { } fin) return;
        var etat = EtatCreditSansTiersPayant(fin, aujourdhui, joursAlerte);
        if (etat is null) return;
        await journal.EcrireAsync(utilisateurId, ActionJournalCredit, "vente", venteId,
            new Dictionary<string, object?>
            {
                ["vente"] = numero, ["patient_id"] = pid, ["date_fin_droit"] = fin, ["mode"] = "credit", ["tiers_payant"] = false, ["etat"] = etat
            }, Pharma.Domain.Securite.Permissions.CaisseVendre, ct);
    }
}

/// <summary>
/// « Mettre à jour la date » de fin de droits (caisse, liste de suivi) : saisie immédiate, avec le droit existant de modifier un
/// patient (vendre), inscrite au journal comme toute modification de la fiche (patient_modifie, ancienne et nouvelle valeur).
/// </summary>
public sealed class MettreAJourFinDroit(IPatients patients, IJournal journal, IHorloge horloge)
{
    public async Task ExecuterAsync(long patientId, DateOnly? dateFinDroit, long utilisateurId, CancellationToken ct = default)
    {
        var actuelle = await patients.FicheAsync(patientId, ct) ?? throw new RegleMetierException("Patient introuvable.");
        if (actuelle.DateFinDroit == dateFinDroit) return;
        if (!await patients.ModifierAsync(actuelle with { DateFinDroit = dateFinDroit }, ct)) throw new RegleMetierException("Patient introuvable.");
        await journal.EcrireAsync(utilisateurId, "patient_modifie", "patient", patientId,
            new Dictionary<string, object?> { ["date_fin_droit"] = new { avant = actuelle.DateFinDroit, apres = dateFinDroit } },
            Pharma.Domain.Securite.Permissions.CaisseVendre, ct);
    }
}

/// <summary>Ligne de la liste de suivi des droits Chifa expirés ou bientôt expirés.</summary>
public sealed record SuiviDroitChifa(long PatientId, string Nom, string? NumAssure, string? Organisme, DateOnly DateFinDroit, DateTime? DerniereVente);

/// <summary>Filtres : état (expires, bientot ou null = les deux), organisme (CNAS/CASNOS ou null), période de la date de fin (incluse).</summary>
public sealed record FiltreSuiviDroits(string? Etat, string? Organisme, DateOnly? Du, DateOnly? Au, DateOnly Aujourdhui, int JoursAlerte);

public interface ISuiviDroitsChifa
{
    Task<Page<SuiviDroitChifa>> PageAsync(FiltreSuiviDroits filtre, DemandePage page, CancellationToken ct = default);
}

/// <summary>Patients › « Droits expirés ou bientôt expirés » : patients CNAS/CASNOS dont la date est dépassée ou dans les N jours.</summary>
public sealed class ListerSuiviDroitsChifa(ISuiviDroitsChifa suivi, Caisse.ISessionsCaisse caisse, IHorloge horloge)
{
    public async Task<Page<SuiviDroitChifa>> ExecuterAsync(string? etat, string? organisme, DateOnly? du, DateOnly? au, DemandePage page,
        CancellationToken ct = default)
    {
        var e = etat is "expires" or "bientot" ? etat : null;
        var o = organisme is "CNAS" or "CASNOS" ? organisme : null;
        var jours = (await caisse.ParametresAsync(ct)).JoursAlerteFinDroit;
        return await suivi.PageAsync(new FiltreSuiviDroits(e, o, du, au, horloge.Aujourdhui, jours), page, ct);
    }
}
