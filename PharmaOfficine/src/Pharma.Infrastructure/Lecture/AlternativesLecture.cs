using Npgsql;
using Pharma.Application.Catalogue;

namespace Pharma.Infrastructure.Lecture;

/// <summary>Vue v_alternatives : même DCI, dosage et forme, avec du stock.</summary>
public sealed class AlternativesLecture(NpgsqlDataSource db) : IAlternativesLecture
{
    public async Task<IReadOnlyList<ProduitAlternatif>> AlternativesAsync(long produitId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT a.alternative_id, a.alternative, p.prix_public, a.stock_alternative::int
            FROM v_alternatives a
            JOIN produit p ON p.id = a.alternative_id
            WHERE a.produit_id = $1
            ORDER BY a.stock_alternative DESC, a.alternative
            """);
        cmd.Parameters.AddWithValue(produitId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<ProduitAlternatif>();
        while (await r.ReadAsync(ct))
            liste.Add(new ProduitAlternatif(r.GetInt64(0), r.GetString(1), r.GetDecimal(2), r.GetInt32(3)));
        return liste;
    }
}
