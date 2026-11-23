using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

/// <summary>Correctifs de la revue de code du 2026-11-23 : retour sur prestation, dette relue sous verrou, journaux dans la transaction, borne d'attente.</summary>
public class CorrectifsRevueTests
{
    private static (EnregistrerRetourClient Cas, RetoursEnMemoire Stock) MonterRetour(VenteRetournable v)
    {
        var stock = new RetoursEnMemoire(v);
        var codes = new CodesEnMemoire();
        var droits = new DroitsParEmploye(new() { [8] = ["caisse.vendre", "caisse.retour_especes"] });
        return (new EnregistrerRetourClient(stock, new SessionsEnMemoire(), droits,
            new VerificationCodeComptoir(codes, new VerificateurFactice(), new HorlogeFixe(new DateOnly(2026, 11, 23)), new JournalEspion())), stock);
    }

    private static VenteAnnulable VenteRetour(decimal partPatient, string mode = "especes", long? patientId = null, decimal solde = 0, decimal paye = 0) =>
        new(20, "T-000020", DateTime.UtcNow, "Vendeur", patientId is null ? null : "Patient fictif", partPatient, partPatient, 0, mode, "validee", false, false, [],
            SoldeCreditPatient: solde, PatientId: patientId, PayeSurVente: paye);

    [Fact]
    public async Task Retour_de_la_derniere_boite_rend_500_DA_sur_une_vente_de_500_de_produit_et_200_de_prestation()
    {
        var vente = new VenteRetournable(VenteRetour(700), [new LigneRetournable(1, 10, "Produit fictif", "L1", 1, 0, 500, [])], null, PartPatientPrestations: 200);
        var (cas, stock) = MonterRetour(vente);
        var r = await cas.ExecuterAsync(20, "Erreur de produit", [new(1, 1, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal(500m, r.ARendre);
        Assert.Equal(500m, stock.Ecrit!.Montant);
    }

    [Fact]
    public async Task Retour_sur_vente_a_credit_avec_prestation_ne_reduit_pas_la_dette_de_la_prestation()
    {
        // Dette 700 (500 produit + 200 prestation), rien payé : le retour de la boîte retire 500 de la dette, pas 700
        var vente = new VenteRetournable(VenteRetour(700, "credit", patientId: 3, solde: 700), [new LigneRetournable(1, 10, "Produit fictif", "L1", 1, 0, 500, [])], null,
            PartPatientPrestations: 200);
        var (cas, stock) = MonterRetour(vente);
        await cas.ExecuterAsync(20, "Erreur de produit", [new(1, 1, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal(500m, stock.Ecrit!.ReductionDette);
    }

    [Fact]
    public async Task Retour_vente_a_credit_demande_la_relecture_de_la_dette_et_du_paye_sous_verrou()
    {
        var vente = new VenteRetournable(VenteRetour(500, "credit", patientId: 3, solde: 400, paye: 100), [new LigneRetournable(1, 10, "Produit fictif", "L1", 1, 0, 500, [])]);
        var (cas, stock) = MonterRetour(vente);
        await cas.ExecuterAsync(20, "Erreur de produit", [new(1, 1, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal(new LectureDette(3, 400, 100), stock.Ecrit!.Lecture);
    }

    [Fact]
    public async Task Retour_paye_comptant_sans_dette_ne_demande_aucune_relecture()
    {
        var vente = new VenteRetournable(VenteRetour(500, patientId: 3), [new LigneRetournable(1, 10, "Produit fictif", "L1", 1, 0, 500, [])]);
        var (cas, stock) = MonterRetour(vente);
        await cas.ExecuterAsync(20, "Erreur de produit", [new(1, 1, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Null(stock.Ecrit!.Lecture);
    }

    [Fact]
    public async Task Le_journal_du_retour_est_remis_a_la_transaction_d_ecriture()
    {
        var vente = new VenteRetournable(VenteRetour(500), [new LigneRetournable(1, 10, "Produit fictif", "L1", 1, 0, 500, [])]);
        var (cas, stock) = MonterRetour(vente);
        await cas.ExecuterAsync(20, "Erreur de produit", [new(1, 1, "stock", null)], 8, "Caisse 1", null, null);
        var entree = Assert.Single(stock.Ecrit!.Journaux!);
        Assert.Equal("retour_client", entree.Action);
        Assert.Equal("T-000020", entree.Details(new ContexteJournal(20, "T-000020"))["ticket"]);
    }

    [Fact]
    public async Task Le_journal_de_l_annulation_est_remis_a_la_transaction_et_la_dette_est_relue_sous_verrou()
    {
        var v = new VenteAnnulable(12, "T-000012", DateTime.UtcNow, "Vendeur", "Patient fictif", 470, 470, 0, "credit", "validee", true, false,
            [new LigneVenteAnnulable("Produit fictif", 2, 470)], 0, 470, null, null, 3, 0);
        var stock = new AnnulationsEnMemoire(v);
        var droits = new DroitsParEmploye(new() { [5] = ["caisse.vendre"] });
        var cas = new AnnulerVente(stock, new SessionsEnMemoire(), droits,
            new VerificationCodeComptoir(new CodesEnMemoire(), new VerificateurFactice(), new HorlogeFixe(new DateOnly(2026, 11, 23)), new JournalEspion()), new JournalEspion());
        await cas.ExecuterAsync(12, "Erreur de produit", 5, "Caisse 1", null, null);
        Assert.Contains(stock.Ecrite!.Journaux!, j => j.Action == "vente_annulee");
        Assert.Equal(new LectureDette(3, 470, 0), stock.Ecrite.Lecture);
    }

    [Fact]
    public async Task Le_journal_de_l_encaissement_de_dette_est_remis_a_la_transaction_avec_le_numero_du_recu()
    {
        var stock = new CreditsEnMemoire(210);
        await new EncaisserReglementCredit(stock, new SessionsEnMemoire()).ExecuterAsync(3, 200, "especes", 5, "Caisse 1");
        var entree = stock.JournalEncaissement!;
        Assert.Equal(("reglement_credit", 3L), (entree.Action, entree.EnregistrementId));
        Assert.Equal("RC-000007", entree.Details(new ContexteJournal(0, "RC-000007"))["recu"]);
    }

    [Fact]
    public async Task Encaissement_de_dette_avec_plus_de_deux_decimales_refuse()
    {
        var e = await Assert.ThrowsAsync<RegleMetierException>(() =>
            new EncaisserReglementCredit(new CreditsEnMemoire(210), new SessionsEnMemoire()).ExecuterAsync(3, 100.005m, "especes", 5, "Caisse 1"));
        Assert.Contains("2 décimales", e.Message);
    }

    [Fact]
    public async Task Le_journal_de_la_correction_de_dette_est_remis_a_la_transaction()
    {
        var stock = new CreditsEnMemoire(210);
        await new CorrigerDetteCredit(stock, new DroitsFactices { Titulaire = true }).ExecuterAsync(3, 100, "Erreur de reprise", 1, "Caisse 1");
        Assert.Equal("correction_credit", stock.JournalCorrection!.Action);
    }

    [Fact]
    public async Task Le_journal_de_la_vente_sans_ordonnance_est_remis_a_la_transaction_d_enregistrement()
    {
        var ventes = new VentesEspion();
        var cas = new Encaisser(new CatalogueEnMemoire(), new PatientsEnMemoire(), ventes, new SessionsEnMemoire(),
            new DroitsFactices("caisse.vendre", "caisse.vendre_sans_ordonnance"), new HorlogeFixe(CatalogueEnMemoire.Aujourdhui), new ScansEnMemoire(),
            journal: new JournalEspion());
        await cas.ExecuterAsync(CaisseApplicationTests.Commande(9, 90), 5, "Caisse 1");
        Assert.Contains(ventes.Recu!.Journaux!, j => j.Action == "vente_sans_ordonnance");
    }

    [Fact]
    public async Task La_mise_en_attente_demande_au_serveur_de_garantir_la_borne_de_20_par_poste()
    {
        var attente = new AttenteEnMemoire();
        await new GererVentesEnAttente(attente).MettreEnAttenteAsync("Caisse 1", 5, "Patient fictif", 2, 470, "{\"a\":1}");
        Assert.Equal(GererVentesEnAttente.MaxParPoste, attente.MaxRecu);
        Assert.Equal(20, GererVentesEnAttente.MaxParPoste);
    }
}
