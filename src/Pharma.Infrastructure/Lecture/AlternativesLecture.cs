using Npgsql;
using Pharma.Application.Catalogue;

namespace Pharma.Infrastructure.Lecture;

/// <summary>
/// Équivalents en stock d'un produit (même DCI) : PrixPublic porte le prix servi (PPA du lot qui serait servi, sinon prix du catalogue,
/// SHP compris), pour proposer d'abord le moins cher (2026-11-14).
/// </summary>
public sealed class AlternativesLecture(NpgsqlDataSource db) : IAlternativesLecture
{
    public async Task<IReadOnlyList<ProduitAlternatif>> AlternativesAsync(long produitId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(EquivalentsComptoirSql.Selection + """
             JOIN produit ref ON ref.id = $1
            WHERE p.dci_id = ref.dci_id AND p.id <> ref.id AND p.actif AND COALESCE(e.stock, 0) > 0
            """);
        cmd.Parameters.AddWithValue(produitId);
        await using (var refCmd = db.CreateCommand("SELECT dosage, forme FROM produit WHERE id = $1"))
        {
            refCmd.Parameters.AddWithValue(produitId);
            await using var rr = await refCmd.ExecuteReaderAsync(ct);
            if (!await rr.ReadAsync(ct)) return [];
            var (dosage, forme) = (rr.IsDBNull(0) ? null : rr.GetString(0), rr.IsDBNull(1) ? null : rr.GetString(1));
            await rr.DisposeAsync();
            var liste = new List<ProduitAlternatif>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var e = EquivalentsComptoirSql.Lire(r);
                liste.Add(new ProduitAlternatif(e.Id, e.NomCommercial, e.PrixServi, e.Stock,
                    string.Equals(e.Dosage, dosage, StringComparison.Ordinal) && string.Equals(e.Forme, forme, StringComparison.Ordinal), e.Dosage, e.Forme, e.Laboratoire));
            }
            return liste;
        }
    }
}
