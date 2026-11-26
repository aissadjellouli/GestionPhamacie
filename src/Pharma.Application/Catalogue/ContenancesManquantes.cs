using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Catalogue;

/// <summary>
/// Produit actif : contenance actuelle (vide : à compléter), boîtes vendues sur 12 mois et boîtes en stock, pour saisir d'abord les plus utiles.
/// </summary>
public sealed record ContenanceManquante(long ProduitId, string Nom, string? Forme, string? Dosage, string? Conditionnement, int Vendues12Mois, int Stock,
    int? Contenance = null);

public interface IContenancesManquantes
{
    /// <summary>
    /// Produits actifs, les plus vendus sur 12 mois d'abord (puis le stock, puis le nom) : sans contenance seulement, ou tous
    /// (<paramref name="toutes"/>, pour corriger).
    /// </summary>
    Task<Page<ContenanceManquante>> ListerAsync(string? recherche, DemandePage page, CancellationToken ct = default, bool toutes = false);
    /// <summary>Contenance écrite seulement si le produit n'en a toujours pas ; nom du produit, ou null s'il n'existe pas ou en a déjà une.</summary>
    Task<string?> EnregistrerSiVideAsync(long produitId, int contenance, CancellationToken ct = default);
    /// <summary>
    /// Correction : contenance remplacée seulement si elle vaut encore <paramref name="attendue"/> (lue avant, vide comprise) ; nom du produit,
    /// ou null si le produit n'existe pas ou si un collègue l'a changée entre-temps.
    /// </summary>
    Task<string?> CorrigerAsync(long produitId, int? attendue, int contenance, CancellationToken ct = default);
}

/// <summary>
/// Contenances des produits (Stock › Contenances et Administration › Contenances des produits, décision du titulaire du 2026-11-26) :
/// compléter les contenances que la nomenclature ne donne pas, les produits vendus d'abord, et corriger une contenance déjà renseignée.
/// Même droit et même contrôle que la fiche produit (catalogue.creer, entier de 1 à 10000) ; chaque saisie est inscrite au journal
/// (produit_modifie, avant / après). Changée entre-temps par un collègue : refus explicite, jamais d'écrasement silencieux.
/// </summary>
public sealed class GererContenancesManquantes(IContenancesManquantes depot, IPermissionService droits, IJournal journal)
{
    public const string Motif = "saisie en lot (Contenances)";

    public async Task<Page<ContenanceManquante>> ListerAsync(long utilisateurId, string? recherche, DemandePage page, CancellationToken ct = default,
        bool toutes = false)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CatalogueCreer, "saisir les contenances", ct);
        return await depot.ListerAsync(string.IsNullOrWhiteSpace(recherche) ? null : recherche.Trim(), page, ct, toutes);
    }

    /// <summary><paramref name="avant"/> : contenance affichée à l'écran (vide : produit à compléter).</summary>
    public async Task EnregistrerAsync(long utilisateurId, long produitId, int? contenance, CancellationToken ct = default, int? avant = null)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CatalogueCreer, "saisir les contenances", ct);
        if (contenance is not { } c || !ContenanceProduit.SaisieValide(c))
            throw new RegleMetierException($"Contenance : nombre entier d’unités par boîte, de {ContenanceProduit.Minimum} à {ContenanceProduit.Maximum}.", "contenance");
        if (avant == c) return;
        var nom = (avant is null ? await depot.EnregistrerSiVideAsync(produitId, c, ct) : await depot.CorrigerAsync(produitId, avant, c, ct))
                  ?? throw new RegleMetierException("La contenance de ce produit vient d’être changée par un collègue (ou le produit n’existe plus) : rechargez la liste.", "contenance");
        await journal.EcrireAsync(utilisateurId, "produit_modifie", "produit", produitId, new Dictionary<string, object?>
        {
            ["produit"] = nom,
            ["contenance"] = new Dictionary<string, object?> { ["avant"] = avant, ["apres"] = c },
            ["motif"] = Motif
        }, Permissions.CatalogueCreer, ct);
    }
}
