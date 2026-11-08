using Npgsql;
using Pharma.Application.Alertes;

namespace Pharma.Infrastructure.Lecture;

public sealed class AlertesLecture(NpgsqlDataSource db) : IAlertesLecture
{
    public async Task<IReadOnlyList<Alerte>> ListerOuvertesAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT a.id, a.type, a.niveau, a.produit_id, a.lot_id, a.message, a.valeur_dzd, a.statut,
                   a.mise_a_jour_le, p.nom_commercial, l.numero_lot
            FROM alerte a
            JOIN produit p  ON p.id = a.produit_id
            LEFT JOIN lot l ON l.id = a.lot_id
            WHERE a.statut IN ('nouvelle', 'vue')
            ORDER BY CASE a.niveau WHEN 'urgent' THEN 0 WHEN 'attention' THEN 1 ELSE 2 END,
                     a.valeur_dzd DESC NULLS LAST, a.mise_a_jour_le DESC
            """;
        await using var cmd = db.CreateCommand(sql);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<Alerte>();
        while (await r.ReadAsync(ct))
            liste.Add(new Alerte(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetInt64(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetDecimal(6), r.GetString(7), r.GetDateTime(8),
                r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10)));
        return liste;
    }
}
