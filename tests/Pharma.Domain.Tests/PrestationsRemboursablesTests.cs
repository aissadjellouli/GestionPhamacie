using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Domain.Tests;

/// <summary>Prestations remboursables par un organisme et garde-fou du prix à 0 (décisions du titulaire, 2026-11-15, script 108).</summary>
public class PrestationsRemboursablesTests
{
    private static readonly TypePrestation Injection = new(3, "INJ", "Injection", 300m, 9m, true);

    private static Vente TiersPayant80() => new() { Type = TypeVente.TiersPayant, TauxTiersPayant = 80 };

    [Fact]
    public void Prestation_a_prix_zero_refusee_avec_le_message_d_administration()
    {
        var ex = Assert.Throws<RegleMetierException>(() => new Vente().AjouterPrestation(Injection with { Prix = 0 }, 1));
        Assert.Equal("Injection : prix à fixer dans Administration › Prestations.", ex.Message);
    }

    [Fact]
    public void Prestation_remboursable_part_organisme_comme_un_produit()
    {
        // taux 80 % × min(300, 250) × 2 = 400 ; le patient paie le reste
        var v = TiersPayant80();
        var l = v.AjouterPrestation(Injection with { Remboursable = true, TarifReference = 250m }, 2);
        Assert.True(l.PriseEnCharge);
        Assert.Equal((400m, 200m), (l.MontantOrganisme, l.MontantPatient));
        Assert.Equal((400m, 200m, 600m), (v.PartOrganisme, v.PartPatient, v.MontantTotal));
    }

    [Fact]
    public void Prestation_remboursable_tarif_au_dessus_du_prix_plafonne_au_prix()
    {
        var l = TiersPayant80().AjouterPrestation(Injection with { Remboursable = true, TarifReference = 500m }, 1);
        Assert.Equal(240m, l.MontantOrganisme);
    }

    [Fact]
    public void Prestation_non_remboursable_inchangee_le_patient_paie_tout()
    {
        var v = TiersPayant80();
        var l = v.AjouterPrestation(Injection, 1);
        Assert.False(l.PriseEnCharge);
        Assert.Equal((0m, 300m, 0m), (l.MontantOrganisme, l.MontantPatient, v.PartOrganisme));
    }

    [Fact]
    public void Prestation_remboursable_en_vente_libre_le_patient_paie_tout()
    {
        var l = new Vente().AjouterPrestation(Injection with { Remboursable = true, TarifReference = 250m }, 1);
        Assert.Equal(0m, l.MontantOrganisme);
    }

    [Fact]
    public void Prestation_remboursable_entre_dans_le_reste_a_charge_saisi()
    {
        var v = TiersPayant80();
        v.AjouterPrestation(Injection with { Remboursable = true, TarifReference = 250m }, 1);
        v.SaisirResteACharge(100m);   // possible : la prestation est prise en charge
        Assert.Equal(200m, v.PartOrganisme);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(true, 0.0)]
    [InlineData(true, 10.005)]
    public void Remboursable_sans_tarif_valable_refusee(bool remboursable, double? tarif) =>
        Assert.Equal("tarif_reference", Assert.Throws<RegleMetierException>(() =>
            RegleTypePrestation.VerifierRemboursement(remboursable, tarif is null ? null : (decimal)tarif.Value)).Champ);

    [Fact]
    public void Non_remboursable_le_tarif_n_est_pas_garde()
    {
        Assert.Null(RegleTypePrestation.VerifierRemboursement(false, 120m));
        Assert.Equal(120m, RegleTypePrestation.VerifierRemboursement(true, 120m));
    }
}
