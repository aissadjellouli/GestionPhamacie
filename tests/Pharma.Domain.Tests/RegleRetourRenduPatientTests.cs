using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Domain.Tests;

/// <summary>
/// Retour d'une vente régularisée « rendu au patient » (décision du titulaire du 2026-11-21, script 159, même modèle que le script 158) :
/// la part de l'écart déjà rendue au patient pour les boîtes rendues (même prorata) est retenue sur ce que le retour lui rend, d'abord sur
/// l'argent payé, puis sur l'avoir recrédité ; le reste devient une dette du patient, après l'alerte au vendeur ; vente sans patient : refus.
/// Ensuite seulement, la règle des dettes (script 158) s'applique à ce qui reste à rendre.
/// </summary>
public class RegleRetourRenduPatientTests
{
    [Fact]
    public void Part_de_l_ecart_au_prorata_de_la_part_caisse_des_boites_rendues()
    {
        // 5 boîtes, part caisse 400 ; écart rendu 40 (8 par boîte) : 2 boîtes → 16 ; il reste 24 sur 3 boîtes (240) : 1 boîte → 8
        Assert.Equal(16m, RegleRetour.MontantRegularisation(400, 5, 2, 40, 400));
        Assert.Equal(8m, RegleRetour.MontantRegularisation(400, 5, 1, 24, 240));
        // Deux lignes : 100 % (part caisse 200) et 50 % (part caisse 100), écart rendu 40 : les 2 boîtes à 100 % → 26,67
        Assert.Equal(26.67m, RegleRetour.MontantRegularisation(200, 2, 2, 40, 300));
    }

    [Fact]
    public void Retenue_couverte_par_l_argent_paye_aucune_dette()
    {
        var r = RegleRetour.Retenir(16, 40, 0, true, false);
        Assert.Equal((16m, 0m, 0m, 16m), (r.SurArgent, r.SurAvoir, r.Dette, r.Total));
    }

    [Fact]
    public void Retenue_sur_l_argent_puis_sur_l_avoir_recredite()
    {
        var r = RegleRetour.Retenir(16, 10, 20, true, false);
        Assert.Equal((10m, 6m, 0m), (r.SurArgent, r.SurAvoir, r.Dette));
    }

    [Fact]
    public void Retenue_superieure_au_montant_a_rendre_le_reste_devient_une_dette_apres_l_alerte()
    {
        // Boîtes à 100 % : rien de payé, 26,67 déjà rendus pour elles : alerte exigée, puis dette
        var e = Assert.Throws<RegleMetierException>(() => RegleRetour.Retenir(26.67m, 0, 0, true, false));
        Assert.Equal("dette_retenue", e.Champ);
        Assert.Contains("26,67", e.Message.Replace('.', ','));
        var r = RegleRetour.Retenir(26.67m, 0, 0, true, true);
        Assert.Equal((0m, 0m, 26.67m), (r.SurArgent, r.SurAvoir, r.Dette));
        // Couverte en partie : seul le reste devient une dette
        var p = RegleRetour.Retenir(30, 12, 0, true, true);
        Assert.Equal((12m, 18m), (p.SurArgent, p.Dette));
    }

    [Fact]
    public void Vente_sans_patient_reste_non_couvert_retour_refuse_meme_confirme()
    {
        var e = Assert.Throws<RegleMetierException>(() => RegleRetour.Retenir(26.67m, 0, 0, false, true));
        Assert.Equal("dette_retenue", e.Champ);
        Assert.Contains("Vente sans patient", e.Message);
        // Couverte par le remboursement : permis
        Assert.Equal(16m, RegleRetour.Retenir(16, 40, 0, false, false).SurArgent);
    }

    [Fact]
    public void Combinaison_avec_les_dettes_la_retenue_d_abord_puis_le_choix_sur_ce_qui_reste()
    {
        // Payé 40 pour les boîtes rendues, 16 retenus : 24 restent ; il doit 200 par ailleurs : choix exigé sur les 24
        var retenue = RegleRetour.Retenir(16, 40, 0, true, false);
        var reste = 40 - retenue.SurArgent;
        Assert.Throws<RegleMetierException>(() => RegleRetour.Rembourser(0, reste, 0, 0, 200, true, null));
        var deduire = RegleRetour.Rembourser(0, reste, 0, 0, 200, true, RegleRetour.DettesDeduire);
        Assert.Equal((24m, 0m), (deduire.ImputeSurDettes, deduire.ARendre));
        // Retenue qui absorbe tout l'argent : plus rien à rendre, aucun choix
        var tout = RegleRetour.Rembourser(0, 40 - RegleRetour.Retenir(40, 40, 0, true, false).SurArgent, 0, 0, 200, true, null);
        Assert.Equal((0m, false), (tout.ARendre, tout.ChoixExige));
    }

    [Fact]
    public void Achat_a_credit_dette_diminuee_de_la_part_et_retenue_ajoutee_a_part()
    {
        // Vente à crédit, rien payé : dette diminuée de 40 (règle 158) ; la retenue de 16 n'a aucun argent à prendre : dette, confirmation
        var rem = RegleRetour.Rembourser(40, 0, 0, 0, 100, true, null);
        Assert.Equal((40m, 0m, 60m), (rem.DetteVenteDiminuee, rem.ARendre, rem.DetteRestante));
        Assert.Throws<RegleMetierException>(() => RegleRetour.Retenir(16, 0, 0, true, false));
        Assert.Equal(16m, RegleRetour.Retenir(16, 0, 0, true, true).Dette);
    }
}
