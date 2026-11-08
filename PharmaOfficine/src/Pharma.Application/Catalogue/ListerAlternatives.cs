namespace Pharma.Application.Catalogue;

/// <summary>Produit de même DCI, dosage et forme, disponible en stock.</summary>
public sealed record ProduitAlternatif(long ProduitId, string NomCommercial, decimal PrixPublic, int Stock);

public interface IAlternativesLecture
{
    Task<IReadOnlyList<ProduitAlternatif>> AlternativesAsync(long produitId, CancellationToken ct = default);
}

/// <summary>Alternatives proposées en caisse quand un produit est en rupture.</summary>
public sealed class ListerAlternatives(IAlternativesLecture alternatives)
{
    public Task<IReadOnlyList<ProduitAlternatif>> ExecuterAsync(long produitId, CancellationToken ct = default) =>
        alternatives.AlternativesAsync(produitId, ct);
}
