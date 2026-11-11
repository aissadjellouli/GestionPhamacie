using Pharma.Application.Surveillance;

namespace Pharma.Api.Taches;

/// <summary>
/// Calcul des anomalies de surveillance au démarrage puis toutes les heures, même sans activité : les anomalies qui dépendent
/// du temps écoulé (retour sans avoir, crédit jamais remboursé, comptage des produits sensibles en retard) apparaissent à temps.
/// Les ventes, inventaires et connexions le relancent aussi aussitôt.
/// </summary>
public sealed class SurveillancePeriodique(IServiceProvider services) : BackgroundService
{
    private static readonly TimeSpan Intervalle = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Surveiller ne lève jamais : un échec est inscrit au journal du serveur
            using (var scope = services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<Surveiller>().ExecuterAsync(stoppingToken);
            await Task.Delay(Intervalle, stoppingToken);
        }
    }
}
