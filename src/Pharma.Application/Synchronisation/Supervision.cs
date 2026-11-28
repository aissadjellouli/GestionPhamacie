using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;

namespace Pharma.Application.Synchronisation;

// ---- Synchronisation, lot S3 : supervision (bandeau, alertes au titulaire) et purge de la file ----
// docs/conception-synchronisation.md § 8 (supervision) et § 20 (purge, reprise). Rien de tout cela ne bloque une vente : tâches de
// fond et lectures seules ; une alerte qui échoue est signalée au titulaire (bandeau de l'accueil), jamais propagée.

/// <summary>
/// Réglages de supervision et de purge (sync_parametre, script 140). Valeurs de départ proposées (le titulaire les règle) :
/// bandeau à 1 h (§ 8), alerte de retard à 24 h (§ 8), erreur persistante à 1 h, purge après 30 jours (§ 3.4).
/// </summary>
public sealed record ReglagesSupervisionSync(int BandeauMinutes, int RetardHeures, int ErreurMinutes, bool PurgeActive, int PurgeJours)
{
    public const int BandeauMinutesDefaut = 60, RetardHeuresDefaut = 24, ErreurMinutesDefaut = 60, PurgeJoursDefaut = 30;
    public static readonly ReglagesSupervisionSync Defaut = new(BandeauMinutesDefaut, RetardHeuresDefaut, ErreurMinutesDefaut, true, PurgeJoursDefaut);

    /// <summary>Bornes (aussi contraintes en base) ; message métier au premier réglage hors bornes.</summary>
    public void Verifier()
    {
        if (BandeauMinutes is < 1 or > 10_080) throw new RegleMetierException("Seuil du bandeau : entre 1 minute et 7 jours (10 080 minutes).", "bandeau_minutes");
        if (RetardHeures is < 1 or > 720) throw new RegleMetierException("Seuil d’alerte de retard : entre 1 heure et 30 jours (720 heures).", "retard_heures");
        if (ErreurMinutes is < 1 or > 10_080) throw new RegleMetierException("Seuil d’alerte d’erreur persistante : entre 1 minute et 7 jours (10 080 minutes).", "erreur_minutes");
        if (PurgeJours is < 1 or > 365) throw new RegleMetierException("Conservation des lots accusés : entre 1 et 365 jours.", "purge_jours");
    }
}

/// <summary>Ce qui n'est pas encore accusé par le serveur central : plus ancienne opération et nombre d'opérations.</summary>
public sealed record AttenteSync(DateTime? Depuis, long Operations);

public static class NaturesAlerteSync
{
    public const string Retard = "retard";
    public const string Erreur = "erreur";
    public const string Certificat = "certificat";
    public const string Quota = "quota";
    /// <summary>Nouvelle anomalie d'intégrité constatée par le serveur central (lot S4) : une alerte par nouvelle anomalie, sans délai
    /// d'une heure ; la dernière anomalie signalée est notée à part (sync_parametre.integrite_anomalie_signalee, script 144).</summary>
    public const string Integrite = "integrite";
    /// <summary>Base de l'officine revenue en arrière (lot S6.2) : une alerte par détection, sans délai d'une heure ; envoi noté à part
    /// (sync_parametre.retour_arriere_alerte_le, script 149).</summary>
    public const string RetourArriere = "retour_arriere";
    /// <summary>Sujets répétés au plus une fois par heure tant que le problème dure (colonnes alerte_*_le, script 140).</summary>
    public static readonly IReadOnlyList<string> Toutes = [Retard, Erreur, Certificat, Quota];
}

/// <summary>État lu pour la supervision. <paramref name="DernieresAlertes"/> : dernière alerte envoyée par nature.</summary>
/// <param name="Integrite">Dernier résumé de la vérification du serveur central (lot S4) ; null tant qu'aucun n'est reçu.</param>
/// <param name="IntegriteSignalee">N° de la dernière anomalie du serveur central déjà signalée au titulaire.</param>
public sealed record EtatSupervisionSync(bool Active, bool BaseEnLectureSeule, AttenteSync Attente, DateTime? ErreurDepuisLe, string? ErreurNature,
    string? DerniereErreur, IReadOnlyDictionary<string, DateTime> DernieresAlertes, ReglagesSupervisionSync Reglages,
    IntegriteCentrale? Integrite = null, long IntegriteSignalee = 0, RetourArriereSync? RetourArriere = null);

public interface ISupervisionSyncDepot
{
    Task<EtatSupervisionSync> LireAsync(CancellationToken ct = default);
    /// <summary>Enregistre l'envoi d'une alerte (<see cref="NaturesAlerteSync"/>) : sert à n'en envoyer qu'une par heure.</summary>
    Task NoterAlerteAsync(string nature, DateTime quandUtc, CancellationToken ct = default);
    /// <summary>Note la dernière anomalie d'intégrité du serveur central signalée au titulaire (jamais en arrière, script 144).</summary>
    Task NoterIntegriteSignaleeAsync(long derniereAnomalieId, CancellationToken ct = default);
    Task EnregistrerReglagesAsync(ReglagesSupervisionSync reglages, CancellationToken ct = default);
    /// <summary>Lot S6.2 : alerte « base revenue en arrière » envoyée pour la détection en cours (script 149).</summary>
    Task NoterAlerteRetourArriereAsync(DateTime quandUtc, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>Mémoire de 30 secondes de l'état de supervision : chaque poste interroge le bandeau toutes les minutes.</summary>
public sealed class MemoireSupervisionSync(IHorloge horloge)
{
    public static readonly TimeSpan Duree = TimeSpan.FromSeconds(30);
    private readonly object verrou = new();
    private (DateTime Quand, EtatSupervisionSync Etat)? gardee;

    public async Task<EtatSupervisionSync> LireAsync(ISupervisionSyncDepot depot, CancellationToken ct)
    {
        lock (verrou)
            if (gardee is { } g && horloge.MaintenantUtc - g.Quand < Duree && horloge.MaintenantUtc >= g.Quand) return g.Etat;
        var etat = await depot.LireAsync(ct);
        lock (verrou) gardee = (horloge.MaintenantUtc, etat);
        return etat;
    }

    public void Oublier() { lock (verrou) gardee = null; }
}

/// <summary>Bandeau d'un poste : affiché quand <paramref name="Affiche"/>.</summary>
public sealed record BandeauSync(bool Affiche, DateTime? Depuis, long Operations)
{
    public static readonly BandeauSync Aucun = new(false, null, 0);
}

/// <summary>
/// Bandeau « Synchronisation en attente depuis … (n opérations) » (§ 8), au même emplacement que « Serveur injoignable ». Affiché au
/// titulaire et aux employés dont le rôle a le droit « sync.suivre » ; seulement si la synchronisation est activée et que la plus
/// ancienne opération non accusée dépasse le seuil réglé. Lecture seule, jamais bloquant : toute erreur de lecture = pas de bandeau.
/// </summary>
/// <param name="packs">Sans le pack G, jamais de bandeau : la synchronisation est arrêtée volontairement (décision du titulaire du 2026-11-27).</param>
public sealed class ConsulterAttenteSync(ISupervisionSyncDepot depot, MemoireSupervisionSync memoire, IPermissionService droits, IHorloge horloge,
    ITraceErreurs? trace = null, Offre.IPacksOffre? packs = null)
{
    public async Task<BandeauSync> ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        try
        {
            if (packs is not null && !await packs.InclusAsync(Offre.PacksOffre.MultiOfficines, ct)) return BandeauSync.Aucun;
            if (!await droits.EstTitulaireAsync(utilisateurId, ct) && !await droits.APermissionAsync(utilisateurId, Domain.Securite.Permissions.SynchronisationSuivre, ct))
                return BandeauSync.Aucun;
            return Decider(await memoire.LireAsync(depot, ct), horloge.MaintenantUtc);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            trace?.Erreur("Bandeau de synchronisation illisible", e);
            return BandeauSync.Aucun;
        }
    }

    /// <summary>Règle d'affichage, sans accès à la base.</summary>
    public static BandeauSync Decider(EtatSupervisionSync e, DateTime maintenantUtc) =>
        e is { Active: true, BaseEnLectureSeule: false, Attente: { Depuis: { } depuis, Operations: > 0 } a }
        && maintenantUtc - depuis >= TimeSpan.FromMinutes(e.Reglages.BandeauMinutes)
            ? new BandeauSync(true, depuis, a.Operations)
            : BandeauSync.Aucun;
}

/// <summary>
/// Alertes au titulaire (SMS ou e-mail du mécanisme existant), au plus une fois par heure et par nature tant que le problème dure :
/// retard au-delà du seuil, erreur d'envoi persistante, certificat proche de l'échéance, quota ou plafond refusé par le central.
/// Appelée chaque minute par la tâche de fond ; rien n'est fait si la synchronisation est désactivée ou si la base est en lecture seule.
/// Aucune donnée de patient dans les messages (nombres, dates et message technique du dernier envoi).
/// </summary>
/// <param name="packs">Sans le pack G (décision du titulaire du 2026-11-27) : la synchronisation est arrêtée volontairement, ni retard, ni erreur
/// d'envoi, ni certificat, ni quota ne sont signalés (sinon une alerte par heure sans fin) ; intégrité et retour en arrière le restent.</param>
public sealed class SurveillerSynchronisation(ISupervisionSyncDepot depot, ICertificatSync certificat, IAlertesTitulaire alertes, IHorloge horloge,
    ITraceErreurs? trace = null, Offre.IPacksOffre? packs = null)
{
    public static readonly TimeSpan Intervalle = TimeSpan.FromHours(1);
    /// <summary>Le certificat de l'officine est renouvelé automatiquement à 60 jours de l'échéance (EnvoyerLotsSync.RenouvelerAvant) :
    /// s'il reste moins de 30 jours, le renouvellement échoue depuis 30 jours.</summary>
    public static readonly TimeSpan DelaiCertificat = TimeSpan.FromDays(30);

    /// <returns>Natures des alertes envoyées pendant ce passage.</returns>
    public async Task<IReadOnlyList<string>> ExecuterAsync(CancellationToken ct = default)
    {
        var e = await depot.LireAsync(ct);
        if (!e.Active || e.BaseEnLectureSeule) return [];
        var maintenant = horloge.MaintenantUtc;
        var envoyees = new List<string>();
        var avecPackG = packs is null || await packs.InclusAsync(Offre.PacksOffre.MultiOfficines, ct);
        foreach (var (nature, objet, detail, sms) in Constater(e, LireCertificat(), maintenant))
        {
            // Sans le pack G : intégrité, retour en arrière et certificat (décision du 2026-11-28 : le certificat se renouvelle aussi sans G ;
            // un renouvellement qui échoue doit être signalé, sinon plus aucun code d'offre ne descend à son échéance)
            if (!avecPackG && nature is not (NaturesAlerteSync.Integrite or NaturesAlerteSync.RetourArriere or NaturesAlerteSync.Certificat)) continue;
            if (nature == NaturesAlerteSync.Integrite)
                // Une alerte par nouvelle anomalie du serveur central : la dernière signalée est notée AVANT l'envoi
                await depot.NoterIntegriteSignaleeAsync(e.Integrite!.DerniereAnomalieId, ct);
            else if (nature == NaturesAlerteSync.RetourArriere)
                // Une alerte par détection : notée AVANT l'envoi (une mise en file impossible est signalée, jamais répétée chaque minute)
                await depot.NoterAlerteRetourArriereAsync(maintenant, ct);
            else
            {
                if (e.DernieresAlertes.TryGetValue(nature, out var derniere) && maintenant >= derniere && maintenant - derniere < Intervalle) continue;
                // Le moment est noté AVANT l'envoi : si la mise en file échoue, elle est signalée au titulaire (alerte non envoyée)
                // et ne se répète pas chaque minute
                await depot.NoterAlerteAsync(nature, maintenant, ct);
            }
            await AlerteNonEnvoyeeSignalee.SansBlocageAsync(() => alertes.SynchronisationAsync(objet, detail, sms, ct), $"synchronisation ({nature})", trace);
            envoyees.Add(nature);
        }
        return envoyees;
    }

    private CertificatSyncInfo? LireCertificat()
    {
        try { return certificat.Lire(); }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.Security.Cryptography.CryptographicException)
        {
            trace?.Erreur("Certificat de synchronisation illisible pour la supervision", ex);
            return null;
        }
    }

    /// <summary>Alertes à envoyer maintenant, avant le contrôle du délai d'une heure (règle pure, testée).</summary>
    public static IReadOnlyList<(string Nature, string Objet, string Detail, string Sms)> Constater(EtatSupervisionSync e, CertificatSyncInfo? cert, DateTime maintenantUtc)
    {
        var liste = new List<(string, string, string, string)>();
        var r = e.Reglages;
        var erreur = e.DerniereErreur is { Length: > 0 } d ? $"\n\nDernier message : {Court(d)}" : "";

        if (e.Attente is { Depuis: { } depuis, Operations: > 0 } a && maintenantUtc - depuis >= TimeSpan.FromHours(r.RetardHeures))
        {
            var duree = Duree(maintenantUtc - depuis);
            liste.Add((NaturesAlerteSync.Retard, $"Synchronisation en retard de {duree}",
                $"{a.Operations} opération(s) attendent d’être envoyées au serveur central depuis {duree} (seuil réglé : {r.RetardHeures} h). "
                + "Le travail de l’officine n’est pas affecté ; vérifiez la connexion Internet et l’écran Administration › Synchronisation." + erreur,
                $"Synchronisation en retard de {duree} : {a.Operations} operation(s) en attente."));
        }
        if (e is { ErreurNature: NaturesErreurSync.Autre, ErreurDepuisLe: { } depuisErreur } && maintenantUtc - depuisErreur >= TimeSpan.FromMinutes(r.ErreurMinutes))
        {
            var duree = Duree(maintenantUtc - depuisErreur);
            liste.Add((NaturesAlerteSync.Erreur, "Synchronisation : erreur d’envoi persistante",
                $"L’envoi au serveur central échoue sans interruption depuis {duree}." + erreur
                + "\n\nÀ faire : voir Administration › Synchronisation (dernière erreur).",
                $"Synchronisation : erreur d'envoi depuis {duree}."));
        }
        if (cert is not null)
        {
            var reste = cert.EcheanceUtc - maintenantUtc;
            if (reste < DelaiCertificat)
            {
                var objet = reste <= TimeSpan.Zero ? "Synchronisation : certificat de l’officine échu" : "Synchronisation : certificat de l’officine proche de l’échéance";
                var quand = reste <= TimeSpan.Zero ? $"est échu depuis le {cert.EcheanceUtc.ToLocalTime():dd/MM/yyyy}"
                    : $"arrive à échéance le {cert.EcheanceUtc.ToLocalTime():dd/MM/yyyy} (dans {Math.Max(1, (int)Math.Ceiling(reste.TotalDays))} jour(s))";
                liste.Add((NaturesAlerteSync.Certificat, objet,
                    $"Le certificat qui identifie ce serveur auprès du serveur central {quand}. Son renouvellement automatique n’a pas abouti. "
                    + "Sans certificat valable, plus rien n’est envoyé.\n\nÀ faire : Administration › Synchronisation › Activation (nouveau code d’activation de l’éditeur).",
                    reste <= TimeSpan.Zero ? $"Synchronisation : certificat de l'officine echu depuis le {cert.EcheanceUtc.ToLocalTime():dd/MM/yyyy}."
                        : $"Synchronisation : certificat de l'officine expire le {cert.EcheanceUtc.ToLocalTime():dd/MM/yyyy}."));
            }
        }
        if (e.ErreurNature == NaturesErreurSync.Quota)
            liste.Add((NaturesAlerteSync.Quota, "Synchronisation : quota refusé par le serveur central",
                "Le serveur central refuse les envois (quota ou plafond atteint). L’envoi reprend automatiquement quand il l’autorise ; les opérations restent en attente, rien n’est perdu." + erreur,
                "Synchronisation : quota ou plafond refuse par le serveur central."));
        if (e.Integrite is { DerniereAnomalieId: > 0 } integ && integ.DerniereAnomalieId > e.IntegriteSignalee)
        {
            // Les 20 plus récentes sont transmises : au-delà, le nombre vient du n° (les n° du central se suivent par officine,
            // mais peuvent sauter ; « au moins » n'est pas affiché pour rester simple)
            var nouvelles = integ.Anomalies.Where(a => a.Id > e.IntegriteSignalee).ToList();
            var natures = string.Join(", ", nouvelles.Select(a => IntegriteCentrale.LibelleNature(a.Nature)).Distinct().Take(3));
            var nombre = Math.Max(1, nouvelles.Count);
            liste.Add((NaturesAlerteSync.Integrite, "Synchronisation : anomalie d’intégrité constatée par le serveur central",
                $"Le serveur central a constaté {nombre} nouvelle(s) anomalie(s) dans les journaux reçus de l’officine"
                + (natures.Length > 0 ? $" : {natures}." : ".")
                + " Rien n’a été corrigé, ni ici ni au serveur central : les données reçues sont gardées telles quelles comme preuve."
                + "\n\nÀ faire : ne rien corriger ; voir Administration › Synchronisation › Intégrité au central, puis prévenir l’éditeur.",
                $"Synchronisation : {nombre} anomalie(s) d'integrite constatee(s) par le serveur central."));
        }
        if (e.RetourArriere is { } ra && (ra.AlerteLeUtc is null || ra.AlerteLeUtc < ra.LeUtc))
            liste.Add((NaturesAlerteSync.RetourArriere, "Synchronisation suspendue : base de l’officine revenue en arrière",
                $"Le serveur central a reçu jusqu’au lot {ra.DernierLotCentral} de l’époque {ra.Epoque}, alors que cette base n’en a formé que {ra.DernierLotLocal} : "
                + "la base a été restaurée depuis une sauvegarde, ou le 2e serveur a pris le relais avec un retard de réplication. "
                + "L’envoi au serveur central est suspendu ; le travail de l’officine n’est pas affecté. "
                + (ra.SequencesAvancees ? "Les n° des nouvelles opérations ont été avancés au-delà de ceux déjà reçus par le serveur central."
                    : "Les n° des nouvelles opérations n’ont pas encore pu être avancés au-delà de ceux du serveur central (nouvel essai à chaque passage).")
                + " Le serveur central garde les données qu’il a reçues et que la base n’a plus. "
                + EnvoyerLotsSync.PhraseIntegrite.Replace('\'', '’')
                + "\n\nÀ faire (titulaire) : Administration › Synchronisation › État, « Repartir sur une nouvelle époque ».",
                "Synchronisation suspendue : base revenue en arriere (restauration ou bascule). Titulaire : repartir sur une nouvelle epoque. "
                + "Anomalies d'integrite attendues au central pour les rangs restaures : pas une falsification."));
        return liste;
    }

    private static string Court(string message) => message.Length <= 300 ? message : message[..300] + "…";

    public static string Duree(TimeSpan d) =>
        d.TotalDays >= 2 ? $"{(int)d.TotalDays} jours" : d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes:00}" : $"{Math.Max(1, (int)d.TotalMinutes)} min";
}

/// <summary>Réglages de supervision et de purge (écran Admin › Synchronisation, droit admin.parametres), journalisés.</summary>
public sealed class ReglerSupervisionSync(ISupervisionSyncDepot depot, MemoireSupervisionSync memoire, IJournal journal)
{
    public async Task ExecuterAsync(ReglagesSupervisionSync r, long utilisateurId, CancellationToken ct = default)
    {
        r.Verifier();
        var avant = (await depot.LireAsync(ct)).Reglages;
        await depot.EnregistrerReglagesAsync(r, ct);
        memoire.Oublier();
        await journal.EcrireAsync(utilisateurId, "reglages_supervision_synchronisation", "sync_parametre", 1, new Dictionary<string, object?>
        {
            ["bandeau_minutes"] = $"{avant.BandeauMinutes} -> {r.BandeauMinutes}",
            ["retard_heures"] = $"{avant.RetardHeures} -> {r.RetardHeures}",
            ["erreur_minutes"] = $"{avant.ErreurMinutes} -> {r.ErreurMinutes}",
            ["purge_active"] = $"{avant.PurgeActive} -> {r.PurgeActive}",
            ["purge_jours"] = $"{avant.PurgeJours} -> {r.PurgeJours}"
        }, null, ct);
    }
}

// ---- Purge de la file d'envoi (sync_sortie) --------------------------------------------------------------------------------

/// <summary>État de la purge (écran) : réglage, dernière exécution, cumul, taille de la file.</summary>
/// <param name="DernierLotPurge">Les lots de l'époque <paramref name="EpoquePurge"/> jusqu'à ce n° ont perdu leurs lignes (0 : aucun).</param>
/// <param name="LignesGardees">Estimation du nombre de lignes de la file (statistiques PostgreSQL ; null : inconnue).</param>
public sealed record EtatPurgeSync(bool Active, int DelaiJours, int? EpoquePurge, long DernierLotPurge, DateTime? DernierePurgeLe, long DernieresLignes,
    long TotalLignes, string? DerniereErreur, long? LignesGardees, long OctetsFile, bool BaseEnLectureSeule);

/// <summary>Résultat d'une purge ; <paramref name="DernierLot"/> : n° du dernier lot purgé par cet appel (0 : rien).</summary>
public sealed record ResultatPurgeSync(int Lots, long Lignes, long Octets, long DernierLot)
{
    public static readonly ResultatPurgeSync Rien = new(0, 0, 0, 0);
    public static ResultatPurgeSync operator +(ResultatPurgeSync a, ResultatPurgeSync b) =>
        new(a.Lots + b.Lots, a.Lignes + b.Lignes, a.Octets + b.Octets, Math.Max(a.DernierLot, b.DernierLot));
}

public interface IPurgeSyncDepot
{
    Task<EtatPurgeSync> EtatAsync(CancellationToken ct = default);
    /// <summary>sync_purger : retire les lignes de lots ACCUSÉS depuis plus de <paramref name="delaiJours"/> jours (au plus
    /// <paramref name="maxLots"/> lots, dans l'ordre), jamais une ligne d'un lot non accusé ni d'un chargement en cours.</summary>
    Task<ResultatPurgeSync> PurgerAsync(int delaiJours, int maxLots, CancellationToken ct = default);
    /// <summary>Erreur de la dernière purge (null : efface).</summary>
    Task NoterErreurAsync(string? erreur, CancellationToken ct = default);
}

/// <summary>
/// Purge de <c>sync_sortie</c> après accusé du serveur central (§ 12.5, § 13.6, § 20). La file garde chaque lot accusé pendant le délai de
/// conservation réglé (30 jours par défaut) : c'est ce qui permet de renvoyer à l'identique un lot que le serveur central aurait
/// perdu (restauration de sa base). Au-delà, les lignes sont retirées (la ligne du lot reste dans <c>sync_lot</c> : numéros, empreinte et
/// colonnes retirées figées) ; un serveur central qui a besoin d'un lot purgé reçoit alors un chargement complet (état actuel de la base,
/// seule référence), demandé automatiquement. Prend le verrou d'envoi (jamais en même temps qu'un passage d'envoi) ; passe son tour s'il est pris.
/// </summary>
public sealed class PurgerSortieSync(IPurgeSyncDepot depot, VerrouSync verrou, ITraceErreurs? trace = null)
{
    public const int LotsParLot = 20;
    public const int LotsMaxParPassage = 40;

    /// <returns>null : purge non faite (désactivée, base en lecture seule, passage d'envoi en cours) ; sinon le cumul du passage.</returns>
    public async Task<ResultatPurgeSync?> ExecuterAsync(int lotsMaxParPassage = LotsMaxParPassage, CancellationToken ct = default)
    {
        var etat = await depot.EtatAsync(ct);
        if (!etat.Active || etat.BaseEnLectureSeule) return null;
        if (!verrou.Prendre()) return null;
        try
        {
            var total = ResultatPurgeSync.Rien;
            for (var lots = 0; lots < lotsMaxParPassage; lots += LotsParLot)
            {
                ct.ThrowIfCancellationRequested();
                var r = await depot.PurgerAsync(etat.DelaiJours, Math.Min(LotsParLot, lotsMaxParPassage - lots), ct);
                total += r;
                if (r.Lots < LotsParLot) break;
            }
            await depot.NoterErreurAsync(null, ct);
            return total;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            trace?.Erreur("Purge de la file de synchronisation impossible", e);
            try { await depot.NoterErreurAsync($"Purge impossible ({e.GetType().Name}) : voir le journal du serveur.", CancellationToken.None); }
            catch (Exception e2) when (e2 is not OperationCanceledException) { trace?.Erreur("Erreur de purge non mémorisée", e2); }
            throw;
        }
        finally { verrou.Rendre(); }
    }
}
