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

/// <summary>Écart confirmé : CE serveur est écarté, l'autre est retenu.</summary>
public sealed record EcartDetecte(DateTime DepuisUtc, string? ServeurRetenu, long TimelineLocale, long TimelineRetenue);

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

/// <summary>État en mémoire de la détection (singleton) : dernière décision, état écarté, dernière alerte envoyée.</summary>
public sealed class SuiviServeurEcarte : IServeurEcarteDetecte
{
    private readonly object _verrou = new();
    private EcartDetecte? _ecart;

    public EcartDetecte? Ecart { get { lock (_verrou) return _ecart; } }
    public event Action<EcartDetecte?>? Change;

    internal DecisionEcart? Precedente { get; set; }
    internal DateTime? DerniereAlerteUtc { get; set; }

    public void Lever()
    {
        lock (_verrou) { _ecart = null; Precedente = null; DerniereAlerteUtc = null; }
        Change?.Invoke(null);
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
/// Un passage de la surveillance (toutes les 30 s, tâche SurveillanceSecondServeur). Côté retenu et lignée différente : alerte au
/// titulaire seulement (au plus une par heure). Côté écarté : seul l'état est posé (le lot 3 fait le reste, le lot 5 alerte).
/// </summary>
public sealed class SurveillerServeurEcarte(IReleveServeurs releves, SuiviServeurEcarte suivi, IAlertesTitulaire alertes, IHorloge horloge, ITraceErreurs? trace = null)
{
    public static readonly TimeSpan Intervalle = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan IntervalleAlertes = TimeSpan.FromHours(1);

    /// <summary>Null si rien à surveiller (autre serveur non réglé). Déjà écarté : l'état est gardé, plus de relevé.</summary>
    public async Task<DecisionEcart?> ExecuterAsync(CancellationToken ct = default)
    {
        if (!releves.AutreServeurRegle) return null;
        if (suivi.Ecart is { } e) return new DecisionEcart(VerdictEcart.Ecarte, true, e.TimelineLocale, e.TimelineRetenue);

        var local = await releves.LocalAsync(ct);
        var autre = local is { EnRecovery: false } ? await releves.AutreAsync(ct) : null;
        var decision = local is { EnRecovery: true }
            ? new DecisionEcart(VerdictEcart.Normal, false)   // ce serveur est le 2e serveur en veille : rien à surveiller ici
            : DecisionServeurEcarte.Evaluer(local, autre, suivi.Precedente);
        suivi.Precedente = decision;

        switch (decision)
        {
            case { Verdict: VerdictEcart.Ecarte, Confirme: true }:
                suivi.Ecarter(new EcartDetecte(horloge.MaintenantUtc, releves.AutreServeur, decision.TimelineLocale!.Value, decision.TimelineAutre!.Value));
                break;
            case { Verdict: VerdictEcart.Retenu, Confirme: true }:
                await AlerterAsync("L'ancien serveur principal (" + releves.AutreServeur + ") a encore une base en écriture, avec une timeline plus ancienne ("
                    + decision.TimelineAutre + " contre " + decision.TimelineLocale + " ici) : il aurait dû être écarté. Ce serveur reste le serveur retenu ; rien n'est changé ici.", ct);
                break;
            case { Verdict: VerdictEcart.LigneeDifferente, Confirme: true }:
                await AlerterAsync("L'autre serveur (" + releves.AutreServeur + ") a une base en écriture qui n'est pas une copie de celle-ci "
                    + "(identifiant système différent) : vérifiez le réglage SecondServeur:AutreServeur. Aucun serveur n'est écarté.", ct);
                break;
            case { Verdict: VerdictEcart.Normal }:
                suivi.DerniereAlerteUtc = null;   // situation rétablie : la prochaine alerte repart sans délai
                break;
        }
        return decision;
    }

    private async Task AlerterAsync(string message, CancellationToken ct)
    {
        var maintenant = horloge.MaintenantUtc;
        if (suivi.DerniereAlerteUtc is { } a && maintenant - a < IntervalleAlertes) return;
        suivi.DerniereAlerteUtc = maintenant;
        await AlerteNonEnvoyeeSignalee.SansBlocageAsync(() => alertes.SauvegardeEnEchecAsync(false, message, ct), "serveur écarté (2e serveur)", trace);
    }
}
