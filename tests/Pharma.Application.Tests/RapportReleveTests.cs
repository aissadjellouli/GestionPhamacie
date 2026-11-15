using Pharma.Application.Caisse;

namespace Pharma.Application.Tests;

/// <summary>Recette 2026-11-15 : le PDF d'une relève (caisse restée ouverte) ne s'intitule plus « Rapport de clôture ».</summary>
public class RapportReleveTests
{
    private static DetailSession Session(bool releve) => new(1, 1, "Dr Titulaire", "Poste ADH", DateTime.UtcNow.AddHours(-3), 5000,
        DateTime.UtcNow, 4550, 9550, 6250, -3300, 7, 2147.50m, [], Releve: releve);

    [Fact]
    public void Releve_titre_rapport_de_releve_et_heure_de_releve()
    {
        Assert.Equal(("Rapport de relève de caisse", "Relève"), (Session(true).TitreRapport, Session(true).LibelleFin));
    }

    [Fact]
    public void Cloture_garde_son_titre()
    {
        Assert.Equal(("Rapport de clôture de caisse", "Clôture"), (Session(false).TitreRapport, Session(false).LibelleFin));
    }
}
