using Npgsql;
using NpgsqlTypes;

namespace Pharma.Infrastructure.Achats;

/// <summary>
/// Relevé de la dette envers les grossistes (039_suivi_dettes.sql) : chaque changement de fournisseur.solde y est inscrit
/// par un déclencheur. Avant de modifier la dette, le logiciel précise ici le contexte du mouvement, dans la même transaction.
/// </summary>
internal static class ReleveGrossiste
{
    public const string Reception = "reception";
    public const string Reglement = "reglement";
    public const string Avoir = "avoir";
    public const string AnnulationReglement = "annulation_reglement";

    public static async Task ContexteAsync(NpgsqlConnection cx, NpgsqlTransaction tx, string type, long? utilisateurId, string? reference, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT dette_fournisseur_contexte($1, $2, $3)", cx, tx);
        cmd.Parameters.AddWithValue(type);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)utilisateurId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)reference ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
