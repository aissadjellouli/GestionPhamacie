using Pharma.Desktop.Services;

namespace Pharma.Application.Tests;

/// <summary>Saisie de la remise au poste : une valeur négative ou trop grande est refusée avec un message, jamais ramenée à 0 en silence.</summary>
public class SaisieRemisePosteTests
{
    [Theory]
    [InlineData("-5")]
    [InlineData("-")]
    [InlineData(" -0 ")]
    [InlineData("5-")]
    public void Saisie_negative_refusee_avec_message(string saisie)
    {
        var (valeur, erreur) = SaisieRemise.Lire(saisie);
        Assert.Null(valeur);
        Assert.Equal("La remise ne peut pas être négative : saisissez un nombre entier de 0 à 100 %.", erreur);
    }

    [Theory]
    [InlineData("101")]
    [InlineData("1000")]
    [InlineData("99999999999999")]
    public void Saisie_superieure_a_100_refusee_avec_message(string saisie)
    {
        var (valeur, erreur) = SaisieRemise.Lire(saisie);
        Assert.Null(valeur);
        Assert.Equal("La remise est un nombre entier de 0 à 100 %.", erreur);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("0", 0)]
    [InlineData("007", 7)]
    [InlineData("15", 15)]
    [InlineData("100", 100)]
    public void Saisie_valide_retenue(string saisie, int attendu)
    {
        var (valeur, erreur) = SaisieRemise.Lire(saisie);
        Assert.Equal(attendu, valeur);
        Assert.Null(erreur);
    }
}
