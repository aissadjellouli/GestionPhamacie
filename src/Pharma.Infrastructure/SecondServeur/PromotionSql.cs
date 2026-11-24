using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Pharma.Application.SecondServeur;

namespace Pharma.Infrastructure.SecondServeur;

// ---- Bascule du 2e serveur, vague 2, lot V2-4 : accès base du retrait (serveur 1) et de la promotion (2e serveur) ----------------

/// <summary>
/// Serveur 1, retrait : attente de la fin des transactions de l'API et LSN final. Source de connexions SANS verrou de lecture seule (la même que l'écart gardé en base),
/// sinon elle ne pourrait plus rien lire une fois le serveur écarté. Aucune donnée de patient n'est lue.
/// </summary>
public sealed class EcrituresServeurSql(NpgsqlDataSource source, string nomApplicationApi = ConnexionsLectureSeule.NomApplicationApi, TimeSpan? sondage = null) : IEcrituresServeur
{
    private readonly TimeSpan _sondage = sondage ?? TimeSpan.FromMilliseconds(200);

    public async Task<bool> AttendreFinTransactionsApiAsync(TimeSpan delai, CancellationToken ct = default)
    {
        var fin = DateTime.UtcNow + delai;
        while (true)
        {
            if (await TransactionsOuvertesAsync(ct) == 0) return true;
            if (DateTime.UtcNow >= fin) return false;
            await Task.Delay(_sondage, ct);
        }
    }

    /// <summary>Connexions de l'API (nom d'application) qui ont une transaction ouverte (en cours ou « idle in transaction »), hors la connexion de cette lecture.</summary>
    public async Task<long> TransactionsOuvertesAsync(CancellationToken ct = default)
    {
        await using var cmd = source.CreateCommand("""
            SELECT count(*) FROM pg_stat_activity
            WHERE datname = current_database() AND backend_type = 'client backend' AND pid <> pg_backend_pid()
              AND application_name = $1 AND xact_start IS NOT NULL AND state <> 'idle'
            """);
        cmd.Parameters.AddWithValue(nomApplicationApi);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<string?> LsnCourantAsync(CancellationToken ct = default)
    {
        await using var cmd = source.CreateCommand("SELECT pg_current_wal_lsn()::text");
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
}

/// <summary>Journal des actions écrit par le serveur lui-même : journal_audit sans compte d'utilisateur (utilisateur_id NULL), comme les ruptures d'intégrité.</summary>
public sealed class JournalSystemeSql(NpgsqlDataSource source) : IJournalSysteme
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task EcrireAsync(string action, string? tableCible, IReadOnlyDictionary<string, object?> details, CancellationToken ct = default)
    {
        await using var cmd = source.CreateCommand("INSERT INTO journal_audit (utilisateur_id, action, table_cible, details) VALUES (NULL, $1, $2, $3)");
        cmd.Parameters.AddWithValue(action);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)tableCible ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(details, Json), NpgsqlDbType = NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// 2e serveur : lectures sur la copie et promotion. Ne promeut JAMAIS une base qui n'est pas en recovery (contrôle avant pg_promote, en plus du refus de PostgreSQL).
/// </summary>
public sealed partial class BasePromotionSql(NpgsqlDataSource source) : IBasePromotion
{
    public const int DelaiPromotionSecondes = 60;

    [GeneratedRegex("^[0-9A-Fa-f]{1,8}/[0-9A-Fa-f]{1,8}$")]
    private static partial Regex FormatLsn();

    public async Task<EtatCopie?> LireCopieAsync(CancellationToken ct = default)
    {
        try
        {
            await using var cx = await source.OpenConnectionAsync(ct);
            bool enRecovery;
            await using (var cmd = new NpgsqlCommand("SELECT pg_is_in_recovery()", cx)) enRecovery = (bool)(await cmd.ExecuteScalarAsync(ct))!;
            DateTime? derniere = null;
            await using (var cmd = new NpgsqlCommand("SELECT pg_last_xact_replay_timestamp()", cx))
                if (await cmd.ExecuteScalarAsync(ct) is DateTime d) derniere = DateTime.SpecifyKind(d, DateTimeKind.Utc);
            // Lignée et timeline : droit du script 163b ; sans lui, null (jamais une valeur devinée)
            long? identifiant = null;
            int? timeline = null;
            try
            {
                await using var cmd = new NpgsqlCommand("SELECT system_identifier FROM pg_control_system()", cx);
                if (await cmd.ExecuteScalarAsync(ct) is long id) identifiant = id;
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege) { }
            try
            {
                await using var cmd = new NpgsqlCommand("SELECT timeline_id FROM pg_control_checkpoint()", cx);
                if (await cmd.ExecuteScalarAsync(ct) is int t) timeline = t;
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege) { }
            return new EtatCopie(enRecovery, identifiant, timeline, derniere);
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            return null;
        }
    }

    public async Task<bool> RetraitVisibleAsync(CancellationToken ct = default)
    {
        await using var cmd = source.CreateCommand("SELECT EXISTS (SELECT 1 FROM serveur_ecarte WHERE leve_le IS NULL AND nature = 'retrait')");
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> LsnRejoueAtteintAsync(string lsn, CancellationToken ct = default)
    {
        if (!FormatLsn().IsMatch(lsn)) return false;   // jamais une valeur non vérifiée vers la base
        await using var cmd = source.CreateCommand("SELECT coalesce(pg_last_wal_replay_lsn() >= $1::pg_lsn, false)");
        cmd.Parameters.AddWithValue(lsn);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<ResultatPromotionBase> PromouvoirAsync(CancellationToken ct = default)
    {
        await using var cx = await source.OpenConnectionAsync(ct);
        await using (var verif = new NpgsqlCommand("SELECT pg_is_in_recovery()", cx))
            if (!(bool)(await verif.ExecuteScalarAsync(ct))!) return ResultatPromotionBase.NonPromue;   // une base en écriture ne se promeut pas
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_promote(true, $1)", cx) { CommandTimeout = DelaiPromotionSecondes + 30 };
            cmd.Parameters.AddWithValue(DelaiPromotionSecondes);
            return await cmd.ExecuteScalarAsync(ct) is true ? ResultatPromotionBase.Promue : ResultatPromotionBase.NonPromue;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege) { return ResultatPromotionBase.DroitAbsent; }
        catch (PostgresException) { return ResultatPromotionBase.NonPromue; }
    }
}

/// <summary>bascule_decision (script 166) : ajout seul, jamais de modification ; source sans verrou de lecture seule.</summary>
public sealed class DecisionsBasculeSql(NpgsqlDataSource source) : IDecisionsBasculeDepot
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> AjouterAsync(DecisionBascule d, int? timelineApres, CancellationToken ct = default)
    {
        await using var cmd = source.CreateCommand("""
            INSERT INTO bascule_decision (decidee_le, cas, timeline_avant, timeline_apres, lsn_final, derniere_donnee_recue, rapports, sondes, motif)
            SELECT $1, $2, $3, $4, $5, $6, $7, $8, $9
            WHERE NOT EXISTS (SELECT 1 FROM bascule_decision WHERE decidee_le = $1 AND cas = $2)
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = DateTime.SpecifyKind(d.DecideLeUtc, DateTimeKind.Utc), NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new NpgsqlParameter { Value = d.Cas, NpgsqlDbType = NpgsqlDbType.Varchar });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)d.TimelineAvant ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)timelineApres ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)d.LsnFinal ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Varchar });
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = d.DerniereTransactionRecueUtc is { } r ? DateTime.SpecifyKind(r, DateTimeKind.Utc) : DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.TimestampTz
        });
        cmd.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(d.Rapports, Json), NpgsqlDbType = NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(d.Sondes, Json), NpgsqlDbType = NpgsqlDbType.Jsonb });
        var motif = $"{d.Verdict} : {d.Motif}";
        cmd.Parameters.Add(new NpgsqlParameter { Value = motif.Length > 500 ? motif[..500] : motif, NpgsqlDbType = NpgsqlDbType.Varchar });
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }
}
