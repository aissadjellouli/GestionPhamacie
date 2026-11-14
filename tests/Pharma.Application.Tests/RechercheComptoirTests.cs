using Pharma.Application.Catalogue;
using Pharma.Domain.Catalogue;

namespace Pharma.Application.Tests;

public class RechercheComptoirTests
{
    private static ProduitResume Resume(long id, string nom, int stock) => new(id, nom, "Amoxicilline", "1 g", "cp", 300, stock, ClasseReglementaire.Libre);
    private static ProduitPrixServi Info(long id, int stock, decimal prix, string dosage = "1 g", string forme = "cp", long dci = 1) =>
        new(id, "P" + id, dci, dosage, forme, stock, prix);

    private sealed class Catalogue(params ProduitResume[] produits) : ICatalogueLecture
    {
        public Task<IReadOnlyList<ProduitResume>> RechercherAsync(string texte, int limite, CancellationToken ct = default, bool inclureInactifs = false) =>
            Task.FromResult<IReadOnlyList<ProduitResume>>(produits);
    }

    private sealed class Equivalents(params ProduitPrixServi[] tous) : IEquivalentsComptoir
    {
        public Task<IReadOnlyList<ProduitPrixServi>> ProduitsAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProduitPrixServi>>(tous.Where(t => ids.Contains(t.Id)).ToList());
        public Task<IReadOnlyList<ProduitPrixServi>> EnStockAsync(IReadOnlyCollection<long> dciIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProduitPrixServi>>(tous.Where(t => t.DciId is { } d && dciIds.Contains(d) && t.Stock > 0).ToList());
    }

    [Fact]
    public async Task Produits_en_rupture_en_bas_de_liste_ordre_garde_sinon()
    {
        var cas = new RechercherAuComptoir(new Catalogue(Resume(1, "A", 0), Resume(2, "B", 5), Resume(3, "C", 0), Resume(4, "D", 2)),
            new Equivalents(Info(1, 0, 300), Info(2, 5, 300), Info(3, 0, 300), Info(4, 2, 300)));
        Assert.Equal([2L, 4L, 1L, 3L], (await cas.ExecuterAsync("amox", null)).Select(c => c.Produit.Id));
    }

    [Fact]
    public void Rupture_remplacant_exact_le_moins_cher_sinon_autre_dosage()
    {
        var p = Resume(1, "Amox A", 0);
        var tous = new[] { Info(2, 10, 320), Info(3, 4, 280), Info(4, 50, 150, dosage: "500 mg") };
        var c = RechercherAuComptoir.Proposer(p, Info(1, 0, 300), tous);
        Assert.Equal((3L, true), (c.Remplacant!.ProduitId, c.Remplacant.Exact));
        Assert.Null(c.MoinsCher);

        var sansExact = RechercherAuComptoir.Proposer(p, Info(1, 0, 300), [Info(4, 50, 150, dosage: "500 mg")]);
        Assert.Equal((4L, false), (sansExact.Remplacant!.ProduitId, sansExact.Remplacant.Exact));
    }

    [Fact]
    public void En_stock_meme_produit_moins_cher_suggere_jamais_plus_cher_ni_autre_dosage()
    {
        var p = Resume(1, "Amox A", 5);
        var c = RechercherAuComptoir.Proposer(p, Info(1, 5, 300), [Info(2, 3, 350), Info(3, 4, 250), Info(4, 9, 100, dosage: "500 mg")]);
        Assert.Equal(3L, c.MoinsCher!.ProduitId);
        Assert.Null(c.Remplacant);

        var dejaLeMoinsCher = RechercherAuComptoir.Proposer(p, Info(1, 5, 200), [Info(2, 3, 350), Info(3, 4, 250)]);
        Assert.Null(dejaLeMoinsCher.MoinsCher);
    }

    private sealed class Alternatives(params ProduitAlternatif[] liste) : IAlternativesLecture
    {
        public Task<IReadOnlyList<ProduitAlternatif>> AlternativesAsync(long produitId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProduitAlternatif>>(liste);
    }

    [Fact]
    public async Task Equivalents_du_panier_du_moins_cher_au_plus_cher()
    {
        var liste = await new ListerAlternatives(new Alternatives(
            new ProduitAlternatif(2, "B", 320, 50), new ProduitAlternatif(3, "C", 280, 2), new ProduitAlternatif(4, "D", 100, 9, Exact: false))).ExecuterAsync(1);
        Assert.Equal([3L, 2L], liste.Select(a => a.ProduitId));
    }
}
