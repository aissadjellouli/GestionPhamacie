using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Commun;

namespace Pharma.Infrastructure.Securite;

/// <summary>Écriture et lecture de journal_audit (non modifiable, déclencheur trg_journal_immuable).</summary>
public sealed class JournalSql(NpgsqlDataSource db) : IJournal, IJournalLecture
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task EcrireAsync(long utilisateurId, string action, string? tableCible, long? enregistrementId,
        IReadOnlyDictionary<string, object?> details, string? permissionCode, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO journal_audit (utilisateur_id, action, table_cible, enregistrement_id, details, permission_code)
            VALUES ($1, $2, $3, $4, $5, $6)
            """);
        AjouterParametres(cmd, utilisateurId, action, tableCible, enregistrementId, details, permissionCode);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Même insertion, dans la transaction d'une autre écriture.</summary>
    internal static async Task EcrireAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long utilisateurId, string action,
        string? tableCible, long? enregistrementId, IReadOnlyDictionary<string, object?> details, string? permissionCode,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO journal_audit (utilisateur_id, action, table_cible, enregistrement_id, details, permission_code)
            VALUES ($1, $2, $3, $4, $5, $6)
            """, cx, tx);
        AjouterParametres(cmd, utilisateurId, action, tableCible, enregistrementId, details, permissionCode);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AjouterParametres(NpgsqlCommand cmd, long utilisateurId, string action, string? tableCible,
        long? enregistrementId, IReadOnlyDictionary<string, object?> details, string? permissionCode)
    {
        cmd.Parameters.AddWithValue(utilisateurId);
        cmd.Parameters.AddWithValue(action);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)tableCible ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)enregistrementId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(details, Json), NpgsqlDbType = NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)permissionCode ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
    }

    public async Task<IReadOnlyList<EntreeJournal>> DernieresAsync(int nombre, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT j.cree_le, COALESCE(u.nom_complet, '—'), j.action, j.details::text, a.nom_complet, j.permission_code
            FROM journal_audit j
            LEFT JOIN utilisateur u ON u.id = j.utilisateur_id
            LEFT JOIN utilisateur a ON a.id = j.autorise_par
            ORDER BY j.cree_le DESC, j.id DESC
            LIMIT $1
            """);
        cmd.Parameters.AddWithValue(nombre);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<EntreeJournal>();
        while (await r.ReadAsync(ct))
            liste.Add(new EntreeJournal(r.GetDateTime(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5)));
        return liste;
    }
}
