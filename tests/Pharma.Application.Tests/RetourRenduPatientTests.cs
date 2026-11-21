using Pharma.Application.Caisse;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

/// <summary>
/// Retour d'une vente régularisée « rendu au patient » (décision du titulaire du 2026-11-21, script 159). Vente Chifa : 5 boîtes à 100 DA,
/// 80 % (part caisse 400, patient 100), payée en espèces ; volet rattaché au prix Chifa 110 (part Chifa 440) : l'organisme couvre 40 de
/// plus, rendus au patient (8 par boîte). Au retour, cette part est retenue sur ce qui lui est rendu.
/// </summary>
public class RetourRenduPatientTests
{
    private static VenteRetournable Vente(decimal dette = 0, decimal retenuParRetours = 0, int dejaRendues = 0, string mode = "especes") => new(
        new VenteAnnulable(50, "T-000050", DateTime.UtcNow, "Vendeur", "Patient Fictif", 500, 100, 400, mode, "validee", false, false, [],
            SoldeCreditPatient: dette, PatientId: 7, DejaRendu: dejaRendues * 20m,
            Regularisation: new RegularisationVente(4, RegularisationPartOrganisme.AvoirPatient, 40, 0, retenuParRetours)),
        [new LigneRetournable(1, 10, "Sirop fictif", "S1", 5, dejaRendues, 100, [], 400)],
        new OrganismeRetour(440, dejaRendues * 88m, "chifa"));

    /// <summary>
    /// Deux lignes : A à 100 % (2 boîtes à 100, part caisse 200, patient 0) et B à 50 % (2 boîtes à 100, part caisse 100, patient 100),
    /// payée en espèces ; part Chifa 340, 40 rendus au patient. Rendre les 2 boîtes A : rien de payé, 26,67 déjà rendus pour elles.
    /// </summary>
    private static VenteRetournable VenteMixte(bool avecPatient = true, decimal dette = 0) => new(
        new VenteAnnulable(60, "T-000060", DateTime.UtcNow, "Vendeur", avecPatient ? "Patient Fictif" : null, 400, 100, 300, "especes", "validee", false, false, [],
            SoldeCreditPatient: dette, PatientId: avecPatient ? 7 : null,
            Regularisation: new RegularisationVente(5, RegularisationPartOrganisme.AvoirPatient, 40, 0)),
        [new LigneRetournable(1, 10, "Injectable fictif", "A1", 2, 0, 0, [], 200), new LigneRetournable(2, 11, "Sirop fictif", "B1", 2, 0, 100, [], 100)],
        new OrganismeRetour(340, 0, "chifa"));

    private static (EnregistrerRetourClient Cas, RetoursEnMemoire Stock, JournalEspion Journal) Monter(VenteRetournable v, SessionsEnMemoire? sessions = null)
    {
        var stock = new RetoursEnMemoire(v);
        var codes = new CodesEnMemoire();
        codes.Comptes[8] = new CompteComptoir(8, "assistante", "Assistante Fictive", "hash:4826", 0, null);
        var droits = new DroitsParEmploye(new() { [5] = ["caisse.vendre"], [8] = ["caisse.vendre", "caisse.retour_especes"] });
        var journal = new JournalEspion();
        return (new EnregistrerRetourClient(stock, sessions ?? new SessionsEnMemoire(), droits,
            new VerificationCodeComptoir(codes, new VerificateurFactice(), new HorlogeFixe(new DateOnly(2026, 11, 21)), journal), journal), stock, journal);
    }

    private static readonly LigneRetourDemandee[] DeuxBoites = [new(1, 2, "stock", null)];

    [Fact]
    public async Task La_part_deja_rendue_est_retenue_sur_le_remboursement_au_prorata()
    {
        var (cas, stock, journal) = Monter(Vente());
        var r = await cas.ExecuterAsync(50, "Erreur", DeuxBoites, 8, "Caisse 1", null, null);
        // Part payée 40, retenue 16 (2 × 8) : 24 rendus ; part organisme retirée au prix Chifa : 176
        Assert.Equal((24m, 16m, 0m, 176m), (r.ARendre, r.AvoirRetenu, r.DetteAvoirRetenu, r.PartOrganismeRetiree));
        var e = stock.Ecrit!;
        Assert.Equal((40m, 16m, 0m), (e.Lignes[0].Montant, e.Lignes[0].MontantAvoirRetenu, e.Lignes[0].MontantRegularisation));
        Assert.Equal((24m, 24m, 0m), (e.Montant, e.Rendu, e.DetteAvoirRetenu));
        var details = journal.Details[journal.Actions.IndexOf("retour_client")];
        Assert.Equal((16m, 16m), (details["avoir_retenu"], details["avoir_retenu_sur_remboursement"]));
        Assert.Null(details["dette_avoir_retenu"]);
        Assert.Null(details["part_regularisation"]);
    }

    [Fact]
    public async Task Dernieres_boites_le_solde_exact_de_la_retenue()
    {
        // 2 boîtes déjà rendues (16 retenus) ; les 3 dernières : tout ce qui reste, 24 ; part organisme : solde exact 264
        var (cas, stock, _) = Monter(Vente(retenuParRetours: 16, dejaRendues: 2));
        var r = await cas.ExecuterAsync(50, "Erreur", [new(1, 3, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal((24m, 36m, 264m), (r.AvoirRetenu, r.ARendre, r.PartOrganismeRetiree));
        Assert.Equal(60m, stock.Ecrit!.Lignes[0].Montant);
    }

    [Fact]
    public async Task Retenue_superieure_au_montant_a_rendre_alerte_puis_dette_du_patient()
    {
        var (cas, stock, journal) = Monter(VenteMixte());
        var sansConfirmation = await Assert.ThrowsAsync<RegleMetierException>(() =>
            cas.ExecuterAsync(60, "Erreur", [new(1, 2, "stock", null)], 8, "Caisse 1", null, null));
        Assert.Equal("dette_retenue", sansConfirmation.Champ);
        Assert.Null(stock.Ecrit);

        var r = await cas.ExecuterAsync(60, "Erreur", [new(1, 2, "stock", null)], 8, "Caisse 1", null, null, detteRetenue: true);
        Assert.Equal((0m, 26.67m, 26.67m, 26.67m), (r.ARendre, r.AvoirRetenu, r.DetteAvoirRetenu, r.DetteRestante));
        var e = stock.Ecrit!;
        Assert.Equal((0m, 0m, 26.67m), (e.Montant, e.Rendu, e.DetteAvoirRetenu));
        Assert.Equal((0m, 26.67m), (e.Lignes[0].Montant, e.Lignes[0].MontantAvoirRetenu));
        Assert.Equal(26.67m, journal.Details[journal.Actions.IndexOf("retour_client")]["dette_avoir_retenu"]);
    }

    [Fact]
    public async Task Rien_a_rendre_et_dette_ajoutee_la_validation_du_droit_de_rendre_reste_exigee()
    {
        var (cas, _, _) = Monter(VenteMixte());
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            cas.ExecuterAsync(60, "Erreur", [new(1, 2, "stock", null)], 5, "Caisse 1", null, null, detteRetenue: true));
        var r = await cas.ExecuterAsync(60, "Erreur", [new(1, 2, "stock", null)], 5, "Caisse 1", "assistante", "4826", detteRetenue: true);
        Assert.Equal("Assistante Fictive", r.ValidePar);
    }

    [Fact]
    public async Task Vente_sans_patient_reste_non_couvert_retour_refuse()
    {
        var (cas, stock, _) = Monter(VenteMixte(avecPatient: false));
        var e = await Assert.ThrowsAsync<RegleMetierException>(() =>
            cas.ExecuterAsync(60, "Erreur", [new(1, 2, "stock", null)], 8, "Caisse 1", null, null, detteRetenue: true));
        Assert.Equal("dette_retenue", e.Champ);
        Assert.Null(stock.Ecrit);
    }

    [Fact]
    public async Task Combinaison_avec_les_dettes_la_retenue_d_abord_puis_deduire_de_ses_dettes()
    {
        // Il doit 200 par ailleurs : 40 payés − 16 retenus = 24, soumis au choix ; « Déduire » : 24 imputés, rien rendu
        var (cas, stock, journal) = Monter(Vente(dette: 200));
        var choix = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(50, "Erreur", DeuxBoites, 8, "Caisse 1", null, null));
        Assert.Equal("dettes_patient", choix.Champ);
        Assert.Contains("24", choix.Message);
        var r = await cas.ExecuterAsync(50, "Erreur", DeuxBoites, 8, "Caisse 1", null, null, accordClient: true, dettes: RegleRetour.DettesDeduire);
        Assert.Equal((0m, 24m, 16m, 176m), (r.ARendre, r.ImputeSurDettes, r.AvoirRetenu, r.DetteRestante));
        Assert.Equal((24m, 24m, 0m), (stock.Ecrit!.Montant, stock.Ecrit.ImputationDettes, stock.Ecrit.Rendu));
        Assert.Equal(24m, journal.Details[journal.Actions.IndexOf("reglement_credit")]["montant"]);
    }

    [Fact]
    public async Task Achat_a_credit_dette_diminuee_de_la_part_puis_retenue_ajoutee_a_sa_dette()
    {
        // Vente à crédit (100), rien payé : dette 100. Retour de 2 boîtes : 40 retirés de sa dette ; 16 retenus, rien à prendre : dette
        var (cas, stock, _) = Monter(Vente(dette: 100, mode: "credit"), new SessionsEnMemoire { Ouverte = null });
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(50, "Erreur", DeuxBoites, 8, "Caisse 1", null, null));
        var r = await cas.ExecuterAsync(50, "Erreur", DeuxBoites, 8, "Caisse 1", null, null, detteRetenue: true);
        Assert.Equal((0m, 40m, 16m, 76m), (r.ARendre, r.DetteDiminuee, r.DetteAvoirRetenu, r.DetteRestante));
        Assert.Equal((40m, 40m, 16m, 0m), (stock.Ecrit!.Montant, stock.Ecrit.ReductionDette, stock.Ecrit.DetteAvoirRetenu, stock.Ecrit.Rendu));
    }

    [Fact]
    public async Task Regularisation_a_la_charge_du_patient_inchangee()
    {
        // Script 158 : rien n'est retenu
        var v = Vente() with { Vente = Vente().Vente with { Regularisation = new RegularisationVente(4, RegularisationPartOrganisme.ChargePatient, 40, 40) } };
        var (cas, stock, _) = Monter(v with { Organisme = new OrganismeRetour(360, 0, "chifa") });
        var r = await cas.ExecuterAsync(50, "Erreur", DeuxBoites, 8, "Caisse 1", null, null);
        Assert.Equal((56m, 16m, 0m), (r.ARendre, r.PartRegularisation, r.AvoirRetenu));
        Assert.Equal(0m, stock.Ecrit!.Lignes[0].MontantAvoirRetenu);
    }
}
