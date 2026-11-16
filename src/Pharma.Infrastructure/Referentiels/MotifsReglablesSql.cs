using Npgsql;
using Pharma.Application.Referentiels;
using Pharma.Domain.Referentiels;

namespace Pharma.Infrastructure.Referentiels;

/// <summary>Table motif_reglable (094_motifs_reglables.sql) : jamais de suppression.</summary>
public sealed class MotifsReglablesSql(NpgsqlDataSource db) : IMotifsReglables
{
    public async Task<IReadOnlyList<MotifReglable>> ListerAsync(string? liste, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, liste, code, libelle, actif, systeme, ordre, effet, detail, modele_code_central FROM motif_reglable
            WHERE $1::text IS NULL OR liste = $1 ORDER BY liste, ordre, libelle
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)liste ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        var resultat = new List<MotifReglable>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            resultat.Add(new MotifReglable(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetBoolean(5), r.GetInt32(6),
                r.GetString(7), r.GetString(8), r.GetBoolean(9)));
        return resultat;
    }

    public Task<long> CreerAsync(string liste, string code, string libelle, CancellationToken ct = default) =>
        CreerAsync(liste, code, libelle, "aucun", "aucun", ct);

    /// <summary>Effet et détail (tiroir, script 096) : fixés à la création.</summary>
    public async Task<long> CreerAsync(string liste, string code, string libelle, string effet, string detail, CancellationToken ct = default)
    {
        // Nouveau motif placé avant « Autre » (ordre 999)
        await using var cmd = db.CreateCommand("""
            INSERT INTO motif_reglable (liste, code, libelle, ordre, effet, detail)
            VALUES ($1, $2, $3, (SELECT LEAST(COALESCE(max(ordre) FILTER (WHERE ordre < 999), 0) + 10, 998) FROM motif_reglable WHERE liste = $1), $4, $5)
            RETURNING id
            """);
        cmd.Parameters.AddWithValue(liste);
        cmd.Parameters.AddWithValue(code);
        cmd.Parameters.AddWithValue(libelle);
        cmd.Parameters.AddWithValue(effet);
        cmd.Parameters.AddWithValue(detail);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> ModifierAsync(long id, string libelle, bool actif, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE motif_reglable SET libelle = $2, actif = $3 OR systeme, modifie_le = now() WHERE id = $1
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(libelle);
        cmd.Parameters.AddWithValue(actif);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
