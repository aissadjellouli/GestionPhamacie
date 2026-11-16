using Pharma.Application.Fiscal;

namespace Pharma.Api.Taches;

/// <summary>
/// Clôtures fiscales (lot 3) : peu après le démarrage puis toutes les 6 heures, scelle chaque mois échu du journal des ventes
/// (rapport mensuel archivé) et rattrape les clôtures de caisse dont le sceau a échoué (ScellerPeriodesEchues).
/// </summary>
public sealed class ClotureFiscaleMensuelle(IServiceProvider services, ILogger<ClotureFiscaleMensuelle> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var faits = await scope.ServiceProvider.GetRequiredService<ScellerPeriodesEchues>().ExecuterAsync(stoppingToken);
                if (faits > 0) logger.LogInformation("Clôtures fiscales scellées : {Nombre}", faits);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Échec des clôtures fiscales planifiées");
            }
            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}
