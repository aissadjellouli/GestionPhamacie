using Pharma.Domain.Commun;
using Pharma.Domain.Factures;

namespace Pharma.Domain.Tests;

public class RegleFactureTests
{
    // Vente tiers payant fictive : 2 produits, remise de 14 DA sur la part patient
    private static readonly LigneVendue[] Vente =
    [
        new(1, "T-000003", 10, "Produit A", 0, 2, 100, 400, 14),
        new(1, "T-000003", 11, "Produit B", 0, 1, 40, 160, 14)
    ];

    [Fact]
    public void Facture_patient_reprend_la_part_patient_et_la_remise()
    {
        var lignes = RegleFacture.DepuisVentes(Destinataire.Patient, Vente);
        Assert.Equal([100m, 40m, -14m], lignes.Select(l => l.MontantTtc));
        Assert.All(lignes, l => Assert.Equal("patient", l.Part));
        Assert.Equal(126m, RegleFacture.Calculer(lignes).Ttc);
    }

    [Fact]
    public void Facture_organisme_reprend_la_part_prise_en_charge_sans_remise()
    {
        var lignes = RegleFacture.DepuisVentes(Destinataire.Organisme, Vente);
        Assert.Equal([400m, 160m], lignes.Select(l => l.MontantTtc));
        Assert.Equal(200m, lignes[0].PrixUnitaireTtc);
    }

    [Fact]
    public void Tva_calculee_depuis_le_prix_ttc()
    {
        var t = RegleFacture.Calculer([RegleFacture.Libre("Article parapharmacie", 1, 1190, 19), RegleFacture.Libre("Médicament", 2, 100, 0)]);
        Assert.Equal((1200m, 190m, 1390m), (t.Ht, t.Tva, t.Ttc));
    }

    [Fact]
    public void Numeros_annuels_factures_et_avoirs()
    {
        Assert.Equal("F-2026-0007", RegleFacture.Numero(false, 2026, 7));
        Assert.Equal("AV-2026-0001", RegleFacture.Numero(true, 2026, 1));
    }

    [Fact]
    public void Rejet_correction_avoir_et_suivi_controles()
    {
        Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierRejet("facture", "emise", " "));
        Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierRejet("facture", "rejetee", "Erreur de taux"));
        Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierReprise("emise"));
        RegleFacture.VerifierReprise("rejetee");
        Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierAvoir("facture", "emise", 1000, 800, 300, "Retour"));
        Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierAvoir("facture", "remplacee", 1000, 0, 100, "Retour"));
        Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierHorsSuivi(true, null));
        RegleFacture.VerifierHorsSuivi(false, null);
    }

    [Fact]
    public void Ligne_libre_et_facture_vide_refusees()
    {
        Assert.Throws<RegleMetierException>(() => RegleFacture.Libre(" ", 1, 10, 0));
        Assert.Throws<RegleMetierException>(() => RegleFacture.Libre("Frais", 0, 10, 0));
        Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierEmission([]));
    }

    [Fact]
    public void Reglement_impute_dans_l_ordre_au_plus_le_reste() =>
        Assert.Equal([(5L, 300m), (6L, 200m)], RegleFacture.Imputer(500, [(5L, 300m), (6L, 450m), (7L, 100m)]));

    [Theory]
    [InlineData(0, "Zéro dinar")]
    [InlineData(1, "Un dinar")]
    [InlineData(71, "Soixante et onze dinars")]
    [InlineData(80, "Quatre-vingts dinars")]
    [InlineData(91, "Quatre-vingt-onze dinars")]
    [InlineData(200, "Deux cents dinars")]
    [InlineData(1250.5, "Mille deux cent cinquante dinars et cinquante centimes")]
    [InlineData(80000, "Quatre-vingt mille dinars")]
    [InlineData(2_300_021, "Deux millions trois cent mille vingt et un dinars")]
    public void Montant_en_lettres(decimal montant, string attendu) => Assert.Equal(attendu, MontantEnLettres.Dinars(montant));
}
