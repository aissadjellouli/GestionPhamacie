using Pharma.Domain.Ventes;

namespace Pharma.Domain.Tests;

/// <summary>
/// Rapprochement d'un volet après un retour (décision du titulaire du 2026-11-21 : ajuster, script 157). Vente : 5 boîtes à 100 DA,
/// 80 % (part caisse 400, soit 80 par boîte) ; prix Chifa 90 (part Chifa 72 par boîte). Aucune part n'est déduite deux fois ni oubliée,
/// dans les deux ordres : la créance finale vaut toujours 72 × boîtes gardées.
/// </summary>
public class RapprochementVoletApresRetourTests
{
    private const decimal Taux = 80;
    private const decimal PartCaisse = 400;

    private static LigneVoletRapprochee Volet(int quantite, decimal prix = 90) => new("C1", quantite, prix, 1, "Produit fictif");
    private static LigneVenteRapprochee Vente(int rendues) => new(1, "Produit fictif", 5, 100, rendues);

    /// <summary>Créance comme v_vente_part_organisme (hors majoration) : (part Chifa + part rendue avant) sinon part caisse, moins les retours.</summary>
    private static decimal Creance(decimal? partChifa, decimal rendueAvant, decimal retiree) =>
        (partChifa is { } c ? c + rendueAvant : PartCaisse) - retiree;

    /// <summary>Part organisme d'un retour de <paramref name="boites"/> boîtes, comme EnregistrerRetourClient (prorata de ce qui reste).</summary>
    private static decimal Retour(decimal partRetenue, decimal dejaRetiree, int dejaRendues, int boites)
    {
        var ratio = RegleRetour.RatioRetenu(partRetenue - dejaRetiree, RegleRetour.PartCaisseRestante(PartCaisse, 5, dejaRendues));
        var calcule = RegleRetour.MontantOrganisme(PartCaisse, 5, boites, ratio);
        return RegleRetour.TotalOrganisme(calcule, partRetenue, dejaRetiree, dejaRendues + boites >= 5);
    }

    [Fact]
    public void Volet_corrige_apres_retour_compare_aux_boites_nettes_et_concorde()
    {
        // 2 boîtes rendues (part organisme retirée 160) ; volet corrigé : 3 boîtes au prix de la vente
        var c = RapprochementVolet.Comparer([Volet(3, 100)], [Vente(2)], 5, Taux, PartCaisse - 160);
        Assert.True(c.Concorde);
        Assert.Equal((240m, 240m, 0m), (c.PartOrganismeVente, c.PartOrganismeChifa!.Value, c.EcartPartOrganisme));
        Assert.Equal((3, 2), (c.Lignes[0].QuantiteVente!.Value, c.Lignes[0].QuantiteRendue));
    }

    [Fact]
    public void Volet_non_corrige_apres_retour_ecart_de_quantite_mais_part_des_seules_boites_gardees()
    {
        var c = RapprochementVolet.Comparer([Volet(5, 100)], [Vente(2)], 5, Taux, PartCaisse - 160);
        var l = Assert.Single(c.Lignes);
        Assert.Equal(RapprochementVolet.Quantite, l.Ecart);
        Assert.Equal(240m, c.PartOrganismeChifa);   // 3 boîtes servies nettes, jamais 5
        Assert.Equal(0m, c.EcartPartOrganisme);     // aucune régularisation : rien n'est déduit une seconde fois
        Assert.Contains("5 sur le volet, 3 servie(s) après 2 rendue(s)", RapprochementVolet.Resume(c));
    }

    [Fact]
    public void Produit_entierement_rendu_absent_du_volet_ne_fait_pas_d_ecart()
    {
        var autre = new LigneVenteRapprochee(2, "Autre produit", 1, 50, 1);
        var c = RapprochementVolet.Comparer([Volet(5, 100)], [Vente(0), autre], 5, Taux, PartCaisse);
        Assert.True(c.Concorde);
        Assert.DoesNotContain(c.Lignes, l => l.ProduitId == 2);
    }

    [Fact]
    public void Produit_entierement_rendu_encore_sur_le_volet_ecart_et_aucune_part_chifa()
    {
        var c = RapprochementVolet.Comparer([Volet(5, 100)], [Vente(5)], 5, Taux, 0);
        Assert.Equal(RapprochementVolet.Quantite, Assert.Single(c.Lignes).Ecart);
        Assert.Equal((0m, 0m), (c.PartOrganismeChifa!.Value, c.EcartPartOrganisme));
    }

    [Fact]
    public void Retour_puis_volet_puis_retour_creance_exacte_sans_double_deduction()
    {
        // 1. Retour de 2 boîtes avant le volet : 160 retirés (part caisse)
        var r1 = Retour(PartCaisse, 0, 0, 2);
        Assert.Equal(160m, r1);
        Assert.Equal(240m, Creance(null, 0, r1));
        // 2. Volet corrigé (3 boîtes à 90) : part Chifa 216 des boîtes gardées ; écart 240 − 216 = 24 seulement (jamais 24 + 160)
        var c = RapprochementVolet.Comparer([Volet(3)], [Vente(2)], 5, Taux, PartCaisse - r1);
        Assert.Equal((216m, 24m), (c.PartOrganismeChifaHorsMajoration!.Value, c.EcartPartOrganisme));
        var chifa = c.PartOrganismeChifaHorsMajoration!.Value;
        Assert.Equal(216m, Creance(chifa, r1, r1));   // 72 × 3 : le premier retour n'est déduit qu'une fois
        // Journal fiscal de la régularisation : déplacement = Chifa + rendu avant − caisse = −24 (= −écart)
        Assert.Equal(-c.EcartPartOrganisme, chifa + r1 - PartCaisse);
        // 3. Retour d'1 boîte après la régularisation : 72 (part Chifa de la boîte)
        var partRetenue = chifa + r1;
        var r2 = Retour(partRetenue, r1, 2, 1);
        Assert.Equal(72m, r2);
        Assert.Equal(144m, Creance(chifa, r1, r1 + r2));
        // 4. Dernières boîtes : le solde exact, créance nulle
        var r3 = Retour(partRetenue, r1 + r2, 3, 2);
        Assert.Equal(144m, r3);
        Assert.Equal(0m, Creance(chifa, r1, r1 + r2 + r3));
    }

    [Fact]
    public void Volet_puis_retour_meme_creance_que_dans_l_autre_ordre()
    {
        // 1. Volet (5 boîtes à 90) : part Chifa 360, écart 40 régularisé
        var c = RapprochementVolet.Comparer([Volet(5)], [Vente(0)], 5, Taux, PartCaisse);
        var chifa = c.PartOrganismeChifaHorsMajoration!.Value;
        Assert.Equal((360m, 40m), (chifa, c.EcartPartOrganisme));
        // 2. Retour de 2 boîtes : 144 (72 × 2), créance 216 comme dans l'ordre inverse
        var r1 = Retour(chifa, 0, 0, 2);
        Assert.Equal(144m, r1);
        Assert.Equal(216m, Creance(chifa, 0, r1));
        // 3. Retour d'1 boîte : 72 ; puis les 2 dernières : solde exact
        var r2 = Retour(chifa, r1, 2, 1);
        Assert.Equal(72m, r2);
        Assert.Equal(144m, Creance(chifa, 0, r1 + r2));
        Assert.Equal(144m, Retour(chifa, r1 + r2, 3, 2));
    }

    [Fact]
    public void Sans_regularisation_le_prorata_reste_celui_de_la_caisse()
    {
        var r1 = Retour(PartCaisse, 0, 0, 1);
        var r2 = Retour(PartCaisse, r1, 1, 3);
        Assert.Equal((80m, 240m), (r1, r2));
        Assert.Equal(80m, Retour(PartCaisse, r1 + r2, 4, 1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void Arrondis_aucun_centime_perdu_ni_gagne(int premier)
    {
        // Chifa 91,13 (part 72,904 par boîte, non ronde) : la somme des retours vaut exactement la part retenue
        var c = RapprochementVolet.Comparer([Volet(5, 91.13m)], [Vente(0)], 5, Taux, PartCaisse);
        var chifa = c.PartOrganismeChifaHorsMajoration!.Value;
        var r1 = Retour(chifa, 0, 0, premier);
        var r2 = Retour(chifa, r1, premier, 5 - premier);
        Assert.Equal(chifa, r1 + r2);
    }
}
