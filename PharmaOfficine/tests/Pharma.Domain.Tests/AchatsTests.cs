using Pharma.Domain.Achats;
using Pharma.Domain.Commun;

namespace Pharma.Domain.Tests;

public class AchatsTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 11, 8);

    private static LigneReceptionSaisie Ligne(int qte = 10, string lot = "L1", DateOnly? peremption = null, string? gtin = null, params string[] series) =>
        new(1, "Produit", lot, peremption ?? Aujourdhui.AddYears(2), qte, 100m, gtin, series);

    [Fact]
    public void Reception_valide_acceptee() =>
        RegleAchats.VerifierReception("BL-1", [Ligne(), Ligne(lot: "L2", gtin: "03400000000001", series: ["S1", "S2"])], Aujourdhui);

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void Numero_de_bl_obligatoire(string? bl) =>
        Assert.Throws<RegleMetierException>(() => RegleAchats.VerifierReception(bl, [Ligne()], Aujourdhui));

    [Fact]
    public void Lot_perime_ou_expirant_ce_jour_refuse() =>
        Assert.Contains("périmé", Assert.Throws<RegleMetierException>(() =>
            RegleAchats.VerifierReception("BL", [Ligne(peremption: Aujourdhui)], Aujourdhui)).Message);

    [Fact]
    public void Plus_de_series_que_de_boites_refuse() =>
        Assert.Throws<RegleMetierException>(() =>
            RegleAchats.VerifierReception("BL", [Ligne(qte: 1, gtin: "03400000000001", series: ["S1", "S2"])], Aujourdhui));

    [Fact]
    public void Serie_scannee_deux_fois_refusee() =>
        Assert.Throws<RegleMetierException>(() =>
            RegleAchats.VerifierReception("BL", [Ligne(gtin: "03400000000001", series: ["S1", "S1"])], Aujourdhui));

    [Fact]
    public void Series_sans_gtin_refusees() =>
        Assert.Throws<RegleMetierException>(() => RegleAchats.VerifierReception("BL", [Ligne(series: ["S1"])], Aujourdhui));

    [Fact]
    public void Commande_vide_ou_doublon_refusee()
    {
        Assert.Throws<RegleMetierException>(() => RegleAchats.VerifierCommande([]));
        Assert.Throws<RegleMetierException>(() => RegleAchats.VerifierCommande(
            [new LigneCommandeSaisie(1, "A", 1, 10), new LigneCommandeSaisie(1, "A", 2, 10)]));
        Assert.Throws<RegleMetierException>(() => RegleAchats.VerifierCommande([new LigneCommandeSaisie(1, "A", 0, 10)]));
    }

    [Theory]
    [InlineData(10, 100, 0, 1000)]
    [InlineData(3, 33.33, 19, 118.99)]   // 99,99 × 1,19 = 118,988 arrondi au centime
    public void Montant_ttc(int qte, decimal prixHt, decimal tva, decimal attendu) =>
        Assert.Equal(attendu, RegleAchats.MontantTtc(qte, prixHt, tva));

    [Theory]
    [InlineData(StatutRetour.Brouillon, StatutRetour.Envoye, true)]
    [InlineData(StatutRetour.Brouillon, StatutRetour.Refuse, true)]
    [InlineData(StatutRetour.Envoye, StatutRetour.AvoirRecu, true)]
    [InlineData(StatutRetour.Envoye, StatutRetour.Refuse, true)]
    [InlineData(StatutRetour.Brouillon, StatutRetour.AvoirRecu, false)]   // pas d'avoir sans envoi
    [InlineData(StatutRetour.AvoirRecu, StatutRetour.Refuse, false)]
    [InlineData(StatutRetour.Refuse, StatutRetour.Envoye, false)]
    public void Transitions_du_retour(StatutRetour de, StatutRetour vers, bool permis)
    {
        if (permis) RegleRetour.VerifierTransition(de, vers);
        else Assert.Throws<RegleMetierException>(() => RegleRetour.VerifierTransition(de, vers));
    }

    [Fact]
    public void Retour_au_dela_du_stock_refuse() =>
        Assert.Throws<RegleMetierException>(() => RegleRetour.VerifierCreation(6, 5, "L1"));

    [Fact]
    public void Codes_des_motifs_et_statuts_identiques_a_la_base()
    {
        Assert.Equal("peremption_proche", MotifRetour.PeremptionProche.Code());
        Assert.Equal(StatutRetour.AvoirRecu, CodesMetier.StatutRetourDepuisCode("avoir_recu"));
    }
}
