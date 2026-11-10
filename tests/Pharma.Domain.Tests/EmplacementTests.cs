using Pharma.Domain.Commun;
using Pharma.Domain.Stock;

namespace Pharma.Domain.Tests;

public class EmplacementTests
{
    [Theory]
    [InlineData(" a ", "02", "3", "A-02-3")]
    [InlineData("F", "01", null, "F-01")]
    [InlineData("r", null, null, "R")]
    public void Code_compose_en_majuscules(string rayon, string? etagere, string? tiroir, string attendu) =>
        Assert.Equal(attendu, RegleEmplacement.Composer(rayon, etagere, tiroir).Code);

    [Theory]
    [InlineData(null, "01", null)]          // rayon obligatoire
    [InlineData("A", null, "3")]            // tiroir sans étagère
    [InlineData("A-1", null, null)]         // tiret interdit dans un niveau
    [InlineData("A", "étagère", null)]      // lettres sans accent et chiffres seulement
    public void Code_refuse(string? rayon, string? etagere, string? tiroir) =>
        Assert.Throws<RegleMetierException>(() => RegleEmplacement.Composer(rayon, etagere, tiroir));

    [Fact]
    public void Rangement_vente_et_reserve_dans_la_bonne_zone()
    {
        RegleEmplacement.VerifierRangement(ZoneEmplacement.Vente, ZoneEmplacement.Reserve, 1, 2);
        RegleEmplacement.VerifierRangement(null, null, null, null);   // non rangé
        Assert.Throws<RegleMetierException>(() => RegleEmplacement.VerifierRangement(ZoneEmplacement.Reserve, null, 1, null));
        Assert.Throws<RegleMetierException>(() => RegleEmplacement.VerifierRangement(ZoneEmplacement.Vente, ZoneEmplacement.Vente, 1, 2));
    }
}
