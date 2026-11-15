using Pharma.Domain.Catalogue;

namespace Pharma.Application.Catalogue;

public sealed record ProduitResume(
    long Id, string NomCommercial, string? Dci, string? Dosage, string? Forme,
    decimal PrixPublic, int Stock, ClasseReglementaire Classe);

public interface ICatalogueLecture
{
    /// <summary>Code-barres exact, puis nom ou DCI, tolérant aux fautes.</summary>
    /// <summary>
    /// auComptoir (2026-11-15) : avant de couper la liste, les produits disponibles d'abord, puis ceux en rupture qui ont un remplaçant
    /// (même DCI en stock), en dernier ceux en rupture sans remplaçant ; le code-barres exact reste en tête.
    /// </summary>
    Task<IReadOnlyList<ProduitResume>> RechercherAsync(string texte, int limite, CancellationToken ct = default, bool inclureInactifs = false, bool auComptoir = false);
}

/// <summary>Recherche au comptoir (scan ou saisie).</summary>
public sealed class RechercherProduits(ICatalogueLecture catalogue)
{
    public const int LimiteParDefaut = 20;
    public const int LimiteMax = 50;

    public Task<IReadOnlyList<ProduitResume>> ExecuterAsync(string? texte, int? limite, CancellationToken ct = default, bool inclureInactifs = false)
    {
        var recherche = texte?.Trim() ?? "";
        if (recherche.Length == 0)
            return Task.FromResult<IReadOnlyList<ProduitResume>>([]);
        return catalogue.RechercherAsync(recherche, Math.Clamp(limite ?? LimiteParDefaut, 1, LimiteMax), ct, inclureInactifs);
    }
}
