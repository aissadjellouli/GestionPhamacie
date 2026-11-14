using Pharma.Application.Alertes;
using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Stock;
using Pharma.Domain.Alertes;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

internal sealed class LotsDelaissesFactices(params AlerteLotDelaisse[] alertes) : IAlertesLotsDelaisses
{
    public List<IReadOnlyCollection<long>> Appels { get; } = [];
    public Task<IReadOnlyList<AlerteLotDelaisse>> AlerterAsync(IReadOnlyCollection<long> produits, CancellationToken ct = default)
    {
        Appels.Add(produits);
        return Task.FromResult<IReadOnlyList<AlerteLotDelaisse>>(alertes);
    }
}

internal sealed class AlertesLectureEspion : IAlertesLecture
{
    /// <summary>Types demandés à chaque lecture.</summary>
    public List<IReadOnlyCollection<string>> Types { get; } = [];
    public Task<IReadOnlyList<Alerte>> ListerOuvertesAsync(IReadOnlyCollection<string> types, CancellationToken ct = default)
    {
        Types.Add(types);
        return Task.FromResult<IReadOnlyList<Alerte>>([]);
    }
    public Task<int> CompterUrgentesAsync(IReadOnlyCollection<string> types, CancellationToken ct = default)
    {
        Types.Add(types);
        return Task.FromResult(0);
    }
}

public class LotDelaisseTests
{
    private static (Encaisser Cas, CatalogueEnMemoire Catalogue, VentesEspion Ventes, AlertesTitulaireEspion Titulaire) Caisse(
        string situation, params AlerteLotDelaisse[] alertes)
    {
        var (catalogue, ventes, titulaire) = (new CatalogueEnMemoire(), new VentesEspion(), new AlertesTitulaireEspion());
        catalogue.Situations[20] = new SituationLotVente(20, situation, "B1182", 19, "A0900", CatalogueEnMemoire.Aujourdhui.AddDays(60));
        var cas = new Encaisser(catalogue, new PatientsEnMemoire(), ventes, new SessionsEnMemoire(), new DroitsFactices("caisse.vendre"),
            new HorlogeFixe(CatalogueEnMemoire.Aujourdhui), new ScansEnMemoire(),
            lotsDelaisses: new LotsDelaissesFactices(alertes), alertesTitulaire: titulaire);
        return (cas, catalogue, ventes, titulaire);
    }

    private static CommandeVente Commande(string? raison) =>
        new([new LigneCommande(2, 20, 1, RaisonLot: raison)], false, null, null, null, null, 0, ModePaiement.Especes, null);

    [Fact]
    public async Task Lot_plus_ancien_en_stock_raison_obligatoire_a_l_encaissement()
    {
        var (cas, _, ventes, _) = Caisse(SituationLot.Obligatoire);
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(Commande(null), 5, "Caisse 1"));
        Assert.Equal("raison_lot", ex.Champ);
        Assert.Contains("A0900", ex.Message);
        Assert.Null(ventes.Recu);

        await cas.ExecuterAsync(Commande(RaisonLot.Introuvable), 5, "Caisse 1");
        Assert.Equal(RaisonLot.Introuvable, ventes.Recu!.Vente.Lignes.Single().RaisonLot);
    }

    [Fact]
    public async Task Peremption_acceptee_seulement_si_le_logiciel_la_verifie_et_le_vendeur_la_valide()
    {
        // Le lot servi ne périme pas avant : « péremption » refusée
        var (obligatoire, _, _, _) = Caisse(SituationLot.Obligatoire);
        await Assert.ThrowsAsync<RegleMetierException>(() => obligatoire.ExecuterAsync(Commande(RaisonLot.Peremption), 5, "Caisse 1"));

        // Vérifiée, mais pas encore validée par le vendeur : refusée à l'encaissement
        var (verifiee, _, ventes, _) = Caisse(SituationLot.Peremption);
        await Assert.ThrowsAsync<RegleMetierException>(() => verifiee.ExecuterAsync(Commande(null), 5, "Caisse 1"));
        await verifiee.ExecuterAsync(Commande(RaisonLot.Peremption), 5, "Caisse 1");
        Assert.Equal(RaisonLot.Peremption, ventes.Recu!.Vente.Lignes.Single().RaisonLot);
    }

    [Fact]
    public async Task Apercu_donne_la_situation_sans_exiger_la_raison()
    {
        var (cas, _, _, _) = Caisse(SituationLot.Peremption);
        var a = await cas.CalculerAsync(Commande(null), 5);
        Assert.Equal((SituationLot.Peremption, "A0900"), (a.Situations!.Single().Situation, a.Situations!.Single().LotDelaisse));
    }

    [Fact]
    public async Task Sans_lot_plus_ancien_rien_n_est_demande()
    {
        var (cas, _, ventes, _) = Caisse(SituationLot.Aucune);
        await cas.ExecuterAsync(Commande(RaisonLot.Introuvable), 5, "Caisse 1");
        Assert.Null(ventes.Recu!.Vente.Lignes.Single().RaisonLot);
    }

    [Fact]
    public async Task Apres_la_vente_alerte_notifiee_au_titulaire()
    {
        var (cas, _, _, titulaire) = Caisse(SituationLot.Obligatoire,
            new AlerteLotDelaisse(3, "urgent", "Amoxicilline, lot A0900 : 4 boîte(s) en stock…"));
        await cas.ExecuterAsync(Commande(RaisonLot.DemandeClient), 5, "Caisse 1");
        Assert.Equal([("Amoxicilline, lot A0900 : 4 boîte(s) en stock…", true)], titulaire.LotsDelaisses);
    }

    [Fact]
    public async Task Alertes_reservees_vues_par_le_titulaire_seul()
    {
        var lecture = new AlertesLectureEspion();
        await new ListerAlertesOuvertes(lecture, new DroitsFactices("stock.consulter", "achats.commander", "stock.ajuster")).ExecuterAsync(5);
        await new ListerAlertesOuvertes(lecture, new DroitsFactices { Titulaire = true }).ExecuterAsync(1);
        await new ListerAlertesOuvertes(lecture, new DroitsFactices("stock.consulter", "achats.commander")).CompterUrgentesAsync(5);
        Assert.DoesNotContain("lot_delaisse", lecture.Types[0]);
        Assert.Contains("lot_delaisse", lecture.Types[1]);
        Assert.DoesNotContain("lot_delaisse", lecture.Types[2]);
        Assert.Contains("lot_delaisse", AlertesReservees.Types);
    }

    [Fact]
    public void Chaque_employe_voit_les_alertes_sur_lesquelles_il_peut_agir()
    {
        // Vendeur : vendre (et réceptionner) ; il peut vendre d'abord un lot qui périme, pas commander ni ajuster
        Assert.Equal(["peremption", "risque_ecoulement"],
            AlertesSelonDroits.TypesVisibles(false, ["caisse.vendre", "stock.consulter", "achats.receptionner"]).Order());
        // Commander : ruptures, stocks critiques, surstocks et dormants
        Assert.Equal(["dormant", "rupture", "stock_critique", "surstock"], AlertesSelonDroits.TypesVisibles(false, ["achats.commander"]).Order());
        // Périmé : déclarer périmé ou retourner au grossiste ; le retour vaut aussi pour les lots qui périment bientôt
        Assert.Equal(["peremption", "perime", "risque_ecoulement"], AlertesSelonDroits.TypesVisibles(false, ["achats.retour_fournisseur"]).Order());
        Assert.Equal(["perime"], AlertesSelonDroits.TypesVisibles(false, ["stock.declarer_perime"]));
        // Aucun droit d'action : aucune alerte ; le titulaire voit tout, ancien lot délaissé compris
        Assert.Empty(AlertesSelonDroits.TypesVisibles(false, ["stock.consulter"]));
        Assert.Equal(8, AlertesSelonDroits.TypesVisibles(true, []).Count);
    }

    [Fact]
    public async Task Inventaire_d_un_produit_reserve_au_titulaire()
    {
        var inventaires = new InventairesEnMemoire();
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new CommencerInventaireProduit(inventaires, new DroitsFactices("stock.ajuster")).ExecuterAsync(2, 5));
        Assert.Equal(8, await new CommencerInventaireProduit(inventaires, new DroitsFactices { Titulaire = true }).ExecuterAsync(2, 1));
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            new CommencerInventaireProduit(inventaires, new DroitsFactices { Titulaire = true }).ExecuterAsync(0, 1));
    }

    [Theory]
    [InlineData("aucune", null, null)]
    [InlineData("peremption", "peremption", "peremption")]
    [InlineData("peremption", "abimee", "abimee")]
    [InlineData("obligatoire", "demande_client", "demande_client")]
    public void Raison_retenue(string situation, string? choix, string? attendue) =>
        Assert.Equal(attendue, SituationLot.Raison(situation, choix, "Produit", "B1", "A1", exiger: true));

    [Theory]
    [InlineData("obligatoire", null)]
    [InlineData("obligatoire", "peremption")]
    [InlineData("obligatoire", "vol")]
    [InlineData("peremption", null)]
    public void Raison_refusee(string situation, string? choix) =>
        Assert.Throws<RegleMetierException>(() => SituationLot.Raison(situation, choix, "Produit", "B1", "A1", exiger: true));
}
