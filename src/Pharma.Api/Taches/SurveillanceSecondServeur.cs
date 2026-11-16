using Pharma.Application.Integrite;
using Pharma.Application.SecondServeur;

namespace Pharma.Api.Taches;

/// <summary>
/// 2e serveur (script 122) : au démarrage, ce serveur se déclare principal (version de l'API, clé présente, dossiers joignables) ;
/// puis, chaque minute, la réplication vers le 2e serveur est vérifiée et le titulaire prévenu si elle décroche ou prend du retard.
/// </summary>
public sealed class SurveillanceSecondServeur(IServiceProvider services, CeServeur ce, ILogger<SurveillanceSecondServeur> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await DeclarerAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                if (await scope.ServiceProvider.GetRequiredService<SurveillerReplication>().ExecuterAsync(stoppingToken))
                    logger.LogWarning("Réplication vers le 2e serveur décrochée ou en retard : titulaire prévenu");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Surveillance de la réplication impossible");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task DeclarerAsync(CancellationToken ct)
    {
        try
        {
            string? empreinte = null;
            bool? cle = null;
            try { empreinte = services.GetRequiredService<IScelleurIntegrite>().EmpreinteCle; cle = true; }
            catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException) { cle = false; }
            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ISecondServeurDepot>().DeclarerPrincipalAsync(new ServeurOfficine(
                ce.Nom, RolesServeur.Principal, null, null, false, false, false, false, ce.VersionApi, cle, empreinte, null,
                Endpoints.SecondServeurEndpoints.Joignable(ce.DossierChifa), Endpoints.SecondServeurEndpoints.Joignable(ce.DossierMdn), null), ct);
            logger.LogInformation("Serveur {Serveur} déclaré principal (API {Version})", ce.Nom, ce.VersionApi);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Script 122 pas encore passé, base injoignable : l'API sert quand même
            logger.LogWarning("2e serveur : déclaration du serveur principal impossible ({Erreur})", ex.Message);
        }
    }
}
