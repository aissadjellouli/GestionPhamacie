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

    private DateOnly? rappelsOffreVerifiesLe;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                // Lot O6 : le rapport quotidien par e-mail fait partie du pack E (pilotage et notifications) ; la file part toujours
                // (alertes de sécurité comprises)
                if (await scope.ServiceProvider.GetRequiredService<Pharma.Application.Offre.IPacksOffre>()
                        .InclusAsync(Pharma.Application.Offre.PacksOffre.Pilotage, stoppingToken)
                    && await scope.ServiceProvider.GetRequiredService<EnvoyerRapportQuotidien>().ExecuterAsync(DateTime.Now, stoppingToken))
                    logger.LogInformation("Rapport quotidien mis en file");
                // Rappels de fin d'offre (90, 60, 30, 15, 7, 1 jours, délai de grâce ; une fois chacun) : une vérification par jour,
                // jamais bloquant pour la file ; refaite au jour suivant ou au redémarrage de l'API
                try
                {
                    var jour = DateOnly.FromDateTime(DateTime.Now);
                    if (rappelsOffreVerifiesLe != jour)
                    {
                        if (await scope.ServiceProvider.GetRequiredService<Pharma.Application.Offre.RappelsOffre>().ExecuterAsync(stoppingToken) is { } rappel)
                            logger.LogInformation("Rappel de fin d'offre mis en file ({Etape})", rappel);
                        rappelsOffreVerifiesLe = jour;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Rappel de fin d'offre non envoyé");
                }
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
                // Copie de secours trop ancienne : titulaire prévenu (une alerte par période réglée)
                if (await scope.ServiceProvider.GetRequiredService<SurveillerCopieSecours>().ExecuterAsync(stoppingToken))
                    logger.LogWarning("Copie de secours en retard : titulaire prévenu");
                // Aucune sauvegarde réussie sur le disque externe depuis N jours : titulaire prévenu (une alerte par période)
                if (await scope.ServiceProvider.GetRequiredService<SurveillerSauvegardeDisque>().ExecuterAsync(stoppingToken))
                    logger.LogWarning("Aucune sauvegarde sur disque externe depuis le délai réglé : titulaire prévenu");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Échec de la sauvegarde automatique");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
