using Pharma.Domain.Achats;
using Pharma.Domain.Commun;

namespace Pharma.Domain.Tests;

public class RegleComptageReceptionTests
{
    private static readonly DateOnly Peremption = new(2028, 6, 30);

    private static LigneReceptionSaisie Facture(long produit, string lot, int quantite, decimal prix = 100m, int ug = 0) =>
        new(produit, $"Produit {produit}", lot, Peremption, quantite, prix, Ug: ug);

    private static LigneComptee Compte(long produit, string lot, int quantite, params string[] series) =>
        new(produit, $"Produit {produit}", lot, Peremption, quantite, series.Length > 0 ? "03400930000001" : null, series.Length > 0 ? series : null);

    [Fact]
    public void Sans_ecart_la_facture_entre_avec_les_Data_Matrix_du_comptage()
    {
        var f = RegleComptageReception.Fusionner([Facture(1, "A1", 2)], [Compte(1, " a1 ", 2, "S1", "S2")]);
        Assert.Empty(f.Ecarts);
        Assert.Empty(f.Manquants);
        Assert.Equal(["S1", "S2"], f.Lignes.Single().NumerosSerie);
        Assert.Equal("03400930000001", f.Lignes.Single().Gtin);
    }

    [Fact]
    public void Boites_manquantes_entrees_comme_facturees_puis_sorties()
    {
        var f = RegleComptageReception.Fusionner([Facture(1, "A1", 10)], [Compte(1, "A1", 8)]);
        Assert.Equal(10, f.Lignes.Single().Quantite);           // la dette suit la facture
        Assert.Equal(new LigneManquante(0, 2), f.Manquants.Single());
        Assert.Equal((2, 0), (f.BoitesManquantes, f.BoitesEnTrop));
    }

    [Fact]
    public void Boites_en_trop_entrees_en_unites_gratuites()
    {
        var f = RegleComptageReception.Fusionner([Facture(1, "A1", 10, ug: 2)], [Compte(1, "A1", 12)]);
        var l = f.Lignes.Single();
        Assert.Equal((12, 4, 8), (l.Quantite, l.Ug, l.Facturees));   // 8 boîtes facturées, comme sur la facture
        Assert.Empty(f.Manquants);
        Assert.Equal(2, f.BoitesEnTrop);
    }

    [Fact]
    public void Lot_compte_absent_de_la_facture_et_lot_facture_non_compte()
    {
        var f = RegleComptageReception.Fusionner([Facture(1, "A1", 5)], [Compte(2, "B7", 3)]);
        Assert.Equal(2, f.Lignes.Count);
        Assert.Equal(new LigneManquante(0, 5), f.Manquants.Single());
        var gratuite = f.Lignes[1];
        Assert.Equal((3, 3, 0m), (gratuite.Quantite, gratuite.Ug, gratuite.PrixAchatHt));
        Assert.Equal([(5, 0), (0, 3)], f.Ecarts.Select(e => (e.Facture, e.Compte)));
    }

    [Fact]
    public void Comptage_controle_comme_une_reception()
    {
        var aujourdhui = new DateOnly(2026, 11, 11);
        Assert.Throws<RegleMetierException>(() => RegleComptageReception.VerifierComptage(" ", [Compte(1, "A1", 1)], aujourdhui));
        Assert.Throws<RegleMetierException>(() => RegleComptageReception.VerifierComptage("BL1", [], aujourdhui));
        var ex = Assert.Throws<RegleMetierException>(() => RegleComptageReception.VerifierComptage("BL1", [Compte(1, "A1", 1), Compte(1, "a1", 2)], aujourdhui));
        Assert.Contains("deux fois", ex.Message);
        RegleComptageReception.VerifierComptage("BL1", [Compte(1, "A1", 1), Compte(1, "A2", 2)], aujourdhui);
    }
}
