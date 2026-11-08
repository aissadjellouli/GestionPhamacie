using Npgsql;
using Pharma.Application.Alertes;

namespace Pharma.Infrastructure.Alertes;

public sealed class AlertesEcriture(NpgsqlDataSource db) : IAlertesEcriture
{
    public async Task<bool> ReporterAsync(long alerteId, DateOnly jusquAu, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE alerte
            SET statut = 'reportee', reportee_jusqu_au = $2, mise_a_jour_le = now()
            WHERE id = $1 AND statut IN ('nouvelle', 'vue')
            """);
        cmd.Parameters.AddWithValue(alerteId);
        cmd.Parameters.AddWithValue(jusquAu);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> TraiterAsync(long alerteId, string? actionPrise, long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE alerte
            SET statut = 'traitee', action_prise = $2, traitee_par = $3, traitee_le = now(), mise_a_jour_le = now()
            WHERE id = $1 AND statut IN ('nouvelle', 'vue', 'reportee')
            """);
        cmd.Parameters.AddWithValue(alerteId);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)actionPrise ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(utilisateurId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
