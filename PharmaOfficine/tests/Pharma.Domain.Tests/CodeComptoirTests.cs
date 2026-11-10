using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Domain.Tests;

public class CodeComptoirTests
{
    [Theory]
    [InlineData("2580")]
    [InlineData("731946")]
    public void Code_de_4_a_6_chiffres_accepte(string code) => CodeComptoir.VerifierFormat(code);

    [Theory]
    [InlineData(null)]
    [InlineData("123")]
    [InlineData("1234567")]
    [InlineData("12a4")]
    [InlineData("0000")]
    [InlineData("1234")]
    [InlineData("98765")]
    public void Code_trop_court_trop_long_ou_evident_refuse(string? code) =>
        Assert.Throws<RegleMetierException>(() => CodeComptoir.VerifierFormat(code));

    [Theory]
    [InlineData("caisse.vendre", true)]
    [InlineData("caisse.remise", true)]
    [InlineData("credit.accorder", true)]
    [InlineData("stock.consulter", true)]
    [InlineData("admin.utilisateurs", false)]
    [InlineData("achats.commander", false)]
    [InlineData("rapports.ventes", false)]
    public void Connexion_par_code_limitee_aux_droits_du_comptoir(string permission, bool autorise) =>
        Assert.Equal(autorise, CodeComptoir.Autorise(permission));
}
