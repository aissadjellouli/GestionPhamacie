using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Securite;

namespace Pharma.Infrastructure.Securite;

/// <summary>Code comptoir des employés (utilisateur.pin_hash), essais manqués et blocage (022_caisse_partagee.sql).</summary>
public sealed class CodesComptoirSql(NpgsqlDataSource db) : ICodesComptoir
{
    private const string Select = """
        SELECT id, identifiant, nom_complet, pin_hash, code_comptoir_essais, code_comptoir_bloque_jusqu FROM utilisateur
        """;

    public async Task<CompteComptoir?> TrouverActifAsync(string identifiant, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"{Select} WHERE identifiant = $1 AND actif");
        cmd.Parameters.AddWithValue(identifiant);
        return (await Lire(cmd, ct)).FirstOrDefault();
    }

    public async Task<CompteComptoir?> ParIdAsync(long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"{Select} WHERE id = $1");
        cmd.Parameters.AddWithValue(utilisateurId);
        return (await Lire(cmd, ct)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<CompteComptoir>> AvecCodeAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"{Select} WHERE actif AND pin_hash IS NOT NULL ORDER BY nom_complet");
        return await Lire(cmd, ct);
    }

    // Ligne verrouillée par l'UPDATE : les essais simultanés passent un par un. Blocage expiré : le compte repart à 1.
    public async Task<int?> ReserverEssaiAsync(long utilisateurId, DateTime maintenant, int essaisMax, DateTime bloqueJusqu, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE utilisateur
            SET code_comptoir_essais = CASE WHEN code_comptoir_bloque_jusqu IS NOT NULL THEN 1 ELSE code_comptoir_essais + 1 END,
                code_comptoir_bloque_jusqu = CASE
                    WHEN (CASE WHEN code_comptoir_bloque_jusqu IS NOT NULL THEN 1 ELSE code_comptoir_essais + 1 END) >= $2 THEN $3
                    ELSE NULL END
            WHERE id = $1 AND (code_comptoir_bloque_jusqu IS NULL OR code_comptoir_bloque_jusqu <= $4)
            RETURNING code_comptoir_essais
            """);
        cmd.Parameters.AddWithValue(utilisateurId);
        cmd.Parameters.AddWithValue(essaisMax);
        cmd.Parameters.Add(new NpgsqlParameter { Value = bloqueJusqu, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new NpgsqlParameter { Value = maintenant, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        return await cmd.ExecuteScalarAsync(ct) is int n ? n : null;
    }

    public async Task EnregistrerEssaiAsync(long utilisateurId, int essais, DateTime? bloqueJusqu, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE utilisateur SET code_comptoir_essais = $2, code_comptoir_bloque_jusqu = $3 WHERE id = $1");
        cmd.Parameters.AddWithValue(utilisateurId);
        cmd.Parameters.AddWithValue(essais);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)bloqueJusqu ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DefinirAsync(long utilisateurId, string? codeHash, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "UPDATE utilisateur SET pin_hash = $2, code_comptoir_essais = 0, code_comptoir_bloque_jusqu = NULL WHERE id = $1");
        cmd.Parameters.AddWithValue(utilisateurId);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)codeHash ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyList<CompteComptoir>> Lire(NpgsqlCommand cmd, CancellationToken ct)
    {
        var liste = new List<CompteComptoir>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new CompteComptoir(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetInt32(4), r.IsDBNull(5) ? null : r.GetDateTime(5)));
        return liste;
    }
}
