using Npgsql;
using NpgsqlTypes;

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

    /// <summary>Après un abandon : le 2e serveur ne compte de nouveau que s'il a écrit les journaux au moins jusqu'à la position notée.</summary>
    internal const string SqlStandbyRepond = """
        SELECT EXISTS (SELECT 1 FROM pg_stat_replication WHERE write_lsn >= $1) AND current_setting('synchronous_standby_names') <> ''
        """;

    private static readonly object verrouSuspension = new();
    private static NpgsqlLogSequenceNumber? suspenduJusqua;

    /// <summary>
    /// Position des journaux du principal notée au dernier abandon (null : aucune suspension). Tant que le 2e serveur n'a pas écrit jusque-là,
    /// aucune transaction ne l'attend : essai réel du 2026-11-25, réseau figé entre les deux serveurs, PostgreSQL voit encore la copie
    /// « sync » jusqu'à <c>wal_sender_timeout</c> (60 s par défaut) et chaque vente attendait alors 2 s. Une position, pas une heure :
    /// aucune dépendance aux horloges des deux PC.
    /// </summary>
    public static NpgsqlLogSequenceNumber? SuspenduJusqua { get { lock (verrouSuspension) return suspenduJusqua; } }

    /// <summary>Note la position à atteindre par le 2e serveur avant de l'attendre de nouveau (la plus avancée l'emporte).</summary>
    public static void Suspendre(NpgsqlLogSequenceNumber position)
    {
        lock (verrouSuspension)
            if (suspenduJusqua is not { } actuelle || position > actuelle) suspenduJusqua = position;
    }

    /// <summary>Lève la suspension si elle porte toujours sur cette position (un abandon plus récent la garde).</summary>
    private static void Lever(NpgsqlLogSequenceNumber position)
    {
        lock (verrouSuspension)
            if (suspenduJusqua == position) suspenduJusqua = null;
    }

    /// <summary>
    /// À appeler juste après l'ouverture de la transaction. Pose <c>SET LOCAL synchronous_commit = remote_write</c> seulement si un 2e serveur
    /// est connecté, que le serveur est configuré pour l'attendre et, après un abandon, que le 2e serveur répond de nouveau (journaux écrits
    /// au-delà de la position notée) ; renvoie vrai dans ce cas.
    /// </summary>
    public static async Task<bool> ActiverAsync(NpgsqlConnection cx, NpgsqlTransaction tx, CancellationToken ct = default)
    {
        var suspension = SuspenduJusqua;
        bool connecte;
        await using (var cmd = new NpgsqlCommand(suspension is null ? SqlStandbyConnecte : SqlStandbyRepond, cx, tx))
        {
            if (suspension is { } p) cmd.Parameters.Add(new NpgsqlParameter { Value = p, NpgsqlDbType = NpgsqlDbType.PgLsn });
            connecte = (bool)(await cmd.ExecuteScalarAsync(ct))!;
        }
        if (!connecte) return false;
        if (suspension is { } levee) Lever(levee);
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
            var annule = await cmd.ExecuteScalarAsync(c) is true;
            // Attente abandonnée : plus d'attente du 2e serveur tant qu'il n'a pas écrit jusqu'ici (il répond de nouveau)
            if (annule) await SuspendreApresAbandonAsync(db, c);
            return annule;
        }, ct);
    }

    private static async Task SuspendreApresAbandonAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        try
        {
            await using var cmd = db.CreateCommand("SELECT pg_current_wal_lsn()");
            if (await cmd.ExecuteScalarAsync(ct) is NpgsqlLogSequenceNumber position) Suspendre(position);
        }
        catch (NpgsqlException) { /* position illisible : pas de suspension, comportement d'avant (attente bornée à 2 s) */ }
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
