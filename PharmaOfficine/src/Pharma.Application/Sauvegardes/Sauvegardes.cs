using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Domain.Commun;

namespace Pharma.Application.Sauvegardes;

public sealed record ReglagesSauvegarde(bool Actif, string? Dossier, TimeOnly Heure, int Conservation, DateOnly? DerniereExecution);

/// <summary>Statut : reussie, echouee ou disque_absent. Déclenchement : automatique ou manuel.</summary>
public sealed record EntreeSauvegarde(
    long Id, DateTime Debut, DateTime? Fin, string Statut, string Declenchement, string? Fichier, long? TailleOctets, string? Message, string? Utilisateur);

public sealed record NouvelleEntreeSauvegarde(
    DateTime Debut, DateTime Fin, string Statut, string Declenchement, string? Fichier, long? TailleOctets, string? Message, long? UtilisateurId);

public interface ISauvegardesDepot
{
    Task<ReglagesSauvegarde> ReglagesAsync(CancellationToken ct = default);
    Task EnregistrerReglagesAsync(bool actif, string? dossier, TimeOnly heure, int conservation, CancellationToken ct = default);
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
}

public sealed record ResultatSauvegarde(string Statut, string? Fichier, long? TailleOctets, string Message);

/// <summary>
/// Sauvegarde de la base sur le disque externe, puis suppression des plus anciennes au-delà de la conservation.
/// Disque absent ou échec : inscrit au journal et signalé au titulaire (SMS et e-mail).
/// </summary>
public sealed class LancerSauvegarde(ISauvegardesDepot depot, IStockageSauvegarde stockage, IAlertesTitulaire alertes, IHorloge horloge)
{
    public async Task<ResultatSauvegarde> ExecuterAsync(bool automatique, long? utilisateurId, DateTime maintenantLocal, CancellationToken ct = default)
    {
        var r = await depot.ReglagesAsync(ct);
        if (string.IsNullOrWhiteSpace(r.Dossier))
            throw new RegleMetierException("Choisissez d’abord le dossier de sauvegarde (disque externe).");
        var declenchement = automatique ? "automatique" : "manuel";
        var debut = horloge.MaintenantUtc;

        if (!stockage.DisqueDisponible(r.Dossier))
        {
            var message = $"Le disque de sauvegarde ({Path.GetPathRoot(r.Dossier)}) est absent ou inaccessible.";
            await Terminer(new ResultatSauvegarde("disque_absent", null, null, message), debut, declenchement, utilisateurId, ct);
            await Signaler(true, message, ct);
            return new ResultatSauvegarde("disque_absent", null, null, message);
        }

        var fichier = Path.Combine(r.Dossier, $"pharmacie_{maintenantLocal:yyyyMMdd_HHmmss}.dump");
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
            resultat = new ResultatSauvegarde("reussie", fichier, taille,
                $"Sauvegarde de {taille / 1024.0 / 1024.0:0.0} Mo" + (supprimes > 0 ? $", {supprimes} ancienne(s) supprimée(s)." : "."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            resultat = new ResultatSauvegarde("echouee", null, null, ex.Message);
        }

        await Terminer(resultat, debut, declenchement, utilisateurId, ct);
        if (resultat.Statut == "echouee") await Signaler(false, resultat.Message, ct);
        return resultat;
    }

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

public sealed class ModifierReglagesSauvegarde(ISauvegardesDepot depot, IJournal journal)
{
    public async Task ExecuterAsync(bool actif, string? dossier, TimeOnly heure, int conservation, long utilisateurId, CancellationToken ct = default)
    {
        var d = string.IsNullOrWhiteSpace(dossier) ? null : dossier.Trim();
        if (d is not null && !Path.IsPathFullyQualified(d))
            throw new RegleMetierException("Indiquez un chemin complet, par exemple E:\\SauvegardesPharmacie.");
        if (actif && d is null) throw new RegleMetierException("Choisissez le dossier avant d’activer les sauvegardes.");
        if (conservation is < 1 or > 365) throw new RegleMetierException("Nombre de sauvegardes conservées entre 1 et 365.");
        await depot.EnregistrerReglagesAsync(actif, d, heure, conservation, ct);
        await journal.EcrireAsync(utilisateurId, "reglages_sauvegarde", "parametre_sauvegarde", 1,
            new Dictionary<string, object?> { ["actif"] = actif, ["dossier"] = d, ["heure"] = heure.ToString("HH:mm"), ["conservation"] = conservation },
            Domain.Securite.Permissions.AdminSauvegardes, ct);
    }
}
