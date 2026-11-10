using Pharma.Application.Alertes;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Administration;

/// <summary>
/// Seuil d'alerte de stock d'un produit. SeuilAuto : ventes moyennes par jour × (délai de livraison + stock de sécurité).
/// SeuilManuel : saisi par le pharmacien, il remplace le seuil automatique (0 : pas d'alerte de stock).
/// </summary>
public sealed record SeuilStockProduit(long ProduitId, string Nom, int Stock, decimal VentesParJour, int SeuilAuto, int? SeuilManuel, string Statut)
{
    public int SeuilApplique => SeuilManuel ?? SeuilAuto;
}

public interface ISeuilsStock
{
    Task<SeuilStockProduit?> ProduitAsync(long produitId, CancellationToken ct = default);
    /// <summary>Produits dont le seuil a été saisi par le pharmacien.</summary>
    Task<IReadOnlyList<SeuilStockProduit>> ManuelsAsync(CancellationToken ct = default);
    Task DefinirAsync(long produitId, int? seuil, CancellationToken ct = default);
}

public sealed class ObtenirSeuilStock(ISeuilsStock seuils)
{
    public Task<SeuilStockProduit?> ExecuterAsync(long produitId, CancellationToken ct = default) => seuils.ProduitAsync(produitId, ct);
}

public sealed class ListerSeuilsStockManuels(ISeuilsStock seuils)
{
    public Task<IReadOnlyList<SeuilStockProduit>> ExecuterAsync(CancellationToken ct = default) => seuils.ManuelsAsync(ct);
}

/// <summary>Saisit (ou efface : retour à l'automatique) le seuil d'un produit, recalcule les alertes et l'inscrit au journal.</summary>
public sealed class DefinirSeuilStock(ISeuilsStock seuils, IPermissionService droits, IMoteurAlertes alertes, IJournal journal)
{
    public const int Maximum = 100_000;

    public async Task<SeuilStockProduit> ExecuterAsync(long produitId, int? seuil, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AdminParametres, "régler les seuils de stock", ct);
        if (seuil is < 0 or > Maximum)
            throw new RegleMetierException($"Le seuil doit être entre 0 et {Maximum:N0} boîtes (vide : automatique).");
        var avant = await seuils.ProduitAsync(produitId, ct) ?? throw new RegleMetierException("Produit introuvable.");

        await seuils.DefinirAsync(produitId, seuil, ct);
        await alertes.GenererAsync(ct);
        await journal.EcrireAsync(utilisateurId, "seuil_stock", "produit", produitId,
            new Dictionary<string, object?> { ["produit"] = avant.Nom, ["avant"] = avant.SeuilManuel, ["apres"] = seuil, ["automatique"] = avant.SeuilAuto },
            Permissions.AdminParametres, ct);
        return (await seuils.ProduitAsync(produitId, ct))!;
    }
}
