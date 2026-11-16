using Pharma.Application.Synchronisation;

namespace Pharma.Api.Taches;

/// <summary>
/// Synchronisation, lot S3 : toutes les heures, retire de la file d'envoi (sync_sortie) les lignes des lots accusés depuis plus longtemps
/// que le délai de conservation réglé (conception § 20). Jamais une ligne d'un lot non accusé. Journal : nombres seulement, aucune donnée.
/// </summary>
public sealed class PurgeSynchronisation(IServiceProvider services, ILogger<PurgeSynchronisation> logger) : BackgroundService
{
    public static readonly TimeSpan Intervalle = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var r = await scope.ServiceProvider.GetRequiredService<PurgerSortieSync>().ExecuterAsync(ct: stoppingToken);
                if (r is { Lots: > 0 })
                    logger.LogInformation("Synchronisation : {Lots} lot(s) purgé(s) de la file ({Lignes} ligne(s), jusqu'au lot {Dernier})", r.Lots, r.Lignes, r.DernierLot);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Synchronisation : purge de la file impossible");
            }
            await Task.Delay(Intervalle, stoppingToken);
        }
    }
}
