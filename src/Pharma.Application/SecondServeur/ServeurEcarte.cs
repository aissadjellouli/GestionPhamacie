using Pharma.Application.Commun;
using Pharma.Application.Notifications;

namespace Pharma.Application.SecondServeur;

// ---- Bascule du 2e serveur, lot 1 : détection d'un serveur écarté en cours de marche (2026-11-23) ----------------------------
// docs/taches/2026-11-23-bascule-2e-serveur/plan.md § 2. Ce lot ne fait que DÉTECTER et exposer l'état ; le passage en lecture seule
// (intergiciel, connexions) est le lot 3, qui s'appuie sur IServeurEcarteDetecte.

/// <summary>
/// Ce qu'un relevé lit sur une base : en recovery (copie en lecture seule) ou non, timeline (monte à chaque promotion, null en
/// recovery ou illisible), identifiant système PostgreSQL (même valeur = même lignée de base ; null si illisible, droits insuffisants).
/// </summary>
public sealed record ReleveServeur(bool EnRecovery, long? Timeline, long? IdentifiantSysteme);

/// <summary>Relevés de la base locale et de celle de l'autre serveur (SecondServeur:AutreServeur) ; null = injoignable ou illisible.</summary>
public interface IReleveServeurs
{
    /// <summary>Vrai si SecondServeur:AutreServeur est réglé : sinon aucune détection n'est possible.</summary>
    bool AutreServeurRegle { get; }
    /// <summary>Adresse de l'autre serveur telle que réglée (sans le mot de passe), pour les messages.</summary>
    string? AutreServeur { get; }
    Task<ReleveServeur?> LocalAsync(CancellationToken ct = default);
    Task<ReleveServeur?> AutreAsync(CancellationToken ct = default);
}

public enum VerdictEcart
{
    /// <summary>Rien à signaler (la base locale ou celle de l'autre est en lecture seule, ou une seule base en écriture).</summary>
    Normal,
    /// <summary>Autre serveur injoignable, relevé local illisible, lignée ou timeline illisible : jamais une décision.</summary>
    Inconnu,
    /// <summary>Deux bases en écriture mais d'identifiants système différents : ce sont deux bases distinctes ; alerte seulement.</summary>
    LigneeDifferente,
    /// <summary>Même lignée, deux bases en écriture, timelines égales : cas anormal, conduite non tranchée (question Q10 du plan).</summary>
    TimelinesEgales,
    /// <summary>Même lignée, deux bases en écriture, timeline de l'autre plus grande : l'autre est retenu, CE serveur est écarté.</summary>
    Ecarte,
    /// <summary>Même lignée, deux bases en écriture, timeline de l'autre plus petite : CE serveur est retenu, l'autre est l'ancien principal.</summary>
    Retenu
}

/// <param name="Confirme">Vrai si le relevé précédent donnait déjà le même verdict avec les mêmes timelines (2 relevés concordants).</param>
public sealed record DecisionEcart(VerdictEcart Verdict, bool Confirme, long? TimelineLocale = null, long? TimelineAutre = null);

/// <summary>Logique pure : qui est retenu entre deux bases en écriture.</summary>
public static class DecisionServeurEcarte
{
    /// <summary>
    /// Le verdict ne devient « confirmé » qu'avec deux relevés concordants successifs (<paramref name="precedente"/> : décision du
    /// passage d'avant, null au premier passage ou après un relevé inconnu). Une seule lecture ne fait donc jamais écarter un serveur.
    /// </summary>
    public static DecisionEcart Evaluer(ReleveServeur? local, ReleveServeur? autre, DecisionEcart? precedente = null)
    {
        var brute = Constater(local, autre);
        var confirme = brute.Verdict is VerdictEcart.Ecarte or VerdictEcart.Retenu or VerdictEcart.TimelinesEgales or VerdictEcart.LigneeDifferente
                       && precedente is { } p && p.Verdict == brute.Verdict && p.TimelineLocale == brute.TimelineLocale && p.TimelineAutre == brute.TimelineAutre;
        return brute with { Confirme = confirme };
    }

    private static DecisionEcart Constater(ReleveServeur? local, ReleveServeur? autre)
    {
        if (local is null || autre is null) return new DecisionEcart(VerdictEcart.Inconnu, false);
        // Une base en recovery n'écrit pas : pas de conflit possible (cas normal du 2e serveur en veille, ou du principal vu du 2e)
        if (local.EnRecovery || autre.EnRecovery) return new DecisionEcart(VerdictEcart.Normal, false);
        if (local.IdentifiantSysteme is not { } idLocal || autre.IdentifiantSysteme is not { } idAutre)
            return new DecisionEcart(VerdictEcart.Inconnu, false);   // lignée illisible (droits sur pg_control_system ?) : pas de décision
        if (idLocal != idAutre) return new DecisionEcart(VerdictEcart.LigneeDifferente, false, local.Timeline, autre.Timeline);
        if (local.Timeline is not { } tl || autre.Timeline is not { } ta) return new DecisionEcart(VerdictEcart.Inconnu, false);
        var verdict = ta > tl ? VerdictEcart.Ecarte : ta < tl ? VerdictEcart.Retenu : VerdictEcart.TimelinesEgales;
        return new DecisionEcart(verdict, false, tl, ta);
    }
}

/// <summary>
/// Écart confirmé : CE serveur est écarté, l'autre est retenu. <paramref name="Indecis"/> : deux bases en écriture avec la même timeline
/// (décision du titulaire sur Q10) : les deux serveurs se mettent en lecture seule, aucun n'est « retenu » (ServeurRetenu null) et
/// l'état n'est pas gardé en base (la table serveur_ecarte exige une timeline retenue plus grande) ; un redémarrage le relit.
/// </summary>
/// <param name="Retrait">
/// Vague 2, lot V2-4 (cas 2) : ce serveur a été mis hors jeu à la demande du 2e serveur, AVANT sa promotion (nature « retrait » de serveur_ecarte). La timeline du serveur
/// retenu n'est pas encore connue : <c>TimelineRetenue</c> vaut 0 et n'est pas écrite en base. Gardé en base, collant, levée manuelle seulement.
/// </param>
/// <param name="Isole">
/// Vague 2, lot V2-1b (règle de majorité, Q32) : ce serveur s'est lui-même mis en lecture seule (copie perdue ET moins de la moitié des postes vus), sans
/// serveur retenu (motif « isole »). Comme « Indecis », cet état n'est PAS gardé en base : serveur_ecarte exige un serveur retenu et une timeline retenue plus
/// grande, qu'un isolement n'a pas ; il est levé automatiquement (SurveillerIsolement) et un redémarrage de l'API l'oublie (la règle se réapplique 2 minutes après).
/// </param>
public sealed record EcartDetecte(DateTime DepuisUtc, string? ServeurRetenu, long TimelineLocale, long TimelineRetenue, bool Indecis = false, bool Isole = false, bool Retrait = false)
{
    /// <summary>Vrai pour les états qui ne s'écrivent pas dans serveur_ecarte (indécis, isolé).</summary>
    public bool EnMemoireSeulement => Indecis || Isole;
}

/// <summary>
/// Interface que le lot 3 branche (intergiciel 503, initialiseur de connexion, tâches de fond, healthcheck) : l'état détecté.
/// Ecart non null = ce serveur est écarté (état collant : il ne s'efface que par <see cref="Lever"/>).
/// </summary>
public interface IServeurEcarteDetecte
{
    EcartDetecte? Ecart { get; }
    /// <summary>Levée au passage à « écarté » (et à la levée), depuis le fil de la surveillance ; ne doit pas bloquer.</summary>
    event Action<EcartDetecte?>? Change;
    /// <summary>Levée manuelle d'un faux positif (commande console du lot 3).</summary>
    void Lever();
    /// <summary>Reprise d'un état gardé en base au démarrage (lot 3, script 163) ; sans effet si déjà écarté.</summary>
    void Restaurer(EcartDetecte ecart);
}

/// <summary>Sortes d'alertes au titulaire de la surveillance du 2e serveur ; chacune a sa propre limite d'une par heure.</summary>
public enum AlerteSecondServeur { Ecarte, AncienPrincipal, TimelinesEgales, LigneeDifferente, Isole }

/// <summary>État en mémoire de la détection (singleton) : dernière décision, état écarté, dernière alerte envoyée.</summary>
public sealed class SuiviServeurEcarte : IServeurEcarteDetecte
{
    private readonly object _verrou = new();
    private EcartDetecte? _ecart;

    public EcartDetecte? Ecart { get { lock (_verrou) return _ecart; } }
    public event Action<EcartDetecte?>? Change;

    internal DecisionEcart? Precedente { get; set; }
    // Dernière alerte envoyée par sorte d'alerte (une par heure au plus chacune). Gardée en mémoire seulement : aucun mécanisme existant
    // ne garde cet horodatage en base ; un redémarrage de l'API autorise donc une alerte de plus (au plus une par redémarrage).
    private readonly Dictionary<AlerteSecondServeur, DateTime> _alertes = [];

    internal DateTime? DerniereAlerteUtc(AlerteSecondServeur sorte) { lock (_verrou) return _alertes.TryGetValue(sorte, out var d) ? d : null; }
    internal void NoterAlerte(AlerteSecondServeur sorte, DateTime quand) { lock (_verrou) _alertes[sorte] = quand; }
    internal void OublierAlertes() { lock (_verrou) _alertes.Clear(); }

    public void Lever()
    {
        lock (_verrou) { _ecart = null; Precedente = null; _alertes.Clear(); }
        Change?.Invoke(null);
    }

    /// <summary>Levée automatique d'un isolement : les alertes déjà envoyées sont gardées (au plus une par heure, même si l'isolement revient).</summary>
    internal bool LeverIsolement()
    {
        lock (_verrou) { if (_ecart is not { Isole: true }) return false; _ecart = null; Precedente = null; }
        Change?.Invoke(null);
        return true;
    }

    /// <summary>Un isolement devient un écart définitif (le 2e serveur s'est promu : timeline plus récente constatée) : le verrou reste, l'écart est gardé en base.</summary>
    internal bool RemplacerIsolement(EcartDetecte definitif)
    {
        lock (_verrou) { if (_ecart is not { Isole: true }) return false; _ecart = definitif; }
        Change?.Invoke(definitif);
        return true;
    }

    public void Restaurer(EcartDetecte ecart)
    {
        lock (_verrou) { if (_ecart is not null) return; _ecart = ecart; }
        Change?.Invoke(ecart);
    }

    internal bool Ecarter(EcartDetecte ecart)
    {
        lock (_verrou) { if (_ecart is not null) return false; _ecart = ecart; }
        Change?.Invoke(ecart);
        return true;
    }
}

/// <summary>
/// Un passage de la surveillance (toutes les 30 s, tâche SurveillanceSecondServeur). Alertes au titulaire, au plus une par heure et par
/// sorte, sans donnée de patient : serveur écarté (état posé ici, lecture seule par le lot 3), ancien principal qui écrit encore,
/// lignée différente, deux bases à la même timeline (Q10 : alerte AVANT le verrou, puis lecture seule ici aussi).
/// </summary>
public sealed class SurveillerServeurEcarte(IReleveServeurs releves, SuiviServeurEcarte suivi, IAlertesTitulaire alertes, IHorloge horloge, ITraceErreurs? trace = null)
{
    public static readonly TimeSpan Intervalle = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan IntervalleAlertes = TimeSpan.FromHours(1);

    /// <summary>Null si rien à surveiller (autre serveur non réglé). Déjà écarté : l'état est gardé, plus de relevé, alerte toutes les heures.</summary>
    public async Task<DecisionEcart?> ExecuterAsync(CancellationToken ct = default)
    {
        if (!releves.AutreServeurRegle) return null;
        if (suivi.Ecart is { } e && !e.Isole)
        {
            if (e.Indecis) await AlerterTimelinesEgalesAsync(releves.AutreServeur, e.TimelineLocale, ct);
            else await AlerterEcarteAsync(e.ServeurRetenu ?? releves.AutreServeur, ct);
            return new DecisionEcart(e.Indecis ? VerdictEcart.TimelinesEgales : VerdictEcart.Ecarte, true, e.TimelineLocale, e.TimelineRetenue);
        }

        // Isolement (V2-1b) : SurveillerIsolement le lève ; ici seulement, si l'autre serveur est devenu principal avec une timeline plus récente, il devient définitif
        var isole = suivi.Ecart is { Isole: true };
        var local = await releves.LocalAsync(ct);
        var autre = local is { EnRecovery: false } ? await releves.AutreAsync(ct) : null;
        var decision = local is { EnRecovery: true }
            ? new DecisionEcart(VerdictEcart.Normal, false)   // ce serveur est le 2e serveur en veille : rien à surveiller ici
            : DecisionServeurEcarte.Evaluer(local, autre, suivi.Precedente);
        suivi.Precedente = decision;
        if (isole && decision is not { Verdict: VerdictEcart.Ecarte, Confirme: true }) return decision;   // isolé : aucune autre alerte ni écart ici

        switch (decision)
        {
            case { Verdict: VerdictEcart.Ecarte, Confirme: true }:
                var ecart = new EcartDetecte(horloge.MaintenantUtc, releves.AutreServeur, decision.TimelineLocale!.Value, decision.TimelineAutre!.Value);
                if (isole) suivi.RemplacerIsolement(ecart); else suivi.Ecarter(ecart);
                await AlerterEcarteAsync(ecart.ServeurRetenu, ct);
                break;
            case { Verdict: VerdictEcart.TimelinesEgales, Confirme: true }:
                // Q10 : l'alerte part AVANT le verrou (la base est encore en écriture), puis ce serveur passe en lecture seule
                await AlerterTimelinesEgalesAsync(releves.AutreServeur, decision.TimelineLocale, ct);
                suivi.Ecarter(new EcartDetecte(horloge.MaintenantUtc, null, decision.TimelineLocale!.Value, decision.TimelineLocale.Value, Indecis: true));
                break;
            case { Verdict: VerdictEcart.Retenu, Confirme: true }:
                await AlerterAsync(AlerteSecondServeur.AncienPrincipal, "Un ancien serveur principal écrit encore",
                    "Un ancien serveur principal (" + releves.AutreServeur + ") écrit encore : sa base est en écriture, avec une timeline plus ancienne ("
                    + decision.TimelineAutre + " contre " + decision.TimelineLocale + " ici). Ce serveur reste le serveur retenu.\n\n"
                    + "À faire : arrêtez l'ancien serveur ou remettez-le en 2e serveur, et vérifiez que tous les postes sont branchés sur ce serveur.",
                    "Un ancien serveur principal ecrit encore : il doit etre arrete ou remis en 2e serveur.", ct);
                break;
            case { Verdict: VerdictEcart.LigneeDifferente, Confirme: true }:
                await AlerterAsync(AlerteSecondServeur.LigneeDifferente, "L'autre serveur n'est pas une copie de celui-ci",
                    "L'autre serveur (" + releves.AutreServeur + ") a une base en écriture qui n'est pas une copie de celle-ci "
                    + "(identifiant système différent) : vérifiez le réglage SecondServeur:AutreServeur. Aucun serveur n'est écarté.",
                    "L'autre serveur n'est pas une copie de celui-ci : verifiez le reglage SecondServeur:AutreServeur.", ct);
                break;
            case { Verdict: VerdictEcart.Normal }:
                suivi.OublierAlertes();   // situation rétablie : la prochaine alerte repart sans délai
                break;
        }
        return decision;
    }

    public static string MessageTimelinesEgales(string? autreServeur, long? timeline) =>
        $"Deux bases en écriture avec la même timeline ({timeline}) : ce serveur et l'autre ({autreServeur}). Aucun ne peut être retenu : les deux passent en "
        + "lecture seule. Arrêtez PostgreSQL sur celui que vous n'avez pas choisi, puis redémarrez l'API de l'autre.";

    private Task AlerterTimelinesEgalesAsync(string? autreServeur, long? timeline, CancellationToken ct) =>
        AlerterAsync(AlerteSecondServeur.TimelinesEgales, "Aucun serveur ne peut être désigné", MessageTimelinesEgales(autreServeur, timeline),
            "Aucun serveur ne peut etre designe : les deux sont en lecture seule, decision du titulaire necessaire.", ct);

    private Task AlerterEcarteAsync(string? serveurRetenu, CancellationToken ct)
    {
        var retenu = string.IsNullOrWhiteSpace(serveurRetenu) ? "l'autre serveur" : serveurRetenu;
        return AlerterAsync(AlerteSecondServeur.Ecarte, "Ce serveur a été remplacé",
            $"Ce serveur a été remplacé par {retenu} ; il est en lecture seule ; les postes doivent basculer.",
            $"Ce serveur a ete remplace par {retenu} ; il est en lecture seule ; les postes doivent basculer.", ct);
    }

    /// <summary>Au plus une alerte par heure et par sorte ; jamais bloquant, aucune donnée de patient.</summary>
    private async Task AlerterAsync(AlerteSecondServeur sorte, string objet, string detail, string sms, CancellationToken ct)
    {
        var maintenant = horloge.MaintenantUtc;
        if (suivi.DerniereAlerteUtc(sorte) is { } a && maintenant - a < IntervalleAlertes) return;
        suivi.NoterAlerte(sorte, maintenant);
        await AlerteNonEnvoyeeSignalee.SansBlocageAsync(() => alertes.SecondServeurAsync(objet, detail, sms, ct), "serveur écarté (2e serveur)", trace);
    }
}
