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
    /// <summary>Script 152 : passage répété toutes les 10 s tant que l'encaissement attend le contrôle de démarrage du serveur central.</summary>
    public static readonly TimeSpan IntervalleControleDemarrage = TimeSpan.FromSeconds(10);

    /// <summary>Attente avant le passage suivant : 1 min, puis doublée à chaque échec consécutif, plafonnée.</summary>
    public static TimeSpan Attente(int echecsConsecutifs) =>
        echecsConsecutifs <= 0 ? Intervalle : TimeSpan.FromTicks(Math.Min(AttenteMax.Ticks, Intervalle.Ticks << Math.Min(echecsConsecutifs, 10)));

    // 2e serveur : l'API refuse déjà de démarrer sur une base en veille (ReplicationOfficine.RefusDemarrage) ; en plus, chaque
    // passage vérifie pg_is_in_recovery() et n'envoie rien sur une base en lecture seule (EnvoyerLotsSync.ArretLectureSeule).
    private static Task<bool> PackSynchronisationAsync(IServiceProvider sp, CancellationToken ct) =>
        sp.GetRequiredService<Pharma.Application.Offre.IPacksOffre>().InclusAsync(Pharma.Application.Offre.PacksOffre.MultiOfficines, ct);

    /// <summary>Résultats d'ordres du central pas encore accusés par lui (journal de sortie, table sync_ordre_resultat).</summary>
    private static async Task<bool> ResultatsOrdresEnAttenteAsync(IServiceProvider sp, CancellationToken ct)
    {
        await using var cmd = sp.GetRequiredService<Npgsql.NpgsqlDataSource>().CreateCommand("""
            SELECT EXISTS (SELECT 1 FROM sync_sortie s, sync_parametre p WHERE p.id = 1 AND p.active AND s.nom_table = 'sync_ordre_resultat'
                           AND (s.lot_numero IS NULL OR s.lot_numero > p.dernier_accuse))
            """);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private async Task AppliquerOrdresAsync(IServiceProvider sp, CancellationToken ct)
    {
        try { await sp.GetRequiredService<AppliquerOrdresCentraux>().ExecuterAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Synchronisation : ordres du serveur central non traités"); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var echecs = 0;
        // Au démarrage : les modèles de réglages déjà reçus du serveur central sont appliqués sans attendre un échange (central injoignable, redémarrage)
        try
        {
            using var demarrage = services.CreateScope();
            await demarrage.ServiceProvider.GetRequiredService<Pharma.Application.Reglages.AppliquerReglagesMutualises>().ExecuterAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Réglages mutualisés : application au démarrage impossible");
        }
        // Script 152 (2026-11-19) : une base restaurée ne le sait pas d'elle-même ; l'encaissement attend la réponse du serveur central,
        // au plus le délai réglé. Premier passage aussitôt (sans les 30 s), puis toutes les 10 s tant que l'attente dure.
        var controleAttendu = false;
        try
        {
            using var demarrage = services.CreateScope();
            controleAttendu = await demarrage.ServiceProvider.GetRequiredService<IEnvoiSyncDepot>().NoterDemarrageAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Synchronisation : démarrage non noté (encaissement non suspendu)");
        }
        if (!controleAttendu) await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                // Lot O6 (décision 3) : sans le pack G, la synchronisation s'arrête ; un passage n'est fait que s'il reste des résultats
                // d'ordres déjà reçus à remonter au central (le central ne reste pas dans un état faux). Les ordres reçus sont encore traités.
                if (!await PackSynchronisationAsync(scope.ServiceProvider, stoppingToken) && !await ResultatsOrdresEnAttenteAsync(scope.ServiceProvider, stoppingToken))
                {
                    await AppliquerOrdresAsync(scope.ServiceProvider, stoppingToken);
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                    continue;
                }
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
                // Lots S5.1 et S5.5 : ordres reçus du serveur central (descendus par ce passage ou un précédent), appliqués ou mis à confirmer
                try
                {
                    var ordres = await scope.ServiceProvider.GetRequiredService<AppliquerOrdresCentraux>().ExecuterAsync(stoppingToken);
                    if (ordres > 0) logger.LogInformation("Synchronisation : {Ordres} ordre(s) du serveur central traité(s)", ordres);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Synchronisation : ordres du serveur central non traités");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                echecs++;
                logger.LogError(ex, "Synchronisation : passage impossible");
            }
            var attente = Attente(echecs);
            if (controleAttendu)
            {
                try
                {
                    using var scope = services.CreateScope();
                    var reste = await scope.ServiceProvider.GetRequiredService<IEnvoiSyncDepot>().AttenteEncaissementAsync(stoppingToken);
                    if (reste > 0) attente = IntervalleControleDemarrage;
                    else
                    {
                        // Essais rapprochés du démarrage (central injoignable) : ils ne comptent pas pour l'attente croissante, sinon le
                        // passage suivant (détection, avance du compteur) partirait 30 min plus tard (constaté à l'essai du 2026-11-19)
                        controleAttendu = false;
                        echecs = Math.Min(echecs, 1);
                        attente = Attente(echecs);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Synchronisation : attente de l'encaissement non lue");
                }
            }
            await Task.Delay(attente, stoppingToken);
        }
    }
}
