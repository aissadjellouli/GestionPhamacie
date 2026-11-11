using Microsoft.Extensions.Logging;
using Pharma.Application.Chifa;

namespace Pharma.Infrastructure.Chifa;

/// <summary>
/// Lit les factures signées déposées par Chifa_Officine dans son dossier d'export.
/// Le format exact des fichiers n'est pas public : à compléter avec un exemple anonymisé
/// fourni par la pharmacie pilote ou avec les spécifications de la CNAS.
/// Ce connecteur ne lit jamais la base interne de la CNAS. Chaque fichier lu est archivé dans la GED (GED\Chifa), sans rien écraser.
/// </summary>
public sealed class ChifaFichiersConnecteur(string dossierExport, ILogger<ChifaFichiersConnecteur> logger, string? dossierArchive = null) : IChifaConnecteur
{
    public Task<IReadOnlyList<FactureChifa>> LireFacturesSigneesAsync(DateOnly depuis, CancellationToken ct = default)
    {
        if (!Directory.Exists(dossierExport))
        {
            logger.LogWarning("Dossier Chifa introuvable : {Dossier}", dossierExport);
            return Task.FromResult<IReadOnlyList<FactureChifa>>([]);
        }

        var fichiers = Directory.EnumerateFiles(dossierExport, "*", SearchOption.AllDirectories)
            .Where(f => File.GetLastWriteTime(f) >= depuis.ToDateTime(TimeOnly.MinValue))
            .ToList();
        logger.LogInformation("{Nombre} fichiers Chifa trouvés depuis le {Date}", fichiers.Count, depuis);
        Archiver(fichiers);

        // TODO : analyser le format des factures signées une fois connu.
        throw new NotImplementedException(
            "Format des factures signées Chifa_Officine à documenter (voir docs/chifa.md).");
    }

    /// <summary>Copie des fichiers lus dans la GED (même arborescence) ; un fichier déjà archivé, identique en taille, n'est pas recopié.</summary>
    private void Archiver(IEnumerable<string> fichiers)
    {
        if (string.IsNullOrWhiteSpace(dossierArchive) || Path.GetFullPath(dossierArchive).TrimEnd('\\') == Path.GetFullPath(dossierExport).TrimEnd('\\')) return;
        foreach (var f in fichiers)
            try
            {
                var cible = Path.Combine(dossierArchive, Path.GetRelativePath(dossierExport, f));
                if (File.Exists(cible) && new FileInfo(cible).Length == new FileInfo(f).Length) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(cible)!);
                File.Copy(f, cible, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Fichier Chifa non archivé dans la GED : {Fichier} ({Erreur})", f, e.Message);
            }
    }
}
