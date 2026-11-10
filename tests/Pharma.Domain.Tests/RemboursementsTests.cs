using Pharma.Domain.Ventes;

namespace Pharma.Domain.Tests;

public class RemboursementsTests
{
    [Fact]
    public void Retour_rendu_au_prix_paye_apres_remise()
    {
        // Ligne à 1 000 DA, remise de 10 % sur la vente : le patient a payé 900 DA
        Assert.Equal(900m, RegleRetour.MontantPaye(1000, 2, 2, 900, 1000));
        Assert.Equal(450m, RegleRetour.MontantPaye(1000, 2, 1, 900, 1000));
        Assert.Equal(500m, RegleRetour.MontantPaye(1000, 2, 1, 1000, 1000));
    }

    [Fact]
    public void Total_rendu_jamais_au_dela_de_la_part_payee_et_solde_exact_au_dernier_retour()
    {
        Assert.Equal(300m, RegleRetour.TotalARendre(333.34m, 900, 600, toutRendu: false));
        Assert.Equal(300.01m, RegleRetour.TotalARendre(300m, 900, 599.99m, toutRendu: true));
    }

    [Theory]
    [InlineData("especes", 470, 0, 0, 0, 470)]          // vente payée : tout est rendu
    [InlineData("especes", 470, 235, 0, 0, 235)]        // un retour a déjà rendu la moitié
    [InlineData("credit", 470, 0, 470, 470, 0)]         // crédit non remboursé : la dette disparaît
    [InlineData("credit", 470, 0, 170, 170, 300)]       // crédit remboursé en partie : 300 DA rendus
    [InlineData("credit", 470, 0, 0, 0, 470)]           // crédit entièrement remboursé : tout est rendu
    [InlineData("credit", 470, 200, 470, 270, 0)]       // retour déjà déduit de la dette
    public void Annulation_rend_le_reste_et_diminue_la_dette(string mode, decimal part, decimal deja, decimal solde, decimal reduction, decimal aRendre) =>
        Assert.Equal((reduction, aRendre), RegleAnnulation.Remboursement(mode, part, deja, solde));
}
