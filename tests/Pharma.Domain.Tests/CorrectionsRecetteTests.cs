using Pharma.Domain.Stock;
using Pharma.Domain.Ventes;

namespace Pharma.Domain.Tests;

/// <summary>Règles corrigées après la recette : remboursement d'une vente à crédit, lot rappelé chez un autre grossiste.</summary>
public class CorrectionsRecetteTests
{
    // ---- Vente à crédit : seul ce que le patient a vraiment payé sur la vente est rendu ----

    [Fact]
    public void Annulation_credit_jamais_payee_dette_abandonnee_ne_rend_rien()
    {
        // Vente à crédit de 1 000 DA, dette corrigée à 0 par le titulaire : rien n'a été payé, rien ne sort de la caisse
        Assert.Equal((0m, 0m), RegleAnnulation.RemboursementCredit(1000, 1000, payeSurVente: 0, soldeCredit: 0));
    }

    [Fact]
    public void Annulation_credit_dette_corrigee_en_partie_rend_rien_et_diminue_la_dette_restante()
    {
        // Deux ventes de 1 000 DA, dette ramenée à 500 : l'annulation d'une vente diminue la dette de 500, rien n'est rendu
        Assert.Equal((500m, 0m), RegleAnnulation.RemboursementCredit(1000, 1000, payeSurVente: 0, soldeCredit: 500));
    }

    [Fact]
    public void Annulation_credit_en_partie_remboursee_rend_ce_qui_a_ete_paye()
    {
        // 400 DA déjà remboursés sur cette vente : dette diminuée de 600, 400 rendus en espèces
        Assert.Equal((600m, 400m), RegleAnnulation.RemboursementCredit(1000, 1000, payeSurVente: 400, soldeCredit: 600));
    }

    [Fact]
    public void Retour_partiel_credit_diminue_d_abord_la_dette()
    {
        // Boîtes rendues pour 300 DA sur une vente de 1 000 dont 400 payés : la dette baisse de 300, rien n'est rendu
        Assert.Equal((300m, 0m), RegleAnnulation.RemboursementCredit(300, 1000, payeSurVente: 400, soldeCredit: 600));
    }

    [Fact]
    public void Retour_credit_entierement_paye_rend_l_argent()
    {
        Assert.Equal((0m, 300m), RegleAnnulation.RemboursementCredit(300, 1000, payeSurVente: 1000, soldeCredit: 0));
    }

    // ---- Lot rappelé : bloqué chez tous les grossistes, même pour une livraison d'un autre ----

    [Fact]
    public void Lot_bloque_chez_un_autre_grossiste_est_un_conflit()
    {
        var autre = new LotExistant(6, new DateOnly(2027, 11, 30), false, 0, 100, Bloque: true, AutreGrossiste: true);
        Assert.Equal([ConflitLot.LotBloque], RegleLot.Conflits([autre], new DateOnly(2028, 5, 31)));
    }

    [Fact]
    public void Lot_d_un_autre_grossiste_ne_sert_ni_a_completer_ni_a_comparer_la_date()
    {
        var autre = new LotExistant(6, new DateOnly(2027, 11, 30), false, 0, 100, Bloque: true, AutreGrossiste: true);
        var decision = RegleLot.Decider([autre], new DateOnly(2028, 5, 31), new ChoixLot(false, EntrerBloque: true), "Amlodipine", "AM55");
        Assert.Null(decision.Completer);
        Assert.True(decision.NouveauBloque);
    }

    [Fact]
    public void Lot_rappele_ailleurs_refuse_sans_choix()
    {
        var autre = new LotExistant(6, new DateOnly(2027, 11, 30), false, 0, 100, Bloque: true, AutreGrossiste: true);
        Assert.Throws<Pharma.Domain.Commun.RegleMetierException>(() =>
            RegleLot.Decider([autre], new DateOnly(2028, 5, 31), new ChoixLot(false, false), "Amlodipine", "AM55"));
    }
}
