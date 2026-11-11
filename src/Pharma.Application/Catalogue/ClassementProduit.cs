using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Catalogue;

/// <summary>Catégorie de produits : médicament ou autre produit (dermo-cosmétique, hygiène…), pour les totaux de la caisse.</summary>
public sealed record CategorieProduit(string Code, string Libelle, bool Medicament);

/// <summary>Classement d'un produit : catégorie et, pour un psychotrope, sa liste (I ou II) qui fixe son SHP.</summary>
public sealed record ClassementProduit(long ProduitId, string Nom, string Classe, string Categorie, string? ListePsychotrope);

public interface IClassementProduits
{
    Task<IReadOnlyList<CategorieProduit>> CategoriesAsync(CancellationToken ct = default);
    Task<ClassementProduit?> LireAsync(long produitId, CancellationToken ct = default);
    Task<bool> ModifierAsync(long produitId, string categorie, string? listePsychotrope, CancellationToken ct = default);
}

public sealed class ListerCategoriesProduits(IClassementProduits classement)
{
    public Task<IReadOnlyList<CategorieProduit>> ExecuterAsync(CancellationToken ct = default) => classement.CategoriesAsync(ct);
}

public sealed class LireClassementProduit(IClassementProduits classement)
{
    public async Task<ClassementProduit> ExecuterAsync(long produitId, CancellationToken ct = default) =>
        await classement.LireAsync(produitId, ct) ?? throw new RegleMetierException("Produit introuvable.");
}

/// <summary>
/// Change la catégorie d'un produit et, pour un psychotrope, sa liste (I ou II), qui fixe son SHP (2,50 ou 1,50 DA par boîte).
/// Droit de créer un produit ; inscrit au journal avec l'ancien et le nouveau classement.
/// </summary>
public sealed class ModifierClassementProduit(IClassementProduits classement, IPermissionService droits, IJournal journal)
{
    public async Task ExecuterAsync(long produitId, string? categorie, string? listePsychotrope, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CatalogueCreer, "classer un produit", ct);
        var actuel = await classement.LireAsync(produitId, ct) ?? throw new RegleMetierException("Produit introuvable.");
        var categories = await classement.CategoriesAsync(ct);
        var code = categorie?.Trim();
        if (code is null || categories.All(c => c.Code != code))
            throw new RegleMetierException("Choisissez une catégorie de la liste.", "categorie");
        var liste = string.IsNullOrWhiteSpace(listePsychotrope) ? null : listePsychotrope.Trim();
        if (liste is not (null or "liste_i" or "liste_ii"))
            throw new RegleMetierException("Liste d'un psychotrope : liste I ou liste II.", "liste_psychotrope");
        if (actuel.Classe != "psychotrope") liste = null;   // la liste ne sert qu'aux psychotropes

        if (!await classement.ModifierAsync(produitId, code, liste, ct)) throw new RegleMetierException("Produit introuvable.");
        await journal.EcrireAsync(utilisateurId, "produit_classe", "produit", produitId,
            new Dictionary<string, object?>
            {
                ["produit"] = actuel.Nom, ["categorie_avant"] = actuel.Categorie, ["categorie"] = code,
                ["liste_avant"] = actuel.ListePsychotrope, ["liste"] = liste
            }, Permissions.CatalogueCreer, ct);
    }
}
