using Npgsql;
using NpgsqlTypes;

namespace Pharma.Infrastructure.Caisse;

/// <summary>
/// Relevé de la dette des patients (038_releve_credit.sql) : chaque changement de patient.solde_credit y est inscrit par
/// un déclencheur. Avant de modifier la dette, le logiciel précise ici le contexte du mouvement (type, vente, employé,
/// poste, référence), dans la même transaction.
/// </summary>
internal static class ReleveCredit
{
    public const string VenteCredit = "vente_credit";
    public const string Remboursement = "remboursement";
    public const string Retour = "retour";
    public const string Annulation = "annulation";
    public const string Correction = "correction";

    public static async Task ContexteAsync(NpgsqlConnection cx, NpgsqlTransaction tx, string type, long? venteId, long? utilisateurId,
        string? poste, string? reference, CancellationToken ct, long? sessionCaisseId = null)
    {
        // Poste non fourni : celui de la caisse où l'opération est enregistrée
        await using var cmd = new NpgsqlCommand(
            "SELECT credit_contexte($1, $2, $3, COALESCE($4, (SELECT poste FROM session_caisse WHERE id = $6)), $5)", cx, tx);
        cmd.Parameters.AddWithValue(type);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)venteId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)utilisateurId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)poste ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)reference ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)sessionCaisseId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
