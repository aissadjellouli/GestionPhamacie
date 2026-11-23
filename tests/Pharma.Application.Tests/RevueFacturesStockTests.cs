using Pharma.Application.Factures;
using Pharma.Application.Pilotage;
using Pharma.Application.Stock;
using Pharma.Application.Tracabilite;
using Pharma.Application.Tresorerie;
using Pharma.Domain.Commun;
using Pharma.Domain.Factures;

namespace Pharma.Application.Tests;

/// <summary>Correctifs de la revue : factures, trésorerie, stock, inventaire, traçabilité.</summary>
public class RevueFacturesStockTests
{
    private static readonly HorlogeFixe Horloge = new(new DateOnly(2026, 11, 9));

    private static FactureResume Resume(long id, string statut = "emise", decimal ttc = 1000) =>
        new(id, "facture", $"F-2026-{id:0000}", new DateOnly(2026, 11, 9), "organisme", "CNAS", ttc, 0, 0, 0, ttc, statut, 1, false, null,
            null, null, null, null, 1);

    private static FactureDetail Detail(string statut, decimal ttc = 1000, decimal ht = 840) =>
        new(Resume(101, statut, ttc), null, null, null, null, null, ht, ttc - ht, null, null, "Employé", [], []);

    // ---- Factures ----

    [Fact]
    public async Task Factures_a_solder_lues_par_identifiant_meme_au_dela_de_300()
    {
        var f = new FacturesEnMemoire();
        for (var i = 1; i <= 350; i++) f.AEncaisser.Add(Resume(i, ttc: 100));
        var t = new TresorerieEnMemoire();
        var cas = new SaisirMouvementTresorerie(t, new DroitsFactices("tresorerie.gerer"), Horloge, new JournalEspion(), f);

        await cas.ExecuterAsync(2, null, "versement_organisme", 100, null, null, "VIR-1", 1, null, null, 5, [350L]);

        Assert.Equal([(350L, 100m)], t.Mouvements.Single().Factures!);
    }

    [Fact]
    public async Task Facture_choisie_plus_a_encaisser_refusee()
    {
        var f = new FacturesEnMemoire();
        var cas = new SaisirMouvementTresorerie(new TresorerieEnMemoire(), new DroitsFactices("tresorerie.gerer"), Horloge, new JournalEspion(), f);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(2, null, "versement_organisme", 100, null, null, "VIR-1", 1, null, null, 5, [999L]));
    }

    [Fact]
    public async Task Avoir_une_ligne_au_taux_de_la_facture_coherent_avec_les_totaux()
    {
        var f = new FacturesEnMemoire { Detail = Detail("emise", ttc: 1190, ht: 1000) };
        await new Facturation(f, new DroitsFactices("factures.gerer"), Horloge, new JournalEspion()).AvoirAsync(101, 119, "Retour", 5);

        var avoir = f.Emises.Single();
        Assert.Equal(19m, avoir.Lignes.Single().TauxTva);
        Assert.Equal(new Totaux(100, 19, 119), avoir.Totaux);
    }

    [Fact]
    public async Task Facture_remplacee_ne_se_retire_pas_du_suivi()
    {
        var f = new FacturesEnMemoire { Detail = Detail("remplacee") };
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() =>
            new Facturation(f, new DroitsFactices("factures.gerer"), Horloge, new JournalEspion()).SuiviAsync(101, true, "Litige", 5));
        Assert.Contains("remplacée", ex.Message);
        Assert.Null(f.Suivi);
    }

    [Fact]
    public void Panier_moyen_arrondi_moitie_vers_le_haut() =>
        Assert.Equal(0.51m, CalculsPilotage.PanierMoyen(1.01m, 2));   // l'arrondi bancaire donnait 0,50

    // ---- Inventaire ----

    [Fact]
    public async Task Cloture_inventaire_journal_dans_la_transaction_et_comptes_transmis_pour_relecture_sous_verrou()
    {
        var depot = new InventairesEnMemoire();
        await new TerminerInventaire(depot, new DroitsFactices("stock.ajuster")).ExecuterAsync(5, 2);

        Assert.Equal("inventaire_termine", depot.JournalClôture!.Action);
        Assert.All(depot.Decisions!, d => Assert.NotNull(d.Compte));
    }

    // ---- Correction d'entrée ----

    [Fact]
    public async Task Correction_vers_un_numero_de_lot_rappele_bloque_le_lot()
    {
        var depot = new EntreesStockEnMemoire();
        depot.Rappeles.Add((1, "RAPPEL1"));
        var cas = new CorrigerEntreeStock(depot, new DroitsFactices("stock.corriger_entree"));

        await cas.ExecuterAsync(7, new DemandeCorrectionLot(null, "rappel1", null, null, null, "Erreur de saisie"), 5);

        Assert.True(depot.Ecrite!.BloquerLot);
        Assert.Equal(true, depot.Ecrite.Journal!.Details["lot_bloque_numero_rappele"]);
    }

    [Fact]
    public async Task Correction_vers_un_numero_ordinaire_ne_bloque_pas()
    {
        var depot = new EntreesStockEnMemoire();
        await new CorrigerEntreeStock(depot, new DroitsFactices("stock.corriger_entree"))
            .ExecuterAsync(7, new DemandeCorrectionLot(null, "AM56", null, null, null, "Erreur de saisie"), 5);
        Assert.False(depot.Ecrite!.BloquerLot);
    }

    // ---- Traçabilité, mobile ----

    private sealed class ParcoursAvecCommentaire : ITracabiliteLecture
    {
        public Task<ParcoursBoite?> ParcoursAsync(string codeOuSerie, CancellationToken ct = default) =>
            Task.FromResult<ParcoursBoite?>(new ParcoursBoite(1, codeOuSerie, "Produit", "L1", new DateOnly(2027, 1, 1), "Grossiste", "BL", DateTime.UtcNow,
                "vendue", false, "T-1", DateTime.UtcNow, null, "Patient Dupont",
                [new EtapeParcours("vente", DateTime.UtcNow, "Remis à Mme Dupont")], 4, false));
    }

    [Fact]
    public async Task Commentaire_des_etapes_masque_avec_le_patient()
    {
        var sans = await new ConsulterParcoursBoite(new ParcoursAvecCommentaire(), new DroitsFactices("stock.consulter")).ExecuterAsync("2000000001272", 5);
        Assert.Null(sans!.Patient);
        Assert.All(sans.Etapes, e => Assert.Null(e.Commentaire));

        var avec = await new ConsulterParcoursBoite(new ParcoursAvecCommentaire(), new DroitsFactices("stock.declarer_perime")).ExecuterAsync("2000000001272", 5);
        Assert.Equal("Patient Dupont", avec!.Patient);
        Assert.Equal("Remis à Mme Dupont", avec.Etapes.Single().Commentaire);
    }

    [Fact]
    public async Task Date_deja_confirmee_entre_temps_refusee_sans_journal()
    {
        var (m, journal) = (new MobileEnMemoire { ConfirmationPossible = false }, new JournalEspion());
        var cas = new Pharma.Application.Mobile.ConfirmerPeremption(m, new DroitsFactices("stock.ajuster"), new HorlogeFixe(MobileEnMemoire.Aujourdhui), journal);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(70, new DateOnly(2028, 1, 31), 2));
        Assert.Empty(journal.Actions);
    }
}
