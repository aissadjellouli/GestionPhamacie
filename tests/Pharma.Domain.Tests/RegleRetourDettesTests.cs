using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Domain.Tests;

/// <summary>
/// Retour et dettes du patient (décision du titulaire du 2026-11-21, script 158) : 1) sans dette, tout ce qu'il a payé lui est rendu ;
/// 2) il a payé sa part et doit encore de l'argent : choix exigé, « déduire de ses dettes » (recommandé) ou « rendre quand même » ;
/// 3) achat à crédit : rien n'est rendu, sa dette diminue. S'applique aussi à la part de la régularisation « à la charge du patient ».
/// </summary>
public class RegleRetourDettesTests
{
    [Fact]
    public void Patient_sans_dette_tout_est_rendu_sans_choix()
    {
        var r = RegleRetour.Rembourser(0, 40, 0, 0, 0, true, null);
        Assert.Equal((0m, 0m, 0m, 40m, false), (r.DetteVenteDiminuee, r.RegularisationDiminuee, r.ImputeSurDettes, r.ARendre, r.ChoixExige));
    }

    [Fact]
    public void Patient_inconnu_tout_est_rendu_meme_avec_une_dette_saisie()
    {
        var r = RegleRetour.Rembourser(0, 40, 0, 0, 500, false, null);
        Assert.Equal((40m, false), (r.ARendre, r.ChoixExige));
    }

    [Fact]
    public void Patient_endette_qui_a_paye_choix_exige_avant_tout()
    {
        var e = Assert.Throws<RegleMetierException>(() => RegleRetour.Rembourser(0, 40, 0, 0, 200, true, null));
        Assert.Equal("dettes_patient", e.Champ);
        Assert.Contains("200", e.Message);
        Assert.Throws<RegleMetierException>(() => RegleRetour.Rembourser(0, 40, 0, 0, 200, true, "autre"));
    }

    [Fact]
    public void Deduire_de_ses_dettes_au_plus_ce_qu_il_doit_le_surplus_est_rendu()
    {
        var tout = RegleRetour.Rembourser(0, 40, 0, 0, 200, true, RegleRetour.DettesDeduire);
        Assert.Equal((40m, 0m, true, 200m), (tout.ImputeSurDettes, tout.ARendre, tout.ChoixExige, tout.DetteRestante));
        var surplus = RegleRetour.Rembourser(0, 40, 0, 0, 15, true, RegleRetour.DettesDeduire);
        Assert.Equal((15m, 25m), (surplus.ImputeSurDettes, surplus.ARendre));
    }

    [Fact]
    public void Rendre_quand_meme_rien_n_est_impute()
    {
        var r = RegleRetour.Rembourser(0, 40, 0, 0, 200, true, RegleRetour.DettesRendre);
        Assert.Equal((0m, 40m, true), (r.ImputeSurDettes, r.ARendre, r.ChoixExige));
    }

    [Fact]
    public void Achat_a_credit_rien_n_est_rendu_la_dette_diminue_sans_choix()
    {
        // Vente à crédit de 100, rien payé : retour de 40, dette 100 → 60 ; plus rien à rendre, aucun choix
        var r = RegleRetour.Rembourser(40, 0, 0, 0, 100, true, null);
        Assert.Equal((40m, 0m, 0m, false, 60m), (r.DetteVenteDiminuee, r.ImputeSurDettes, r.ARendre, r.ChoixExige, r.DetteRestante));
    }

    [Fact]
    public void Credit_en_partie_paye_et_autres_dettes_la_part_payee_suit_la_regle_du_choix()
    {
        // Retour de 40 sur une vente à crédit : 10 impayés retirés de la dette, 30 déjà payés ; il doit encore 50 sur une autre vente
        var e = Assert.Throws<RegleMetierException>(() => RegleRetour.Rembourser(10, 30, 0, 0, 60, true, null));
        Assert.Equal("dettes_patient", e.Champ);
        var r = RegleRetour.Rembourser(10, 30, 0, 0, 60, true, RegleRetour.DettesDeduire);
        Assert.Equal((10m, 30m, 0m), (r.DetteVenteDiminuee, r.ImputeSurDettes, r.ARendre));
    }

    [Fact]
    public void Regularisation_impayee_retiree_de_la_dette_jamais_rendue()
    {
        // Vente payée ; écart 40 mis à sa charge, impayé (sa seule dette) ; retour de 2 boîtes sur 5 : 16 retirés de sa dette.
        // Il doit encore 24 (régularisation des boîtes gardées) : choix exigé pour les 40 payés de sa part
        var e = Assert.Throws<RegleMetierException>(() => RegleRetour.Rembourser(0, 40, 16, 40, 40, true, null));
        Assert.Equal("dettes_patient", e.Champ);
        var rendre = RegleRetour.Rembourser(0, 40, 16, 40, 40, true, RegleRetour.DettesRendre);
        Assert.Equal((16m, 0m, 40m, 24m), (rendre.RegularisationDiminuee, rendre.ImputeSurDettes, rendre.ARendre, rendre.DetteRestante));
        var deduire = RegleRetour.Rembourser(0, 40, 16, 40, 40, true, RegleRetour.DettesDeduire);
        Assert.Equal((16m, 24m, 16m), (deduire.RegularisationDiminuee, deduire.ImputeSurDettes, deduire.ARendre));
    }

    [Fact]
    public void Regularisation_payee_rendue_avec_la_part_du_patient()
    {
        // Écart 40 payé ; plus aucune dette : 40 + 16 rendus
        var r = RegleRetour.Rembourser(0, 40, 16, 0, 0, true, null);
        Assert.Equal((0m, 56m, false), (r.RegularisationDiminuee, r.ARendre, r.ChoixExige));
    }

    [Fact]
    public void Regularisation_en_partie_payee_l_impaye_d_abord_puis_le_choix()
    {
        // Écart 40 dont 30 payés (10 dus) ; autre dette 50 : dette 60. Retour : 16 de l'écart → 10 retirés de la dette, 6 payés à rendre
        var r = RegleRetour.Rembourser(0, 40, 16, 10, 60, true, RegleRetour.DettesDeduire);
        Assert.Equal((10m, 46m, 0m, 50m), (r.RegularisationDiminuee, r.ImputeSurDettes, r.ARendre, r.DetteRestante));
    }

    [Fact]
    public void Part_de_la_regularisation_au_prorata_de_la_part_caisse_des_boites_rendues()
    {
        // 5 boîtes, part caisse 400 ; écart 40 : 2 boîtes → 16 ; après, il reste 24 sur 3 boîtes (240) : 1 boîte → 8
        Assert.Equal(16m, RegleRetour.MontantRegularisation(400, 5, 2, 40, 400));
        Assert.Equal(8m, RegleRetour.MontantRegularisation(400, 5, 1, 24, 240));
        // Ligne non remboursée, régularisation épuisée : rien
        Assert.Equal(0m, RegleRetour.MontantRegularisation(0, 5, 2, 40, 400));
        Assert.Equal(0m, RegleRetour.MontantRegularisation(400, 5, 2, 0, 400));
        // Jamais plus que ce qui reste
        Assert.Equal(10m, RegleRetour.MontantRegularisation(400, 5, 5, 10, 80));
    }

    [Fact]
    public void Regularisation_faite_apres_un_retour_porte_sur_les_seules_boites_gardees()
    {
        // 2 boîtes rendues avant le volet ; écart 24 sur les 3 gardées (part caisse restante 240) : 1 boîte → 8, puis 2 → le reste 16
        Assert.Equal(8m, RegleRetour.MontantRegularisation(400, 5, 1, 24, 240));
        Assert.Equal(16m, RegleRetour.MontantRegularisation(400, 5, 2, 16, 160));
    }
}
