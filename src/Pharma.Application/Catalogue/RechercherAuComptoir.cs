namespace Pharma.Application.Catalogue;

/// <summary>
/// Prix servi d'un produit : prix que le patient paiera pour une boîte du lot qui serait servi (premier à périmer, vendable), PPA de ce
/// lot sinon prix du catalogue, SHP compris. DciId, Dosage et Forme : pour reconnaître le même produit d'un autre laboratoire.
/// </summary>
public sealed record ProduitPrixServi(long Id, string NomCommercial, long? DciId, string? Dosage, string? Forme, int Stock, decimal PrixServi,
    string? Laboratoire = null);

public interface IEquivalentsComptoir
{
    /// <summary>Prix servi, DCI, dosage et forme de ces produits (stock vendable compris, 0 si rupture).</summary>
    Task<IReadOnlyList<ProduitPrixServi>> ProduitsAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default);

    /// <summary>Produits actifs de ces DCI avec du stock vendable (lots ni périmés ni bloqués).</summary>
    Task<IReadOnlyList<ProduitPrixServi>> EnStockAsync(IReadOnlyCollection<long> dciIds, CancellationToken ct = default);
}

/// <summary>
/// Produit proposé au comptoir : PrixServi (voir <see cref="ProduitPrixServi"/>) ; Remplacant, pour un produit en rupture ; MoinsCher,
/// pour un produit en stock dont le même produit d'un autre laboratoire (même DCI, dosage et forme) est moins cher.
/// </summary>
public sealed record ProduitComptoir(ProduitResume Produit, decimal PrixServi, ProduitAlternatif? Remplacant, ProduitAlternatif? MoinsCher);

/// <summary>
/// Recherche au comptoir par nom ou DCI (2026-11-14) : produits en stock d'abord (ordre de pertinence gardé), produits en rupture
/// en bas ; depuis le 2026-11-15 cet ordre est appliqué par la base AVANT de couper la liste (avec tout le catalogue de la nomenclature,
/// les produits disponibles ne sont plus évincés), et parmi les ruptures, celles qui ont un remplaçant avant celles qui n'en ont pas. Rupture : remplaçant = l'équivalent exact (même DCI, dosage, forme) en stock le moins cher, à défaut la même DCI dans un
/// autre dosage ou une autre forme (à valider par le pharmacien). En stock : le même produit d'un autre laboratoire moins cher est
/// suggéré, sans changer l'ordre (le client peut demander explicitement le produit cherché). Prix comparé : prix servi.
/// </summary>
public sealed class RechercherAuComptoir(ICatalogueLecture catalogue, IEquivalentsComptoir equivalents)
{
    public async Task<IReadOnlyList<ProduitComptoir>> ExecuterAsync(string? texte, int? limite, CancellationToken ct = default)
    {
        var recherche = texte?.Trim() ?? "";
        if (recherche.Length == 0) return [];
        var trouves = await catalogue.RechercherAsync(recherche, Math.Clamp(limite ?? RechercherProduits.LimiteParDefaut, 1, RechercherProduits.LimiteMax), ct, auComptoir: true);
        if (trouves.Count == 0) return [];

        var infos = (await equivalents.ProduitsAsync(trouves.Select(p => p.Id).ToList(), ct)).ToDictionary(p => p.Id);
        var dcis = infos.Values.Where(i => i.DciId is not null).Select(i => i.DciId!.Value).Distinct().ToList();
        var enStock = dcis.Count == 0 ? [] : await equivalents.EnStockAsync(dcis, ct);

        // Disponible, puis rupture avec remplaçant, puis rupture sans remplaçant (ordre de la base gardé dans chaque groupe)
        return trouves
            .Select((p, rang) => (Proposition: Proposer(p, infos.GetValueOrDefault(p.Id), enStock), Rang: rang))
            .OrderBy(x => x.Proposition.Produit.Stock > 0 ? 0 : x.Proposition.Remplacant is not null ? 1 : 2).ThenBy(x => x.Rang)
            .Select(x => x.Proposition)
            .ToList();
    }

    public static ProduitComptoir Proposer(ProduitResume p, ProduitPrixServi? info, IReadOnlyList<ProduitPrixServi> enStock)
    {
        var prix = info?.PrixServi ?? p.PrixPublic;
        if (info?.DciId is not { } dci) return new ProduitComptoir(p, prix, null, null);
        var memeDci = enStock.Where(e => e.DciId == dci && e.Id != p.Id && e.Stock > 0).ToList();
        bool Exact(ProduitPrixServi e) => string.Equals(e.Dosage, info.Dosage, StringComparison.Ordinal) && string.Equals(e.Forme, info.Forme, StringComparison.Ordinal);
        static ProduitAlternatif Vers(ProduitPrixServi e, bool exact) => new(e.Id, e.NomCommercial, e.PrixServi, e.Stock, exact, e.Dosage, e.Forme, e.Laboratoire);

        if (p.Stock <= 0)
        {
            var exact = memeDci.Where(Exact).OrderBy(e => e.PrixServi).ThenByDescending(e => e.Stock).FirstOrDefault();
            var remplacant = exact is not null ? Vers(exact, true)
                : memeDci.OrderBy(e => e.PrixServi).ThenByDescending(e => e.Stock).Select(e => Vers(e, false)).FirstOrDefault();
            return new ProduitComptoir(p, prix, remplacant, null);
        }
        var moinsCher = memeDci.Where(e => Exact(e) && e.PrixServi < prix).OrderBy(e => e.PrixServi).ThenByDescending(e => e.Stock).FirstOrDefault();
        return new ProduitComptoir(p, prix, null, moinsCher is null ? null : Vers(moinsCher, true));
    }
}
