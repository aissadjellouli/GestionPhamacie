namespace Pharma.Application.Catalogue;

/// <summary>
/// Produit de même DCI disponible en stock (PrixPublic : prix servi, SHP compris). <see cref="Exact"/> : même dosage et même forme (substituable) ;
/// sinon autre dosage ou autre forme, à valider par le pharmacien.
/// </summary>
public sealed record ProduitAlternatif(long ProduitId, string NomCommercial, decimal PrixPublic, int Stock,
    bool Exact = true, string? Dosage = null, string? Forme = null, string? Laboratoire = null);

public interface IAlternativesLecture
{
    /// <summary>Tous les produits actifs de même DCI avec du stock (exacts ou non).</summary>
    Task<IReadOnlyList<ProduitAlternatif>> AlternativesAsync(long produitId, CancellationToken ct = default);
}

/// <summary>
/// Équivalents proposés quand un produit manque : les équivalents exacts s'il y en a,
/// sinon la même DCI dans un autre dosage ou une autre forme.
/// </summary>
public sealed class ListerAlternatives(IAlternativesLecture alternatives)
{
    public async Task<IReadOnlyList<ProduitAlternatif>> ExecuterAsync(long produitId, CancellationToken ct = default)
    {
        var tous = await alternatives.AlternativesAsync(produitId, ct);
        var exacts = tous.Where(a => a.Exact).ToList();
        // Le moins cher d'abord (prix servi : lot qui serait servi, SHP compris), puis le plus de stock (2026-11-14)
        return (exacts.Count > 0 ? exacts : tous.Where(a => !a.Exact))
            .OrderBy(a => a.PrixPublic).ThenByDescending(a => a.Stock).ToList();
    }
}
