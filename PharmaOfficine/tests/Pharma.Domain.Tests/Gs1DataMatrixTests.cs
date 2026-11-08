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
}
