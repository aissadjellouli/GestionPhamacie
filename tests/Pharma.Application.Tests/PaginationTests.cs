using Pharma.Application.Achats;
using Pharma.Application.Commun;

namespace Pharma.Application.Tests;

public class PaginationTests
{
    [Fact]
    public void Sans_valeur_la_page_est_la_premiere_et_porte_dix_lignes()
    {
        var p = DemandePage.Lire(null, null);
        Assert.Equal((1, 10, 0), (p.Page, p.Taille, p.Decalage));
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(30, 30)]
    [InlineData(200, 200)]
    [InlineData(1, 5)]       // en dessous du minimum
    [InlineData(5000, 200)]  // au-dessus du maximum
    public void La_taille_reste_entre_cinq_et_deux_cents(int demandee, int attendue) =>
        Assert.Equal(attendue, DemandePage.Lire(1, demandee).Taille);

    [Fact]
    public void Le_decalage_suit_la_page_et_la_taille() => Assert.Equal(40, DemandePage.Lire(3, 20).Decalage);

    [Fact]
    public void Un_critere_vide_ne_filtre_pas()
    {
        var f = FiltreAchats.Lire(0, "  ", null, null, "");
        Assert.Equal(new FiltreAchats(), f);
        Assert.Equal(new FiltreReleve(), FiltreReleve.Lire(null, null, " "));
    }

    [Fact]
    public void Les_criteres_saisis_sont_nettoyes()
    {
        var f = FiltreAchats.Lire(3, " envoyee ", new DateOnly(2026, 11, 1), null, " BL-12 ");
        Assert.Equal(new FiltreAchats(3, "envoyee", new DateOnly(2026, 11, 1), null, "BL-12"), f);
    }
}
