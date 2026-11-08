using Pharma.Application.Catalogue;
using Pharma.Domain.Achats;
using Pharma.Domain.Stock;

namespace Pharma.Application.Tests;

public class CatalogueTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 11, 8);

    private static Lot NouveauLot(long id, string numero, int joursRestants, bool bloque = false, int quantite = 10) => new()
    {
        Id = id, ProduitId = 1, NumeroLot = numero, QuantiteDisponible = quantite, Bloque = bloque,
        DatePeremption = Aujourdhui.AddDays(joursRestants),
        Fournisseur = new Fournisseur { RaisonSociale = "Grossiste A" }
    };

    [Fact]
    public async Task Lot_fefo_saute_le_lot_bloque_qui_expire_en_premier()
    {
        var cas = new ObtenirLotFefo(
            new LotsEnMemoire(NouveauLot(1, "BLOQUE", 30, bloque: true), NouveauLot(2, "BON", 200)),
            new HorlogeFixe(Aujourdhui));

        var resultat = await cas.ExecuterAsync(produitId: 1);

        Assert.NotNull(resultat.Lot);
        Assert.Equal("BON", resultat.Lot.NumeroLot);
        Assert.Equal("Grossiste A", resultat.Lot.Fournisseur);
        var ecarte = Assert.Single(resultat.Ecartes);
        Assert.Equal("BLOQUE", ecarte.NumeroLot);
        Assert.Equal(RaisonNonVendable.Bloque, ecarte.Raison);
    }

    [Fact]
    public async Task Aucun_lot_vendable_mais_le_perime_est_explique()
    {
        var cas = new ObtenirLotFefo(
            new LotsEnMemoire(NouveauLot(1, "PERIME", -1), NouveauLot(2, "EPUISE", 90, quantite: 0)),
            new HorlogeFixe(Aujourdhui));

        var resultat = await cas.ExecuterAsync(produitId: 1);

        Assert.Null(resultat.Lot);
        var ecarte = Assert.Single(resultat.Ecartes);   // l'épuisé n'est pas un « écarté »
        Assert.Equal(RaisonNonVendable.Perime, ecarte.Raison);
    }

    [Theory]
    [InlineData(null, RechercherProduits.LimiteParDefaut)]
    [InlineData(0, 1)]
    [InlineData(500, RechercherProduits.LimiteMax)]
    public async Task Limite_de_recherche_bornee(int? demandee, int attendue)
    {
        var catalogue = new CatalogueEspion();
        await new RechercherProduits(catalogue).ExecuterAsync("  doli ", demandee);

        Assert.Equal("doli", catalogue.Texte);
        Assert.Equal(attendue, catalogue.Limite);
    }

    [Fact]
    public async Task Recherche_vide_n_interroge_pas_la_base()
    {
        var catalogue = new CatalogueEspion();
        var resultat = await new RechercherProduits(catalogue).ExecuterAsync("   ", null);

        Assert.Empty(resultat);
        Assert.Null(catalogue.Texte);
    }
}
