using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Domain.Tests;

public class VenteTests
{
    [Theory]
    // Montants relevés sur de vraies factures Chifa (CNAS) : part officine au centime
    [InlineData(386.94, 3, 80, 928.66)]   // 1 160,82 x 80 % = 928,656
    [InlineData(156.48, 3, 100, 469.44)]
    [InlineData(10.01, 1, 50, 5.01)]      // 5,005 : arrondi au centime supérieur à la moitié
    public void Part_de_l_organisme_au_centime_comme_Chifa(decimal prix, int quantite, decimal taux, decimal attendu)
    {
        var ligne = new LigneVente { Quantite = quantite, PrixUnitaire = prix };
        ligne.CalculerTiersPayant(baseRemboursement: prix, tauxPourcent: taux);
        Assert.Equal(attendu, ligne.MontantOrganisme);
        Assert.Equal(prix * quantite - attendu, ligne.MontantPatient);
    }

    [Fact]
    public void Part_cnas_a_80_pour_cent()
    {
        var ligne = new LigneVente { Quantite = 2, PrixUnitaire = 350m };
        ligne.CalculerTiersPayant(baseRemboursement: 350m, tauxPourcent: 80m);
        Assert.Equal(560m, ligne.MontantOrganisme);
        Assert.Equal(140m, ligne.MontantPatient);
    }

    [Fact]
    public void Credit_refuse_au_dela_du_plafond()
    {
        var patient = new Patient { PlafondCredit = 5000m };
        patient.AccorderCredit(4500m, peutDepasserPlafond: false);
        Assert.Throws<RegleMetierException>(() => patient.AccorderCredit(1000m, peutDepasserPlafond: false));
        patient.AccorderCredit(1000m, peutDepasserPlafond: true);
        Assert.Equal(5500m, patient.SoldeCredit);
    }

    [Fact]
    public void Totaux_recalcules_a_chaque_ligne()
    {
        var vente = new Vente();
        var ligne = new LigneVente { Quantite = 2, PrixUnitaire = 350m };
        ligne.CalculerTiersPayant(baseRemboursement: 350m, tauxPourcent: 80m);
        vente.AjouterLigne(ligne);
        vente.AjouterLigne(new LigneVente { Quantite = 1, PrixUnitaire = 120m });

        Assert.Equal(820m, vente.MontantTotal);
        Assert.Equal(560m, vente.PartOrganisme);
        Assert.Equal(260m, vente.PartPatient);
    }
}
