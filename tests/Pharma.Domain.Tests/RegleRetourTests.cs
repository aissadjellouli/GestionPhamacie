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
    public void Vente_annulee_refusee()
    {
        RegleRetour.VerifierVente("validee");
        Assert.Throws<RegleMetierException>(() => RegleRetour.VerifierVente("annulee"));
    }

    // ---- Tiers payant (script 155, option A du 2026-11-21) ----

    [Fact]
    public void Boite_remboursee_permise_hors_bordereau_retiree_d_abord_d_un_bordereau_en_preparation_refusee_apres_depot()
    {
        RegleRetour.VerifierPartOrganisme(deposee: false, dansUnBordereau: false);
        var preparation = Assert.Throws<RegleMetierException>(() => RegleRetour.VerifierPartOrganisme(false, true));
        Assert.Contains("retirez-la d’abord du bordereau", preparation.Message);
        var depot = Assert.Throws<RegleMetierException>(() => RegleRetour.VerifierPartOrganisme(true, true));
        Assert.Equal("Vente déposée à la CNAS : retour impossible dans le logiciel tant que la procédure de la CNAS n’est pas connue", depot.Message);
        // Annulation après dépôt : même message (plus de « régularisation Chifa » inexistante)
        Assert.Equal(depot.Message, Assert.Throws<RegleMetierException>(() => RegleAnnulation.VerifierAnnulable("validee", true, null, deposee: true)).Message);
    }

    [Theory]
    // Taux 80 % : 2 boîtes, part organisme 800 ; 1 rendue → 400 (sans régularisation : ratio 1)
    [InlineData(800, 2, 1, 1, 400)]
    // Taux 100 % : 3 boîtes, part organisme 900 (patient 0) ; 1 rendue → 300
    [InlineData(900, 3, 1, 1, 300)]
    // Ligne non remboursée : rien
    [InlineData(0, 2, 1, 1, 0)]
    // Après régularisation (part Chifa 720 sur 800 à la caisse : ratio 0,9) : 1 boîte sur 2 → 360
    [InlineData(800, 2, 1, 0.9, 360)]
    // Arrondi au centime : 100 / 3 boîtes
    [InlineData(100, 3, 1, 1, 33.33)]
    public void Part_organisme_des_boites_rendues_au_prorata_de_la_part_retenue(decimal ligne, int vendue, int rendue, decimal ratio, decimal attendu) =>
        Assert.Equal(attendu, RegleRetour.MontantOrganisme(ligne, vendue, rendue, ratio));

    [Fact]
    public void Ratio_retenu_part_chifa_sur_part_caisse_et_rien_sans_part_organisme()
    {
        Assert.Equal(1m, RegleRetour.RatioRetenu(1040, 1040));
        Assert.Equal(0.9m, RegleRetour.RatioRetenu(720, 800));
        Assert.Equal(0m, RegleRetour.RatioRetenu(0, 800));
        Assert.Equal(0m, RegleRetour.RatioRetenu(500, 0));
    }

    [Fact]
    public void Part_organisme_jamais_au_dela_du_reste_et_solde_exact_au_dernier_retour()
    {
        // 3 retours d'une boîte sur 3 à 100 : 33,33 + 33,33 puis le solde 33,34
        Assert.Equal(33.33m, RegleRetour.TotalOrganisme(33.33m, 100, 0, false));
        Assert.Equal(33.34m, RegleRetour.TotalOrganisme(33.33m, 100, 66.66m, true));
        // Jamais plus que ce qui reste
        Assert.Equal(10m, RegleRetour.TotalOrganisme(33.33m, 100, 90, false));
        Assert.Equal(0m, RegleRetour.TotalOrganisme(33.33m, 100, 100, false));
    }

    [Fact]
    public void Destination_obligatoire() =>
        Assert.Throws<RegleMetierException>(() => RegleRetour.Destination(null));
}
