using Pharma.Application.Serialisation;
using Pharma.Domain.Commun;

namespace Pharma.Api.Taches;

/// <summary>
/// Rejeu de la file de sérialisation (script 120) toutes les minutes : les opérations mises en file à la réception, à la vente et au
/// retour sont envoyées à la base nationale dès qu'elle répond ; hors ligne, elles attendent (la vente n'est jamais bloquée).
/// Charge aussi la norme GS1 paramétrée (parametre_gs1_ai) utilisée par la lecture des Data Matrix.
/// </summary>
public sealed class SerialisationHorsLigne(IServiceProvider services, ILogger<SerialisationHorsLigne> logger) : BackgroundService
{
    private static readonly TimeSpan Intervalle = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var file = scope.ServiceProvider.GetRequiredService<IFileSerialisation>();
                Gs1Norme.Courante = new Gs1Norme(await file.IdentifiantsGs1Async(stoppingToken));
                if ((await file.ParametresAsync(stoppingToken)).Actif)
                {
                    var bilan = await scope.ServiceProvider.GetRequiredService<TraiterFileSerialisation>().ExecuterAsync(stoppingToken);
                    if (bilan.Reussies + bilan.Refusees > 0 || bilan.ConnecteurIndisponible)
                        logger.LogInformation("Sérialisation : {Reussies} réussie(s), {Refusees} refusée(s), connecteur indisponible : {Indisponible}",
                            bilan.Reussies, bilan.Refusees, bilan.ConnecteurIndisponible);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                // Base indisponible ou erreur inattendue : nouvel essai au prochain passage
                logger.LogError(e, "Sérialisation : passage de la file en échec");
            }
            await Task.Delay(Intervalle, stoppingToken);
        }
    }
}
