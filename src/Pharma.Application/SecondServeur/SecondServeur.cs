using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;

namespace Pharma.Application.SecondServeur;

// ---- 2e serveur dans l'officine (2026-11-16, script 122) ---------------------------------------------------------------
// Réplication PostgreSQL en continu (streaming physique) du serveur principal vers le 2e serveur ; API de secours en veille
// (service en démarrage manuel) ; bascule décidée par une personne (scripts/second-serveur/basculer-second-serveur.ps1).

public static class RolesServeur
{
    public const string Principal = "principal";
    public const string Second = "second";
}

/// <summary>
/// Un serveur de l'officine (table serveur_officine, répliquée : lisible des deux côtés). Réglages propres à chaque serveur :
/// dossier d'échange CHIFA-Officine et dossier du logiciel MDN. Cases cochées par le titulaire. Version, clé, imprimantes et
/// dossiers joignables : relevés par l'API du serveur principal à son démarrage, ou par scripts/second-serveur/verifier-second-serveur.ps1.
/// </summary>
public sealed record ServeurOfficine(
    string Nom, string Role, string? DossierChifa, string? DossierMdn,
    bool ChifaInstalle, bool LecteurToken, bool MdnInstalle, bool DouchetteTestee,
    string? VersionApi, bool? ClePresente, string? EmpreinteCle, string? Imprimantes,
    bool? DossierChifaJoignable, bool? DossierMdnJoignable, DateTime? ReleveLe);

/// <summary>
/// Réplication vue du serveur principal (pg_stat_replication, pg_replication_slots). DetailsVisibles : faux si le compte de l'API n'a
/// pas le rôle pg_monitor (retards illisibles ; scripts/second-serveur/preparer-principal.ps1 le donne).
/// </summary>
public sealed record EtatReplication(bool EnRecovery, bool Connecte, string? Etat, string? Client, long? RetardOctets, double? RetardSecondes,
    bool SlotExiste, bool SlotActif, bool DetailsVisibles);

public sealed record ReglagesReplication(int SeuilSecondes = ReglagesReplication.SecondesParDefaut, long SeuilOctets = ReglagesReplication.OctetsParDefaut,
    DateTime? DerniereAlerteUtc = null)
{
    public const int SecondesParDefaut = 300;
    public const long OctetsParDefaut = 64L * 1024 * 1024;
}

public sealed record ModificationServeur(string? DossierChifa, string? DossierMdn, bool ChifaInstalle, bool LecteurToken, bool MdnInstalle, bool DouchetteTestee);

public interface ISecondServeurDepot
{
    Task<IReadOnlyList<ServeurOfficine>> ServeursAsync(CancellationToken ct = default);
    Task<EtatReplication> ReplicationAsync(string slot, CancellationToken ct = default);
    Task<ReglagesReplication> ReglagesAsync(CancellationToken ct = default);
    Task EnregistrerSeuilsAsync(int seuilSecondes, long seuilOctets, CancellationToken ct = default);
    Task NoterAlerteAsync(DateTime? alerteUtc, CancellationToken ct = default);
    /// <summary>Faux si le serveur n'existe pas.</summary>
    Task<bool> ModifierServeurAsync(string nom, ModificationServeur m, long utilisateurId, CancellationToken ct = default);
    /// <summary>Au démarrage de l'API en écriture : ce serveur devient « principal », les autres « second » ; relevé de version, clé, imprimantes.</summary>
    Task DeclarerPrincipalAsync(ServeurOfficine releve, CancellationToken ct = default);
}

public static class ReplicationOfficine
{
    /// <summary>Nom du slot de réplication créé par preparer-principal.ps1 (le principal garde les WAL tant que le 2e serveur ne les a pas reçus).</summary>
    public const string Slot = "second_serveur";

    /// <summary>Réplication à surveiller : un 2e serveur est déclaré, ou le slot existe.</summary>
    public static bool Configuree(IReadOnlyList<ServeurOfficine> serveurs, EtatReplication e) =>
        e.SlotExiste || serveurs.Any(s => s.Role == RolesServeur.Second);

    /// <summary>Problème à signaler au titulaire, ou null : 2e serveur décroché, ou retard au-delà d'un seuil.</summary>
    public static string? Probleme(EtatReplication e, ReglagesReplication r)
    {
        if (e.EnRecovery) return null;   // ce serveur est lui-même le 2e serveur : rien à surveiller ici
        if (!e.Connecte)
            return "Réplication vers le 2e serveur décrochée : le 2e serveur ne reçoit plus les modifications (PC éteint, réseau, PostgreSQL arrêté ?)."
                + (e.SlotExiste ? " Le principal garde les modifications en attente (slot) : surveillez la place sur son disque." : "");
        if (e.RetardSecondes is { } s && s > r.SeuilSecondes)
            return $"Réplication vers le 2e serveur en retard : {Math.Round(s)} s (seuil {r.SeuilSecondes} s).";
        if (e.RetardOctets is { } o && o > r.SeuilOctets)
            return $"Réplication vers le 2e serveur en retard : {o / 1024 / 1024} Mo à recevoir (seuil {r.SeuilOctets / 1024 / 1024} Mo).";
        return null;
    }

    /// <summary>
    /// Démarrage de l'API : jamais deux API qui écrivent. Refus (message clair) si la base de ce serveur est en lecture seule
    /// (2e serveur pas encore basculé) ou si l'autre serveur déclaré (SecondServeur:AutreServeur) a lui aussi une base principale.
    /// Deux bases principales : celle qui a la plus haute timeline (promue le plus récemment, par la bascule) garde la main ; l'ancienne
    /// (revenue après une panne, sans avoir été remise en 2e serveur) refuse. Timelines égales ou inconnues : les deux refusent.
    /// Inconnu (base ou autre serveur injoignable) : pas de refus ici.
    /// </summary>
    public static string? RefusDemarrage(bool? baseEnRecovery, bool? autreServeurPrincipal, string? autreServeur, long? timeline = null, long? timelineAutre = null)
    {
        if (autreServeurPrincipal == true && baseEnRecovery == false && timeline is { } moi && timelineAutre is { } autre && moi > autre)
            autreServeurPrincipal = false;   // ce serveur est le plus récemment promu : c'est l'autre qui doit s'arrêter
        if (baseEnRecovery == true)
            return "API non démarrée : la base de ce serveur est une copie en lecture seule (2e serveur en veille, réplication en cours). "
                + "Pour en faire le serveur principal, lancez scripts\\second-serveur\\basculer-second-serveur.ps1 sur ce serveur "
                + "(procédure « 2e serveur » de docs/mise-en-production.md).";
        if (autreServeurPrincipal == true)
            return $"API non démarrée : l'autre serveur ({autreServeur}) a lui aussi une base principale en service. Deux serveurs qui "
                + "écrivent en même temps rendraient les données incohérentes. Arrêtez l'API et PostgreSQL de l'un des deux, puis remettez-le "
                + "en 2e serveur (scripts\\second-serveur\\remettre-en-second.ps1).";
        return null;
    }
}

public sealed record TableauSecondServeur(string? CeServeur, EtatReplication Replication, ReglagesReplication Reglages,
    IReadOnlyList<ServeurOfficine> Serveurs, string? Probleme, string? VersionApi, string? EmpreinteCle);

/// <summary>Écran « 2e serveur » (titulaire).</summary>
public sealed class ObtenirSecondServeur(ISecondServeurDepot depot)
{
    public async Task<TableauSecondServeur> ExecuterAsync(string ceServeur, string? versionApi, string? empreinteCle, CancellationToken ct = default)
    {
        var serveurs = await depot.ServeursAsync(ct);
        var e = await depot.ReplicationAsync(ReplicationOfficine.Slot, ct);
        var r = await depot.ReglagesAsync(ct);
        var probleme = ReplicationOfficine.Configuree(serveurs, e) ? ReplicationOfficine.Probleme(e, r) : null;
        return new TableauSecondServeur(ceServeur, e, r, serveurs, probleme, versionApi, empreinteCle);
    }
}

/// <summary>
/// Vérifiée chaque minute par l'API : réplication décrochée ou en retard au-delà du seuil, le titulaire est prévenu
/// (au plus une alerte par heure tant que le problème dure ; l'alerte est remise à zéro quand tout redevient normal).
/// </summary>
public sealed class SurveillerReplication(ISecondServeurDepot depot, IAlertesTitulaire alertes, IHorloge horloge, ITraceErreurs? trace = null)
{
    public static readonly TimeSpan IntervalleAlertes = TimeSpan.FromHours(1);

    /// <summary>Vrai si une alerte a été envoyée.</summary>
    public async Task<bool> ExecuterAsync(CancellationToken ct = default)
    {
        var serveurs = await depot.ServeursAsync(ct);
        var e = await depot.ReplicationAsync(ReplicationOfficine.Slot, ct);
        if (!ReplicationOfficine.Configuree(serveurs, e)) return false;
        var r = await depot.ReglagesAsync(ct);
        var probleme = ReplicationOfficine.Probleme(e, r);
        if (probleme is null)
        {
            if (r.DerniereAlerteUtc is not null && !e.EnRecovery) await depot.NoterAlerteAsync(null, ct);
            return false;
        }
        var maintenant = horloge.MaintenantUtc;
        if (r.DerniereAlerteUtc is { } a && maintenant - a < IntervalleAlertes) return false;
        await depot.NoterAlerteAsync(maintenant, ct);
        await AlerteNonEnvoyeeSignalee.SansBlocageAsync(() => alertes.SauvegardeEnEchecAsync(false, probleme, ct), "réplication du 2e serveur", trace);
        return true;
    }
}

/// <summary>Réglages propres à un serveur et cases du titulaire (écran « 2e serveur »).</summary>
public sealed class ModifierServeurOfficine(ISecondServeurDepot depot, IJournal journal)
{
    public const string Action = "reglages_serveur_officine";

    public async Task ExecuterAsync(string nom, ModificationServeur m, long utilisateurId, CancellationToken ct = default)
    {
        var propre = m with { DossierChifa = Dossier(m.DossierChifa, "dossier_chifa"), DossierMdn = Dossier(m.DossierMdn, "dossier_mdn") };
        if (!await depot.ModifierServeurAsync((nom ?? "").Trim().ToUpperInvariant(), propre, utilisateurId, ct))
            throw new RegleMetierException("Serveur inconnu : il est déclaré au démarrage de son API ou par le script initialiser-second-serveur.ps1.");
        await journal.EcrireAsync(utilisateurId, Action, "serveur_officine", null, new Dictionary<string, object?>
        {
            ["serveur"] = nom, ["dossier_chifa"] = propre.DossierChifa, ["dossier_mdn"] = propre.DossierMdn,
            ["chifa_installe"] = m.ChifaInstalle, ["lecteur_token"] = m.LecteurToken, ["mdn_installe"] = m.MdnInstalle, ["douchette_testee"] = m.DouchetteTestee
        }, PermissionServiceExtensions.MarqueTitulaire, ct);
    }

    /// <summary>Chemin complet (lecteur ou partage réseau) ou rien (le réglage Chifa:DossierExport du serveur s'applique).</summary>
    public static string? Dossier(string? dossier, string champ)
    {
        var d = string.IsNullOrWhiteSpace(dossier) ? null : dossier.Trim();
        if (d is not null && !Path.IsPathFullyQualified(d))
            throw new RegleMetierException("Indiquez un chemin complet, par exemple D:\\CHIFA_OFFICINE\\CNAS_SIGNED.", champ);
        return d;
    }
}

/// <summary>Seuils d'alerte de la réplication (titulaire).</summary>
public sealed class ModifierSeuilsReplication(ISecondServeurDepot depot, IJournal journal)
{
    public async Task ExecuterAsync(int seuilSecondes, int seuilMo, long utilisateurId, CancellationToken ct = default)
    {
        if (seuilSecondes is < 10 or > 86_400) throw new RegleMetierException("Retard toléré : entre 10 secondes et 24 heures.", "seuil_secondes");
        if (seuilMo is < 1 or > 100_000) throw new RegleMetierException("Retard toléré : entre 1 et 100 000 Mo.", "seuil_mo");
        await depot.EnregistrerSeuilsAsync(seuilSecondes, seuilMo * 1024L * 1024, ct);
        await journal.EcrireAsync(utilisateurId, "seuils_replication", null, null,
            new Dictionary<string, object?> { ["seuil_secondes"] = seuilSecondes, ["seuil_mo"] = seuilMo }, PermissionServiceExtensions.MarqueTitulaire, ct);
    }
}

/// <summary>Ce serveur, tel que l'API l'a lu à son démarrage : nom du PC, dossiers retenus (Chifa, MDN), version de l'API.</summary>
public sealed record CeServeur(string Nom, string? DossierChifa, string? DossierMdn, string? VersionApi);

/// <summary>Dossiers propres à ce serveur : la ligne de serveur_officine à son nom l'emporte sur appsettings (Chifa:DossierExport, Mdn:Dossier).</summary>
public static class DossiersServeur
{
    public static string? Choisir(string? reglageServeur, string? configuration) =>
        !string.IsNullOrWhiteSpace(reglageServeur) ? reglageServeur.Trim() : string.IsNullOrWhiteSpace(configuration) ? null : configuration.Trim();
}
