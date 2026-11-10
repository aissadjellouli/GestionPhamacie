using Npgsql;
using Pharma.Application.Suivi;

namespace Pharma.Infrastructure.Suivi;

/// <summary>Sommes dues de tous les tiers (vue v_suivi_dettes, 039_suivi_dettes.sql) et délai d'alerte.</summary>
public sealed class SuiviDettesSql(NpgsqlDataSource db) : ISuiviDettes
{
    public async Task<IReadOnlyList<SommeDue>> SommesDuesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT sens, categorie, tiers_id, tiers, libelle, date_reference, echeance, montant, rejetee FROM v_suivi_dettes");
        var liste = new List<SommeDue>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new SommeDue(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.GetString(3), r.GetString(4),
                r.GetFieldValue<DateOnly>(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateOnly>(6), r.GetDecimal(7), r.GetBoolean(8)));
        return liste;
    }

    public async Task<int> DelaiAlerteAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT COALESCE((SELECT delai_alerte_dette_jours FROM parametre_caisse WHERE id = 1), 30)");
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task ModifierDelaiAlerteAsync(int jours, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE parametre_caisse SET delai_alerte_dette_jours = $1 WHERE id = 1");
        cmd.Parameters.AddWithValue(jours);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
