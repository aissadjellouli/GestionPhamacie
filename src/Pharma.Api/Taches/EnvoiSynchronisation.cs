using Pharma.Application.Synchronisation;

namespace Pharma.Api.Taches;

/// <summary>
/// Synchronisation, lot S2 : environ chaque minute, constitue et envoie les lots au serveur central (conception § 3, § 12).
/// Ne fait rien tant que la synchronisation est désactivée, sans adresse ou sans activation. En erreur (coupure d'Internet,
/// serveur central indisponible), attente croissante (1, 2, 4… jusqu'à 30 min) ; rien n'est perdu : un lot reste en attente
/// jusqu'à son accusé et repart à l'identique. Jamais sur le chemin d'une vente.
/// </summary>
public sealed class EnvoiSynchronisation(IServiceProvider services, ILogger<EnvoiSynchronisation> logger) : BackgroundService
{
    public static readonly TimeSpan Intervalle = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan AttenteMax = TimeSpan.FromMinutes(30);

    /// <summary>Attente avant le passage suivant : 1 min, puis doublée à chaque échec consécutif, plafonnée.</summary>
    public static TimeSpan Attente(int echecsConsecutifs) =>
        echecsConsecutifs <= 0 ? Intervalle : TimeSpan.FromTicks(Math.Min(AttenteMax.Ticks, Intervalle.Ticks << Math.Min(echecsConsecutifs, 10)));

    // 2e serveur : l'API refuse déjà de démarrer sur une base en veille (ReplicationOfficine.RefusDemarrage) ; en plus, chaque
    // passage vérifie pg_is_in_recovery() et n'envoie rien sur une base en lecture seule (EnvoyerLotsSync.ArretLectureSeule).
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var echecs = 0;
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var r = await scope.ServiceProvider.GetRequiredService<EnvoyerLotsSync>().ExecuterAsync(DateTime.UtcNow, ct: stoppingToken);
                if (r.EnErreur)
                {
                    // Refus définitif (contenu refusé, lots manquants) : pas de renvoi en boucle, attente maximale, erreur affichée à l'écran
                    echecs = r.Bloquant ? 10 : echecs + 1;
                    logger.LogWarning("Synchronisation : envoi interrompu ({Erreur}), nouvel essai dans {Attente}", r.Erreur, Attente(echecs));
                }
                else
                {
                    echecs = 0;
                    if (r.LotsAccuses > 0) logger.LogInformation("Synchronisation : {Lots} lot(s) accusé(s) par le serveur central", r.LotsAccuses);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                echecs++;
                logger.LogError(ex, "Synchronisation : passage impossible");
            }
            await Task.Delay(Attente(echecs), stoppingToken);
        }
    }
}
