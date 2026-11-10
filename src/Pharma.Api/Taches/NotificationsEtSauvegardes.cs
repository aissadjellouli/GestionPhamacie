using Pharma.Application.Notifications;
using Pharma.Application.Sauvegardes;

namespace Pharma.Api.Taches;

/// <summary>
/// Vide la file des messages toutes les 30 secondes (nouvelles tentatives à délai croissant)
/// et met en file le rapport quotidien une fois l'heure réglée passée.
/// </summary>
public sealed class EnvoiNotifications(IServiceProvider services, ILogger<EnvoiNotifications> logger) : BackgroundService
{
    private static readonly TimeSpan Periode = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                if (await scope.ServiceProvider.GetRequiredService<EnvoyerRapportQuotidien>().ExecuterAsync(DateTime.Now, stoppingToken))
                    logger.LogInformation("Rapport quotidien mis en file");
                var bilan = await scope.ServiceProvider.GetRequiredService<TraiterFileAttente>().ExecuterAsync(DateTime.UtcNow, stoppingToken);
                if (bilan.Envoyes + bilan.Echecs > 0)
                    logger.LogInformation("Notifications : {Envoyes} envoyée(s), {Echecs} en échec (nouvel essai prévu)", bilan.Envoyes, bilan.Echecs);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Échec du traitement de la file des notifications");
            }
            await Task.Delay(Periode, stoppingToken);
        }
    }
}

/// <summary>Sauvegarde automatique quotidienne de la base, vérifiée chaque minute.</summary>
public sealed class SauvegardesAutomatiques(IServiceProvider services, ILogger<SauvegardesAutomatiques> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                if (await scope.ServiceProvider.GetRequiredService<SauvegardeAutomatique>().ExecuterAsync(DateTime.Now, stoppingToken) is { } r)
                    logger.Log(r.Statut == "reussie" ? LogLevel.Information : LogLevel.Error, "Sauvegarde automatique : {Statut} · {Message}", r.Statut, r.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Échec de la sauvegarde automatique");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
