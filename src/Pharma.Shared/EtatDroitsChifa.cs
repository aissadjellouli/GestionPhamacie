namespace Pharma.Shared;

/// <summary>État des droits Chifa d'un patient en tiers payant CNAS/CASNOS (contrôle de fin de droits, 2026-11-15).</summary>
public enum EtatDroits
{
    /// <summary>Hors tiers payant CNAS/CASNOS : rien à signaler.</summary>
    NonConcerne,
    /// <summary>Aucune date de fin de droits sur la fiche : « à renseigner » (facultatif).</summary>
    Absente,
    Valides,
    /// <summary>Date dans les N jours (N réglable, Administration › Caisse) : avertissement orange.</summary>
    BientotExpires,
    /// <summary>Date strictement avant aujourd'hui : avertissement rouge.</summary>
    Expires
}

/// <summary>
/// Contrôle de la date de fin de droits Chifa (règles du titulaire, 2026-11-15) : jamais de blocage de vente ; date absente
/// signalée, date proche (dans les N jours, aujourd'hui compris) en orange, date dépassée en rouge ; vente à crédit d'un patient aux
/// droits expirés : avertissement renforcé. Utilisé par le poste et par le serveur.
/// </summary>
public static class EtatDroitsChifa
{
    public const int JoursAlerteParDefaut = 30, JoursAlerteMax = 365;

    public static bool OrganismeChifa(string? codeOrganisme) => codeOrganisme is "CNAS" or "CASNOS";

    public static EtatDroits Etat(bool tiersPayantChifa, DateOnly? dateFinDroit, DateOnly aujourdhui, int joursAlerte)
    {
        if (!tiersPayantChifa) return EtatDroits.NonConcerne;
        if (dateFinDroit is not { } fin) return EtatDroits.Absente;
        if (fin < aujourdhui) return EtatDroits.Expires;
        return fin.DayNumber - aujourdhui.DayNumber <= Math.Max(0, joursAlerte) ? EtatDroits.BientotExpires : EtatDroits.Valides;
    }

    public static string MessageBientot(DateOnly fin) => $"Droits Chifa jusqu’au {fin:dd/MM/yyyy} : prévenir le patient.";

    public static string MessageExpires(DateOnly fin) => $"Droits Chifa expirés depuis le {fin:dd/MM/yyyy} : vérifiez la carte (vente non bloquée).";

    public const string MessageCredit = "Droits expirés et vente à crédit : risque de non-paiement.";
}
