using Pharma.Domain.Rapports;

namespace Pharma.Domain.Tests;

public class PeriodeRapportTests
{
    private static readonly DateOnly Jeudi = new(2026, 11, 12);

    [Fact]
    public void Semaine_du_lundi_au_dimanche()
    {
        var p = PeriodeRapport.Calculer(TypePeriode.Semaine, Jeudi);
        Assert.Equal((new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 15), 7), (p.Du, p.Au, p.NombreJours));
        Assert.Equal("Semaine du 9 au 15 novembre 2026", p.Libelle);
        Assert.Equal(new DateOnly(2026, 11, 9), PeriodeRapport.Calculer(TypePeriode.Semaine, new DateOnly(2026, 11, 15)).Du);   // dimanche
    }

    [Fact]
    public void Mois_complet_et_jour()
    {
        var m = PeriodeRapport.Calculer(TypePeriode.Mois, new DateOnly(2028, 2, 10));
        Assert.Equal((new DateOnly(2028, 2, 1), new DateOnly(2028, 2, 29)), (m.Du, m.Au));     // année bissextile
        Assert.Equal("Février 2028", m.Libelle);
        Assert.Equal("Jeudi 12 novembre 2026", PeriodeRapport.Calculer(TypePeriode.Jour, Jeudi).Libelle);
    }

    [Fact]
    public void Semaine_a_cheval_sur_deux_mois() =>
        Assert.Equal("Semaine du 30 novembre au 6 décembre 2026", PeriodeRapport.Calculer(TypePeriode.Semaine, new DateOnly(2026, 12, 2)).Libelle);
}
