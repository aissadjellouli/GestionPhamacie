using Pharma.Application.Integrite;

namespace Pharma.Api.Taches;

/// <summary>
/// Intégrité des journaux : au démarrage, crée la clé de scellement si elle n'existe pas (fichier DPAPI hors de la base) ; puis chaque
/// nuit, à l'heure de parametre_integrite.heure_verification (3 h par défaut), vérifie chaque journal chaîné et ses sceaux. Une rupture
/// est inscrite au journal des actions et signalée au titulaire (VerifierIntegrite).
/// </summary>
public sealed class VerificationIntegriteNocturne(IServiceProvider services, ILogger<VerificationIntegriteNocturne> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Lecture de l'empreinte : crée la clé au premier démarrage (la clé elle-même n'est jamais journalisée)
            var empreinte = services.GetRequiredService<IScelleurIntegrite>().EmpreinteCle;
            logger.LogInformation("Clé de scellement des journaux prête (empreinte {Empreinte})", empreinte);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Clé de scellement des journaux indisponible");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var heure = await HeureAsync(stoppingToken);
            var maintenant = DateTime.Now;
            var prochain = maintenant.Date.AddHours(heure);
            if (prochain <= maintenant) prochain = prochain.AddDays(1);
            await Task.Delay(prochain - maintenant, stoppingToken);
            await VerifierAsync(stoppingToken);
        }
    }

    private async Task<int> HeureAsync(CancellationToken ct)
    {
        try
        {
            using var scope = services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<IIntegriteDepot>().ParametresAsync(ct)).HeureVerification;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Heure de vérification d'intégrité illisible : 3 h");
            return 3;
        }
    }

    private async Task VerifierAsync(CancellationToken ct)
    {
        try
        {
            using var scope = services.CreateScope();
            var etats = await scope.ServiceProvider.GetRequiredService<VerifierIntegrite>().ExecuterAsync(null, ct);
            var ruptures = etats.Count(e => e.Statut != VerifierIntegrite.Ok);
            if (ruptures == 0) logger.LogInformation("Intégrité des journaux vérifiée : {Chaines} chaîne(s) intacte(s)", etats.Count);
            else logger.LogWarning("Intégrité des journaux : {Ruptures} chaîne(s) rompue(s) ou en erreur sur {Chaines}", ruptures, etats.Count);
            // Lot 3 : continuité des n° de tickets du journal des ventes
            var anomalies = await scope.ServiceProvider.GetRequiredService<Pharma.Application.Fiscal.ControlerNumerotation>().ExecuterAsync(null, ct);
            if (anomalies.Count > 0) logger.LogWarning("Numérotation des tickets : {Anomalies} anomalie(s)", anomalies.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Échec de la vérification d'intégrité des journaux");
        }
    }
}
