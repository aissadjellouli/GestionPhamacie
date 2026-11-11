using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Stock;

/// <summary>Lot en stock, tel qu'on le propose à la sortie (périmé ou cassé).</summary>
public sealed record LotEnStock(long LotId, long ProduitId, string Produit, string NumeroLot, DateOnly DatePeremption, int Quantite, bool Bloque,
    bool SousRegistre);

/// <summary>Sortie de stock : boîtes périmées ou cassées retirées de la vente et détruites (registre des psychotropes tenu par la base).</summary>
public sealed record SortieStockAEcrire(long LotId, int Quantite, string Motif, string Reference, long UtilisateurId);

public interface ISortiesStock
{
    Task<IReadOnlyList<LotEnStock>> LotsAsync(long produitId, CancellationToken ct = default);
    Task<LotEnStock?> LotAsync(long lotId, CancellationToken ct = default);
    /// <summary>Lot, mouvement de stock, boîtes détruites, en une transaction ; renvoie le nombre de boîtes identifiées détruites.</summary>
    Task<int> SortirAsync(SortieStockAEcrire s, CancellationToken ct = default);
}

public sealed class ListerLotsEnStock(ISortiesStock sorties)
{
    public Task<IReadOnlyList<LotEnStock>> ExecuterAsync(long produitId, CancellationToken ct = default) => sorties.LotsAsync(produitId, ct);
}

/// <summary>
/// Déclaration de boîtes périmées ou cassées (droit stock.declarer_perime) : elles sortent du stock et sont détruites ; pour un
/// psychotrope, le registre est tenu par la base (destruction ou casse). Inscrit au journal.
/// </summary>
public sealed class DeclarerSortieStock(ISortiesStock sorties, IPermissionService droits, IJournal journal)
{
    public static readonly string[] Motifs = ["perime", "casse"];

    public async Task<int> ExecuterAsync(long lotId, int quantite, string? motif, string? commentaire, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.StockDeclarerPerime, "déclarer des boîtes périmées ou cassées", ct);
        var m = motif?.Trim().ToLowerInvariant();
        if (m is null || !Motifs.Contains(m)) throw new RegleMetierException("Choisissez le motif : périmé ou cassé.", "motif_sortie");
        var lot = await sorties.LotAsync(lotId, ct) ?? throw new RegleMetierException("Lot introuvable.");
        if (quantite < 1 || quantite > lot.Quantite)
            throw new RegleMetierException($"{lot.Produit}, lot {lot.NumeroLot} : de 1 à {lot.Quantite} boîte(s).", "quantite_sortie");
        var texte = string.IsNullOrWhiteSpace(commentaire) ? null : commentaire.Trim();
        if (texte is { Length: > 200 }) throw new RegleMetierException("Commentaire trop long (200 caractères au plus).", "commentaire_sortie");
        if (m == "casse" && texte is null) throw new RegleMetierException("Indiquez ce qui s’est passé (boîte cassée, abîmée…).", "commentaire_sortie");

        var reference = (m == "perime" ? "Périmé" : "Cassé") + (texte is null ? "" : " : " + texte);
        var detruites = await sorties.SortirAsync(new SortieStockAEcrire(lotId, quantite, m, reference, utilisateurId), ct);
        await journal.EcrireAsync(utilisateurId, "sortie_stock", "lot", lotId,
            new Dictionary<string, object?>
            {
                ["produit"] = lot.Produit, ["lot"] = lot.NumeroLot, ["quantite"] = quantite, ["motif"] = m, ["commentaire"] = texte,
                ["boites_detruites"] = detruites
            }, Permissions.StockDeclarerPerime, ct);
        return detruites;
    }
}
