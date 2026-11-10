using Npgsql;
using Pharma.Application.Catalogue;
using Pharma.Domain.Achats;
using Pharma.Domain.Stock;

namespace Pharma.Infrastructure.Lecture;

public sealed class LotsLecture(NpgsqlDataSource db) : ILotsLecture
{
    private const string Sql = """
        SELECT l.id, l.produit_id, l.reception_id, l.fournisseur_id, l.numero_lot, l.date_peremption,
               l.quantite_disponible, l.prix_achat_ht, l.bloque, l.motif_blocage, l.date_a_verifier,
               f.raison_sociale
        FROM lot l
        JOIN fournisseur f ON f.id = l.fournisseur_id
        WHERE l.produit_id = $1 AND l.quantite_disponible > 0
        """;

    public async Task<IReadOnlyList<Lot>> LotsEnStockAsync(long produitId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(Sql);
        cmd.Parameters.AddWithValue(produitId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var lots = new List<Lot>();
        while (await r.ReadAsync(ct))
        {
            var fournisseurId = r.GetInt64(3);
            lots.Add(new Lot
            {
                Id = r.GetInt64(0),
                ProduitId = r.GetInt64(1),
                ReceptionId = r.GetInt64(2),
                FournisseurId = fournisseurId,
                NumeroLot = r.GetString(4),
                DatePeremption = r.GetFieldValue<DateOnly>(5),
                QuantiteDisponible = r.GetInt32(6),
                PrixAchatHt = r.GetDecimal(7),
                Bloque = r.GetBoolean(8),
                MotifBlocage = r.IsDBNull(9) ? null : r.GetString(9),
                DateAVerifier = r.GetBoolean(10),
                Fournisseur = new Fournisseur { Id = fournisseurId, RaisonSociale = r.GetString(11) }
            });
        }
        return lots;
    }
}
