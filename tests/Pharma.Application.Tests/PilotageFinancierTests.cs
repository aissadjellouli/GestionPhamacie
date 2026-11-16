using Pharma.Application.Commun;
using Pharma.Application.Pilotage;
using Pharma.Application.Rapports;
using Pharma.Domain.Rapports;
using Pharma.Domain.Securite;

namespace Pharma.Application.Tests;

/// <summary>Tableau de bord financier (lot 11) : calculs et droits.</summary>
public class PilotageFinancierTests
{
    [Fact]
    public void Marge_et_taux_sur_le_ca_ht()
    {
        var m = new MargeRegroupee("Doliprane", 10, 1000m, 750m);
        Assert.Equal(250m, m.Marge);
        Assert.Equal(25.0m, m.Taux);
        Assert.Null(new MargeRegroupee("X", 0, 0m, 0m).Taux);
    }

    [Fact]
    public void Variation_et_panier_moyen()
    {
        Assert.Equal(20.0m, CalculsPilotage.Variation(1200m, 1000m));
        Assert.Equal(-50.0m, CalculsPilotage.Variation(500m, 1000m));
        Assert.Null(CalculsPilotage.Variation(500m, 0m));
        Assert.Equal(333.33m, CalculsPilotage.PanierMoyen(1000m, 3));
        Assert.Null(CalculsPilotage.PanierMoyen(1000m, 0));
        Assert.Null(new Comparaison(10m, null).VariationPct);
    }

    [Theory]
    [InlineData(TypePeriode.Jour, "2026-11-16", "2026-11-16", "2026-11-15", "2026-11-15")]
    [InlineData(TypePeriode.Semaine, "2026-11-16", "2026-11-18", "2026-11-09", "2026-11-11")]
    [InlineData(TypePeriode.Mois, "2026-11-01", "2026-11-16", "2026-10-01", "2026-10-16")]
    [InlineData(TypePeriode.Mois, "2026-03-01", "2026-03-31", "2026-02-01", "2026-02-28")]
    [InlineData(TypePeriode.Mois, "2026-02-01", "2026-02-28", "2026-01-01", "2026-01-31")]
    [InlineData(TypePeriode.Dates, "2026-11-10", "2026-11-14", "2026-11-05", "2026-11-09")]
    public void Periode_precedente(TypePeriode type, string du, string au, string pdu, string pau)
    {
        var (d, a) = CalculsPilotage.PeriodePrecedente(type, DateOnly.Parse(du), DateOnly.Parse(au));
        Assert.Equal(DateOnly.Parse(pdu), d);
        Assert.Equal(DateOnly.Parse(pau), a);
    }

    [Fact]
    public void Periode_en_cours_arretee_a_aujourdhui()
    {
        var aujourdhui = new DateOnly(2026, 11, 16);
        Assert.Equal(aujourdhui, CalculsPilotage.AuEffectif(new DateOnly(2026, 11, 30), aujourdhui));
        Assert.Equal(new DateOnly(2026, 10, 31), CalculsPilotage.AuEffectif(new DateOnly(2026, 10, 31), aujourdhui));
    }

    [Theory]
    [InlineData(0, "0 à 30 jours")]
    [InlineData(30, "0 à 30 jours")]
    [InlineData(31, "31 à 60 jours")]
    [InlineData(90, "61 à 90 jours")]
    [InlineData(91, "plus de 90 jours")]
    [InlineData(null, "non déposé")]
    public void Tranche_d_age_des_creances(int? age, string tranche) => Assert.Equal(tranche, CalculsPilotage.Tranche(age));

    [Fact]
    public void Age_et_regroupement_des_creances()
    {
        var aujourdhui = new DateOnly(2026, 11, 16);
        Assert.Equal(46, CalculsPilotage.AgeJours(new DateOnly(2026, 10, 1), aujourdhui));
        Assert.Equal(0, CalculsPilotage.AgeJours(new DateOnly(2026, 11, 20), aujourdhui));
        var t = CalculsPilotage.ParTranche([(100, 50m), (10, 20m), (5, 30m), (null, 7m)]);
        Assert.Equal(["0 à 30 jours", "plus de 90 jours", "non déposé"], t.Select(x => x.Tranche));
        Assert.Equal(50m, t[0].Montant);
        Assert.Equal(2, t[0].Nombre);
    }

    [Fact]
    public void Stock_dormant_rotation_et_couverture()
    {
        var maintenant = new DateTime(2026, 11, 16, 12, 0, 0);
        Assert.True(CalculsPilotage.EstDormant(5, null, maintenant, 90));
        Assert.True(CalculsPilotage.EstDormant(5, maintenant.AddDays(-91), maintenant, 90));
        Assert.False(CalculsPilotage.EstDormant(5, maintenant.AddDays(-10), maintenant, 90));
        Assert.False(CalculsPilotage.EstDormant(0, null, maintenant, 90));

        // 30 jours de ventes à 100 000 DA (coût) ; stock de 400 000 DA : 3 rotations par an, 120 jours de couverture
        var s = new StockPilotage(400_000m, 50, 100_000m, 30, 90, 3, 12_000m, 1, 2);
        Assert.Equal(3.0m, s.RotationAnnuelle);
        Assert.Equal(120m, s.CouvertureJours);
        Assert.Null(CalculsPilotage.RotationAnnuelle(1m, 30, 0m));
        Assert.Null(CalculsPilotage.CouvertureJours(0m, 30, 10m));
    }

    [Fact]
    public void Taux_de_rejet_par_motif()
    {
        var r = new RejetsChifa(200, 100_000m, [new("R1", "Ordonnance illisible", 6, 3000m), new("R2", "Droits expirés", 4, 1500m)]);
        Assert.Equal(10, r.Rejets);
        Assert.Equal(5.0m, r.TauxRejet);
        Assert.Equal(3.0m, r.TauxMotif(r.ParMotif[0]));
        Assert.Null(new RejetsChifa(0, 0m, []).TauxRejet);
    }

    [Fact]
    public async Task Refuse_sans_le_droit_des_marges()
    {
        var cas = new ObtenirPilotageFinancier(null!, null!, new DroitsFactices(Permissions.RapportsVentes), new HorlogeFixe());
        await Assert.ThrowsAsync<DroitRefuseException>(() => cas.ExigerAccesAsync(1, default));
    }

    [Fact]
    public async Task Accepte_le_titulaire_et_le_droit_des_marges()
    {
        await new ObtenirPilotageFinancier(null!, null!, new DroitsFactices { Titulaire = true }, new HorlogeFixe()).ExigerAccesAsync(1, default);
        await new ObtenirPilotageFinancier(null!, null!, new DroitsFactices(Permissions.RapportsMarges), new HorlogeFixe()).ExigerAccesAsync(1, default);
    }

    private sealed class HorlogeFixe : IHorloge
    {
        public DateTime MaintenantUtc => new(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);
        public DateOnly Aujourdhui => new(2026, 11, 16);
    }
}
