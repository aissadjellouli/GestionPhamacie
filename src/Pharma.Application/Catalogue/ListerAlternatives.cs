namespace Pharma.Application.Catalogue;

/// <summary>
/// Produit proposé en remplacement, en stock (PrixPublic : prix servi, SHP compris). <see cref="Exact"/> : même DCI, dosage et forme
/// (substituable) ; sinon autre dosage ou autre forme, à valider par le pharmacien. ListeBlanche : autorisé par le pharmacien en
/// remplacement de ce produit (2026-11-16). FabricationLocale : fabriqué en Algérie (saisie, sinon nomenclature).
/// </summary>
public sealed record ProduitAlternatif(long ProduitId, string NomCommercial, decimal PrixPublic, int Stock,
    bool Exact = true, string? Dosage = null, string? Forme = null, string? Laboratoire = null,
    bool ListeBlanche = false, bool FabricationLocale = false);

public interface IAlternativesLecture
{
    /// <summary>Produits actifs en stock de même DCI (exacts ou non) et ceux de la liste blanche du produit.</summary>
    Task<IReadOnlyList<ProduitAlternatif>> AlternativesAsync(long produitId, CancellationToken ct = default);
}

/// <summary>
/// Équivalents proposés quand un produit manque : équivalents exacts et liste blanche ; la même DCI dans un autre dosage ou une
/// autre forme seulement si « équivalent exact seulement » est désactivé (2026-11-16). Voir <see cref="RegleSubstitution"/>.
/// Sans réglage lisible : équivalent exact seulement.
/// </summary>
public sealed class ListerAlternatives(IAlternativesLecture alternatives, ISubstitution? substitution = null)
{
    public async Task<IReadOnlyList<ProduitAlternatif>> ExecuterAsync(long produitId, CancellationToken ct = default)
    {
        var exactSeulement = substitution is null || (await substitution.ParametreAsync(ct)).EquivalentExactSeulement;
        return RegleSubstitution.Choisir(await alternatives.AlternativesAsync(produitId, ct), exactSeulement);
    }
}
