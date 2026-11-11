using Npgsql;
using Pharma.Application.Alertes;

namespace Pharma.Infrastructure.Alertes;

public sealed class AlertesEcriture(NpgsqlDataSource db) : IAlertesEcriture
{
    public async Task<bool> ReporterAsync(long alerteId, DateOnly jusquAu, bool titulaire, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE alerte
            SET statut = 'reportee', reportee_jusqu_au = $2, mise_a_jour_le = now()
            WHERE id = $1 AND statut IN ('nouvelle', 'vue') AND ($3 OR type <> ALL ($4))
            """);
        cmd.Parameters.AddWithValue(alerteId);
        cmd.Parameters.AddWithValue(jusquAu);
        cmd.Parameters.AddWithValue(titulaire);
        cmd.Parameters.AddWithValue(AlertesReservees.Types.ToArray());
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> TraiterAsync(long alerteId, string? actionPrise, long utilisateurId, bool titulaire, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE alerte
            SET statut = 'traitee', action_prise = $2, traitee_par = $3, traitee_le = now(), mise_a_jour_le = now()
            WHERE id = $1 AND statut IN ('nouvelle', 'vue', 'reportee') AND ($4 OR type <> ALL ($5))
            """);
        cmd.Parameters.AddWithValue(alerteId);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)actionPrise ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(utilisateurId);
        cmd.Parameters.AddWithValue(titulaire);
        cmd.Parameters.AddWithValue(AlertesReservees.Types.ToArray());
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
