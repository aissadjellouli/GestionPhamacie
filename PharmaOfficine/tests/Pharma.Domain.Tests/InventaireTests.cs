using Pharma.Domain.Commun;
using Pharma.Domain.Stock;

namespace Pharma.Domain.Tests;

public class InventaireTests
{
    [Theory]
    [InlineData(0, 0, DecisionEcart.Conforme)]
    [InlineData(-2, -400, DecisionEcart.Ajuste)]      // sous les deux seuils
    [InlineData(-3, -450, DecisionEcart.AValider)]    // trop de boîtes
    [InlineData(1, 3800, DecisionEcart.AValider)]     // une seule boîte, mais trop chère (insuline)
    public void Decision_selon_les_seuils(int ecart, decimal valeur, DecisionEcart attendu) =>
        Assert.Equal(attendu, RegleInventaire.Decider(ecart, valeur, seuilBoites: 2, seuilMontant: 2000));

    [Fact]
    public void Rayon_propose_dates_a_verifier_puis_jamais_compte_puis_le_plus_ancien()
    {
        var hier = new DateTime(2026, 11, 7);
        RayonInventaire R(string r, DateTime? dernier, int lots = 3, int aVerifier = 0, long? enCours = null) => new(r, dernier, lots, aVerifier, enCours);

        Assert.Equal("C", RegleInventaire.Proposer([R("A", null), R("C", hier, aVerifier: 1)])!.Rayon);
        Assert.Equal("B", RegleInventaire.Proposer([R("A", hier), R("B", null)])!.Rayon);
        Assert.Equal("A", RegleInventaire.Proposer([R("A", hier.AddDays(-30)), R("B", hier)])!.Rayon);
        Assert.Equal("D", RegleInventaire.Proposer([R("A", null, aVerifier: 2), R("D", hier, enCours: 9)])!.Rayon);   // finir l'inventaire commencé
        Assert.Null(RegleInventaire.Proposer([R("V", null, lots: 0)]));                                               // rayon vide
    }

    [Fact]
    public void Comptage_negatif_refuse() =>
        Assert.Throws<RegleMetierException>(() => RegleInventaire.VerifierComptage(-1));
}
