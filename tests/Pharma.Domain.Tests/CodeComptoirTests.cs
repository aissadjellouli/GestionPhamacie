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
    [InlineData("stock.donner", true)]
    [InlineData("stock.declarer_perime", false)]
    [InlineData("admin.utilisateurs", false)]
    [InlineData("achats.commander", false)]
    [InlineData("rapports.ventes", false)]
    public void Connexion_par_code_limitee_aux_droits_du_comptoir(string permission, bool autorise) =>
        Assert.Equal(autorise, CodeComptoir.Autorise(permission));

    [Theory]
    [InlineData("caisse.vendre", true)]
    [InlineData("caisse.remise", true)]
    [InlineData("caisse.annuler_vente", true)]
    [InlineData("caisse.retour_especes", true)]
    [InlineData("caisse.vendre_sans_ordonnance", true)]
    [InlineData("credit.depasser_plafond", true)]
    [InlineData("stock.donner", true)]
    [InlineData("prix.modifier", true)]
    [InlineData("produits.bloquer", true)]
    [InlineData("achats.receptionner", true)]
    [InlineData("role.titulaire", false)]
    [InlineData("admin.utilisateurs", false)]
    [InlineData("admin.parametres", false)]
    [InlineData("admin.sauvegardes", false)]
    [InlineData("tresorerie.gerer", false)]
    [InlineData("financements", false)]
    [InlineData("surveillance", false)]
    [InlineData("notifications", false)]
    [InlineData("psychotropes.delivrer", false)]
    [InlineData("caisse.voir_ecart", false)]
    [InlineData("caisse.remises_auto", false)]
    [InlineData("achats.regler", false)]
    [InlineData("rapports.marges", false)]
    [InlineData(null, false)]
    public void Code_d_un_collegue_limite_a_la_liste_blanche(string? permission, bool autorise) =>
        Assert.Equal(autorise, CodeComptoir.AutorisableParCollegue(permission));

    [Fact]
    public void Blocage_progressif_double_jusqu_a_24_heures()
    {
        Assert.Null(CodeComptoir.DureeBlocage(4));
        Assert.Equal(TimeSpan.FromMinutes(5), CodeComptoir.DureeBlocage(5));
        Assert.Equal(TimeSpan.FromMinutes(10), CodeComptoir.DureeBlocage(6));
        Assert.Equal(TimeSpan.FromMinutes(40), CodeComptoir.DureeBlocage(8));
        Assert.Equal(TimeSpan.FromHours(24), CodeComptoir.DureeBlocage(500));
    }
}
