using Npgsql;

namespace Pharma.Infrastructure.SecondServeur;

/// <summary>
/// Réplication synchrone choisie par transaction (lot V2-7) : pour les ventes, les factures et le registre des psychotropes seulement,
/// la validation attend que le 2e serveur ait reçu et écrit les journaux (<c>synchronous_commit = remote_write</c>), mais uniquement quand
/// il est connecté. Officine à un seul serveur, ou 2e serveur arrêté : aucune instruction n'est envoyée et rien ne change.
/// Côté serveur, <c>synchronous_commit = local</c> reste le réglage par défaut (voir docs/second-serveur-local.md).
/// </summary>
public static class ReplicationSynchrone
{
    /// <summary>Attente maximale de l'accusé du 2e serveur ; au-delà, repli asynchrone (la transaction est déjà validée sur ce serveur).</summary>
    public static readonly TimeSpan DelaiMaximal = TimeSpan.FromSeconds(2);

    private static long abandons;

    /// <summary>Nombre d'attentes abandonnées depuis le démarrage (2e serveur décroché pendant une validation).</summary>
    public static long Abandons => Interlocked.Read(ref abandons);

    /// <summary>Levé à chaque attente abandonnée, avec la durée attendue : l'API en fait une alerte au titulaire.</summary>
    public static event Action<TimeSpan>? AttenteAbandonnee;

    internal const string SqlStandbyConnecte = """
        SELECT EXISTS (SELECT 1 FROM pg_stat_replication) AND current_setting('synchronous_standby_names') <> ''
        """;

    /// <summary>
    /// À appeler juste après l'ouverture de la transaction. Pose <c>SET LOCAL synchronous_commit = remote_write</c> seulement si un 2e serveur
    /// est connecté et que le serveur est configuré pour l'attendre ; renvoie vrai dans ce cas.
    /// </summary>
    public static async Task<bool> ActiverAsync(NpgsqlConnection cx, NpgsqlTransaction tx, CancellationToken ct = default)
    {
        bool connecte;
        await using (var cmd = new NpgsqlCommand(SqlStandbyConnecte, cx, tx))
            connecte = (bool)(await cmd.ExecuteScalarAsync(ct))!;
        if (!connecte) return false;
        await using var set = new NpgsqlCommand("SET LOCAL synchronous_commit = remote_write", cx, tx);
        await set.ExecuteNonQueryAsync(ct);
        return true;
    }

    /// <summary>
    /// Valide la transaction. Si l'attente synchrone est active et dure plus de <see cref="DelaiMaximal"/>, annule l'attente du processus
    /// (<c>pg_cancel_backend</c> ciblé sur l'état SyncRep) : la transaction reste validée localement, la réplication continue en asynchrone.
    /// </summary>
    public static async Task ValiderAsync(NpgsqlDataSource db, NpgsqlConnection cx, NpgsqlTransaction tx, bool synchrone, CancellationToken ct = default)
    {
        if (!synchrone) { await tx.CommitAsync(ct); return; }
        var pid = cx.ProcessID;
        await AttendreBorneAsync(tx.CommitAsync(ct), DelaiMaximal, async c =>
        {
            await using var cmd = db.CreateCommand("SELECT pg_cancel_backend(pid) FROM pg_stat_activity WHERE pid = $1 AND wait_event = 'SyncRep'");
            cmd.Parameters.AddWithValue(pid);
            return await cmd.ExecuteScalarAsync(c) is true;
        }, ct);
    }

    /// <summary>Attend <paramref name="travail"/> ; passé <paramref name="delai"/>, appelle <paramref name="annuler"/> puis attend la fin du travail.</summary>
    public static async Task AttendreBorneAsync(Task travail, TimeSpan delai, Func<CancellationToken, Task<bool>> annuler, CancellationToken ct)
    {
        using var arret = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var minuterie = Task.Delay(delai, arret.Token);
        if (await Task.WhenAny(travail, minuterie) == travail)
        {
            await arret.CancelAsync();
            await travail;
            return;
        }
        var annule = false;
        try { annule = !travail.IsCompleted && await annuler(ct); }
        catch (NpgsqlException) { /* l'annulation échoue : on continue d'attendre, le journal serveur le dira */ }
        if (annule)
        {
            Interlocked.Increment(ref abandons);
            AttenteAbandonnee?.Invoke(delai);
        }
        await travail;
    }
}
