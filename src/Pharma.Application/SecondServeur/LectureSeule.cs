using Pharma.Application.Commun;
using Pharma.Application.Notifications;

namespace Pharma.Application.SecondServeur;

// ---- Bascule du 2e serveur, lot 3 : lecture seule d'un serveur écarté (2026-11-23) -------------------------------------------
// docs/taches/2026-11-23-bascule-2e-serveur/plan.md (périmètre « B allégée »). Ce fichier : persistance de l'écart (script 163),
// verrou des connexions de la base, démarrage en lecture seule (Q8, Q10). L'intergiciel 503 et la console --lever-ecart sont dans l'API.

/// <summary>Verrou de la base de CE serveur : toute nouvelle connexion du pool s'ouvre en lecture seule (implémenté par l'Infrastructure).</summary>
public interface IVerrouBase
{
    /// <summary>Vrai : connexions en lecture seule et pool vidé ; faux : retour à l'écriture (levée).</summary>
    void Appliquer(bool lectureSeule);
}

/// <summary>État « serveur écarté » gardé en base (table serveur_ecarte, script 163) : survit à un redémarrage de l'API.</summary>
public interface IEcartsDepot
{
    /// <summary>Écart actif (non levé), sinon null.</summary>
    Task<EcartDetecte?> LireActifAsync(CancellationToken ct = default);
    /// <summary>Ajoute la ligne ; faux si un écart actif existe déjà (rien n'est modifié).</summary>
    Task<bool> EnregistrerAsync(EcartDetecte ecart, string motif, CancellationToken ct = default);
    /// <summary>Lève l'écart actif (une fois) ; faux s'il n'y en a pas.</summary>
    Task<bool> LeverAsync(string par, string motif, CancellationToken ct = default);
}

/// <summary>
/// Relie l'état détecté (<see cref="IServeurEcarteDetecte"/>) à ses effets : verrou de la base à chaque changement d'état,
/// écriture de l'écart en base (jamais l'état « indécis » de Q10), et levée vue depuis la base (commande console).
/// </summary>
public sealed class GardienServeurEcarte(IServeurEcarteDetecte suivi, IVerrouBase verrou, IEcartsDepot depot, IReleveServeurs releves, ITraceErreurs? trace = null)
{
    private int _abonne;
    private volatile bool _persiste;
    private Task _derniere = Task.CompletedTask;

    /// <summary>Dernière écriture lancée (pour les tests).</summary>
    public Task Derniere => _derniere;

    /// <summary>Abonnement à l'événement Change ; idempotent. À appeler avant tout démarrage de tâche ou de requête.</summary>
    public void Demarrer()
    {
        if (Interlocked.Exchange(ref _abonne, 1) == 1) return;
        suivi.Change += SurChange;
        if (suivi.Ecart is { } deja) SurChange(deja);
    }

    /// <summary>Ligne active trouvée en base mais ignorée faute de 2e serveur déclaré (à signaler au journal par l'appelant), sinon null.</summary>
    public EcartDetecte? EcartIgnore { get; private set; }

    /// <summary>
    /// Au démarrage : relit l'écart actif en base et le restaure (état collant). Base illisible : rien n'est restauré, l'erreur est tracée.
    /// Sans 2e serveur déclaré : aucun verrou, la ligne éventuelle est seulement mémorisée dans <see cref="EcartIgnore"/>.
    /// </summary>
    public async Task<bool> ChargerAsync(CancellationToken ct = default)
    {
        try
        {
            if (await depot.LireActifAsync(ct) is not { } ecart) return false;
            if (!releves.AutreServeurRegle) { EcartIgnore = ecart; return false; }
            _persiste = true;
            suivi.Restaurer(ecart);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            trace?.Erreur("Serveur écarté : état gardé en base illisible (script 163 passé ?)", e);
            return false;
        }
    }

    /// <summary>
    /// Appelée à chaque passage de la surveillance (30 s), tant que ce serveur est écarté : réécrit l'écart si l'écriture a échoué, et
    /// constate une levée faite en base par la commande console (--lever-ecart) pour rendre la main sans redémarrage.
    /// </summary>
    public async Task SynchroniserAsync(CancellationToken ct = default)
    {
        if (!releves.AutreServeurRegle || suivi.Ecart is not { EnMemoireSeulement: false } ecart) return;
        var actif = await depot.LireActifAsync(ct);
        if (actif is not null) { _persiste = true; return; }
        if (_persiste)
        {
            _persiste = false;
            suivi.Lever();   // levée faite en base : retour à l'écriture (Change(null) retire le verrou)
            return;
        }
        await PersisterAsync(ecart, ct);
    }

    private void SurChange(EcartDetecte? ecart)
    {
        try { verrou.Appliquer(ecart is not null); }   // d'abord le verrou (mémoire), puis la trace en base
        catch (Exception e) when (e is not OperationCanceledException) { trace?.Erreur("Serveur écarté : verrou des connexions non appliqué", e); }
        // Retrait (cas 2) : la ligne est écrite par la route de retrait, APRÈS la fin des transactions de l'API (le gardien ne l'écrit qu'en reprise, à la synchronisation)
        if (ecart is { EnMemoireSeulement: false, Retrait: false }) _derniere = Task.Run(() => PersisterAsync(ecart, CancellationToken.None));
    }

    private async Task PersisterAsync(EcartDetecte ecart, CancellationToken ct)
    {
        try
        {
            await depot.EnregistrerAsync(ecart, Motif(ecart), ct);
            _persiste = true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            trace?.Erreur("Serveur écarté : écart non gardé en base (nouvel essai au prochain passage)", e);
        }
    }

    public static string Motif(EcartDetecte e) => e.Retrait
        ? $"Serveur écarté : mise hors jeu demandée par l'autre serveur ({e.ServeurRetenu ?? "inconnu"}) avant sa promotion"
        : $"Serveur écarté : l'autre serveur ({e.ServeurRetenu ?? "inconnu"}) a une timeline plus récente ({e.TimelineRetenue} contre {e.TimelineLocale} ici)";
}

/// <summary>Démarrage de l'API face à un autre serveur principal (Q8, Q10) : décision prise avec les mêmes relevés que la surveillance.</summary>
public static class DemarrageEcarte
{
    /// <summary>
    /// Null : rien à écarter (le refus de démarrage éventuel reste décidé par <see cref="ReplicationOfficine.RefusDemarrage"/>).
    /// Autre serveur plus récent (même lignée, 2 relevés concordants) : ce serveur démarre écarté, en lecture seule (Q8).
    /// Même timeline : écart « indécis » (Q10). Lignée illisible ou injoignable : null (aucune décision sur un doute).
    /// </summary>
    public static async Task<EcartDetecte?> EvaluerAsync(IReleveServeurs releves, IHorloge horloge, TimeSpan pause, CancellationToken ct = default)
    {
        if (!releves.AutreServeurRegle) return null;
        DecisionEcart? precedente = null;
        for (var i = 0; i < 2; i++)
        {
            if (i > 0) await Task.Delay(pause, ct);
            var local = await releves.LocalAsync(ct);
            if (local is null || local.EnRecovery) return null;
            precedente = DecisionServeurEcarte.Evaluer(local, await releves.AutreAsync(ct), precedente);
        }
        return precedente switch
        {
            { Verdict: VerdictEcart.Ecarte, Confirme: true } d => new EcartDetecte(horloge.MaintenantUtc, releves.AutreServeur, d.TimelineLocale!.Value, d.TimelineAutre!.Value),
            { Verdict: VerdictEcart.TimelinesEgales, Confirme: true } d => new EcartDetecte(horloge.MaintenantUtc, null, d.TimelineLocale!.Value, d.TimelineLocale.Value, Indecis: true),
            _ => null
        };
    }

    /// <summary>Évalue, alerte le titulaire si l'état est indécis (avant le verrou), puis pose l'écart. Retourne l'écart posé, ou null.</summary>
    public static async Task<EcartDetecte?> AppliquerAsync(IReleveServeurs releves, SuiviServeurEcarte suivi, IAlertesTitulaire alertes, IHorloge horloge,
        TimeSpan pause, ITraceErreurs? trace = null, CancellationToken ct = default)
    {
        if (suivi.Ecart is not null) return null;
        var ecart = await EvaluerAsync(releves, horloge, pause, ct);
        if (ecart is null) return null;
        if (ecart.Indecis && !(suivi.DerniereAlerteUtc(AlerteSecondServeur.TimelinesEgales) is { } a && horloge.MaintenantUtc - a < SurveillerServeurEcarte.IntervalleAlertes))
        {
            suivi.NoterAlerte(AlerteSecondServeur.TimelinesEgales, horloge.MaintenantUtc);   // même mécanisme et même limite horaire que la surveillance
            await AlerteNonEnvoyeeSignalee.SansBlocageAsync(
                () => alertes.SecondServeurAsync("Aucun serveur ne peut être désigné", SurveillerServeurEcarte.MessageTimelinesEgales(releves.AutreServeur, ecart.TimelineLocale),
                    "Aucun serveur ne peut etre designe : les deux sont en lecture seule, decision du titulaire necessaire.", ct),
                "serveur écarté (2e serveur)", trace);
        }
        suivi.Restaurer(ecart);
        return ecart;
    }
}
