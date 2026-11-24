using Pharma.Application.Commun;
using Pharma.Application.Notifications;

namespace Pharma.Application.SecondServeur;

// ---- Bascule du 2e serveur, vague 2, lot V2-4 : serveur 1, mise hors jeu (cas 2) et alertes reçues du 2e serveur ----------------
// docs/taches/2026-11-23-bascule-2e-serveur/plan-vague-2.md § 3 (cas 2) et contrat-api.md § 2.4 et § 2.5.

/// <summary>Ce que le retrait demande à la base de CE serveur (implémenté par l'Infrastructure sur la source SANS verrou de lecture seule).</summary>
public interface IEcrituresServeur
{
    /// <summary>
    /// Attend la fin des transactions ouvertes par l'API (pg_stat_activity, nom d'application de l'API). Faux si elles ne sont pas terminées
    /// dans <paramref name="delai"/>. Aucune donnée de patient n'est lue.
    /// </summary>
    Task<bool> AttendreFinTransactionsApiAsync(TimeSpan delai, CancellationToken ct = default);
    /// <summary>pg_current_wal_lsn() au format PostgreSQL (« 0/16B3748 »), null si illisible.</summary>
    Task<string?> LsnCourantAsync(CancellationToken ct = default);
}

/// <summary>Journal des actions sensibles écrit par le serveur lui-même (journal_audit, sans compte d'utilisateur) ; source sans verrou de lecture seule.</summary>
public interface IJournalSysteme
{
    Task EcrireAsync(string action, string? tableCible, IReadOnlyDictionary<string, object?> details, CancellationToken ct = default);
}

public enum IssueRetrait { Ecarte, DejaEcarte, Impossible }

/// <param name="LsnFinal">Renseigné si <see cref="IssueRetrait.Ecarte"/> ; pour <see cref="IssueRetrait.DejaEcarte"/>, seulement si le serveur est déjà écarté par un retrait et que le LSN est lisible.</param>
public sealed record ResultatRetrait(IssueRetrait Issue, string? LsnFinal, string Raison);

/// <summary>
/// POST /api/inter-serveurs/retrait (serveur 1) : pose l'écart « retrait » (verrou des connexions, barrage 503, tâches suspendues, par le mécanisme du lot 3),
/// attend la fin des transactions de l'API (10 s au plus), écrit la ligne serveur_ecarte (nature retrait, timeline du retenu inconnue) puis lit le LSN final.
/// L'écart d'un retrait est gardé en base et collant : seule la levée manuelle (--lever-ecart) le retire. Si rien n'a pu être écrit, l'écart posé est levé.
/// </summary>
public sealed class RetraitServeur(IReleveServeurs releves, SuiviServeurEcarte suivi, IEcartsDepot depot, IEcrituresServeur ecritures, IVeilleServeur veille,
    IHorloge horloge, IJournalSysteme audit, ITraceErreurs? trace = null)
{
    public static readonly TimeSpan DelaiTransactions = TimeSpan.FromSeconds(10);
    public const string ActionAudit = "serveur_retrait";
    private readonly SemaphoreSlim _verrou = new(1, 1);

    public async Task<ResultatRetrait> ExecuterAsync(CancellationToken ct = default)
    {
        if (!releves.AutreServeurRegle) return new(IssueRetrait.Impossible, null, "second_serveur_non_declare");
        if (veille.EstVeille) return new(IssueRetrait.Impossible, null, "serveur_en_veille");
        // Un retrait à la fois : un second appel pendant le premier est un serveur « déjà écarté »
        if (!await _verrou.WaitAsync(0, ct)) return new(IssueRetrait.DejaEcarte, null, "retrait_en_cours");
        var ligneEcrite = false;
        var pose = false;
        try
        {
            if (suivi.Ecart is { } existant) return await DejaAsync(existant, ct);
            if (await depot.LireActifAsync(ct) is { } enBase) return await DejaAsync(enBase, ct);

            var local = await releves.LocalAsync(ct);
            if (local is not { EnRecovery: false, Timeline: { } timeline }) return new(IssueRetrait.Impossible, null, "etat_local_illisible");
            var retenu = releves.AutreServeur;
            var ecart = new EcartDetecte(horloge.MaintenantUtc, retenu, timeline, 0, Retrait: true);
            if (!suivi.Ecarter(ecart)) return new(IssueRetrait.DejaEcarte, null, "deja_ecarte");
            pose = true;

            if (!await ecritures.AttendreFinTransactionsApiAsync(DelaiTransactions, ct))
                return new(IssueRetrait.Impossible, null, "transactions_non_terminees");
            await depot.EnregistrerAsync(ecart, GardienServeurEcarte.Motif(ecart), ct);   // faux : une ligne existe déjà (reprise par le gardien), elle convient
            ligneEcrite = true;

            // Lu APRÈS l'écriture de la ligne : tout ce que le 2e serveur doit avoir rejoué pour ne rien perdre
            var lsn = await ecritures.LsnCourantAsync(ct);
            await JournaliserAsync(retenu, lsn, ct);
            if (lsn is null)
            {
                trace?.Erreur("Retrait : LSN final illisible (le serveur reste écarté, levée manuelle)", new InvalidOperationException("lsn_illisible"));
                return new(IssueRetrait.Impossible, null, "lsn_illisible");
            }
            return new(IssueRetrait.Ecarte, lsn, "retrait_fait");
        }
        finally
        {
            // Rien d'écrit en base : l'état posé en mémoire ne doit pas rester (serveur verrouillé sans trace, aucune promotion possible)
            if (pose && !ligneEcrite) suivi.Lever();
            _verrou.Release();
        }
    }

    private async Task<ResultatRetrait> DejaAsync(EcartDetecte existant, CancellationToken ct)
    {
        string? lsn = null;
        if (existant.Retrait)
        {
            try { lsn = await ecritures.LsnCourantAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { trace?.Erreur("Retrait : LSN d'un écart déjà posé illisible", e); }
        }
        return new(IssueRetrait.DejaEcarte, lsn, "deja_ecarte");
    }

    private async Task JournaliserAsync(string? retenu, string? lsn, CancellationToken ct)
    {
        try
        {
            await audit.EcrireAsync(ActionAudit, "serveur_ecarte",
                new Dictionary<string, object?> { ["nature"] = "retrait", ["serveur_retenu"] = retenu, ["lsn_final"] = lsn }, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            trace?.Erreur("Retrait : écrit dans serveur_ecarte mais pas au journal des actions", e);
        }
    }
}

/// <summary>Au plus une alerte par heure et par clé (type, ou type et poste) ; en mémoire seulement (un redémarrage autorise une alerte de plus).</summary>
public sealed class LimiteAlertesHoraire(IHorloge horloge)
{
    public static readonly TimeSpan Intervalle = TimeSpan.FromHours(1);
    private readonly object _verrou = new();
    private readonly Dictionary<string, DateTime> _dernieres = [];

    /// <summary>Vrai (et noté) si aucune alerte de cette clé n'est partie depuis moins d'une heure.</summary>
    public bool Autoriser(string cle)
    {
        var maintenant = horloge.MaintenantUtc;
        lock (_verrou)
        {
            if (_dernieres.TryGetValue(cle, out var d) && maintenant - d < Intervalle) return false;
            _dernieres[cle] = maintenant;
            return true;
        }
    }
}

/// <summary>Types d'alerte acceptés de l'autre serveur : valeurs de fil de Pharma.Shared.TypeAlerteInterServeurs (test d'égalité côté tests).</summary>
public static class TypesAlerteInterServeurs
{
    public const string ReseauPoste = "reseau_poste", ClesJetonsDifferentes = "cles_jetons_differentes", ApiServeur1Arretee = "api_serveur1_arretee",
        EgaliteMajorite = "egalite_majorite", PromotionManuelleNecessaire = "promotion_manuelle_necessaire";

    public static readonly IReadOnlyList<string> Tous = [ReseauPoste, ClesJetonsDifferentes, ApiServeur1Arretee, EgaliteMajorite, PromotionManuelleNecessaire];

    public static bool Connu(string? type) => type is not null && Tous.Contains(type);

    public static (string Objet, string Sms) Textes(string type) => type switch
    {
        ReseauPoste => ("Un poste ne joint plus le serveur", "Un poste ne joint plus le serveur de la pharmacie."),
        ClesJetonsDifferentes => ("Clés des jetons différentes entre les deux serveurs", "Les 2 serveurs n'ont pas la meme cle des jetons : bascule sans reconnexion, retrait et relais impossibles."),
        ApiServeur1Arretee => ("L'API du serveur 1 est arrêtée", "L'API du serveur 1 est arretee : redemarrez-la."),
        EgaliteMajorite => ("Égalité de postes : aucune promotion", "Egalite entre les postes : le 2e serveur ne se promeut pas, decision du titulaire necessaire."),
        _ => ("Promotion du 2e serveur à faire à la main", "La promotion automatique du 2e serveur n'a pas eu lieu : promotion manuelle a decider.")
    };
}

public enum IssueAlerteRecue { Acceptee, TypeInconnu, MessageInvalide, PosteManquant, Limitee }

/// <summary>POST /api/inter-serveurs/alerte (serveur 1) : prévient le titulaire (IAlertesTitulaire), au plus une fois par heure, par type et par poste ; aucune donnée de patient.</summary>
public sealed class RecevoirAlerteInterServeurs(IAlertesTitulaire alertes, LimiteAlertesHoraire limite, ITraceErreurs? trace = null)
{
    public const int MessageMax = 500;

    public async Task<IssueAlerteRecue> ExecuterAsync(string? type, string? poste, string? message, CancellationToken ct = default)
    {
        if (!TypesAlerteInterServeurs.Connu(type)) return IssueAlerteRecue.TypeInconnu;
        if ((message?.Length ?? 0) > MessageMax) return IssueAlerteRecue.MessageInvalide;
        if (type == TypesAlerteInterServeurs.ReseauPoste && string.IsNullOrWhiteSpace(poste)) return IssueAlerteRecue.PosteManquant;
        if ((poste?.Length ?? 0) > 100) return IssueAlerteRecue.MessageInvalide;
        var cle = type == TypesAlerteInterServeurs.ReseauPoste ? type + "|" + poste!.Trim() : type!;
        if (!limite.Autoriser("recue|" + cle)) return IssueAlerteRecue.Limitee;
        // Plafond global des alertes « reseau_poste » (AlerteRelaisPoste.PlafondParHeure par heure, tous postes confondus)
        if (type == TypesAlerteInterServeurs.ReseauPoste
            && !Enumerable.Range(0, AlerteRelaisPoste.PlafondParHeure).Any(i => limite.Autoriser("recue|reseau_poste|global|" + i))) return IssueAlerteRecue.Limitee;
        var (objet, sms) = TypesAlerteInterServeurs.Textes(type!);
        var detail = string.IsNullOrWhiteSpace(message) ? objet : message.Trim();
        if (type == TypesAlerteInterServeurs.ReseauPoste) detail = $"Poste {poste!.Trim()} : {detail}";
        await AlerteNonEnvoyeeSignalee.SansBlocageAsync(() => alertes.SecondServeurAsync(objet, detail, sms, ct), "alerte du 2e serveur", trace);
        return IssueAlerteRecue.Acceptee;
    }
}
