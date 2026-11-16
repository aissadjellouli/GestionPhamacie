using Npgsql;
using Pharma.Application.Catalogue;

namespace Pharma.Infrastructure.Lecture;

/// <summary>
/// Remplaçants en stock d'un produit : même DCI, et produits de sa liste blanche (116). PrixPublic porte le prix servi (PPA du lot qui
/// serait servi, sinon prix du catalogue, SHP compris), pour proposer d'abord le moins cher (2026-11-14).
/// </summary>
public sealed class AlternativesLecture(NpgsqlDataSource db) : IAlternativesLecture
{
    public async Task<IReadOnlyList<ProduitAlternatif>> AlternativesAsync(long produitId, CancellationToken ct = default)
    {
        string? dosage, forme;
        long? dciRef;
        await using (var refCmd = db.CreateCommand("SELECT dosage, forme, dci_id FROM produit WHERE id = $1"))
        {
            refCmd.Parameters.AddWithValue(produitId);
            await using var rr = await refCmd.ExecuteReaderAsync(ct);
            if (!await rr.ReadAsync(ct)) return [];
            (dosage, forme, dciRef) = (rr.IsDBNull(0) ? null : rr.GetString(0), rr.IsDBNull(1) ? null : rr.GetString(1), rr.IsDBNull(2) ? null : rr.GetInt64(2));
        }
        var blanche = (await EquivalentsComptoirSql.ListeBlancheAsync(db, [produitId], ct)).Select(l => l.Remplacant).ToHashSet();

        await using var cmd = db.CreateCommand(EquivalentsComptoirSql.Selection + """
             JOIN produit ref ON ref.id = $1
            WHERE (p.dci_id = ref.dci_id OR p.id = ANY($2)) AND p.id <> ref.id AND p.actif AND COALESCE(e.stock, 0) > 0
            """);
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.AddWithValue(blanche.ToArray());

        var liste = new List<ProduitAlternatif>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var e = EquivalentsComptoirSql.Lire(r);
            var exact = dciRef is not null && e.DciId == dciRef
                && string.Equals(e.Dosage, dosage, StringComparison.Ordinal) && string.Equals(e.Forme, forme, StringComparison.Ordinal);
            liste.Add(new ProduitAlternatif(e.Id, e.NomCommercial, e.PrixServi, e.Stock, exact, e.Dosage, e.Forme, e.Laboratoire,
                blanche.Contains(e.Id), e.FabricationLocale));
        }
        return liste;
    }
}
