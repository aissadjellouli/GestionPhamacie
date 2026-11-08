using Pharma.Application.Alertes;

namespace Pharma.Api.Taches;

/// <summary>Génère les alertes au démarrage de l'API puis chaque matin à 7 h.</summary>
public sealed class AlertesQuotidiennes(IServiceProvider services, ILogger<AlertesQuotidiennes> logger) : BackgroundService
{
    private static readonly TimeSpan HeureQuotidienne = TimeSpan.FromHours(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await GenererAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var maintenant = DateTime.Now;
            var prochain = maintenant.Date + HeureQuotidienne;
            if (prochain <= maintenant) prochain = prochain.AddDays(1);
            await Task.Delay(prochain - maintenant, stoppingToken);
            await GenererAsync(stoppingToken);
        }
    }

    private async Task GenererAsync(CancellationToken ct)
    {
        try
        {
            using var scope = services.CreateScope();
            var n = await scope.ServiceProvider.GetRequiredService<GenererAlertes>().ExecuterAsync(ct);
            logger.LogInformation("Alertes générées ou mises à jour : {Nombre}", n);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Échec de la génération des alertes");
        }
    }
}
