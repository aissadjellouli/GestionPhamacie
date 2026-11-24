using Pharma.Application.Commun;
using Pharma.Application.Notifications;

namespace Pharma.Application.SecondServeur;

// ---- Bascule du 2e serveur, vague 2, lot V2-4 : exécutant de la promotion automatique (2e serveur, API en veille) ---------------
// docs/taches/2026-11-23-bascule-2e-serveur/plan-vague-2.md § 3. La décision (verdict) est celle de DecisionPromotion (lot V2-3) ; ici : garde-fous,
// retrait du serveur 1 (cas 2) avec 3 preuves, fichier de décision, pg_promote, redémarrage. Au redémarrage en principal : enregistrement en base.

/// <summary>Réponse du serveur 1 à POST /api/inter-serveurs/retrait. Fiable : réponse signée, authentique et liée à la requête.</summary>
public sealed record ReponseRetrait(bool Fiable, int? Statut, string? LsnFinal);

/// <summary>Appel signé du retrait (implémentation : API, sur le client signé du lot V2-2).</summary>
public interface IClientRetraitServeur1
{
    Task<ReponseRetrait> DemanderRetraitAsync(CancellationToken ct = default);
}

/// <summary>
/// Alerte envoyée au serveur 1 (POST /api/inter-serveurs/alerte, signé) : la base du 2e serveur est en lecture seule, il ne peut pas prévenir lui-même le titulaire.
/// Vrai si le serveur 1 a répondu 202.
/// </summary>
public interface IAlertesVersServeur1
{
    Task<bool> EnvoyerAsync(string type, string? poste, string message, DateTime survenueUtc, CancellationToken ct = default);
}

/// <summary>Ce que la copie locale (base en recovery) dit d'elle-même ; null = illisible. Timeline : null si le droit du script 163b manque.</summary>
public sealed record EtatCopie(bool EnRecovery, long? IdentifiantSysteme, int? Timeline, DateTime? DerniereTransactionRecueUtc);

public enum ResultatPromotionBase { Promue, NonPromue, DroitAbsent }

/// <summary>Accès à la base du 2e serveur pour la promotion (implémentation : Infrastructure). Jamais d'écriture : la copie est en lecture seule.</summary>
public interface IBasePromotion
{
    Task<EtatCopie?> LireCopieAsync(CancellationToken ct = default);
    /// <summary>Une ligne active serveur_ecarte de nature « retrait » est visible DANS LA COPIE locale (écrite par le serveur 1, reçue par réplication).</summary>
    Task<bool> RetraitVisibleAsync(CancellationToken ct = default);
    /// <summary>pg_last_wal_replay_lsn() &gt;= le LSN donné (toutes les écritures du serveur 1 sont rejouées).</summary>
    Task<bool> LsnRejoueAtteintAsync(string lsn, CancellationToken ct = default);
    /// <summary>SELECT pg_promote(true, 60). Droit absent (42501) : <see cref="ResultatPromotionBase.DroitAbsent"/>. Base qui n'est pas en recovery : refus sans appel.</summary>
    Task<ResultatPromotionBase> PromouvoirAsync(CancellationToken ct = default);
}

/// <summary>Contenu du fichier de décision Journaux/bascule-&lt;horodatage&gt;.json (noms de postes seulement, aucune donnée de patient).</summary>
public sealed record DecisionBascule(string Cas, string Verdict, DateTime DecideLeUtc, int Connus, int Votes, int VotesAuthentifies, int Joignent,
    IReadOnlyList<RapportBascule> Rapports, SondesBascule Sondes, string? LsnFinal, DateTime? DerniereTransactionRecueUtc, int? TimelineAvant, string Motif);

public sealed record RapportBascule(string Poste, bool Serveur1Joint, DateTime RecuLeUtc, DateTime? PerteDepuisUtc, bool Authentifie);

public sealed record SondesBascule(bool BaseJointe, bool ApiJointe, DateTime? BaseMuetteDepuisUtc, DateTime? ApiMuetteDepuisUtc);

public static class CasBascule
{
    public const string Promotion = "promotion", RetraitPromotion = "retrait_promotion";
}

/// <summary>Un fichier de décision pas encore enregistré en base. Decision null : fichier illisible (laissé en place, signalé).</summary>
public sealed record FichierDecision(string Chemin, DecisionBascule? Decision);

public interface IFichiersDecisionBascule
{
    /// <summary>Écrit le fichier (avant la promotion) ; retourne son chemin.</summary>
    Task<string> EcrireAsync(DecisionBascule decision, CancellationToken ct = default);
    Task<IReadOnlyList<FichierDecision>> NonEnregistresAsync(CancellationToken ct = default);
    /// <summary>Renomme le fichier (« enregistré ») : il n'est plus repris au démarrage suivant.</summary>
    Task MarquerEnregistreAsync(string chemin, CancellationToken ct = default);
    /// <summary>Renomme le fichier d'une décision qui n'a CERTAINEMENT pas été suivie d'une promotion (droit absent) : il ne sera jamais enregistré en base.</summary>
    Task AnnulerAsync(string chemin, CancellationToken ct = default);
}

public enum IssuePromotion
{
    /// <summary>Rien à faire à ce tour (verdict autre, pas encore confirmé, ou déjà tenté).</summary>
    Aucune,
    /// <summary>Aucun 2e serveur déclaré, ou ce serveur n'est pas en veille : mécanisme inactif.</summary>
    Inactive,
    Promue,
    /// <summary>Verdict confirmé mais pas de promotion (garde-fou, preuve manquante, droit absent) : journal et alerte.</summary>
    NonPromue
}

public sealed record ResultatPromotion(IssuePromotion Issue, string Raison);

/// <summary>
/// Exécutant de la promotion, appelé après chaque évaluation de la vigie. Une seule promotion par processus (verrou). Seule l'étape irréversible
/// (pg_promote) n'est tentée qu'une fois par processus : un échec laisse la main au titulaire (alerte). Le retrait du serveur 1 est rejouable
/// (409 avec LSN quand il est déjà écarté par un retrait) : il est redemandé à chaque tour tant que les 3 preuves ne sont pas réunies.
/// </summary>
public sealed class ExecutantPromotion(
    VigieSecondServeur vigie, RegistreRapportsPostes registre, SondeServeur1 sonde, IReleveServeurs releves, IBasePromotion baseLocale,
    IClientRetraitServeur1 retrait, IAlertesVersServeur1 alertes, LimiteAlertesHoraire limite, IFichiersDecisionBascule fichiers,
    IJournalDecisions journal, IVeilleServeur veille, IHorloge horloge, ReglagesBascule reglages, Func<bool>? contexteInterdit = null,
    Func<TimeSpan, CancellationToken, Task>? pause = null)
{
    public const int ConfirmationsMinimum = 2;
    public static readonly TimeSpan DelaiPreuves = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PasPreuves = TimeSpan.FromSeconds(1);
    private readonly SemaphoreSlim verrou = new(1, 1);
    private bool tentee, promue;
    private string? derniereRaison;
    private readonly Func<TimeSpan, CancellationToken, Task> attendre = pause ?? ((d, ct) => Task.Delay(d, ct));

    public bool Promue { get { lock (verrou) return promue; } }

    /// <summary>Un tour : alerte éventuelle de la vigie (une par heure et par type), puis promotion si le verdict est confirmé.</summary>
    public async Task<ResultatPromotion> ApresEvaluationAsync(ResultatDecision r, CancellationToken ct = default)
    {
        if (!releves.AutreServeurRegle || !veille.EstVeille) return new(IssuePromotion.Inactive, "inactif");
        if (r.TypeAlerte is { } type) await AlerterAsync(type, MessageVigie(r), ct);
        if (r.Verdict is not (VerdictPromotion.Promotion or VerdictPromotion.RetraitPuisPromotion) || vigie.Confirmations < ConfirmationsMinimum)
            return new(IssuePromotion.Aucune, "pas_de_decision_confirmee");
        if (!await verrou.WaitAsync(0, ct)) return new(IssuePromotion.Aucune, "deja_en_cours");
        try
        {
            if (promue) return new(IssuePromotion.Aucune, "deja_promue");
            if (tentee) return new(IssuePromotion.Aucune, "deja_tentee");
            return await ExecuterAsync(r, ct);
        }
        finally { verrou.Release(); }
    }

    private async Task<ResultatPromotion> ExecuterAsync(ResultatDecision r, CancellationToken ct)
    {
        var cas = r.Verdict == VerdictPromotion.RetraitPuisPromotion ? CasBascule.RetraitPromotion : CasBascule.Promotion;

        // ---- Garde-fous (rien d'irréversible) ----
        if (!reglages.PromotionAutomatique)
            return await RefuserAsync("promotion_automatique_desactivee", "La promotion automatique est désactivée (réglage) : promotion manuelle à décider.", true, ct);
        if (contexteInterdit?.Invoke() == true) return await RefuserAsync("demo_ou_restauration", "Démonstration ou restauration en cours.", false, ct);
        var copie = await baseLocale.LireCopieAsync(ct);
        if (copie is null) return await RefuserAsync("copie_illisible", "Base du 2e serveur illisible.", false, ct);
        if (!copie.EnRecovery) return new(IssuePromotion.Aucune, "base_deja_en_ecriture");   // promotion déjà faite (par le script manuel) : la veille redémarre
        if (copie.IdentifiantSysteme is not { } local)
            return await RefuserAsync("lignee_illisible", "Lignée de la base illisible (droit du script 163b manquant) : promotion manuelle à décider.", true, ct);
        if (sonde.IdentifiantServeur1 is not { } autre)
            return await RefuserAsync("lignee_serveur1_inconnue", "Lignée du serveur 1 jamais lue : promotion manuelle à décider.", true, ct);
        if (local != autre)
            return await RefuserAsync("lignee_differente", "Le serveur 1 n'est pas de la même lignée que cette copie : aucune promotion.", true, ct);

        // ---- Cas 2 : retrait du serveur 1 puis 3 preuves ----
        // Le retrait est rejouable : un serveur 1 déjà mis hors jeu par un retrait répond 409 avec son LSN courant. Une réponse 200 perdue ou une
        // copie en retard ne doivent pas laisser l'officine sans serveur en écriture : le tour suivant redemande le retrait et revérifie les preuves.
        string? lsnFinal = null;
        if (cas == CasBascule.RetraitPromotion)
        {
            var reponse = await retrait.DemanderRetraitAsync(ct);
            if (reponse is not { Fiable: true, Statut: 200 or 409, LsnFinal: { Length: > 0 } lsn })
                return await RefuserAsync("retrait_sans_reponse_fiable", "Le serveur 1 n'a pas confirmé sa mise hors jeu (réponse absente, non fiable ou refusée).", true, ct);
            lsnFinal = lsn;
            var manquante = await AttendrePreuvesAsync(lsn, ct);
            if (manquante is not null)
                return await RefuserAsync(manquante, "Preuve manquante après la mise hors jeu du serveur 1 (" + manquante + ") : aucune promotion.", true, ct);
        }

        // ---- Fichier de décision, puis promotion ----
        var maintenant = horloge.MaintenantUtc;
        var avant = await baseLocale.LireCopieAsync(ct) ?? copie;
        var s = sonde.Derniere ?? new Serveur1Vu(false, false, null, null);
        var decision = new DecisionBascule(cas, r.Verdict.ToString(), maintenant, r.Connus, r.Votes, r.VotesAuthentifies, r.Joignent,
            registre.Rapports().Select(x => new RapportBascule(x.Poste, x.Serveur1Joint, x.RecuLeUtc, x.PerteDepuisUtc, x.Authentifie)).ToList(),
            new SondesBascule(s.BaseJointe, s.ApiJointe, s.BaseMuetteDepuisUtc, s.ApiMuetteDepuisUtc), lsnFinal, avant.DerniereTransactionRecueUtc, avant.Timeline, r.Raison);
        string? fichier = null;
        try { fichier = await fichiers.EcrireAsync(decision, ct); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Le fichier sert à l'audit : son échec ne doit pas empêcher de rendre le service (cas noté au journal)
            await journal.EcrireAsync("fichier_decision_non_ecrit", new { cas, erreur = e.GetType().Name }, ct);
        }
        await journal.EcrireAsync("promotion_demandee", new { cas, lsnFinal, timelineAvant = avant.Timeline, derniereTransactionRecue = avant.DerniereTransactionRecueUtc }, ct);

        // Seule étape irréversible : une seule tentative de pg_promote par processus
        tentee = true;
        ResultatPromotionBase resultat;
        try { resultat = await baseLocale.PromouvoirAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await journal.EcrireAsync("promotion_erreur", new { cas, erreur = e.GetType().Name }, ct);
            resultat = ResultatPromotionBase.NonPromue;
        }
        switch (resultat)
        {
            case ResultatPromotionBase.Promue:
                lock (verrou) promue = true;
                await journal.EcrireAsync("promotion_faite", new { cas }, ct);
                veille.DemanderRedemarrage("promotion");
                return new(IssuePromotion.Promue, cas);
            case ResultatPromotionBase.DroitAbsent:
                if (fichier is not null) await fichiers.AnnulerAsync(fichier, ct);   // certainement pas promu : le fichier ne doit pas être repris au prochain démarrage
                return await RefuserAsync("droit_pg_promote_absent", "Le droit de promouvoir la base manque (script 166b) : promotion manuelle nécessaire.", true, ct);
            default:
                return await RefuserAsync("pg_promote_echec", "La promotion de la base n'a pas abouti dans le délai : vérifiez l'état de la base du 2e serveur.", true, ct);
        }
    }

    /// <summary>Preuves 2 et 3 dans les 60 s ; retourne le nom de la preuve manquante, null si elles sont réunies.</summary>
    private async Task<string?> AttendrePreuvesAsync(string lsn, CancellationToken ct)
    {
        var essais = (int)(DelaiPreuves / PasPreuves);
        var ligneVue = false;
        var lsnAtteint = false;
        for (var i = 0; i < essais; i++)
        {
            if (!ligneVue) ligneVue = await SansErreurAsync(() => baseLocale.RetraitVisibleAsync(ct), ct);
            if (!lsnAtteint) lsnAtteint = await SansErreurAsync(() => baseLocale.LsnRejoueAtteintAsync(lsn, ct), ct);
            if (ligneVue && lsnAtteint) return null;
            await attendre(PasPreuves, ct);
        }
        return !ligneVue ? "ligne_retrait_absente_de_la_copie" : "lsn_final_non_rejoue";
    }

    private static async Task<bool> SansErreurAsync(Func<Task<bool>> lecture, CancellationToken ct)
    {
        try { return await lecture(); }
        catch (Exception e) when (e is not OperationCanceledException) { return false; }
    }

    private async Task<ResultatPromotion> RefuserAsync(string raison, string message, bool alerter, CancellationToken ct)
    {
        if (derniereRaison != raison)
        {
            derniereRaison = raison;
            await journal.EcrireAsync("promotion_refusee", new { raison }, ct);
        }
        if (alerter) await AlerterAsync(TypesAlerteInterServeurs.PromotionManuelleNecessaire, message, ct);
        return new(IssuePromotion.NonPromue, raison);
    }

    private static string MessageVigie(ResultatDecision r) => r.TypeAlerte switch
    {
        ConstantesVigie.AlerteApiServeur1Arretee => "La base du serveur 1 répond mais son API est arrêtée depuis plus de 30 secondes : redémarrez l'API du serveur 1. Aucune promotion.",
        ConstantesVigie.AlerteEgaliteMajorite => $"Serveur 1 injoignable, mais {r.Votes} postes sur {r.Connus} seulement le perdent (égalité) : aucune promotion automatique.",
        _ => r.Raison
    };

    /// <summary>
    /// Alerte vers le serveur 1 (au plus une par heure et par type). Serveur 1 injoignable ou réponse autre que 202 : journal seulement. Jamais bloquant.
    /// </summary>
    private async Task AlerterAsync(string type, string message, CancellationToken ct)
    {
        if (!limite.Autoriser("envoyee|" + type)) return;
        var maintenant = horloge.MaintenantUtc;
        var texte = message.Length > RecevoirAlerteInterServeurs.MessageMax ? message[..RecevoirAlerteInterServeurs.MessageMax] : message;
        var envoyee = false;
        try { envoyee = await alertes.EnvoyerAsync(type, null, texte, maintenant, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { envoyee = false; }
        await journal.EcrireAsync(envoyee ? "alerte_transmise" : "alerte_non_transmise", new { type }, ct);
    }
}

/// <summary>Enregistrement d'une décision de bascule en base (table bascule_decision, script 166) ; ajout seul.</summary>
public interface IDecisionsBasculeDepot
{
    /// <summary>Ajoute la ligne ; faux si une décision de même instant et de même cas existe déjà (reprise après arrêt entre l'écriture et le renommage du fichier).</summary>
    Task<bool> AjouterAsync(DecisionBascule decision, int? timelineApres, CancellationToken ct = default);
}

/// <summary>
/// Au démarrage en PRINCIPAL : chaque fichier bascule-*.json non encore enregistré devient une ligne bascule_decision (timeline après = timeline courante),
/// une ligne du journal d'audit et une alerte au titulaire, puis le fichier est renommé. Idempotent : un fichier déjà enregistré en base n'alerte pas deux fois.
/// </summary>
public sealed class EnregistrerDecisionsBascule(IFichiersDecisionBascule fichiers, IDecisionsBasculeDepot depot, IReleveServeurs releves, IJournalSysteme audit,
    IAlertesTitulaire alertes, ITraceErreurs? trace = null)
{
    public const string ActionAudit = "bascule_promotion";

    public async Task<int> ExecuterAsync(CancellationToken ct = default)
    {
        var enregistres = 0;
        foreach (var f in await fichiers.NonEnregistresAsync(ct))
        {
            if (f.Decision is not { } d)
            {
                trace?.Erreur("Fichier de décision de bascule illisible (laissé en place)", new InvalidDataException("bascule_json"));
                continue;
            }
            var apres = (await releves.LocalAsync(ct))?.Timeline;
            var ajoutee = await depot.AjouterAsync(d, apres is { } t and >= 0 and <= int.MaxValue ? (int)t : null, ct);
            if (ajoutee)
            {
                await EcrireAuditAsync(d, apres, ct);
                await AlerteNonEnvoyeeSignalee.SansBlocageAsync(() => alertes.SecondServeurAsync(Objet(d), Detail(d), Sms(d), ct), "promotion du 2e serveur", trace);
                enregistres++;
            }
            await fichiers.MarquerEnregistreAsync(f.Chemin, ct);
        }
        return enregistres;
    }

    private async Task EcrireAuditAsync(DecisionBascule d, long? apres, CancellationToken ct)
    {
        try
        {
            await audit.EcrireAsync(ActionAudit, "bascule_decision", new Dictionary<string, object?>
            {
                ["cas"] = d.Cas,
                ["decidee_le"] = d.DecideLeUtc,
                ["timeline_avant"] = d.TimelineAvant,
                ["timeline_apres"] = apres,
                ["lsn_final"] = d.LsnFinal,
                ["derniere_donnee_recue"] = d.DerniereTransactionRecueUtc
            }, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            trace?.Erreur("Bascule : décision écrite en base mais pas au journal des actions", e);
        }
    }

    private static string Quand(DateTime? utc) => utc is { } d ? d.ToUniversalTime().ToString("dd/MM/yyyy HH:mm:ss") + " UTC" : "inconnue";

    public static string Objet(DecisionBascule d) => "Le 2e serveur est devenu le serveur principal";

    public static string Detail(DecisionBascule d)
    {
        var raison = d.Cas == CasBascule.Promotion
            ? "le serveur 1 ne répondait plus (base et API) et la majorité des postes le perdait"
            : "le serveur 1 a été mis hors jeu à la demande du 2e serveur (aucun poste ne le joignait)";
        var texte = $"Le 2e serveur s'est promu seul le {Quand(d.DecideLeUtc)} : {raison}. Dernière transaction reçue du serveur 1 : {Quand(d.DerniereTransactionRecueUtc)}.";
        if (d.Cas == CasBascule.Promotion)
            texte += " Ce qui a suivi sur le serveur 1 (s'il écrivait encore) n'est pas dans ce serveur : à reprendre. Le serveur 1 ne doit pas être remis en service avant vérification.";
        return texte;
    }

    public static string Sms(DecisionBascule d) =>
        "Le 2e serveur est devenu le serveur principal (" + (d.Cas == CasBascule.Promotion ? "serveur 1 injoignable" : "serveur 1 mis hors jeu") + "). Voir l'alerte pour la suite.";
}
