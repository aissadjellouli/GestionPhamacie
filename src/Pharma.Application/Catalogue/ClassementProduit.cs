using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Catalogue;

/// <summary>Catégorie de produits : médicament ou autre produit (dermo-cosmétique, hygiène…), pour les totaux de la caisse.</summary>
public sealed record CategorieProduit(string Code, string Libelle, bool Medicament);

/// <summary>
/// Classement d'un produit : catégorie et, pour un psychotrope, sa liste (I ou II) qui fixe son SHP. DansNomenclature : n° d'enregistrement
/// présent dans la nomenclature nationale ; SubstanceSurveillee : sa DCI contient une substance surveillée (la parapharmacie leur est refusée).
/// </summary>
public sealed record ClassementProduit(long ProduitId, string Nom, string Classe, string Categorie, string? ListePsychotrope,
    bool DansNomenclature = false, bool SubstanceSurveillee = false);

/// <summary>
/// Produit à classer (2026-11-20) : encore en catégorie médicament, hors nomenclature (n° vide ou absent), classe « libre », que la règle
/// stricte de la parapharmacie (script 154) n'a pas classé d'office ; le titulaire décide (médicament ou parapharmacie).
/// </summary>
public sealed record ProduitAClasser(long ProduitId, string Nom, string? Dci, string? Dosage, string? Forme, string? NumEnregistrement,
    bool Remboursable, decimal? TarifReference, decimal PrixPublic, int Stock);

public interface IClassementProduits
{
    Task<IReadOnlyList<CategorieProduit>> CategoriesAsync(CancellationToken ct = default);
    Task<ClassementProduit?> LireAsync(long produitId, CancellationToken ct = default);
    Task<bool> ModifierAsync(long produitId, string categorie, string? listePsychotrope, CancellationToken ct = default);
    /// <summary>Produits à classer, filtrés (nom, DCI ou n° d'enregistrement, sans majuscules ni accents) et paginés par le serveur.</summary>
    Task<Page<ProduitAClasser>> AClasserAsync(string? texte, DemandePage page, CancellationToken ct = default);
}

/// <summary>Liste des produits à classer (Administration › Nomenclature, « Produits à classer ») : droit de créer un produit.</summary>
public sealed class ListerProduitsAClasser(IClassementProduits classement, IPermissionService droits)
{
    public async Task<Page<ProduitAClasser>> ExecuterAsync(string? texte, DemandePage page, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CatalogueCreer, "classer des produits", ct);
        return await classement.AClasserAsync(string.IsNullOrWhiteSpace(texte) ? null : texte.Trim(), page, ct);
    }
}

/// <summary>
/// Reclassement groupé (2026-11-20) : les produits cochés passent dans une même catégorie (droit de créer un produit), une ligne de journal
/// par produit (« produit_classe », avant et après). Tout ou rien : un produit refusé (introuvable, n° de la nomenclature, classe autre que
/// « libre » ou DCI de substance surveillée pour la parapharmacie) arrête tout avant la moindre écriture, avec son nom dans le message.
/// Changer la catégorie change le geste de la caisse : remise sur la parapharmacie, don partiel sur un médicament.
/// </summary>
public sealed class ReclasserProduits(IClassementProduits classement, IPermissionService droits, IJournal journal)
{
    /// <summary>Nombre de produits cochés au plus par envoi (une page de la liste, à sa plus grande taille).</summary>
    public const int Maximum = DemandePage.TailleMax;

    public async Task<int> ExecuterAsync(IReadOnlyList<long>? produits, string? categorie, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CatalogueCreer, "classer des produits", ct);
        var ids = (produits ?? []).Distinct().ToList();
        if (ids.Count == 0) throw new RegleMetierException("Cochez au moins un produit.", "produits");
        if (ids.Count > Maximum) throw new RegleMetierException($"{Maximum} produits au plus à la fois.", "produits");
        var categories = await classement.CategoriesAsync(ct);
        var code = categorie?.Trim();
        var cible = categories.FirstOrDefault(c => c.Code == code) ?? throw new RegleMetierException("Choisissez une catégorie de la liste.", "categorie");

        var actuels = new List<ClassementProduit>();
        foreach (var id in ids)
        {
            var actuel = await classement.LireAsync(id, ct) ?? throw new RegleMetierException($"Produit n° {id} introuvable.");
            try { Controler(actuel, cible); }
            catch (RegleMetierException e) { throw new RegleMetierException($"« {actuel.Nom} » : {e.Message}", "categorie"); }
            actuels.Add(actuel);
        }

        var n = 0;
        foreach (var actuel in actuels.Where(a => a.Categorie != cible.Code))
        {
            if (!await classement.ModifierAsync(actuel.ProduitId, cible.Code, actuel.ListePsychotrope, ct)) continue;
            await journal.EcrireAsync(utilisateurId, "produit_classe", "produit", actuel.ProduitId,
                new Dictionary<string, object?>
                {
                    ["produit"] = actuel.Nom, ["categorie_avant"] = actuel.Categorie, ["categorie"] = cible.Code, ["reclassement_groupe"] = true
                }, Permissions.CatalogueCreer, ct);
            n++;
        }
        return n;
    }

    /// <summary>Parapharmacie refusée pour un produit de la nomenclature, d'une classe autre que « libre », ou de substance surveillée.</summary>
    internal static void Controler(ClassementProduit actuel, CategorieProduit cible)
    {
        RegleProduit.ControlerCategorie(cible.Medicament, actuel.Classe, actuel.DansNomenclature);
        if (!cible.Medicament && actuel.SubstanceSurveillee)
            throw new RegleMetierException("Sa DCI correspond à une substance surveillée : c'est un médicament, il reste en catégorie « Médicament ».", "categorie");
    }
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
        if (code != actuel.Categorie) ReclasserProduits.Controler(actuel, categories.First(c => c.Code == code));

        if (!await classement.ModifierAsync(produitId, code, liste, ct)) throw new RegleMetierException("Produit introuvable.");
        await journal.EcrireAsync(utilisateurId, "produit_classe", "produit", produitId,
            new Dictionary<string, object?>
            {
                ["produit"] = actuel.Nom, ["categorie_avant"] = actuel.Categorie, ["categorie"] = code,
                ["liste_avant"] = actuel.ListePsychotrope, ["liste"] = liste
            }, Permissions.CatalogueCreer, ct);
    }
}
