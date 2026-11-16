using System.Text.Json;
using Pharma.Application.Catalogue;
using Pharma.Application.Pilotage;
using Pharma.Domain.Commun;
using Pharma.Shared;

namespace Pharma.Application.Tests;

/// <summary>Décisions du titulaire du 2026-11-16 : valeur du stock hors périmés/bloqués, contenance des produits, durée gardée en attente.</summary>
public class DecisionsTitulaire20261116Tests
{
    // ---- 1. Valeur du stock hors lots périmés et bloqués ----------------------------------------------------------------

    [Fact]
    public void Rotation_et_couverture_calculees_sur_la_valeur_hors_perimes_et_bloques()
    {
        // 400 000 DA de stock vendable, 50 000 DA de lots périmés ou bloqués affichés à part : ils ne comptent pas
        var s = new StockPilotage(400_000m, 50, 100_000m, 30, 90, 3, 12_000m, 1, 2, ValeurExclue: 50_000m, LotsExclus: 4);
        Assert.Equal(3.0m, s.RotationAnnuelle);
        Assert.Equal(120m, s.CouvertureJours);
        Assert.Equal(s.RotationAnnuelle, (s with { ValeurExclue = 0m }).RotationAnnuelle);
        Assert.Equal((50_000m, 4), (s.ValeurExclue, s.LotsExclus));
    }

    // ---- 2. Contenance : analyse du conditionnement de la nomenclature -----------------------------------------------

    [Theory]
    [InlineData("B/30", 30)]
    [InlineData("B/03", 3)]
    [InlineData(" b / 100 ", 100)]
    [InlineData("BOITE DE 30", 30)]
    [InlineData("B/1+1", null)]
    [InlineData("B/30 ET B/90", null)]
    [InlineData("B/30 SACHETS", 30)]
    [InlineData("B/30 SACHETS DE 5 G", null)]
    [InlineData("B/1FL", null)]
    [InlineData("FL/10ML", null)]
    [InlineData("T/15G", null)]
    [InlineData("B/0", null)]
    [InlineData("B/20000", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Conditionnement_analyse_sans_rien_deviner(string? conditionnement, int? attendu) =>
        Assert.Equal(attendu, ContenanceProduit.DepuisConditionnement(conditionnement));

    [Fact]
    public void Plusieurs_lignes_de_nomenclature_retenues_seulement_si_elles_concordent()
    {
        Assert.Equal(30, ContenanceProduit.DepuisNomenclature(["B/30", "BOITE DE 30"]));
        Assert.Null(ContenanceProduit.DepuisNomenclature(["B/30", "B/60"]));
        Assert.Null(ContenanceProduit.DepuisNomenclature(["B/30", "FL/10ML"]));
        Assert.Null(ContenanceProduit.DepuisNomenclature([]));
    }

    [Fact]
    public void Contenance_saisie_prime_sur_la_nomenclature()
    {
        Assert.Equal(28, ContenanceProduit.Retenue(28, 30));
        Assert.Equal(30, ContenanceProduit.Retenue(null, 30));
        Assert.Null(ContenanceProduit.Retenue(null, null));
    }

    [Fact]
    public async Task Contenance_saisie_controlee_et_journalisee_la_nomenclature_n_est_jamais_ecrite()
    {
        var p = new ProduitsModifEnMemoire();
        p.Fiche = p.Fiche with { ContenanceNomenclature = 30, ConditionnementNomenclature = "B/30" };
        var droits = new DroitsFactices("catalogue.creer");

        foreach (var invalide in new[] { 0, -1, 10_001 })
        {
            var e = await Assert.ThrowsAsync<RegleMetierException>(() =>
                new ModifierProduit(p, droits, new JournalEspion()).ExecuterAsync(p.Fiche with { Contenance = invalide }, 1));
            Assert.Equal("contenance", e.Champ);
        }
        Assert.Null(p.Modifiee);

        var journal = new JournalEspion();
        // La saisie envoyée par le poste ne peut pas changer la valeur lue dans la nomenclature
        await new ModifierProduit(p, droits, journal).ExecuterAsync(p.Fiche with { Contenance = 28, ContenanceNomenclature = 99 }, 1);
        Assert.Equal((28, 30), (p.Modifiee!.Contenance, p.Modifiee.ContenanceNomenclature));
        Assert.Equal(["produit_modifie"], journal.Actions);
        Assert.True(journal.Details[0].ContainsKey("contenance"));

        // Contenance laissée vide, rien d'autre changé : rien n'est écrit au journal, la nomenclature n'est pas recopiée
        var journal2 = new JournalEspion();
        await new ModifierProduit(p, droits, journal2).ExecuterAsync(p.Fiche, 1);
        Assert.Null(p.Modifiee!.Contenance);
        Assert.Empty(journal2.Actions);
    }

    // ---- 3. Durée du traitement gardée avec la vente mise en attente ---------------------------------------------------

    [Fact]
    public void Duree_du_traitement_conservee_en_attente_et_rendue_a_la_reprise()
    {
        var panier = new PanierAttenteDto([], false, 0, 0, null, null, new DateOnly(2026, 11, 16), "ORD-1", 0m, null, null,
            DureeTraitementJours: 28);
        var relu = JsonSerializer.Deserialize<PanierAttenteDto>(JsonSerializer.Serialize(panier))!;
        Assert.Equal(28, relu.DureeTraitementJours);
        Assert.Equal("ORD-1", relu.NumeroOrdonnance);

        // Vente mise en attente avant cette version : pas de durée, la reprise fonctionne
        var json = JsonSerializer.Serialize(panier with { DureeTraitementJours = null }).Replace(",\"DureeTraitementJours\":null", "");
        Assert.DoesNotContain("DureeTraitementJours", json);
        Assert.Null(JsonSerializer.Deserialize<PanierAttenteDto>(json)!.DureeTraitementJours);
    }
}
