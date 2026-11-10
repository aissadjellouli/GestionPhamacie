using Pharma.Domain.Achats;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Domain.Tests;

public class RegleReglementTests
{
    private static readonly DateOnly Jour = new(2026, 11, 9);

    [Fact]
    public void Montant_reparti_sur_les_factures_les_plus_anciennes()
    {
        FactureAPayer[] f = [new(3, new DateTime(2026, 11, 5), 500), new(1, new DateTime(2026, 10, 1), 300), new(2, new DateTime(2026, 10, 20), 400)];
        Assert.Equal([(1L, 300m), (2L, 400m), (3L, 100m)], RegleReglement.ImputerAuxPlusAnciennes(800, f));
        Assert.Equal([(1L, 250m)], RegleReglement.ImputerAuxPlusAnciennes(250, f));
    }

    [Theory]
    [InlineData("cheque", null, null)]
    [InlineData("virement", " ", null)]
    [InlineData("effet", "EF-1", null)]
    public void Reference_et_echeance_obligatoires_selon_le_moyen(string moyen, string? reference, string? echeance) =>
        Assert.Throws<RegleMetierException>(() => RegleReglement.Verifier(100, RegleReglement.Moyen(moyen), reference,
            echeance is null ? null : DateOnly.Parse(echeance), 0, Jour, Jour));

    [Fact]
    public void Complement_du_tiroir_seulement_en_especes_et_au_plus_le_montant()
    {
        RegleReglement.Verifier(1000, MoyenReglement.Especes, null, null, 300, Jour, Jour);
        Assert.Throws<RegleMetierException>(() => RegleReglement.Verifier(1000, MoyenReglement.Cheque, "CHQ-1", null, 300, Jour, Jour));
        Assert.Throws<RegleMetierException>(() => RegleReglement.Verifier(1000, MoyenReglement.Especes, null, null, 1200, Jour, Jour));
        Assert.Throws<RegleMetierException>(() => RegleReglement.Verifier(1000, MoyenReglement.Especes, null, null, 0, Jour.AddDays(1), Jour));
    }

    [Fact]
    public void Imputation_au_plus_le_reste_de_chaque_facture_et_le_montant_regle()
    {
        var restes = new Dictionary<long, decimal> { [1] = 300, [2] = 400 };
        RegleReglement.VerifierImputations(700, [(1, 300m), (2, 400m)], restes);
        Assert.Throws<RegleMetierException>(() => RegleReglement.VerifierImputations(700, [(1, 350m)], restes));
        Assert.Throws<RegleMetierException>(() => RegleReglement.VerifierImputations(500, [(1, 300m), (2, 400m)], restes));
        Assert.Throws<RegleMetierException>(() => RegleReglement.VerifierImputations(500, [(9, 100m)], restes));
    }

    [Fact]
    public void Credit_rembourse_au_plus_la_dette()
    {
        RegleReglementCredit.Verifier(200, 210, "especes");
        Assert.Throws<RegleMetierException>(() => RegleReglementCredit.Verifier(250, 210, "especes"));
        Assert.Throws<RegleMetierException>(() => RegleReglementCredit.Verifier(10, 0, "especes"));
        Assert.Throws<RegleMetierException>(() => RegleReglementCredit.Verifier(10, 210, "credit"));
    }
}
