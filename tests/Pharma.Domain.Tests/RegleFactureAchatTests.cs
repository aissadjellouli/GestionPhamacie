using Pharma.Domain.Achats;
using Pharma.Domain.Commun;

namespace Pharma.Domain.Tests;

/// <summary>Chiffres des factures réelles de grossistes (docs/EXEMPLE) : Qouds Para, Setifis Med, KR Distrimed.</summary>
public class RegleFactureAchatTests
{
    private static readonly DateOnly Exp = new(2029, 1, 1);

    private static LigneReceptionSaisie Ligne(long produit, int qte, decimal prix, decimal remise = 0, decimal? ppa = null, decimal? shp = null) =>
        new(produit, "P" + produit, "L" + produit, Exp, qte, prix, RemisePct: remise, Ppa: ppa, Shp: shp);

    [Fact]
    public void Facture_Qouds_Para_remise_retiree_en_pied_de_facture()
    {
        // Six lignes ; deux avec 5 % de remise (1 112,10 et 2 050,00) → remise 158,11 ; net HT 10 521,74 ; ni TVA ni timbre
        var lignes = new[]
        {
            Ligne(1, 2, 556.05m, 5, 695m), Ligne(2, 2, 1458.31m, 0, 1750m), Ligne(3, 10, 143.39m, 0, 182.50m), Ligne(4, 10, 143.39m, 0, 182.50m),
            Ligne(5, 1, 1733.33m, 0, 2080m), Ligne(6, 1, 2050m, 5, 2460m)
        };
        var t = RegleFactureAchat.Totaux(lignes, new Dictionary<long, decimal>(), 0);
        Assert.Equal(10679.85m, t.MontantHt);
        Assert.Equal(158.11m, t.Remise);
        Assert.Equal(10521.74m, t.NetHt);
        Assert.Equal(10521.74m, t.NetAPayer);
        Assert.Equal(13080m, t.PpaTotal);   // « PPA : 13 080,00 » imprimé sur la facture
    }

    [Fact]
    public void Facture_Setifis_et_KR_SHP_par_liste_et_hors_net_a_payer()
    {
        // Setifis : 10 boîtes de liste II → Total SHP 15 ; KR : 10 × 1,50 + 9 × 2,50 = 37,50 ; le SHP n'entre pas dans le net à payer
        var setifis = RegleFactureAchat.Totaux([Ligne(1, 10, 988.46m, 0, 1186.15m, RegleShp.ParBoite("liste_ii"))], new Dictionary<long, decimal>(), 0);
        Assert.Equal(15m, setifis.ShpTotal);
        Assert.Equal(9884.60m, setifis.NetAPayer);
        var kr = RegleFactureAchat.Totaux(
            [Ligne(1, 10, 95.60m, 0, 127.13m, RegleShp.ParBoite("liste_ii")), Ligne(2, 9, 490.31m, 0, 588.38m, RegleShp.ParBoite("liste_i"))],
            new Dictionary<long, decimal>(), 0);
        Assert.Equal(37.50m, kr.ShpTotal);
        Assert.Equal(5368.79m, kr.NetAPayer);
    }

    [Theory]
    [InlineData(556.05, 695.00, 24.99)]      // Qouds : MGE 24,99
    [InlineData(988.46, 1186.15, 20.00)]     // Setifis : Mge 20
    [InlineData(95.60, 127.13, 32.98)]       // KR : 32,98
    public void Marge_comme_la_colonne_MGE_des_factures(decimal prix, decimal ppa, decimal attendu) =>
        Assert.Equal(attendu, RegleFactureAchat.MargePct(prix, ppa));

    [Fact]
    public void Cout_du_stock_net_de_remise_TVA_et_timbre_dans_le_net_a_payer()
    {
        Assert.Equal(528.25m, RegleFactureAchat.PrixNet(556.05m, 5));
        var t = RegleFactureAchat.Totaux([Ligne(1, 2, 100m, 10)], new Dictionary<long, decimal> { [1] = 19 }, 5m);
        Assert.Equal((200m, 20m, 180m, 34.20m, 5m, 219.20m), (t.MontantHt, t.Remise, t.NetHt, t.Tva, t.Timbre, t.NetAPayer));
    }

    [Theory]
    [InlineData("liste_i", 2.50)]
    [InlineData("stupefiant", 2.50)]
    [InlineData("liste_ii", 1.50)]
    [InlineData("libre", 0)]
    public void SHP_selon_la_liste(string classe, decimal attendu) => Assert.Equal(attendu, RegleShp.ParBoite(classe));

    [Theory]
    [InlineData("liste_i", 2.50)]
    [InlineData("liste_ii", 1.50)]
    [InlineData(null, 0)]                       // liste à renseigner sur la fiche
    public void SHP_d_un_psychotrope_selon_sa_liste(string? liste, decimal attendu) =>
        Assert.Equal(attendu, RegleShp.ParBoite("psychotrope", liste));

    [Fact]
    public void Unites_gratuites_a_zero_ou_au_cout_reparti_et_hors_facture()
    {
        // 12 boîtes reçues dont 2 UG, 100 DA HT : facture de 10 boîtes
        var zero = new LigneReceptionSaisie(1, "P", "L", Exp, 12, 100m, Ug: 2);
        Assert.Equal((10, 100m, 100m, 0m), (zero.Facturees, zero.CoutBoite, zero.CoutBoiteRang(9), zero.CoutBoiteRang(10)));
        var reparti = zero with { UgCoutReparti = true };
        Assert.Equal((83.33m, 83.33m), (reparti.CoutBoite, reparti.CoutBoiteRang(11)));   // 1 000 ÷ 12
        var t = RegleFactureAchat.Totaux([zero], new Dictionary<long, decimal>(), 0);
        Assert.Equal((1000m, 1000m), (t.MontantHt, t.NetAPayer));
        Assert.Throws<RegleMetierException>(() => RegleFactureAchat.Verifier([zero with { Ug = 13 }], 0));
        Assert.Throws<RegleMetierException>(() => RegleFactureAchat.Verifier([zero with { RemplacerPrixCatalogue = true }], 0));   // sans PPA
    }

    [Fact]
    public void Remise_timbre_PPA_et_SHP_controles()
    {
        Assert.Throws<RegleMetierException>(() => RegleFactureAchat.Verifier([Ligne(1, 1, 10, remise: 120)], 0));
        Assert.Throws<RegleMetierException>(() => RegleFactureAchat.Verifier([Ligne(1, 1, 10, ppa: -1)], 0));
        Assert.Throws<RegleMetierException>(() => RegleFactureAchat.Verifier([Ligne(1, 1, 10, shp: -1)], 0));
        Assert.Throws<RegleMetierException>(() => RegleFactureAchat.Verifier([Ligne(1, 1, 10)], -1));
    }

    [Fact]
    public void Remise_de_ligne_negative_refusee_avec_message()
    {
        var ex = Assert.Throws<RegleMetierException>(() => RegleFactureAchat.Verifier([Ligne(1, 1, 10, remise: -5)], 0));
        Assert.Equal("P1 : la remise ne peut pas être négative (0 à 100 %).", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Remise_de_ligne_de_0_et_de_100_acceptee(decimal remise) => RegleFactureAchat.Verifier([Ligne(1, 1, 10, remise: remise)], 0);

    [Fact]
    public void Remise_de_ligne_superieure_a_100_refusee_avec_message()
    {
        var ex = Assert.Throws<RegleMetierException>(() => RegleFactureAchat.Verifier([Ligne(1, 1, 10, remise: 101)], 0));
        Assert.Equal("P1 : la remise doit être comprise entre 0 et 100 %.", ex.Message);
    }
}
