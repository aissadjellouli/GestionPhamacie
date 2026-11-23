using Pharma.Application.Notifications;
using Pharma.Application.SecondServeur;

namespace Pharma.Api.Taches;

/// <summary>
/// Règle de majorité côté serveur 1 (vague 2, lot V2-1b) : toutes les 5 s, mise en lecture seule (motif « isole ») du serveur qui a perdu la copie et voit moins de
/// la moitié des postes connus pendant 20 s ; levée automatique quand la copie revient ou la majorité est revue. NON suspendue quand le serveur est écarté
/// (c'est elle qui lève l'isolement). Aucun 2e serveur déclaré ou veille : ne tourne pas.
/// </summary>
public sealed class SurveillanceIsolementTache(IServiceProvider services, IVeilleServeur mode, IReleveServeurs releves, ILogger<SurveillanceIsolementTache> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (mode.EstVeille || !releves.AutreServeurRegle) return;
        var surveiller = services.GetRequiredService<SurveillerIsolement>();
        var avant = IssueIsolement.Inactif;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var issue = await surveiller.ExecuterAsync(scope.ServiceProvider.GetRequiredService<IAlertesTitulaire>(), stoppingToken);
                if (issue != avant)
                {
                    switch (issue)
                    {
                        case IssueIsolement.Isole:
                            logger.LogCritical("Ce serveur est isolé (copie perdue et moins de la moitié des postes vus) : il passe en lecture seule, titulaire prévenu");
                            break;
                        case IssueIsolement.Leve:
                            logger.LogWarning("Isolement levé automatiquement : copie revenue ou majorité des postes revue, le 2e serveur ne s'est pas promu");
                            break;
                        case IssueIsolement.Definitif:
                            logger.LogCritical("Le 2e serveur s'est promu pendant l'isolement : plus de levée automatique, la détection de timeline décide");
                            break;
                    }
                    avant = issue;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Surveillance de l'isolement impossible");
            }
            try { await Task.Delay(SurveillerIsolement.Intervalle, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
