using System.Collections.Concurrent;
using Pharma.Application.Commun;
using Pharma.Application.Notifications;

namespace Pharma.Application.SecondServeur;

// ---- Vague 2, lot V2-1b : présence des postes et auto-mise à l'écart du serveur 1 isolé (règle de majorité Q32) -----------------
// docs/taches/2026-11-23-bascule-2e-serveur/contrat-api.md § 3 et plan-vague-2.md §§ 16 et 18.

/// <summary>Écriture, dans la table presence_poste (script 165), de l'instant où un poste a été vu (UPSERT). Implémentation : Infrastructure.</summary>
public interface IPresenceDepot
{
    Task NoterAsync(string poste, CancellationToken ct = default);
}

/// <summary>
/// Postes vus par CE serveur (en mémoire) : nom d'après l'en-tête X-Pharma-Poste. Sert de numérateur à la règle de majorité (vus depuis moins de
/// <c>PosteVuSecondes</c>) et décide quand écrire en base (au plus une fois par minute et par poste). L'en-tête n'est pas authentifié (contrat § 3.7) :
/// nom limité à 100 caractères, sans caractère de contrôle, et au plus <see cref="PostesMax"/> postes distincts (mémoire et table bornées).
/// </summary>
public sealed class RegistrePostesVus
{
    public const int NomMaxCaracteres = 100;
    public const int PostesMax = 200;
    public static readonly TimeSpan IntervalleEcriture = TimeSpan.FromMinutes(1);

    private sealed class Entree { public DateTime VuLeUtc; public DateTime? EcritLeUtc; }

    private readonly ConcurrentDictionary<string, Entree> postes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object verrou = new();

    /// <summary>Nom utilisable (rogné, 1 à 100 caractères, sans caractère de contrôle), sinon null.</summary>
    public static string? Normaliser(string? brut)
    {
        var nom = brut?.Trim();
        return string.IsNullOrEmpty(nom) || nom.Length > NomMaxCaracteres || nom.Any(char.IsControl) ? null : nom;
    }

    /// <summary>Note le poste vu à <paramref name="maintenantUtc"/>. Vrai si une écriture en base est due (jamais écrit, ou écrit il y a une minute ou plus).</summary>
    public bool Noter(string poste, DateTime maintenantUtc)
    {
        lock (verrou)
        {
            if (!postes.TryGetValue(poste, out var e))
            {
                if (postes.Count >= PostesMax) Purger(maintenantUtc);
                if (postes.Count >= PostesMax) return false;
                e = postes[poste] = new Entree();
            }
            e.VuLeUtc = maintenantUtc;
            if (e.EcritLeUtc is { } dernier && maintenantUtc - dernier < IntervalleEcriture) return false;
            e.EcritLeUtc = maintenantUtc;
            return true;
        }
    }

    /// <summary>Postes vus depuis moins de <paramref name="fenetre"/> (tous, connus ou non : l'appelant fait l'intersection avec les postes connus).</summary>
    public IReadOnlySet<string> Vus(DateTime maintenantUtc, TimeSpan fenetre)
    {
        lock (verrou)
            return postes.Where(kv => maintenantUtc - kv.Value.VuLeUtc < fenetre && kv.Value.VuLeUtc <= maintenantUtc)
                .Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // Oublie les postes vus il y a plus de 10 minutes (mémoire bornée)
    private void Purger(DateTime maintenantUtc)
    {
        foreach (var kv in postes.Where(kv => maintenantUtc - kv.Value.VuLeUtc > TimeSpan.FromMinutes(10)).ToList()) postes.TryRemove(kv.Key, out _);
    }
}

/// <summary>
/// Présence des postes, côté serveur 1 (principal) : appelée à CHAQUE requête portant X-Pharma-Poste (healthcheck compris). Mémoire toujours ; base au
/// plus une fois par minute et par poste, en tâche détachée : jamais bloquant, erreur journalisée sans faire échouer la requête, sans nom de poste.
/// Inactive sur un serveur en veille (aucune écriture) et sans 2e serveur déclaré. Serveur écarté (lecture seule) : mémoire seulement (la levée
/// automatique de l'isolement a besoin de voir revenir les postes), aucune écriture en base.
/// </summary>
public sealed class PresencePostes(RegistrePostesVus registre, IPresenceDepot depot, IServeurEcarteDetecte ecart, IVeilleServeur veille, IReleveServeurs releves,
    IHorloge horloge, ITraceErreurs? trace = null)
{
    public static readonly TimeSpan DelaiEcriture = TimeSpan.FromSeconds(5);
    private Task derniere = Task.CompletedTask;

    /// <summary>Dernière écriture lancée (pour les tests).</summary>
    public Task Derniere => derniere;

    public void Noter(string? enTetePoste)
    {
        if (veille.EstVeille || !releves.AutreServeurRegle) return;
        if (RegistrePostesVus.Normaliser(enTetePoste) is not { } poste) return;
        if (!registre.Noter(poste, horloge.MaintenantUtc)) return;
        if (ecart.Ecart is not null) return;   // lecture seule : rien ne s'écrit en base
        derniere = Task.Run(() => EcrireAsync(poste));
    }

    private async Task EcrireAsync(string poste)
    {
        try
        {
            using var delai = new CancellationTokenSource(DelaiEcriture);
            await depot.NoterAsync(poste, delai.Token);
        }
        catch (Exception e)
        {
            trace?.Erreur("Présence des postes : écriture en base impossible (script 165 passé ?), nouvel essai à la prochaine requête du poste après une minute", e);
        }
    }
}

// ---- Auto-mise à l'écart du serveur 1 isolé (Q32) ------------------------------------------------------------------------------

/// <summary>La copie répliquée est-elle connectée ? (pg_stat_replication non vide). Null = illisible : aucune décision.</summary>
public interface ICopieConnectee
{
    Task<bool?> ConnecteeAsync(CancellationToken ct = default);
}

/// <summary>État du 2e serveur lu par appel signé (GET /api/inter-serveurs/etat). Joignable = faux : aucune réponse valable (réseau, délai, signature).</summary>
public sealed record EtatAutreServeurLu(bool Joignable, string? Role = null, bool? EnRecovery = null);

public interface ILecteurEtatAutreServeur
{
    Task<EtatAutreServeurLu> LireAsync(CancellationToken ct = default);
}

public enum IssueIsolement
{
    /// <summary>Rien à surveiller : un seul serveur, serveur en veille, 2 minutes après le démarrage, autre écart en cours, ou relevé illisible.</summary>
    Inactif,
    /// <summary>Le serveur 1 garde la main (copie connectée, majorité vue, égalité, ou aucun poste connu).</summary>
    Normal,
    /// <summary>Copie perdue et minorité depuis moins de IsolementSecondes : le décompte court.</summary>
    EnAttente,
    /// <summary>Ce passage a mis le serveur en lecture seule (motif isole).</summary>
    Isole,
    /// <summary>Déjà isolé, conditions de levée non réunies.</summary>
    IsoleMaintenu,
    /// <summary>Ce passage a levé l'isolement.</summary>
    Leve,
    /// <summary>Le 2e serveur s'est promu : plus de levée automatique, la détection de timeline (lot 1) prend la suite.</summary>
    Definitif
}

/// <summary>
/// Règle de majorité côté serveur 1 (contrat § 3.2 et § 3.6), évaluée toutes les 5 s par la tâche. Mise à l'écart : copie NON connectée ET postes vus
/// (mémoire, moins de <c>PosteVuSecondes</c>, parmi les postes connus de la base) strictement moins de la moitié des postes connus, sans interruption pendant
/// <c>IsolementSecondes</c>. Égalité : le serveur 1 garde la main. Levée automatique : copie revenue OU vus &gt;= connus/2, seulement si le 2e serveur ne s'est
/// pas promu (états signé : role différent de principal et base en recovery ; injoignable : seulement si la copie est revenue).
/// Les « connus » sont FIGÉS à la mise à l'écart : une fois les écritures coupées, la table de présence ne bouge plus et la fenêtre de 3 minutes
/// ferait fondre le dénominateur à zéro (levée à tort).
/// </summary>
public sealed class SurveillerIsolement(IReleveServeurs releves, ICopieConnectee copie, IPostesConnusDepot postes, RegistrePostesVus vus,
    ILecteurEtatAutreServeur autre, SuiviServeurEcarte suivi, IVeilleServeur veille, IHorloge horloge, ReglagesBascule reglages,
    ITraceErreurs? trace = null)
{
    public static readonly TimeSpan Intervalle = TimeSpan.FromSeconds(5);
    /// <summary>Contrat § 3.2 : règle non appliquée pendant 2 minutes après le démarrage (les postes n'ont pas encore tous été revus).</summary>
    public static readonly TimeSpan GraceDemarrage = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan IntervalleAlertes = TimeSpan.FromHours(1);
    public const string Principal = "principal";

    private readonly DateTime demarrage = horloge.MaintenantUtc;
    private DateTime? debut;
    private IReadOnlySet<string>? connusFiges;
    private bool definitif;

    public IssueIsolement Derniere { get; private set; } = IssueIsolement.Inactif;

    /// <summary>Un passage (toutes les <see cref="Intervalle"/>). <paramref name="alertes"/> : service d'alertes de la portée du passage (il est à portée limitée, ce service est un singleton).</summary>
    public async Task<IssueIsolement> ExecuterAsync(IAlertesTitulaire alertes, CancellationToken ct = default) => Derniere = await EvaluerAsync(alertes, ct);

    private async Task<IssueIsolement> EvaluerAsync(IAlertesTitulaire alertes, CancellationToken ct)
    {
        if (veille.EstVeille || !releves.AutreServeurRegle) return IssueIsolement.Inactif;
        var maintenant = horloge.MaintenantUtc;
        var ecart = suivi.Ecart;
        if (ecart is { Isole: false }) return IssueIsolement.Inactif;   // écart de bascule ou indécis : la détection de timeline (lot 1) s'en charge
        if (ecart is null && maintenant - demarrage < GraceDemarrage) return IssueIsolement.Inactif;

        bool? connectee;
        try { connectee = await copie.ConnecteeAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException) { trace?.Erreur("Isolement : état de la copie illisible", e); return IssueIsolement.Inactif; }
        if (connectee is null) return IssueIsolement.Inactif;   // jamais de décision sur une lecture manquante

        return ecart is null ? await SurveillerAsync(connectee.Value, maintenant, alertes, ct) : await SurveillerIsoleAsync(connectee.Value, maintenant, ct);
    }

    private async Task<IssueIsolement> SurveillerAsync(bool connectee, DateTime maintenant, IAlertesTitulaire alertes, CancellationToken ct)
    {
        definitif = false;
        if (connectee) { debut = null; return IssueIsolement.Normal; }
        PostesConnusLus lus;
        try { lus = await postes.LireAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException) { trace?.Erreur("Isolement : postes connus illisibles", e); return IssueIsolement.Inactif; }
        var connus = PostesConnus.Calculer(lus.Postes, lus.DerniereDonneeRecueUtc, TimeSpan.FromMinutes(reglages.PosteActifMinutes));
        if (connus is null || connus.Count == 0 || Majorite(connus, maintenant)) { debut = null; return IssueIsolement.Normal; }

        debut ??= maintenant;
        if (maintenant - debut < TimeSpan.FromSeconds(reglages.IsolementSecondes)) return IssueIsolement.EnAttente;
        connusFiges = connus;
        var vusAu = VusParmi(connus, maintenant);
        if (!suivi.Ecarter(new EcartDetecte(maintenant, null, 0, 0, Isole: true))) return IssueIsolement.Inactif;
        debut = null;
        await AlerterAsync(alertes, connus.Count, vusAu, ct);
        return IssueIsolement.Isole;
    }

    private async Task<IssueIsolement> SurveillerIsoleAsync(bool connectee, DateTime maintenant, CancellationToken ct)
    {
        if (definitif) return IssueIsolement.Definitif;
        var figes = connusFiges ?? new HashSet<string>();
        // Sans connus figés (état restauré autrement) : rien ne prouve la majorité, seule la copie revenue peut lever
        var majorite = figes.Count > 0 && Majorite(figes, maintenant);
        if (!connectee && !majorite) return IssueIsolement.IsoleMaintenu;

        var etat = await autre.LireAsync(ct);
        // Réponse valable : le 2e serveur doit être une copie (role différent de principal ET base en recovery) ; sinon il s'est promu (ou a écrit) : écart définitif
        if (etat.Joignable && !(!string.Equals(etat.Role, Principal, StringComparison.OrdinalIgnoreCase) && etat.EnRecovery == true))
        {
            definitif = true;
            return IssueIsolement.Definitif;
        }
        // 2e serveur injoignable : on ne le sait pas promu ; seule la copie revenue (il suit de nouveau ce serveur) le prouve
        if (!etat.Joignable && !connectee) return IssueIsolement.IsoleMaintenu;
        if (!suivi.LeverIsolement()) return IssueIsolement.Inactif;
        connusFiges = null;
        debut = null;
        return IssueIsolement.Leve;
    }

    private bool Majorite(IReadOnlySet<string> connus, DateTime maintenant) => 2 * VusParmi(connus, maintenant) >= connus.Count;

    private int VusParmi(IReadOnlySet<string> connus, DateTime maintenant) =>
        vus.Vus(maintenant, TimeSpan.FromSeconds(reglages.PosteVuSecondes)).Count(connus.Contains);

    private async Task AlerterAsync(IAlertesTitulaire alertes, int connus, int vusNb, CancellationToken ct)
    {
        var maintenant = horloge.MaintenantUtc;
        if (suivi.DerniereAlerteUtc(AlerteSecondServeur.Isole) is { } a && maintenant - a < IntervalleAlertes) return;
        suivi.NoterAlerte(AlerteSecondServeur.Isole, maintenant);
        await AlerteNonEnvoyeeSignalee.SansBlocageAsync(() => alertes.SecondServeurAsync(
            "Serveur principal isolé : mis en lecture seule",
            $"Ce serveur a perdu le 2e serveur et ne voit plus que {vusNb} poste(s) sur {connus} : il s'est mis en lecture seule pour ne pas écrire à l'écart du reste de la pharmacie. "
            + "Les postes qu'il ne voit plus peuvent se rattacher au 2e serveur. Il reprend seul la main dès que le 2e serveur ou la majorité des postes reviennent (sauf si le 2e serveur est devenu principal). "
            + "À faire : vérifiez le réseau de la pharmacie et ce serveur.",
            "Serveur principal isole du reste de la pharmacie : lecture seule. Verifiez le reseau.", ct), "serveur isolé (2e serveur)", trace);
    }
}
