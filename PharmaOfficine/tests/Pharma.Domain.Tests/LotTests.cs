using Pharma.Domain.Commun;
using Pharma.Domain.Stock;

namespace Pharma.Domain.Tests;

public class LotTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 11, 8);

    private static Lot NouveauLot(long id, string numero, int joursRestants, int quantite = 10, bool bloque = false) => new()
    {
        Id = id, NumeroLot = numero, QuantiteDisponible = quantite, Bloque = bloque,
        DatePeremption = Aujourdhui.AddDays(joursRestants)
    };

    [Fact]
    public void Fefo_sert_le_lot_qui_expire_en_premier()
    {
        var lots = new[] { NouveauLot(1, "TARD", 400), NouveauLot(2, "TOT", 120), NouveauLot(3, "MILIEU", 200) };
        Assert.Equal("TOT", Lot.Fefo(lots, Aujourdhui)!.NumeroLot);
    }

    [Fact]
    public void Fefo_ignore_perimes_bloques_et_epuises()
    {
        var lots = new[]
        {
            NouveauLot(1, "PERIME", -8),
            NouveauLot(2, "BLOQUE", 30, bloque: true),
            NouveauLot(3, "EPUISE", 40, quantite: 0),
            NouveauLot(4, "BON", 300)
        };
        Assert.Equal("BON", Lot.Fefo(lots, Aujourdhui)!.NumeroLot);
    }

    [Fact]
    public void Lot_perime_le_jour_meme_refuse_a_la_vente()
    {
        var ex = Assert.Throws<RegleMetierException>(() => NouveauLot(1, "A2391", 0).VerifierVendable(Aujourdhui));
        Assert.Contains("périmé", ex.Message);
    }
}
