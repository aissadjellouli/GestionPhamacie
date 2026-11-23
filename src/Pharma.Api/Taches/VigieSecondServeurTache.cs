using Pharma.Application.SecondServeur;

namespace Pharma.Api.Taches;

/// <summary>
/// Vigie du 2e serveur : toutes les SondeSecondes (5 s par défaut) sonde le serveur 1 et évalue la décision. Active seulement en mode veille avec
/// un 2e serveur déclaré. Elle ne promeut rien : le verdict est lu par le lot V2-4.
/// </summary>
public sealed class VigieSecondServeurTache(VigieSecondServeur vigie, IVeilleServeur mode, IReleveServeurs releves, ReglagesBascule reglages,
    ILogger<VigieSecondServeurTache> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!mode.EstVeille || !releves.AutreServeurRegle) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var r = await vigie.EvaluerAsync(stoppingToken);
                logger.LogDebug("Vigie : verdict {Verdict} ({Raison}), connus {Connus}, votes {Votes}", r.Verdict, r.Raison, r.Connus, r.Votes);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Évaluation de la vigie impossible");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(reglages.SondeSecondes), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
