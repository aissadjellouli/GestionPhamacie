using Pharma.Application.Tracabilite;

namespace Pharma.Application.Tests;

public class EtiquetageTests
{
    [Theory]
    [InlineData(3, 400, 0)]     // 0,75 % arrondi vers le bas : jamais « 1 % » trop tôt
    [InlineData(68, 100, 68)]
    [InlineData(100, 100, 100)]
    [InlineData(5, 0, 0)]       // pas de stock : rien à étiqueter
    public void Pourcentage_de_boites_identifiees(int identifiees, int enStock, int attendu) =>
        Assert.Equal(attendu, new EtatEtiquetage(identifiees, enStock, ModeStrict: false).Pourcentage);
}
