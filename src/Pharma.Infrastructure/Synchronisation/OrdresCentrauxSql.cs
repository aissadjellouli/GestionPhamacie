using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Synchronisation;

namespace Pharma.Infrastructure.Synchronisation;

/// <summary>Ordres reçus du serveur central (script 145) : lecture, liste paginée, changement d'état par sync_ordre_noter.</summary>
public sealed class OrdresCentrauxSql(NpgsqlDataSource db) : IOrdresCentrauxDepot
{
    private const string Colonnes = """
        id, type, cible, attendu::text, donnees::text, auteur, cree_le, expire_le, statut, motif, recu_le, traite_le, traite_par
        """;

    public async Task<IReadOnlyList<OrdreRecu>> EnCoursAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Colonnes} FROM sync_ordre_recu WHERE statut IN ('a_appliquer', 'a_confirmer') ORDER BY recu_le, id");
        return await LireListeAsync(cmd, ct);
    }

    public async Task<OrdreRecu?> LireAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Colonnes} FROM sync_ordre_recu WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        return (await LireListeAsync(cmd, ct)).FirstOrDefault();
    }

    public async Task<PageOrdresRecus> ListerAsync(string? statut, int page, int taille, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            SELECT {Colonnes} FROM sync_ordre_recu WHERE ($1::text IS NULL OR statut = $1)
            ORDER BY (statut = 'a_confirmer') DESC, recu_le DESC, id LIMIT $2 OFFSET $3
            """, cnx);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)statut ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(taille);
        cmd.Parameters.AddWithValue((page - 1) * taille);
        var ordres = await LireListeAsync(cmd, ct);
        await using var compte = new NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE $1::text IS NULL OR statut = $1), count(*) FILTER (WHERE statut = 'a_confirmer') FROM sync_ordre_recu
            """, cnx);
        compte.Parameters.Add(new NpgsqlParameter { Value = (object?)statut ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await using var r = await compte.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new PageOrdresRecus(ordres, (int)r.GetInt64(0), (int)r.GetInt64(1));
    }

    public async Task NoterAsync(Guid id, string statut, string? motif, long? par, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_ordre_noter($1, $2, $3, $4)");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(statut);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)motif ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)par ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> BaseEnLectureSeuleAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT pg_is_in_recovery()");
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<IReadOnlyList<OrdreRecu>> LireListeAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<OrdreRecu>();
        while (await r.ReadAsync(ct))
            liste.Add(new OrdreRecu(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                Utc(r.GetDateTime(6)), Utc(r.GetDateTime(7)), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), Utc(r.GetDateTime(10)),
                r.IsDBNull(11) ? null : Utc(r.GetDateTime(11)), r.IsDBNull(12) ? null : r.GetInt64(12)));
        return liste;
    }

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
}
