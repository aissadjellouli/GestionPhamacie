using Pharma.Application.SecondServeur;

namespace Pharma.Application.Tests;

/// <summary>Bascule du 2e serveur, lot 7 : conduite du démarrage de l'API quand le saut des compteurs échoue.</summary>
public class SautCompteursBasculeTests
{
    [Theory]
    [InlineData("42883")]   // script 164 pas passé
    [InlineData("42P01")]
    [InlineData("25006")]   // lecture seule
    public void Fonction_absente_ou_lecture_seule_le_demarrage_continue(string sqlState)
        => Assert.Equal(SuiteSautCompteurs.Poursuivre, SautCompteursBascule.Decider(sqlState, baseInjoignable: false));

    [Fact]
    public void Saut_inactif_sans_2e_serveur_declare_et_actif_avec()
    {
        Assert.False(SautCompteursBascule.Applicable(DeuxServeursDeclares.Est(null), demoActive: false, baseEnRecovery: false));
        Assert.False(SautCompteursBascule.Applicable(DeuxServeursDeclares.Est("  "), demoActive: false, baseEnRecovery: false));
        Assert.True(SautCompteursBascule.Applicable(DeuxServeursDeclares.Est("SRV-2"), demoActive: false, baseEnRecovery: false));
        Assert.False(SautCompteursBascule.Applicable(true, demoActive: true, baseEnRecovery: false));
        Assert.False(SautCompteursBascule.Applicable(true, demoActive: false, baseEnRecovery: true));
    }

    [Fact]
    public void Base_injoignable_le_demarrage_continue()
        => Assert.Equal(SuiteSautCompteurs.Poursuivre, SautCompteursBascule.Decider(null, baseInjoignable: true));

    [Theory]
    [InlineData("23505")]   // violation d'unicité
    [InlineData("40001")]   // sérialisation
    [InlineData("P0001")]   // exception levée par une garde
    [InlineData(null)]
    public void Toute_autre_erreur_le_demarrage_est_refuse(string? sqlState)
        => Assert.Equal(SuiteSautCompteurs.Refuser, SautCompteursBascule.Decider(sqlState, baseInjoignable: false));
}
