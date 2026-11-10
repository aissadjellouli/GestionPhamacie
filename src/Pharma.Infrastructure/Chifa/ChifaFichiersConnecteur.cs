using Microsoft.Extensions.Logging;
using Pharma.Application.Chifa;

namespace Pharma.Infrastructure.Chifa;

/// <summary>
/// Lit les factures signées déposées par Chifa_Officine dans son dossier d'export.
/// Le format exact des fichiers n'est pas public : à compléter avec un exemple anonymisé
/// fourni par la pharmacie pilote ou avec les spécifications de la CNAS.
/// Ce connecteur ne lit jamais la base interne de la CNAS.
/// </summary>
public sealed class ChifaFichiersConnecteur(string dossierExport, ILogger<ChifaFichiersConnecteur> logger) : IChifaConnecteur
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

        // TODO : analyser le format des factures signées une fois connu.
        throw new NotImplementedException(
            "Format des factures signées Chifa_Officine à documenter (voir docs/chifa.md).");
    }
}
