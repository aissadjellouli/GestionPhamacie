using Pharma.Domain.Alertes;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Domain.Tests;

public class AdministrationTests
{
    [Fact]
    public void Seuil_rouge_plus_court_que_l_orange() =>
        Assert.Throws<RegleMetierException>(() => RegleSeuils.VerifierGeneral(orange: 90, rouge: 90));

    [Fact]
    public void Seuils_generaux_valides() => RegleSeuils.VerifierGeneral(orange: 180, rouge: 90);

    [Fact]
    public void Seuil_particulier_compare_au_general_quand_vide()
    {
        // Rouge grossiste 200 avec orange vide (général 180) : incohérent une fois complété
        var ex = Assert.Throws<RegleMetierException>(() => RegleSeuils.VerifierParticulier("Grossiste A", null, 200, 180, 90));
        Assert.Contains("200", ex.Message);
        RegleSeuils.VerifierParticulier("Grossiste A", 195, null, 180, 90);   // orange 195, rouge hérité 90 : correct
    }

    [Fact]
    public void Seuil_particulier_nul_ou_negatif_refuse() =>
        Assert.Throws<RegleMetierException>(() => RegleSeuils.VerifierParticulier("Produit", 0, null, 180, 90));

    [Theory]
    [InlineData(" Amina.B ", "amina.b")]
    [InlineData("vendeur_2", "vendeur_2")]
    public void Identifiant_normalise(string saisi, string attendu) =>
        Assert.Equal(attendu, RegleCompte.NormaliserIdentifiant(saisi));

    [Theory]
    [InlineData("ab")]
    [InlineData("employé")]
    [InlineData("deux mots")]
    [InlineData(null)]
    public void Identifiant_invalide(string? saisi) =>
        Assert.Throws<RegleMetierException>(() => RegleCompte.NormaliserIdentifiant(saisi));

    [Theory]
    [InlineData("court")]
    [InlineData("        ")]
    [InlineData(null)]
    public void Mot_de_passe_trop_faible(string? mdp) =>
        Assert.Throws<RegleMetierException>(() => RegleCompte.VerifierMotDePasse(mdp));
}
