using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;
using Pharma.Domain.Stock;
using Pharma.Domain.Ventes;

namespace Pharma.Domain.Tests;

public class CodesMetierTests
{
    [Fact]
    public void Chaque_valeur_a_un_code_et_revient_a_l_identique()
    {
        foreach (var v in Enum.GetValues<ClasseReglementaire>())
            Assert.Equal(v, CodesMetier.ClasseDepuisCode(v.Code()));
        foreach (var v in Enum.GetValues<StatutBoite>())
            Assert.Equal(v, CodesMetier.StatutBoiteDepuisCode(v.Code()));
        foreach (var v in Enum.GetValues<TypeVente>())
            Assert.Equal(v, CodesMetier.TypeVenteDepuisCode(v.Code()));
        foreach (var v in Enum.GetValues<StatutVente>())
            Assert.Equal(v, CodesMetier.StatutVenteDepuisCode(v.Code()));
    }

    [Theory]
    [InlineData(ClasseReglementaire.ListeI, "liste_i")]
    [InlineData(ClasseReglementaire.ListeII, "liste_ii")]
    [InlineData(ClasseReglementaire.Stupefiant, "stupefiant")]
    public void Codes_identiques_a_la_base(ClasseReglementaire classe, string code) =>
        Assert.Equal(code, classe.Code());

    [Fact]
    public void Code_inconnu_refuse() =>
        Assert.Throws<ArgumentException>(() => CodesMetier.ClasseDepuisCode("inconnu"));
}
