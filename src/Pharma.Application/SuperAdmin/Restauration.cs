using Microsoft.Extensions.DependencyInjection;
using Pharma.Application.Commun;
using Pharma.Application.Ged;
using Pharma.Application.Sauvegardes;
using Pharma.Domain.Commun;

namespace Pharma.Application.SuperAdmin;

// ---- Modèles ----------------------------------------------------------------

/// <summary>Sauvegarde de la base trouvée sur le disque externe ou dans la GED du serveur.</summary>
public sealed record SauvegardeDisponible(string Fichier, string Emplacement, DateTime Date, long Taille);

/// <summary>Ce que le super admin peut restaurer : sauvegardes de la base et copie de la GED sur le disque externe.</summary>
public sealed record ChoixRestauration(string NomBase, bool BaseExiste, string? DossierExterne, bool DisqueBranche, string? SourceGed,
    IReadOnlyList<SauvegardeDisponible> Sauvegardes);

/// <summary>Restauration en cours ou terminée (une à la fois) : étapes, résultat ou erreur.</summary>
public sealed record EtatRestauration(bool EnCours, string? Type, DateTime? Debut, DateTime? Fin, IReadOnlyList<string> Etapes, string? Resultat, string? Erreur);

// ---- Port ---------------------------------------------------------------------

/// <summary>Restauration PostgreSQL : pg_restore dans une base neuve, scripts rejoués, échange avec la base en service.</summary>
public interface IRestaurationBase
{
    string NomBase { get; }
    Task<bool> BaseExisteAsync(CancellationToken ct = default);
    /// <summary>Fichier lisible et complet (pg_restore --list) ; sinon RegleMetierException.</summary>
    Task VerifierAsync(string fichier, CancellationToken ct = default);
    /// <summary>
    /// Restaure dans une base neuve, la met à la version du logiciel (scripts 0xx rejoués), puis prend sa place : la base en service
    /// est renommée « …_avant_restauration_AAAAMMJJ_HHMM », jamais supprimée. Renvoie ce nom (null : il n'y avait plus de base).
    /// </summary>
    Task<string?> RestaurerAsync(string fichier, Action<string> etape, CancellationToken ct = default);
}

// ---- Cas d'usage -----------------------------------------------------------------

/// <summary>
/// Sauvegardes restaurables : dossier du disque externe (réglé en base ; base inaccessible : copie gardée avec le compte du super
/// admin) et copie de la GED du serveur. Les copies de sécurité faites avant une restauration y figurent aussi.
/// </summary>
public sealed class ListerRestaurations(IRestaurationBase restauration, IStockageSauvegarde stockage, ReglagesGed ged, ICompteSuperAdmin comptes,
    SuperAdminCourant courant, ISauvegardesDepot? depot = null)
{
    public async Task<ChoixRestauration> ExecuterAsync(CancellationToken ct = default)
    {
        courant.Exiger("restaurer la base ou la GED");
        var dossier = await DossierExterneAsync(comptes, depot, ct);
        var branche = dossier is not null && stockage.DisqueDisponible(dossier);
        var liste = new List<SauvegardeDisponible>();
        if (branche) liste.AddRange(Lister(dossier!, "disque externe"));
        liste.AddRange(Lister(ged.Dossier(TypesGed.Sauvegardes), "GED du serveur"));
        var sourceGed = branche && Directory.Exists(Path.Combine(dossier!, "GED")) ? Path.Combine(dossier!, "GED") : null;
        return new ChoixRestauration(restauration.NomBase, await restauration.BaseExisteAsync(ct), dossier, branche, sourceGed,
            liste.OrderByDescending(s => s.Date).ToList());
    }

    private IEnumerable<SauvegardeDisponible> Lister(string dossier, string emplacement) =>
        stockage.Sauvegardes(dossier).Select(f => new FileInfo(f)).Where(f => f.Exists)
            .Select(f => new SauvegardeDisponible(f.FullName, emplacement, f.LastWriteTimeUtc, f.Length));

    /// <summary>Dossier des sauvegardes réglé en base (recopié avec le compte), ou la copie si la base ne répond pas.</summary>
    internal static async Task<string?> DossierExterneAsync(ICompteSuperAdmin comptes, ISauvegardesDepot? depot, CancellationToken ct)
    {
        var compte = comptes.Lire();
        if (depot is null) return compte?.DossierSauvegardes;
        try
        {
            var dossier = (await depot.ReglagesAsync(ct)).Dossier;
            if (compte is not null && dossier is not null && dossier != compte.DossierSauvegardes)
                try { comptes.Enregistrer(compte with { DossierSauvegardes = dossier }); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return dossier ?? compte?.DossierSauvegardes;
        }
        catch (Exception e) when (e is not OperationCanceledException and not RegleMetierException)
        {
            return compte?.DossierSauvegardes;
        }
    }
}

/// <summary>
/// Restaurations lancées par le super admin, une à la fois, en tâche de fond (une base se restaure en plusieurs minutes) :
/// le poste suit les étapes. Pendant la restauration de la base, le serveur refuse les autres demandes (maintenance).
/// </summary>
public sealed class RestaurationsSuperAdmin(IServiceScopeFactory portees, IHorloge? horloge = null)
{
    private readonly object verrou = new();
    private EtatRestauration etat = new(false, null, null, null, [], null, null);
    private readonly List<string> etapes = [];

    private DateTime Maintenant => horloge?.MaintenantUtc ?? DateTime.UtcNow;

    /// <summary>Base en cours de restauration : les autres demandes sont refusées.</summary>
    public bool BaseEnCours { get { lock (verrou) return etat is { EnCours: true, Type: "base" }; } }

    public EtatRestauration Etat { get { lock (verrou) return etat with { Etapes = etapes.ToList() }; } }

    private void Etape(string texte)
    {
        lock (verrou) etapes.Add($"{DateTime.Now:HH:mm:ss} {texte}");
    }

    /// <summary>Base : la sauvegarde choisie (parmi celles proposées) remplace la base en service, qui est gardée sous un autre nom.</summary>
    public async Task<EtatRestauration> RestaurerBaseAsync(string? fichier, SuperAdminCourant courant, CancellationToken ct = default)
    {
        var validation = courant.ExigerValidation(ActionsSuperAdmin.RestaurerBase, "restaurer la base de données");
        using (var p = portees.CreateScope())
        {
            var s = p.ServiceProvider;
            var autorise = s.GetRequiredService<SuperAdminCourant>();
            (autorise.Identifiant, autorise.Validation) = (courant.Identifiant, validation);
            var choix = await s.GetRequiredService<ListerRestaurations>().ExecuterAsync(ct);
            if (choix.Sauvegardes.FirstOrDefault(x => string.Equals(x.Fichier, fichier, StringComparison.OrdinalIgnoreCase)) is not { } sauvegarde)
                throw new RegleMetierException("Sauvegarde introuvable : choisissez-la dans la liste.");
            Demarrer("base", () => BaseAsync(sauvegarde, courant.Trace()));
        }
        return Etat;
    }

    /// <summary>GED : les documents du disque externe sont recopiés dans la GED du serveur, sans rien supprimer ni remplacer un document plus récent.</summary>
    public async Task<EtatRestauration> RestaurerGedAsync(SuperAdminCourant courant, CancellationToken ct = default)
    {
        courant.ExigerValidation(ActionsSuperAdmin.RestaurerGed, "restaurer les documents de la GED");
        string source;
        using (var p = portees.CreateScope())
        {
            var s = p.ServiceProvider;
            var dossier = await ListerRestaurations.DossierExterneAsync(s.GetRequiredService<ICompteSuperAdmin>(), s.GetService<ISauvegardesDepot>(), ct);
            source = dossier is not null && Directory.Exists(Path.Combine(dossier, "GED")) ? Path.Combine(dossier, "GED")
                : throw new RegleMetierException("Copie de la GED introuvable : branchez le disque externe des sauvegardes (dossier GED).");
        }
        Demarrer("ged", () => GedAsync(source, courant.Trace()));
        return Etat;
    }

    private void Demarrer(string type, Func<Task<string>> travail)
    {
        lock (verrou)
        {
            if (etat.EnCours) throw new RegleMetierException("Une restauration est déjà en cours : attendez qu’elle se termine.");
            etapes.Clear();
            etat = new EtatRestauration(true, type, Maintenant, null, [], null, null);
        }
        _ = Task.Run(async () =>
        {
            string? resultat = null, erreur = null;
            try { resultat = await travail(); }
            catch (Exception e) { erreur = e.Message; Etape($"Échec : {e.Message}"); }
            lock (verrou) etat = etat with { EnCours = false, Fin = Maintenant, Resultat = resultat, Erreur = erreur };
        });
    }

    private async Task<string> BaseAsync(SauvegardeDisponible sauvegarde, Dictionary<string, object?> trace)
    {
        using var p = portees.CreateScope();
        var s = p.ServiceProvider;
        var restauration = s.GetRequiredService<IRestaurationBase>();
        Etape($"Sauvegarde du {sauvegarde.Date.ToLocalTime():dd/MM/yyyy HH:mm} ({sauvegarde.Emplacement}) : vérification du fichier");
        await restauration.VerifierAsync(sauvegarde.Fichier);

        string? copie = null;
        if (await restauration.BaseExisteAsync())
        {
            Etape("Copie de sécurité de la base actuelle");
            try
            {
                var ged = s.GetRequiredService<ReglagesGed>();
                copie = Path.Combine(ged.Dossier(TypesGed.Sauvegardes), $"pharmacie_avant_restauration_{DateTime.Now:yyyyMMdd_HHmmss}.dump");
                Directory.CreateDirectory(Path.GetDirectoryName(copie)!);
                await s.GetRequiredService<IStockageSauvegarde>().ExporterBaseAsync(copie);
                Etape($"Copie de sécurité : {copie}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // La base actuelle reste de toute façon gardée sous un autre nom
                Etape($"Copie de sécurité impossible ({e.Message}) : la base actuelle sera tout de même gardée sous un autre nom");
                copie = null;
            }
        }
        else Etape("Aucune base en service : la sauvegarde la remplace");

        var ancienne = await restauration.RestaurerAsync(sauvegarde.Fichier, Etape);

        // Journal de la base restaurée : attribué au titulaire, avec le super admin et sa validation
        try
        {
            using var apres = portees.CreateScope();
            var titulaire = await apres.ServiceProvider.GetRequiredService<ITitulaireLecture>().IdAsync();
            trace["fichier"] = sauvegarde.Fichier;
            trace["date_sauvegarde"] = sauvegarde.Date;
            trace["ancienne_base"] = ancienne;
            trace["copie_securite"] = copie;
            if (titulaire is { } t) await apres.ServiceProvider.GetRequiredService<IJournal>().EcrireAsync(t, "restauration_base", null, null, trace, null);
            else Etape("Journal non écrit : aucun pharmacien titulaire actif dans la base restaurée");
        }
        catch (Exception e) { Etape($"Journal non écrit ({e.Message})"); }

        var resultat = $"Base restaurée depuis la sauvegarde du {sauvegarde.Date.ToLocalTime():dd/MM/yyyy à HH:mm}."
                       + (ancienne is null ? "" : $" L’ancienne base est gardée sous le nom {ancienne}.")
                       + " Les opérations faites après cette sauvegarde sont à ressaisir ; les employés se reconnectent.";
        Etape("Terminé");
        return resultat;
    }

    private async Task<string> GedAsync(string source, Dictionary<string, object?> trace)
    {
        using var p = portees.CreateScope();
        var s = p.ServiceProvider;
        var ged = s.GetRequiredService<ReglagesGed>();
        Etape($"Copie de {source} vers {ged.Racine}");
        var copies = await Task.Run(() => s.GetRequiredService<IStockageSauvegarde>().RestaurerDocuments(source, ged.Racine));
        Etape($"{copies} document(s) recopié(s)");
        try
        {
            var titulaire = await s.GetRequiredService<ITitulaireLecture>().IdAsync();
            trace["source"] = source;
            trace["documents"] = copies;
            if (titulaire is { } t) await s.GetRequiredService<IJournal>().EcrireAsync(t, "restauration_ged", null, null, trace, null);
            else Etape("Journal non écrit : aucun pharmacien titulaire actif");
        }
        catch (Exception e) { Etape($"Journal non écrit ({e.Message})"); }
        Etape("Terminé");
        return $"GED restaurée : {copies} document(s) recopié(s) depuis {source}. Rien n’a été supprimé, et les documents plus récents du serveur ont été gardés.";
    }
}
