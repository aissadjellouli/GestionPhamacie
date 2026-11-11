using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Domain.Commun;

namespace Pharma.Application.Sauvegardes;

/// <summary>
/// RetentionGedJours : jours de conservation de la copie des sauvegardes dans la GED (null : réglage du serveur, 7 jours).
/// NettoyageGedAnnees : documents de la GED supprimés au-delà de ce nombre d'années (null : jamais, la GED est gardée).
/// </summary>
public sealed record ReglagesSauvegarde(bool Actif, string? Dossier, TimeOnly Heure, int Conservation, DateOnly? DerniereExecution,
    int? RetentionGedJours = null, int? NettoyageGedAnnees = null);

/// <summary>Statut : reussie, echouee ou disque_absent. Déclenchement : automatique ou manuel.</summary>
public sealed record EntreeSauvegarde(
    long Id, DateTime Debut, DateTime? Fin, string Statut, string Declenchement, string? Fichier, long? TailleOctets, string? Message, string? Utilisateur);

public sealed record NouvelleEntreeSauvegarde(
    DateTime Debut, DateTime Fin, string Statut, string Declenchement, string? Fichier, long? TailleOctets, string? Message, long? UtilisateurId);

public interface ISauvegardesDepot
{
    Task<ReglagesSauvegarde> ReglagesAsync(CancellationToken ct = default);
    Task EnregistrerReglagesAsync(bool actif, string? dossier, TimeOnly heure, int conservation, int? retentionGedJours, int? nettoyageGedAnnees,
        CancellationToken ct = default);
    Task NoterExecutionAsync(DateOnly jour, CancellationToken ct = default);
    Task<long> JournaliserAsync(NouvelleEntreeSauvegarde entree, CancellationToken ct = default);
    Task<IReadOnlyList<EntreeSauvegarde>> JournalAsync(int limite, CancellationToken ct = default);
}

/// <summary>Disque de destination et export de la base (pg_dump).</summary>
public interface IStockageSauvegarde
{
    /// <summary>Faux si le lecteur du dossier (disque externe) n'est pas branché ou pas accessible.</summary>
    bool DisqueDisponible(string dossier);
    /// <summary>Exporte la base dans le fichier ; renvoie sa taille en octets.</summary>
    Task<long> ExporterBaseAsync(string fichier, CancellationToken ct = default);
    /// <summary>Fichiers de sauvegarde du dossier, du plus récent au plus ancien.</summary>
    IReadOnlyList<string> Sauvegardes(string dossier);
    void Supprimer(string fichier);
    /// <summary>Copie un fichier de sauvegarde (le dossier de destination est créé).</summary>
    void Copier(string source, string destination);
    /// <summary>Supprime les sauvegardes du dossier plus anciennes que la date ; renvoie leur nombre.</summary>
    int SupprimerAvant(string dossier, DateTime limite);
    /// <summary>
    /// Copie les documents du dossier (sous-dossiers compris, sauf « exclure ») vers la destination : seulement les fichiers
    /// nouveaux ou modifiés ; rien n'est supprimé à la destination. Renvoie le nombre de fichiers copiés.
    /// </summary>
    int CopierDocuments(string source, string destination, string exclure);
    /// <summary>Supprime les documents du dossier (sauf « exclure ») modifiés avant la date ; renvoie leur nombre.</summary>
    int SupprimerDocumentsAvant(string dossier, DateTime limite, string exclure);
    /// <summary>
    /// Restauration de la GED : recopie les documents de la source (disque externe) qui manquent à la destination ou y sont plus anciens ;
    /// rien n'est supprimé, un document plus récent à la destination est gardé. Renvoie le nombre de documents recopiés.
    /// </summary>
    int RestaurerDocuments(string source, string destination);
}

public sealed record ResultatSauvegarde(string Statut, string? Fichier, long? TailleOctets, string Message);

/// <summary>
/// Sauvegarde quotidienne de la base et de la GED sur le disque externe : la base (pg_dump), puis suppression des plus anciennes
/// au-delà de la conservation ; les documents de la GED (ordonnances, factures…) copiés dans le sous-dossier GED du disque
/// (nouveaux et modifiés seulement). Une copie de la base est gardée dans la GED du serveur (GED\Sauvegardes) le nombre de jours
/// réglé ; disque externe absent, elle est faite quand même. Nettoyage de la GED : seulement si le titulaire a réglé une durée
/// en années. Disque absent ou échec : inscrit au journal et signalé au titulaire (SMS et e-mail).
/// </summary>
public sealed class LancerSauvegarde(ISauvegardesDepot depot, IStockageSauvegarde stockage, IAlertesTitulaire alertes, IHorloge horloge,
    Ged.ReglagesGed? ged = null)
{
    public async Task<ResultatSauvegarde> ExecuterAsync(bool automatique, long? utilisateurId, DateTime maintenantLocal, CancellationToken ct = default)
    {
        var r = await depot.ReglagesAsync(ct);
        if (string.IsNullOrWhiteSpace(r.Dossier))
            throw new RegleMetierException("Choisissez d’abord le dossier de sauvegarde (disque externe).", "dossier");
        var declenchement = automatique ? "automatique" : "manuel";
        var debut = horloge.MaintenantUtc;

        var nom = $"pharmacie_{maintenantLocal:yyyyMMdd_HHmmss}.dump";
        if (!stockage.DisqueDisponible(r.Dossier))
        {
            var message = $"Le disque de sauvegarde ({Path.GetPathRoot(r.Dossier)}) est absent ou inaccessible.";
            // Copie de secours dans la GED du serveur, en attendant le disque externe
            if (ged is not null)
                try
                {
                    var secours = Path.Combine(ged.Dossier(Ged.TypesGed.Sauvegardes), nom);
                    await stockage.ExporterBaseAsync(secours, ct);
                    PurgerGed(r, maintenantLocal);
                    message += $" Copie de secours gardée dans la GED : {secours}.";
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { message += $" Copie dans la GED impossible : {ex.Message}"; }
            await Terminer(new ResultatSauvegarde("disque_absent", null, null, message), debut, declenchement, utilisateurId, ct);
            await Signaler(true, message, ct);
            return new ResultatSauvegarde("disque_absent", null, null, message);
        }

        var fichier = Path.Combine(r.Dossier, nom);
        ResultatSauvegarde resultat;
        try
        {
            var taille = await stockage.ExporterBaseAsync(fichier, ct);
            var supprimes = 0;
            foreach (var ancien in stockage.Sauvegardes(r.Dossier).Skip(r.Conservation))
            {
                stockage.Supprimer(ancien);
                supprimes++;
            }
            var copie = "";
            if (ged is not null)
            {
                try
                {
                    stockage.Copier(fichier, Path.Combine(ged.Dossier(Ged.TypesGed.Sauvegardes), nom));
                    copie = $" Copie dans la GED, gardée {RetentionGed(r)} jour(s).";
                    PurgerGed(r, maintenantLocal);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { copie = $" Copie dans la GED impossible : {ex.Message}"; }
                // Documents de la GED sur le disque externe, avec la base
                try
                {
                    var documents = stockage.CopierDocuments(ged.Racine, Path.Combine(r.Dossier, "GED"), Ged.TypesGed.Sauvegardes);
                    copie += $" GED : {documents} document(s) nouveau(x) ou modifié(s) copié(s) sur le disque.";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { copie += $" Copie des documents de la GED impossible : {ex.Message}"; }
                copie += Nettoyer(r, maintenantLocal);
            }
            resultat = new ResultatSauvegarde("reussie", fichier, taille,
                $"Sauvegarde de {taille / 1024.0 / 1024.0:0.0} Mo" + (supprimes > 0 ? $", {supprimes} ancienne(s) supprimée(s)." : ".") + copie);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            resultat = new ResultatSauvegarde("echouee", null, null, ex.Message);
        }

        await Terminer(resultat, debut, declenchement, utilisateurId, ct);
        if (resultat.Statut == "echouee") await Signaler(false, resultat.Message, ct);
        return resultat;
    }

    /// <summary>Jours de conservation de la copie dans la GED : réglage du titulaire, sinon celui du serveur.</summary>
    private int RetentionGed(ReglagesSauvegarde r) => r.RetentionGedJours ?? ged?.RetentionSauvegardesJours ?? 7;

    /// <summary>Documents de la GED plus anciens que la durée réglée (en années) : supprimés ; sans réglage, rien n'est supprimé.</summary>
    private string Nettoyer(ReglagesSauvegarde r, DateTime maintenantLocal)
    {
        if (r.NettoyageGedAnnees is not { } annees) return "";
        try
        {
            var n = stockage.SupprimerDocumentsAvant(ged!.Racine, maintenantLocal.AddYears(-annees), Ged.TypesGed.Sauvegardes);
            return n > 0 ? $" Nettoyage de la GED : {n} document(s) de plus de {annees} an(s) supprimé(s)." : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $" Nettoyage de la GED impossible : {ex.Message}"; }
    }

    private void PurgerGed(ReglagesSauvegarde r, DateTime maintenantLocal) =>
        stockage.SupprimerAvant(ged!.Dossier(Ged.TypesGed.Sauvegardes), maintenantLocal.AddDays(-RetentionGed(r)));

    private Task Terminer(ResultatSauvegarde r, DateTime debut, string declenchement, long? utilisateurId, CancellationToken ct) =>
        depot.JournaliserAsync(new NouvelleEntreeSauvegarde(debut, horloge.MaintenantUtc, r.Statut, declenchement, r.Fichier, r.TailleOctets, r.Message, utilisateurId), ct);

    // La sauvegarde est faite ou journalisée : un échec de mise en file ne doit pas la masquer
    private async Task Signaler(bool disqueAbsent, string detail, CancellationToken ct)
    {
        try { await alertes.SauvegardeEnEchecAsync(disqueAbsent, detail, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }
}

/// <summary>Sauvegarde automatique quotidienne, une fois l'heure réglée passée.</summary>
public sealed class SauvegardeAutomatique(ISauvegardesDepot depot, LancerSauvegarde lancer)
{
    public async Task<ResultatSauvegarde?> ExecuterAsync(DateTime maintenantLocal, CancellationToken ct = default)
    {
        var r = await depot.ReglagesAsync(ct);
        var jour = DateOnly.FromDateTime(maintenantLocal);
        if (!r.Actif || string.IsNullOrWhiteSpace(r.Dossier) || TimeOnly.FromDateTime(maintenantLocal) < r.Heure || r.DerniereExecution >= jour)
            return null;
        await depot.NoterExecutionAsync(jour, ct);   // avant l'export : pas de boucle si l'export plante l'API
        return await lancer.ExecuterAsync(true, null, maintenantLocal, ct);
    }
}

public sealed class ObtenirSauvegardes(ISauvegardesDepot depot)
{
    public async Task<(ReglagesSauvegarde Reglages, IReadOnlyList<EntreeSauvegarde> Journal)> ExecuterAsync(CancellationToken ct = default) =>
        (await depot.ReglagesAsync(ct), await depot.JournalAsync(30, ct));
}

public sealed class ModifierReglagesSauvegarde(ISauvegardesDepot depot, IJournal journal, SuperAdmin.ICompteSuperAdmin? superAdmin = null)
{
    public async Task ExecuterAsync(bool actif, string? dossier, TimeOnly heure, int conservation, long utilisateurId, CancellationToken ct = default,
        int? retentionGedJours = null, int? nettoyageGedAnnees = null)
    {
        var d = string.IsNullOrWhiteSpace(dossier) ? null : dossier.Trim();
        if (d is not null && !Path.IsPathFullyQualified(d))
            throw new RegleMetierException("Indiquez un chemin complet, par exemple E:\\SauvegardesPharmacie.", "dossier");
        if (actif && d is null) throw new RegleMetierException("Choisissez le dossier avant d’activer les sauvegardes.", "dossier");
        if (conservation is < 1 or > 365) throw new RegleMetierException("Nombre de sauvegardes conservées entre 1 et 365.", "conservation");
        if (retentionGedJours is < 1 or > 365) throw new RegleMetierException("Copie dans la GED : entre 1 et 365 jours (vide : réglage du serveur).", "retention_ged");
        if (nettoyageGedAnnees is < 1 or > 100) throw new RegleMetierException("Nettoyage de la GED : entre 1 et 100 ans (vide : jamais).", "nettoyage_ged");
        await depot.EnregistrerReglagesAsync(actif, d, heure, conservation, retentionGedJours, nettoyageGedAnnees, ct);
        // Copie avec le compte du super admin : la restauration retrouve le disque externe même si la base est détruite
        try
        {
            if (d is not null && superAdmin?.Lire() is { } compte && compte.DossierSauvegardes != d)
                superAdmin.Enregistrer(compte with { DossierSauvegardes = d });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException) { }
        await journal.EcrireAsync(utilisateurId, "reglages_sauvegarde", "parametre_sauvegarde", 1,
            new Dictionary<string, object?> { ["actif"] = actif, ["dossier"] = d, ["heure"] = heure.ToString("HH:mm"), ["conservation"] = conservation,
                ["retention_ged_jours"] = retentionGedJours, ["nettoyage_ged_annees"] = nettoyageGedAnnees },
            Domain.Securite.Permissions.AdminSauvegardes, ct);
    }
}
