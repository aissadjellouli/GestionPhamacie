using Pharma.Domain.Stock;

namespace Pharma.Domain.Tests;

public class LotRaisonsTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 11, 8);

    [Fact]
    public void Le_blocage_prime_sur_la_peremption() =>
        Assert.Equal(RaisonNonVendable.Bloque,
            new Lot { Bloque = true, QuantiteDisponible = 5, DatePeremption = Aujourdhui.AddDays(-1) }.RaisonNonVendable(Aujourdhui));

    [Fact]
    public void Lot_perime() =>
        Assert.Equal(RaisonNonVendable.Perime,
            new Lot { QuantiteDisponible = 5, DatePeremption = Aujourdhui }.RaisonNonVendable(Aujourdhui));

    [Fact]
    public void Lot_epuise() =>
        Assert.Equal(RaisonNonVendable.Epuise,
            new Lot { QuantiteDisponible = 0, DatePeremption = Aujourdhui.AddDays(30) }.RaisonNonVendable(Aujourdhui));

    [Fact]
    public void Lot_vendable_sans_raison()
    {
        var lot = new Lot { QuantiteDisponible = 5, DatePeremption = Aujourdhui.AddDays(30) };
        Assert.Null(lot.RaisonNonVendable(Aujourdhui));
        Assert.True(lot.EstVendable(Aujourdhui));
    }
}
