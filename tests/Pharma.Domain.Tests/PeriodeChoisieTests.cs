using Pharma.Domain.Commun;
using Pharma.Domain.Rapports;

namespace Pharma.Domain.Tests;

public class PeriodeChoisieTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 11, 9);

    [Theory]
    [InlineData(2026, 11, 3, 2026, 11, 9, "Du 3 au 9 novembre 2026")]
    [InlineData(2026, 10, 28, 2026, 11, 3, "Du 28 octobre au 3 novembre 2026")]
    [InlineData(2025, 12, 15, 2026, 1, 10, "Du 15 décembre 2025 au 10 janvier 2026")]
    [InlineData(2026, 11, 5, 2026, 11, 5, "Jeudi 5 novembre 2026")]
    public void Libelle_des_dates_choisies(int a1, int m1, int j1, int a2, int m2, int j2, string attendu) =>
        Assert.Equal(attendu, PeriodeRapport.Choisie(new(a1, m1, j1), new(a2, m2, j2), Aujourdhui).Libelle);

    [Fact]
    public void Fin_avant_debut_futur_ou_plus_d_un_an_refuses()
    {
        Assert.Throws<RegleMetierException>(() => PeriodeRapport.Choisie(new(2026, 11, 9), new(2026, 11, 1), Aujourdhui));
        Assert.Throws<RegleMetierException>(() => PeriodeRapport.Choisie(new(2026, 11, 1), new(2026, 11, 10), Aujourdhui));
        Assert.Throws<RegleMetierException>(() => PeriodeRapport.Choisie(new(2025, 11, 8), new(2026, 11, 9), Aujourdhui));
        Assert.Equal(366, PeriodeRapport.Choisie(new(2025, 11, 9), new(2026, 11, 9), Aujourdhui).NombreJours);
    }

    [Fact]
    public void Decaler_garde_la_meme_duree()
    {
        var p = PeriodeRapport.Choisie(new(2026, 11, 3), new(2026, 11, 9), Aujourdhui).Decaler(-1);
        Assert.Equal((new DateOnly(2026, 10, 27), new DateOnly(2026, 11, 2)), (p.Du, p.Au));
    }

    [Fact]
    public void Regroupement_par_semaine_au_dela_de_deux_mois()
    {
        Assert.False(PeriodeRapport.Choisie(new(2026, 9, 9), new(2026, 11, 9), Aujourdhui).ParSemaine);
        Assert.True(PeriodeRapport.Choisie(new(2026, 6, 1), new(2026, 11, 9), Aujourdhui).ParSemaine);
    }
}

public class PeriodePremierDuMoisTests
{
    [Fact]
    public void Premier_du_mois_ecrit_1er() =>
        Assert.Equal("Du 1er au 9 novembre 2026",
            PeriodeRapport.Choisie(new(2026, 11, 1), new(2026, 11, 9), new(2026, 11, 9)).Libelle);
}
