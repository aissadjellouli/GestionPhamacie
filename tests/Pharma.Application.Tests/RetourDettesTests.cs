using Pharma.Application.Caisse;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

/// <summary>
/// Retour et dettes du patient (décision du titulaire du 2026-11-21, script 158). Vente Chifa : 5 boîtes à 100 DA, 80 % (part caisse 400,
/// patient 100), payée en espèces ; volet rattaché au prix Chifa 90 : écart 40 mis à la charge du patient (8 par boîte).
/// </summary>
public class RetourDettesTests
{
    private static VenteRetournable Vente(decimal dette, decimal regularisationPayee = 0, decimal renduParRetours = 0, int dejaRendues = 0,
        string mode = "especes", decimal payeSurVente = 0) => new(
        new VenteAnnulable(40, "T-000040", DateTime.UtcNow, "Vendeur", "Patient Fictif", 500, 100, 400, mode, "validee", false, false, [],
            SoldeCreditPatient: dette, PatientId: 7, PayeSurVente: payeSurVente, DejaRendu: dejaRendues * 20m,
            Regularisation: new RegularisationVente(3, RegularisationPartOrganisme.ChargePatient, 40, regularisationPayee, renduParRetours)),
        [new LigneRetournable(1, 10, "Sirop fictif", "S1", 5, dejaRendues, 100, [], 400)],
        new OrganismeRetour(360, dejaRendues * 72m, "chifa"));

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
    public async Task Regularisation_payee_et_aucune_dette_la_part_de_l_ecart_est_rendue_avec_la_part_du_patient()
    {
        var (cas, stock, journal) = Monter(Vente(dette: 0, regularisationPayee: 40));
        var r = await cas.ExecuterAsync(40, "Erreur", DeuxBoites, 8, "Caisse 1", null, null);
        Assert.Equal((56m, 16m, 0m, "especes"), (r.ARendre, r.PartRegularisation, r.DetteDiminuee, r.Mode));
        var e = stock.Ecrit!;
        Assert.Equal((56m, 16m, 144m), (e.Montant, e.Lignes[0].MontantRegularisation, e.Lignes[0].MontantOrganisme));
        Assert.Equal((0m, 0m, 56m), (e.ReductionRegularisation, e.ImputationDettes, e.Rendu));
        Assert.Equal(16m, journal.Details[journal.Actions.IndexOf("retour_client")]["part_regularisation"]);
        Assert.DoesNotContain("reglement_credit", journal.Actions);
    }

    [Fact]
    public async Task Regularisation_impayee_retiree_de_la_dette_puis_choix_exige_pour_l_argent_paye()
    {
        // Sa seule dette : l'écart 40, impayé. 16 retirés de sa dette ; il doit encore 24 et a payé sa part (40) : choix exigé
        var (cas, stock, _) = Monter(Vente(dette: 40));
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(40, "Erreur", DeuxBoites, 8, "Caisse 1", null, null));
        Assert.Equal("dettes_patient", e.Champ);
        Assert.Null(stock.Ecrit);
    }

    [Fact]
    public async Task Deduire_de_ses_dettes_imputation_sans_sortie_du_tiroir_et_inscrite_comme_un_encaissement_de_dette()
    {
        // Dette 240 (écart 40 impayé + 200 sur une autre vente). Caisse fermée : rien ne sort du tiroir, le retour passe quand même
        var (cas, stock, journal) = Monter(Vente(dette: 240), new SessionsEnMemoire { Ouverte = null });
        // Sans l'accord du client : refusé
        var sansAccord = await Assert.ThrowsAsync<RegleMetierException>(() =>
            cas.ExecuterAsync(40, "Erreur", DeuxBoites, 8, "Caisse 1", null, null, dettes: RegleRetour.DettesDeduire));
        Assert.Equal("accord_client", sansAccord.Champ);

        var r = await cas.ExecuterAsync(40, "Erreur", DeuxBoites, 8, "Caisse 1", null, null, accordClient: true, dettes: RegleRetour.DettesDeduire);
        Assert.Equal((0m, 40m, 56m, 184m), (r.ARendre, r.ImputeSurDettes, r.DetteDiminuee, r.DetteRestante));
        var e = stock.Ecrit!;
        Assert.Equal((16m, 40m, 0m, 0m), (e.ReductionRegularisation, e.ImputationDettes, e.ReductionDette, e.Rendu));
        Assert.True(e.AccordClient);
        Assert.Null(e.SessionCaisseId);
        var retour = journal.Details[journal.Actions.IndexOf("retour_client")];
        Assert.Equal((RegleRetour.DettesDeduire, 40m, 16m), (retour["choix_dettes"], retour["impute_sur_dettes"], retour["regularisation_dette_diminuee"]));
        var reglement = journal.Details[journal.Actions.IndexOf("reglement_credit")];
        Assert.Equal((40m, "retour_client", 184m), (reglement["montant"], reglement["origine"], reglement["reste_du"]));
    }

    [Fact]
    public async Task Tout_deduit_le_caissier_sans_le_droit_valide_seul()
    {
        var (cas, stock, _) = Monter(Vente(dette: 240));
        var r = await cas.ExecuterAsync(40, "Erreur", DeuxBoites, 5, "Caisse 1", null, null, accordClient: true, dettes: RegleRetour.DettesDeduire);
        Assert.Equal((0m, (string?)null), (r.ARendre, r.ValidePar));
        Assert.Equal(5L, stock.Ecrit!.ValidePar);
    }

    [Fact]
    public async Task Rendre_quand_meme_garde_la_validation_du_droit_de_rendre()
    {
        var (cas, stock, journal) = Monter(Vente(dette: 240));
        // Employé 5 sans le droit caisse.retour_especes : code d'un collègue qui l'a exigé
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            cas.ExecuterAsync(40, "Erreur", DeuxBoites, 5, "Caisse 1", null, null, dettes: RegleRetour.DettesRendre));
        var r = await cas.ExecuterAsync(40, "Erreur", DeuxBoites, 5, "Caisse 1", "assistante", "4826", dettes: RegleRetour.DettesRendre);
        Assert.Equal((40m, 0m, 16m, "Assistante Fictive"), (r.ARendre, r.ImputeSurDettes, r.DetteDiminuee, r.ValidePar));
        Assert.Equal((16m, 0m, 40m), (stock.Ecrit!.ReductionRegularisation, stock.Ecrit.ImputationDettes, stock.Ecrit.Rendu));
        Assert.Equal(RegleRetour.DettesRendre, journal.Details[journal.Actions.IndexOf("retour_client")]["choix_dettes"]);
        Assert.DoesNotContain("reglement_credit", journal.Actions);
    }

    [Fact]
    public async Task Deduire_au_plus_ce_qu_il_doit_le_surplus_est_rendu_avec_validation()
    {
        // Écart payé ; autre dette 30 : 56 dus au patient, 30 déduits, 26 rendus en espèces
        var (cas, stock, _) = Monter(Vente(dette: 30, regularisationPayee: 40));
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            cas.ExecuterAsync(40, "Erreur", DeuxBoites, 5, "Caisse 1", null, null, accordClient: true, dettes: RegleRetour.DettesDeduire));
        var r = await cas.ExecuterAsync(40, "Erreur", DeuxBoites, 8, "Caisse 1", null, null, accordClient: true, dettes: RegleRetour.DettesDeduire);
        Assert.Equal((26m, 30m, 0m), (r.ARendre, r.ImputeSurDettes, r.DetteRestante));
        Assert.Equal((30m, 26m), (stock.Ecrit!.ImputationDettes, stock.Ecrit.Rendu));
    }

    [Fact]
    public async Task Achat_a_credit_la_boite_est_reprise_sans_rien_rendre_dette_diminuee_de_la_part_et_de_l_ecart()
    {
        // Vente à crédit (100) et écart 40, rien payé : dette 140. Retour de 2 boîtes : 40 + 16 retirés de sa dette, rien rendu
        var (cas, stock, _) = Monter(Vente(dette: 140, mode: "credit"), new SessionsEnMemoire { Ouverte = null });
        var r = await cas.ExecuterAsync(40, "Erreur", DeuxBoites, 8, "Caisse 1", null, null);
        Assert.Equal((0m, 56m, 84m), (r.ARendre, r.DetteDiminuee, r.DetteRestante));
        Assert.Equal((40m, 16m, 0m, 0m), (stock.Ecrit!.ReductionDette, stock.Ecrit.ReductionRegularisation, stock.Ecrit.ImputationDettes, stock.Ecrit.Rendu));
    }

    [Fact]
    public async Task Dernieres_boites_le_solde_exact_de_la_regularisation()
    {
        // 2 boîtes déjà rendues (16 de l'écart rendus) ; les 3 dernières : tout ce qui reste, 24
        var (cas, stock, _) = Monter(Vente(dette: 0, regularisationPayee: 24, renduParRetours: 16, dejaRendues: 2));
        var r = await cas.ExecuterAsync(40, "Erreur", [new(1, 3, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal((24m, 84m), (r.PartRegularisation, r.ARendre));
        Assert.Equal(216m, stock.Ecrit!.Lignes[0].MontantOrganisme);
    }

    [Fact]
    public async Task Ancienne_demande_solder_la_dette_vaut_deduire()
    {
        var (cas, stock, _) = Monter(Vente(dette: 240));
        var r = await cas.ExecuterAsync(40, "Erreur", DeuxBoites, 8, "Caisse 1", null, null, solderDette: true, accordClient: true);
        Assert.Equal((40m, 0m), (r.ImputeSurDettes, r.ARendre));
        Assert.Equal(40m, stock.Ecrit!.ImputationDettes);
    }
}
