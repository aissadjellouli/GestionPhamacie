using Pharma.Domain.Commun;

namespace Pharma.Domain.Tests;

public class Gs1DataMatrixTests
{
    private const char Gs = (char)29;

    [Fact]
    public void Lit_gtin_peremption_lot_et_serie()
    {
        var brut = "]d2" + "01" + "03400000000017" + "17" + "270131" + "10" + "G7710" + Gs + "21" + "ABC123";
        Assert.True(Gs1DataMatrix.TryParse(brut, out var dm));
        Assert.Equal("03400000000017", dm!.Gtin);
        Assert.Equal(new DateOnly(2027, 1, 31), dm.Peremption);
        Assert.Equal("G7710", dm.Lot);
        Assert.Equal("ABC123", dm.NumeroSerie);
    }

    [Fact]
    public void Jour_00_signifie_fin_de_mois()
    {
        Assert.True(Gs1DataMatrix.TryParse("01034000000000171728020010L1", out var dm));
        Assert.Equal(new DateOnly(2028, 2, 29), dm!.Peremption);
    }

    // ---- Norme paramétrable (script 120) : par défaut, lecture identique à l'existant ----

    [Theory]
    [InlineData("]d201034000000000171727013110G7710\u001d21ABC123")]
    [InlineData("01034000000000171728020010L1")]
    [InlineData("0103400000000017")]
    [InlineData("01034000")]                      // GTIN tronqué : illisible
    [InlineData("0103400000000017991234")]        // AI 99 non géré : illisible
    [InlineData("17270131")]                      // sans GTIN : illisible
    [InlineData("\u001d\u001d0103400000000017211234567890\u001d10LOT")]
    public void Norme_par_defaut_identique_a_la_lecture_courante(string brut)
    {
        var courant = Gs1DataMatrix.TryParse(brut, out var a);
        var parDefaut = Gs1DataMatrix.TryParse(brut, Gs1Norme.ParDefaut, out var b);
        Assert.Equal(courant, parDefaut);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Norme_par_defaut_porte_01_17_10_21()
    {
        Assert.Equal(["01", "10", "17", "21"], Gs1Norme.ParDefaut.Identifiants.Select(i => i.Code).Order());
    }

    [Fact]
    public void Identifiant_ajoute_par_parametrage_est_lu()
    {
        // Ex. un AI de 3 chiffres ajouté par une norme nationale [À VÉRIFIER IANOR] : porte ici le lot
        var norme = new Gs1Norme([.. Gs1Norme.ParDefaut.Identifiants.Where(i => i.Code != "10"), new Gs1Identifiant("710", ChampGs1.Lot, null)]);
        Assert.True(Gs1DataMatrix.TryParse("0103400000000017710LOTX" + Gs + "21S1", norme, out var dm));
        Assert.Equal("LOTX", dm!.Lot);
        Assert.Equal("S1", dm.NumeroSerie);
        Assert.False(Gs1DataMatrix.TryParse("010340000000001710LOTX", norme, out _)); // 10 retiré : illisible
    }

    [Fact]
    public void Longueur_max_d_une_donnee_variable_est_controlee()
    {
        var norme = new Gs1Norme(Gs1Norme.ParDefaut.Identifiants.Select(i => i.Code == "21" ? i with { LongueurMax = 5 } : i));
        Assert.True(Gs1DataMatrix.TryParse("01034000000000172112345", norme, out _));
        Assert.False(Gs1DataMatrix.TryParse("010340000000001721123456", norme, out _));
    }

    [Fact]
    public void Norme_sans_gtin_retombe_sur_la_norme_par_defaut()
    {
        var avant = Gs1Norme.Courante;
        Gs1Norme.Courante = new Gs1Norme([new Gs1Identifiant("10", ChampGs1.Lot, null)]);
        Assert.Same(Gs1Norme.ParDefaut, Gs1Norme.Courante);
        Gs1Norme.Courante = avant;
    }
}
