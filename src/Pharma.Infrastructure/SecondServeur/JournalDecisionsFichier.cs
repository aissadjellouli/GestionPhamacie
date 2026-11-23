using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pharma.Application.SecondServeur;

namespace Pharma.Infrastructure.SecondServeur;

/// <summary>
/// Journal fichier des décisions de la vigie : une ligne JSON par événement dans « vigie-AAAAMMJJ.jsonl » du dossier des journaux
/// (le serveur en veille n'écrit rien en base). Noms de postes seulement ; une erreur d'écriture est consignée et n'arrête jamais la vigie.
/// </summary>
public sealed class JournalDecisionsFichier(string dossier, Func<DateTime> maintenantUtc, ILogger<JournalDecisionsFichier> logger) : IJournalDecisions
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim verrou = new(1, 1);

    public async Task EcrireAsync(string evenement, object donnees, CancellationToken ct = default)
    {
        var maintenant = maintenantUtc();
        var ligne = JsonSerializer.Serialize(new { utc = maintenant, evenement, donnees }, Options) + Environment.NewLine;
        await verrou.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(dossier);
            await File.AppendAllTextAsync(Path.Combine(dossier, $"vigie-{maintenant:yyyyMMdd}.jsonl"), ligne, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogError(e, "Journal des décisions de la vigie illisible ou non inscriptible ({Evenement})", evenement);
        }
        finally { verrou.Release(); }
    }
}
