using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Administration;

namespace Pharma.Infrastructure.Administration;

/// <summary>Seuils de stock par produit (produit.stock_minimum, vue v_etat_stock de 020_seuils_stock.sql).</summary>
public sealed class SeuilsStockSql(NpgsqlDataSource db) : ISeuilsStock
{
    private const string Requete = """
        SELECT p.id, p.nom_commercial, COALESCE(e.stock, 0)::int, COALESCE(e.conso_jour, 0),
               CEIL(COALESCE(c.conso_jour, 0) * (prm.delai_fournisseur_jours + prm.jours_securite))::int,
               p.stock_minimum, COALESCE(e.statut, 'ok')
        FROM produit p
        CROSS JOIN (SELECT * FROM parametre_alerte WHERE id = 1) prm
        LEFT JOIN v_consommation c ON c.produit_id = p.id
        LEFT JOIN v_etat_stock e ON e.produit_id = p.id
        """;

    public async Task<SeuilStockProduit?> ProduitAsync(long produitId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"{Requete} WHERE p.id = $1");
        cmd.Parameters.AddWithValue(produitId);
        return (await Lire(cmd, ct)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<SeuilStockProduit>> ManuelsAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"{Requete} WHERE p.stock_minimum IS NOT NULL AND p.actif ORDER BY p.nom_commercial");
        return await Lire(cmd, ct);
    }

    public async Task DefinirAsync(long produitId, int? seuil, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE produit SET stock_minimum = $2 WHERE id = $1");
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)seuil ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyList<SeuilStockProduit>> Lire(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<SeuilStockProduit>();
        while (await r.ReadAsync(ct))
            liste.Add(new SeuilStockProduit(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetDecimal(3), r.GetInt32(4),
                r.IsDBNull(5) ? null : r.GetInt32(5), r.GetString(6)));
        return liste;
    }
}
