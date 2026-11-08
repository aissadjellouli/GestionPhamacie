using Pharma.Infrastructure.Impression;

namespace Pharma.Architecture.Tests;

/// <summary>Code-barres dessiné sur la planche d'étiquettes (adaptateur d'impression).</summary>
public class ImpressionTests
{
    [Fact]
    public void Ean13_encode_selon_la_norme()
    {
        // 4006381333931 : exemple de référence ; premier chiffre 4 => parités A B A A B B
        var m = DocumentsAchats.ModulesEan13("4006381333931");
        Assert.Equal(95, m.Length);
        Assert.StartsWith("101" + "0001101" + "0100111" + "0101111", m);   // 0 en A, 0 en B, 6 en A
        Assert.Equal("01010", m.Substring(45, 5));
        Assert.EndsWith("1100110" + "101", m);                            // 1 en C, garde finale
    }

    [Fact]
    public void Ean13_invalide_refuse() =>
        Assert.Throws<ArgumentException>(() => DocumentsAchats.ModulesEan13("2000000000047"));
}
