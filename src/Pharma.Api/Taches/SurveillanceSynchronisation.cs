using Pharma.Application.Synchronisation;

namespace Pharma.Api.Taches;

/// <summary>
/// Synchronisation, lot S3 : chaque minute, vérifie le retard, l'erreur d'envoi persistante, le certificat et les refus de quota, et
/// prévient le titulaire (SMS ou e-mail du mécanisme existant) au plus une fois par heure et par sujet (conception § 8).
/// Ne fait rien si la synchronisation est désactivée ou si la base est en lecture seule (2e serveur en veille). Jamais sur le chemin d'une vente.
/// Journal : nature des alertes seulement, aucune donnée.
/// </summary>
public sealed class SurveillanceSynchronisation(IServiceProvider services, ILogger<SurveillanceSynchronisation> logger) : BackgroundService
{
    public static readonly TimeSpan Intervalle = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var natures = await scope.ServiceProvider.GetRequiredService<SurveillerSynchronisation>().ExecuterAsync(stoppingToken);
                if (natures.Count > 0) logger.LogWarning("Synchronisation : alerte(s) au titulaire ({Natures})", string.Join(", ", natures));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Synchronisation : surveillance impossible");
            }
            await Task.Delay(Intervalle, stoppingToken);
        }
    }
}
