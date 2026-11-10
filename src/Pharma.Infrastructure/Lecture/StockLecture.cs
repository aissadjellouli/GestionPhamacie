using Npgsql;
using Pharma.Application.Stock;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Lecture;

public sealed class StockLecture(NpgsqlDataSource db) : IStockLecture
{
    public async Task<IndicateursStock> IndicateursAsync(CancellationToken ct = default)
    {
        // Même périmètre que v_etat_stock : le périmé ne compte pas dans le stock vendable
        const string sql = """
            SELECT (SELECT count(*) FROM v_etat_stock WHERE stock > 0)::int,
                   (SELECT COALESCE(SUM(v.valeur), 0)                       -- prix réel de chaque boîte (021_prix_boite.sql)
                    FROM lot l JOIN v_valeur_lot v ON v.lot_id = l.id
                    WHERE l.quantite_disponible > 0 AND l.date_peremption > current_date),
                   (SELECT count(*) FROM lot WHERE date_a_verifier AND quantite_disponible > 0)::int
            """;
        await using var cmd = db.CreateCommand(sql);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new IndicateursStock(r.GetInt32(0), r.GetDecimal(1), r.GetInt32(2));
    }

    public async Task<IReadOnlyList<LigneStock>> LignesAsync(CancellationToken ct = default)
    {
        const string sql = """
            WITH lots AS (
                SELECT l.produit_id,
                       count(*)::int                                              AS nb_lots,
                       MIN(l.date_peremption) FILTER (WHERE l.date_peremption > current_date) AS premiere,
                       array_agg(DISTINCT f.raison_sociale ORDER BY f.raison_sociale) AS grossistes,
                       array_agg(l.numero_lot ORDER BY l.date_peremption)          AS numeros
                FROM lot l
                JOIN fournisseur f ON f.id = l.fournisseur_id
                WHERE l.quantite_disponible > 0
                GROUP BY l.produit_id
            ),
            proche AS (
                SELECT DISTINCT produit_id FROM v_risque_peremption
                WHERE niveau IN ('perime', 'rouge', 'orange')
            )
            SELECT e.produit_id, p.nom_commercial, d.nom, p.code_barre, p.classe, e.stock::int,
                   e.couverture_jours::int, lots.premiere, COALESCE(lots.nb_lots, 0),
                   COALESCE(lots.grossistes, '{}'), COALESCE(lots.numeros, '{}'),
                   e.statut, proche.produit_id IS NOT NULL,
                   p.emplacement_id, ev.code, p.reserve_id, er.code
            FROM v_etat_stock e
            JOIN produit p       ON p.id = e.produit_id
            LEFT JOIN dci d      ON d.id = p.dci_id
            LEFT JOIN lots       ON lots.produit_id = e.produit_id
            LEFT JOIN proche     ON proche.produit_id = e.produit_id
            LEFT JOIN emplacement ev ON ev.id = p.emplacement_id
            LEFT JOIN emplacement er ON er.id = p.reserve_id
            ORDER BY p.nom_commercial
            """;
        await using var cmd = db.CreateCommand(sql);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<LigneStock>();
        while (await r.ReadAsync(ct))
            liste.Add(new LigneStock(
                r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), CodesMetier.ClasseDepuisCode(r.GetString(4)),
                r.GetInt32(5), r.IsDBNull(6) ? null : r.GetInt32(6),
                r.IsDBNull(7) ? null : r.GetFieldValue<DateOnly>(7), r.GetInt32(8),
                r.GetFieldValue<string[]>(9), r.GetFieldValue<string[]>(10),
                r.GetString(11), r.GetBoolean(12),
                r.IsDBNull(13) ? null : r.GetInt64(13), r.IsDBNull(14) ? null : r.GetString(14),
                r.IsDBNull(15) ? null : r.GetInt64(15), r.IsDBNull(16) ? null : r.GetString(16)));
        return liste;
    }
}
