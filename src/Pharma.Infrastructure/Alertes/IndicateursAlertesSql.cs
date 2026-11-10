using Npgsql;
using Pharma.Application.Alertes;

namespace Pharma.Infrastructure.Alertes;

/// <summary>Lots à péremption proche par horizon (30, 60, 180 jours), comptés en une lecture.</summary>
public sealed class IndicateursAlertesSql(NpgsqlDataSource db) : IIndicateursAlertes
{
    public async Task<IReadOnlyList<HorizonPeremption>> HorizonsAsync(DateOnly aujourdhui, IReadOnlyList<int> jours, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT h.jours, count(l.id)::int, COALESCE(SUM(l.quantite_disponible), 0)::int,
                   COALESCE(SUM(l.quantite_disponible * COALESCE(l.prix_achat_ht, 0)), 0)
            FROM unnest($2::int[]) AS h(jours)
            LEFT JOIN lot l ON l.quantite_disponible > 0 AND NOT l.bloque
                           AND l.date_peremption > $1 AND l.date_peremption <= $1 + h.jours
            GROUP BY h.jours
            ORDER BY h.jours
            """);
        cmd.Parameters.AddWithValue(aujourdhui);
        cmd.Parameters.AddWithValue(jours.ToArray());
        var liste = new List<HorizonPeremption>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(new HorizonPeremption(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetDecimal(3)));
        return liste;
    }
}
