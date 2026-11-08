using Npgsql;
using Pharma.Application.Catalogue;

namespace Pharma.Infrastructure.Lecture;

/// <summary>Produits de même DCI avec du stock ; « exact » = même dosage et même forme (comme la vue v_alternatives).</summary>
public sealed class AlternativesLecture(NpgsqlDataSource db) : IAlternativesLecture
{
    public async Task<IReadOnlyList<ProduitAlternatif>> AlternativesAsync(long produitId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT alt.id, alt.nom_commercial, alt.prix_public, e.stock::int,
                   (alt.dosage IS NOT DISTINCT FROM p.dosage AND alt.forme IS NOT DISTINCT FROM p.forme) AS exact,
                   alt.dosage, alt.forme
            FROM produit p
            JOIN produit alt    ON alt.dci_id = p.dci_id AND alt.id <> p.id AND alt.actif
            JOIN v_etat_stock e ON e.produit_id = alt.id
            WHERE p.id = $1 AND e.stock > 0
            ORDER BY exact DESC, e.stock DESC, alt.nom_commercial
            """);
        cmd.Parameters.AddWithValue(produitId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<ProduitAlternatif>();
        while (await r.ReadAsync(ct))
            liste.Add(new ProduitAlternatif(r.GetInt64(0), r.GetString(1), r.GetDecimal(2), r.GetInt32(3), r.GetBoolean(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
        return liste;
    }
}
