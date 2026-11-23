using Pharma.Domain.Commun;
using Pharma.Domain.Factures;
using Pharma.Domain.Rapports;

namespace Pharma.Domain.Tests;

/// <summary>Correctifs de la revue : factures, trésorerie, périodes et Data Matrix.</summary>
public class RevueFacturesStockDomaineTests
{
    [Fact]
    public void Correction_sous_le_total_des_avoirs_et_reglements_refusee()
    {
        var ex = Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierCorrection(700, 200, 600));
        Assert.Contains("800", ex.Message);
    }

    [Fact]
    public void Correction_au_niveau_exact_de_ce_qui_est_couvert_acceptee() => RegleFacture.VerifierCorrection(800, 200, 600);

    [Fact]
    public void Plafond_des_avoirs_tient_aussi_pour_une_facture_rejetee()
    {
        RegleFacture.VerifierAvoir("facture", "rejetee", 1000, 800, 200, "Retour");
        Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierAvoir("facture", "rejetee", 1000, 800, 200.01m, "Retour"));
    }

    [Fact]
    public void Ligne_libre_montant_calcule_depuis_le_prix_unitaire_arrondi()
    {
        var l = RegleFacture.Libre("Article", 3, 3.335m, 19);
        Assert.Equal(3.34m, l.PrixUnitaireTtc);
        Assert.Equal(10.02m, l.MontantTtc);   // 3 x 3,34 : se vérifie à la calculatrice
    }

    [Fact]
    public void Periode_suivante_ne_finit_pas_dans_le_futur()
    {
        var aujourdhui = new DateOnly(2026, 11, 9);
        var semaine = PeriodeRapport.Calculer(TypePeriode.Semaine, aujourdhui);
        Assert.Throws<RegleMetierException>(() => semaine.Decaler(1, aujourdhui));
        Assert.Equal(new DateOnly(2026, 11, 8), semaine.Decaler(-1, aujourdhui).Au);
        var mois = PeriodeRapport.Calculer(TypePeriode.Mois, new DateOnly(2026, 10, 15));
        Assert.Throws<RegleMetierException>(() => mois.Decaler(1, aujourdhui));   // novembre se termine après le 9
    }

    [Fact]
    public void Periode_suivante_deja_passee_acceptee()
    {
        var mois = PeriodeRapport.Calculer(TypePeriode.Mois, new DateOnly(2026, 9, 15));
        Assert.Equal(new DateOnly(2026, 10, 31), mois.Decaler(1, new DateOnly(2026, 11, 9)).Au);
    }

    [Fact]
    public void Data_matrix_avec_caractere_isole_en_fin_de_code_illisible()
    {
        Assert.False(Gs1DataMatrix.TryParse("0103400930000014" + "17261130" + "10LOT1\u001d" + "2", out _));
        Assert.True(Gs1DataMatrix.TryParse("0103400930000014" + "17261130" + "10LOT1", out var ok));
        Assert.Equal("LOT1", ok!.Lot);
    }

    [Theory]
    [InlineData("261332")]   // mois 13
    [InlineData("260231")]   // 31 février
    [InlineData("-61130")]   // signe
    [InlineData("26 130")]   // espace
    public void Data_matrix_avec_date_invalide_illisible_sans_exception(string aammjj)
    {
        var code = "0103400930000014" + "17" + aammjj + "10LOT1";
        var lu = true;
        var ex = Record.Exception(() => lu = Gs1DataMatrix.TryParse(code, out _));
        Assert.Null(ex);
        Assert.False(lu);
    }
}
