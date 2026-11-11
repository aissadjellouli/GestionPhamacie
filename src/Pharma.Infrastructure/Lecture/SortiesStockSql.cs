using Npgsql;
using Pharma.Application.Stock;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Lecture;

/// <summary>Sortie de boîtes périmées ou cassées : lot, mouvement (registre tenu par le déclencheur), boîtes détruites.</summary>
public sealed class SortiesStockSql(NpgsqlDataSource db) : ISortiesStock
{
    private const string Requete = """
        SELECT l.id, l.produit_id, p.nom_commercial, l.numero_lot, l.date_peremption, l.quantite_disponible, l.bloque,
               p.classe IN ('psychotrope', 'stupefiant')
        FROM lot l JOIN produit p ON p.id = l.produit_id
        """;

    private static LotEnStock Lire(NpgsqlDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
        r.GetFieldValue<DateOnly>(4), r.GetInt32(5), r.GetBoolean(6), r.GetBoolean(7));

    public async Task<IReadOnlyList<LotEnStock>> LotsAsync(long produitId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(Requete + " WHERE l.produit_id = $1 AND l.quantite_disponible > 0 ORDER BY l.date_peremption, l.id");
        cmd.Parameters.AddWithValue(produitId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<LotEnStock>();
        while (await r.ReadAsync(ct)) liste.Add(Lire(r));
        return liste;
    }

    public async Task<LotEnStock?> LotAsync(long lotId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(Requete + " WHERE l.id = $1");
        cmd.Parameters.AddWithValue(lotId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Lire(r) : null;
    }

    public async Task<int> SortirAsync(SortieStockAEcrire s, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        await using (var verrou = new NpgsqlCommand(
            "UPDATE lot SET quantite_disponible = quantite_disponible - $2 WHERE id = $1 AND quantite_disponible >= $2", cx, tx))
        {
            verrou.Parameters.AddWithValue(s.LotId);
            verrou.Parameters.AddWithValue(s.Quantite);
            if (await verrou.ExecuteNonQueryAsync(ct) == 0) throw new RegleMetierException("Le stock du lot vient de changer : rechargez la liste.");
        }
        // Mouvement négatif : le déclencheur inscrit au registre la destruction (périmé) ou la casse d'un psychotrope
        await using (var mvt = new NpgsqlCommand("""
            INSERT INTO mouvement_stock (lot_id, type, quantite, reference, utilisateur_id) VALUES ($1, $2, $3, $4, $5)
            """, cx, tx))
        {
            mvt.Parameters.AddWithValue(s.LotId);
            mvt.Parameters.AddWithValue(s.Motif);
            mvt.Parameters.AddWithValue(-s.Quantite);
            mvt.Parameters.AddWithValue(s.Reference);
            mvt.Parameters.AddWithValue(s.UtilisateurId);
            await mvt.ExecuteNonQueryAsync(ct);
        }
        // Boîtes identifiées du lot détruites, les plus anciennes d'abord (autant que de boîtes sorties)
        int detruites;
        await using (var boites = new NpgsqlCommand("""
            WITH choisies AS (
                SELECT id FROM boite WHERE lot_id = $1 AND statut = 'en_stock' ORDER BY id LIMIT $2 FOR UPDATE),
            evenements AS (
                INSERT INTO boite_evenement (boite_id, type, utilisateur_id, commentaire)
                SELECT id, CASE WHEN $5 = 'echantillon' THEN 'echantillon' ELSE 'destruction' END, $3, $4 FROM choisies)
            UPDATE boite SET statut = CASE WHEN $5 = 'echantillon' THEN 'donnee' ELSE 'detruite' END WHERE id IN (SELECT id FROM choisies)
            """, cx, tx))
        {
            boites.Parameters.AddWithValue(s.LotId);
            boites.Parameters.AddWithValue(s.Quantite);
            boites.Parameters.AddWithValue(s.UtilisateurId);
            boites.Parameters.AddWithValue(s.Reference);
            boites.Parameters.AddWithValue(s.Motif);
            detruites = await boites.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return detruites;
    }
}
