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
        await Task.WhenAll(SurveillerReplicationAsync(stoppingToken), SurveillerEcartAsync(stoppingToken));
    }

    /// <summary>Bascule du 2e serveur, lot 1 : toutes les 30 s, cet autre serveur a-t-il été promu plus récemment que ce serveur ?</summary>
    private async Task SurveillerEcartAsync(CancellationToken stoppingToken)
    {
        // Sans 2e serveur déclaré : aucune surveillance, aucun relevé, aucun écart
        if (!services.GetRequiredService<IReleveServeurs>().AutreServeurRegle) return;
        var dernier = VerdictEcart.Normal;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Déjà écarté : l'écart est gardé en base (réécrit si l'écriture a échoué) et une levée faite en base par --lever-ecart est reprise ici
                await services.GetRequiredService<GardienServeurEcarte>().SynchroniserAsync(stoppingToken);
                using var scope = services.CreateScope();
                if (await scope.ServiceProvider.GetRequiredService<SurveillerServeurEcarte>().ExecuterAsync(stoppingToken) is { } d && d.Verdict != dernier)
                {
                    dernier = d.Verdict;
                    switch (d.Verdict)
                    {
                        case VerdictEcart.Ecarte when d.Confirme:
                            logger.LogCritical("Ce serveur est écarté : l'autre serveur a une timeline plus récente ({TimelineAutre} contre {TimelineLocale})", d.TimelineAutre, d.TimelineLocale);
                            break;
                        case VerdictEcart.Retenu when d.Confirme:
                            logger.LogCritical("L'ancien serveur principal écrit encore (timeline {TimelineAutre} contre {TimelineLocale}) : titulaire prévenu", d.TimelineAutre, d.TimelineLocale);
                            break;
                        case VerdictEcart.TimelinesEgales when d.Confirme:
                            logger.LogCritical("Deux bases en écriture avec la même timeline ({Timeline}) : ce serveur passe en lecture seule, titulaire prévenu", d.TimelineLocale);
                            break;
                        case VerdictEcart.LigneeDifferente when d.Confirme:
                            logger.LogWarning("L'autre serveur a une base en écriture d'une autre lignée : titulaire prévenu, aucun serveur écarté");
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Détection d'un serveur écarté impossible");
            }
            try { await Task.Delay(SurveillerServeurEcarte.Intervalle, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SurveillerReplicationAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Serveur écarté (lecture seule) : la surveillance de la réplication écrit son alerte en base, elle est suspendue
                if (services.GetRequiredService<IServeurEcarteDetecte>().Ecart is null)
                {
                    using var scope = services.CreateScope();
                    if (await scope.ServiceProvider.GetRequiredService<SurveillerReplication>().ExecuterAsync(stoppingToken))
                        logger.LogWarning("Réplication vers le 2e serveur décrochée ou en retard : titulaire prévenu");
                }
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
            // Serveur écarté (démarré en lecture seule) : ce serveur ne se déclare pas principal
            if (services.GetRequiredService<IServeurEcarteDetecte>().Ecart is not null) return;
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
