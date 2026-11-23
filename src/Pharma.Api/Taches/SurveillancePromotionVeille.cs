using Pharma.Application.SecondServeur;

namespace Pharma.Api.Taches;

/// <summary>
/// Vague 2, lot V2-1 : seule tâche de fond d'une API en veille. Toutes les 5 s, relève la base locale ; dès qu'elle n'est plus en
/// recovery (promotion par le script manuel ou, plus tard, par la décision automatique), l'API s'arrête avec le code
/// <see cref="ModeVeille.CodeSortieRedemarrage"/> : le service Windows la relance en principal (saut des compteurs, tâches de fond).
/// Aucune écriture en base. Un arrêt demandé par un autre lot (<see cref="IVeilleServeur.DemanderRedemarrage"/>) passe par le même chemin.
/// </summary>
public sealed class SurveillancePromotionVeille(IReleveServeurs releves, IVeilleServeur veille, IHostApplicationLifetime vie,
    ILogger<SurveillancePromotionVeille> logger) : BackgroundService
{
    public static readonly TimeSpan Intervalle = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        veille.RedemarrageDemandeLeve += Arreter;
        if (veille.RedemarrageDemande) Arreter(veille.MotifRedemarrage ?? "demande");
        logger.LogInformation("API en veille : base en lecture seule, seules les routes healthcheck, vigie et inter-serveurs répondent");
        var suivi = new SuiviPromotionVeille();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (suivi.Observer(await releves.LocalAsync(stoppingToken)))
                        veille.DemanderRedemarrage("base promue");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Relevé de la base en veille impossible");
                }
                await Task.Delay(Intervalle, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { veille.RedemarrageDemandeLeve -= Arreter; }
    }

    private void Arreter(string motif)
    {
        logger.LogWarning("API en veille arrêtée pour redémarrer en principal ({Motif}) : code de sortie {Code}", motif, ModeVeille.CodeSortieRedemarrage);
        Environment.ExitCode = ModeVeille.CodeSortieRedemarrage;
        vie.StopApplication();
    }
}
