using Pharma.Application.SecondServeur;

namespace Pharma.Api.Taches;

/// <summary>
/// Vigie du 2e serveur : toutes les SondeSecondes (5 s par défaut) sonde le serveur 1 et évalue la décision. Active seulement en mode veille avec
/// un 2e serveur déclaré. Après chaque évaluation, l'exécutant de la promotion (lot V2-4) agit si le verdict est confirmé.
/// </summary>
public sealed class VigieSecondServeurTache(VigieSecondServeur vigie, ExecutantPromotion promotion, IVeilleServeur mode, IReleveServeurs releves, ReglagesBascule reglages,
    ILogger<VigieSecondServeurTache> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!mode.EstVeille || !releves.AutreServeurRegle) return;
        string? derniere = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var r = await vigie.EvaluerAsync(stoppingToken);
                logger.LogDebug("Vigie : verdict {Verdict} ({Raison}), connus {Connus}, votes {Votes}", r.Verdict, r.Raison, r.Connus, r.Votes);
                // Alertes de la vigie (une par heure et par type), puis promotion si le verdict est confirmé deux fois
                var p = await promotion.ApresEvaluationAsync(r, stoppingToken);
                if (p.Issue is IssuePromotion.Promue or IssuePromotion.NonPromue && p.Raison != derniere)
                {
                    derniere = p.Raison;
                    logger.LogCritical("Promotion automatique : {Issue} ({Raison})", p.Issue, p.Raison);
                }
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
