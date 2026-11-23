using Pharma.Application.SecondServeur;

namespace Pharma.Api.Taches;

/// <summary>
/// Bascule du 2e serveur, lot 3 : une tâche de fond qui écrit est arrêtée dès que ce serveur est écarté (base en lecture seule), et
/// relancée (instance neuve) à la levée de l'écart. Les tâches gardées en marche (sauvegardes, surveillance du 2e serveur) ne sont pas enveloppées.
/// </summary>
public sealed class TacheSuspendueSiEcarte(IServiceProvider services, Type typeTache, IServeurEcarteDetecte ecarte, ILogger<TacheSuspendueSiEcarte> logger) : BackgroundService
{
    private static readonly TimeSpan Sondage = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DelaiArret = TimeSpan.FromSeconds(30);

    /// <summary>Remplace chaque tâche de fond déclarée (AddHostedService) dont le type est donné par son enveloppe suspendable.</summary>
    public static void Envelopper(IServiceCollection services, params Type[] types)
    {
        foreach (var type in types)
        {
            var d = services.FirstOrDefault(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == type);
            if (d is null) continue;   // non déclarée (mode démonstration)
            services.Remove(d);
            services.AddSingleton<IHostedService>(sp => new TacheSuspendueSiEcarte(sp, type, sp.GetRequiredService<IServeurEcarteDetecte>(),
                sp.GetRequiredService<ILogger<TacheSuspendueSiEcarte>>()));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (ecarte.Ecart is not null)
                {
                    await Task.Delay(Sondage, stoppingToken);
                    continue;
                }
                using var tache = (BackgroundService)ActivatorUtilities.CreateInstance(services, typeTache);
                await tache.StartAsync(stoppingToken);
                try
                {
                    while (ecarte.Ecart is null) await Task.Delay(Sondage, stoppingToken);
                    logger.LogWarning("Tâche {Tache} suspendue : ce serveur est écarté (lecture seule)", typeTache.Name);
                }
                finally
                {
                    using var delai = new CancellationTokenSource(DelaiArret);
                    try { await tache.StopAsync(delai.Token); }
                    catch (OperationCanceledException) { logger.LogWarning("Tâche {Tache} : arrêt non terminé dans les {Secondes} s", typeTache.Name, DelaiArret.TotalSeconds); }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
