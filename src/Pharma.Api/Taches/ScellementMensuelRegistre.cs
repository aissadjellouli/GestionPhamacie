using Pharma.Application.Commun;
using Pharma.Application.Psychotropes;

namespace Pharma.Api.Taches;

/// <summary>
/// Registre des psychotropes (lot 8) : scelle le mois écoulé (sceau HMAC, motif cloture_mensuelle) au démarrage puis toutes les six
/// heures s'il ne l'est pas encore. Le sceau est ensuite contrôlé par la vérification nocturne des journaux (lot 2).
/// </summary>
public sealed class ScellementMensuelRegistre(IServiceProvider services, ILogger<ScellementMensuelRegistre> logger) : BackgroundService
{
    private static readonly TimeSpan Intervalle = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Laisse démarrer l'API (clé de scellement créée par la vérification nocturne)
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var aujourdhui = scope.ServiceProvider.GetRequiredService<IHorloge>().Aujourdhui;
                var sceau = await scope.ServiceProvider.GetRequiredService<ScellerRegistreMensuel>().ExecuterAsync(aujourdhui, stoppingToken);
                if (sceau is not null)
                    logger.LogInformation("Registre des psychotropes scellé pour le mois {Mois} (sceau {Id}, ligne {Rang})",
                        sceau.PeriodeDu.ToString("MM/yyyy"), sceau.Id, sceau.DernierRang);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scellement mensuel du registre des psychotropes en échec");
            }
            await Task.Delay(Intervalle, stoppingToken);
        }
    }
}
