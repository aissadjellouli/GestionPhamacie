using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Domain.Tests;

public class RegleRetourTests
{
    [Theory]
    [InlineData(350, 3, 1, 116.67)]
    [InlineData(240, 2, 2, 240)]
    [InlineData(100, 3, 2, 66.67)]
    public void Montant_rendu_au_prorata_de_la_part_payee(decimal montant, int vendue, int rendue, decimal attendu) =>
        Assert.Equal(attendu, RegleRetour.Montant(montant, vendue, rendue));

    [Fact]
    public void Quantite_bornee_par_le_vendu_moins_le_deja_rendu()
    {
        RegleRetour.VerifierQuantite("P", 1, 3, 2);
        Assert.Throws<RegleMetierException>(() => RegleRetour.VerifierQuantite("P", 2, 3, 2));
        Assert.Throws<RegleMetierException>(() => RegleRetour.VerifierQuantite("P", 0, 3, 0));
    }

    [Fact]
    public void Tiers_payant_annulee_ou_bordereau_refuses()
    {
        RegleRetour.VerifierVente("validee", false, 0);
        Assert.Throws<RegleMetierException>(() => RegleRetour.VerifierVente("validee", false, 280));
        Assert.Throws<RegleMetierException>(() => RegleRetour.VerifierVente("annulee", false, 0));
        Assert.Throws<RegleMetierException>(() => RegleRetour.VerifierVente("validee", true, 0));
    }

    [Fact]
    public void Destination_obligatoire() =>
        Assert.Throws<RegleMetierException>(() => RegleRetour.Destination(null));
}
