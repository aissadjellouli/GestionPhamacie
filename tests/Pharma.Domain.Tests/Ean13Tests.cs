using Pharma.Domain.Commun;

namespace Pharma.Domain.Tests;

public class Ean13Tests
{
    [Theory]
    [InlineData("2000000001272")]
    [InlineData("2000000004518")]
    public void Codes_internes_valides(string code)
    {
        Assert.True(Ean13.EstValide(code));
        Assert.True(Ean13.Parse(code).EstInterne);
    }

    [Fact]
    public void Cle_incorrecte_refusee() => Assert.False(Ean13.EstValide("2000000001273"));

    [Fact]
    public void Generation_depuis_la_sequence() => Assert.Equal("2000000001272", Ean13.Interne(127).Valeur);
}
