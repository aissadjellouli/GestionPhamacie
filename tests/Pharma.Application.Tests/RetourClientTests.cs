using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

internal sealed class RetoursEnMemoire(VenteRetournable vente) : IRetoursClient
{
    public RetourAEcrire? Ecrit { get; private set; }
    public Task<VenteRetournable?> LireAsync(long id, CancellationToken ct = default) => Task.FromResult<VenteRetournable?>(vente);
    public Task EnregistrerAsync(RetourAEcrire r, CancellationToken ct = default)
    {
        Ecrit = r;
        JournalEspion.Rejouer(r.Journaux, new ContexteJournal(r.VenteId, r.Numero));
        return Task.CompletedTask;
    }
}

public class RetourClientTests
{
    private static VenteRetournable Vente(string mode = "especes", decimal partOrganisme = 0) => new(
        new VenteAnnulable(20, "T-000020", DateTime.UtcNow, "Vendeur", null, 590, 590, partOrganisme, mode, "validee", false, false, []),
        [
            new LigneRetournable(1, 10, "Sirop fictif", "S1", 3, 0, 350, []),
            new LigneRetournable(2, 11, "Comprimé fictif", "C1", 2, 0, 240, [new BoiteVendue(100, "SN-A"), new BoiteVendue(101, "SN-B")])
        ]);

    private static (EnregistrerRetourClient Cas, RetoursEnMemoire Stock) Monter(VenteRetournable v, SessionsEnMemoire? sessions = null,
        Pharma.Application.Patients.IPatients? patients = null)
    {
        var stock = new RetoursEnMemoire(v);
        var codes = new CodesEnMemoire();
        codes.Comptes[8] = new CompteComptoir(8, "assistante", "Assistante Fictive", "hash:4826", 0, null);
        var droits = new DroitsParEmploye(new() { [5] = ["caisse.vendre"], [8] = ["caisse.vendre", "caisse.retour_especes"] });
        var journal = new JournalEspion();
        return (new EnregistrerRetourClient(stock, sessions ?? new SessionsEnMemoire(), droits,
            new VerificationCodeComptoir(codes, new VerificateurFactice(), new HorlogeFixe(new DateOnly(2026, 11, 9)), journal), patients), stock);
    }

    [Fact]
    public async Task Le_caissier_doit_faire_valider_le_retour()
    {
        var (cas, stock) = Monter(Vente());
        LigneRetourDemandee[] l = [new(1, 1, "stock", null)];
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(20, "Traitement arrêté", l, 5, "Caisse 1", null, null));
        var r = await cas.ExecuterAsync(20, "Traitement arrêté", l, 5, "Caisse 1", "assistante", "4826");
        Assert.Equal(("Assistante Fictive", 116.67m, 1), (r.ValidePar, r.ARendre, r.RemisEnStock));
        Assert.Equal(8, stock.Ecrit!.ValidePar);
    }

    [Fact]
    public async Task Boites_identifiees_cochees_et_destination_au_choix()
    {
        var (cas, stock) = Monter(Vente());
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            cas.ExecuterAsync(20, "Mauvais dosage", [new(2, 1, "destruction", null)], 8, "Caisse 1", null, null));
        var r = await cas.ExecuterAsync(20, "Mauvais dosage", [new(2, 1, "destruction", [101]), new(1, 2, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal((2, 1, 353.33m), (r.RemisEnStock, r.ADetruire, r.ARendre));
        Assert.Equal(DestinationRetour.Destruction, stock.Ecrit!.Lignes[0].Destination);
        Assert.Equal([101L], stock.Ecrit.Lignes[0].Boites);
    }

    [Fact]
    public async Task Plus_que_vendu_refuse()
    {
        var (cas, _) = Monter(Vente());
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(20, "Erreur", [new(1, 4, "stock", null)], 8, "Caisse 1", null, null));
    }

    // ---- Tiers payant (script 155, option A du 2026-11-21) ----

    /// <summary>
    /// Vente Chifa : 2 × 500 à 80 % (organisme 800, patient 200), 1 × 300 à 80 % (organisme 240, patient 60), 1 × 100 non remboursée.
    /// Total 1 400, organisme 1 040, patient 360.
    /// </summary>
    private static VenteRetournable VenteTp(string mode = "especes", bool bordereau = false, bool deposee = false, decimal partRetenue = 1040,
        decimal dejaRetiree = 0, string canal = "chifa", string? factureOrganisme = null) => new(
        new VenteAnnulable(21, "T-000021", DateTime.UtcNow, "Vendeur", "Patient Fictif", 1400, 360, 1040, mode, "validee", false, bordereau || deposee, [],
            PatientId: 7, Deposee: deposee),
        [
            new LigneRetournable(1, 10, "Sirop fictif", "S1", 2, 0, 200, [], 800),
            new LigneRetournable(2, 11, "Comprimé fictif", "C1", 1, 0, 60, [], 240),
            new LigneRetournable(3, 12, "Crème fictive", "K1", 1, 0, 100, [])
        ],
        new OrganismeRetour(partRetenue, dejaRetiree, canal, factureOrganisme, "06931126"));

    [Fact]
    public async Task Tiers_payant_80_pour_cent_especes_part_patient_rendue_et_part_organisme_retiree()
    {
        var (cas, stock) = Monter(VenteTp());
        var r = await cas.ExecuterAsync(21, "Erreur", [new(1, 1, "stock", null), new(3, 1, "destruction", null)], 8, "Caisse 1", null, null);
        Assert.Equal((200m, 400m, "chifa", "06931126"), (r.ARendre, r.PartOrganismeRetiree, r.Canal, r.FactureChifa));
        Assert.Equal((100m, 400m), (stock.Ecrit!.Lignes[0].Montant, stock.Ecrit.Lignes[0].MontantOrganisme));
        Assert.Equal((100m, 0m), (stock.Ecrit.Lignes[1].Montant, stock.Ecrit.Lignes[1].MontantOrganisme));
    }

    [Fact]
    public async Task Tiers_payant_a_credit_la_dette_baisse_et_la_creance_aussi()
    {
        var vente = VenteTp("credit");
        var (cas, stock) = Monter(vente with { Vente = vente.Vente with { SoldeCreditPatient = 360 } }, new SessionsEnMemoire { Ouverte = null });
        var r = await cas.ExecuterAsync(21, "Erreur", [new(2, 1, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal((0m, 60m, 240m), (r.ARendre, r.DetteDiminuee, r.PartOrganismeRetiree));
        Assert.Equal(240m, stock.Ecrit!.Lignes[0].MontantOrganisme);
    }

    [Fact]
    public async Task Tiers_payant_paye_par_avoir_l_avoir_est_recredite_et_la_creance_baisse()
    {
        var vente = VenteTp();
        var (cas, stock) = Monter(vente with { Vente = vente.Vente with { PayeParAvoir = 360 } });
        var r = await cas.ExecuterAsync(21, "Erreur", [new(1, 2, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal((0m, 200m, 800m), (r.ARendre, r.AvoirRecredite, r.PartOrganismeRetiree));
        Assert.Equal((200m, 7L), (stock.Ecrit!.AvoirRecredite, stock.Ecrit.PatientAvoirId));
    }

    [Fact]
    public async Task Taux_100_pour_cent_rien_rendu_stock_et_creance_seulement_validation_exigee()
    {
        // Prise en charge totale : 2 × 450, organisme 900, patient 0
        var v = new VenteRetournable(
            new VenteAnnulable(22, "T-000022", DateTime.UtcNow, "Vendeur", "Patient Fictif", 900, 0, 900, null, "validee", false, false, [], PatientId: 7),
            [new LigneRetournable(1, 10, "Insuline fictive", "I1", 2, 0, 0, [], 900)],
            new OrganismeRetour(900, 0, "chifa"));
        // Caissier sans le droit : validation exigée, même sans argent rendu
        var (sansDroit, _) = Monter(v, new SessionsEnMemoire { Ouverte = null });
        await Assert.ThrowsAsync<RegleMetierException>(() => sansDroit.ExecuterAsync(22, "Erreur", [new(1, 1, "stock", null)], 5, "Caisse 1", null, null));
        // Validé (caisse fermée : rien ne sort du tiroir)
        var (cas, stock) = Monter(v, new SessionsEnMemoire { Ouverte = null });
        var r = await cas.ExecuterAsync(22, "Erreur", [new(1, 1, "stock", null)], 5, "Caisse 1", "assistante", "4826");
        Assert.Equal((0m, 450m, "Assistante Fictive"), (r.ARendre, r.PartOrganismeRetiree, r.ValidePar));
        Assert.Equal((0m, 450m), (stock.Ecrit!.Montant, stock.Ecrit.Lignes[0].MontantOrganisme));
    }

    [Fact]
    public async Task Apres_regularisation_prorata_de_la_part_chifa_et_solde_exact_au_dernier_retour()
    {
        // Part Chifa retenue 936 au lieu de 1 040 (ratio 0,9) : 1 boîte de la ligne 1 (800 / 2) → 360
        var (cas, stock) = Monter(VenteTp(partRetenue: 936));
        var r = await cas.ExecuterAsync(21, "Erreur", [new(1, 1, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal(360m, r.PartOrganismeRetiree);
        // Dernières boîtes remboursées rendues (360 déjà retirés) : le solde exact 936 − 360 = 576 (360 + 216)
        var deja = VenteTp(partRetenue: 936, dejaRetiree: 360);
        var suite = deja with { Lignes = [deja.Lignes[0] with { DejaRendue = 1 }, deja.Lignes[1], deja.Lignes[2]] };
        var (fin, stock2) = Monter(suite);
        var r2 = await fin.ExecuterAsync(21, "Erreur", [new(1, 1, "stock", null), new(2, 1, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal(576m, r2.PartOrganismeRetiree);
        Assert.Equal((360m, 216m), (stock2.Ecrit!.Lignes[0].MontantOrganisme, stock2.Ecrit.Lignes[1].MontantOrganisme));
    }

    [Fact]
    public async Task Vente_deposee_boite_remboursee_refusee_avec_le_message_du_titulaire_ligne_non_remboursee_permise()
    {
        var (cas, stock) = Monter(VenteTp(deposee: true));
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(21, "Erreur", [new(1, 1, "stock", null)], 8, "Caisse 1", null, null));
        Assert.Equal(RegleRetour.MessageDeposee, e.Message);
        Assert.Null(stock.Ecrit);
        // Refus total : une boîte remboursée dans la demande suffit
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(21, "Erreur", [new(3, 1, "stock", null), new(2, 1, "stock", null)], 8, "Caisse 1", null, null));
        var r = await cas.ExecuterAsync(21, "Erreur", [new(3, 1, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal((100m, 0m), (r.ARendre, r.PartOrganismeRetiree));
    }

    [Fact]
    public async Task Vente_sur_un_bordereau_en_preparation_la_retirer_d_abord()
    {
        var (cas, _) = Monter(VenteTp(bordereau: true));
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(21, "Erreur", [new(2, 1, "stock", null)], 8, "Caisse 1", null, null));
        Assert.Contains("retirez-la d’abord du bordereau", e.Message);
    }

    [Fact]
    public async Task Facture_d_organisme_du_logiciel_rappel_de_l_avoir_manuel()
    {
        var (cas, _) = Monter(VenteTp(canal: "logiciel", factureOrganisme: "F-2026-000042"));
        var r = await cas.ExecuterAsync(21, "Erreur", [new(2, 1, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal((240m, "logiciel", "F-2026-000042"), (r.PartOrganismeRetiree, r.Canal, r.FactureOrganisme));
    }

    [Fact]
    public async Task Credit_sans_argent_rendu_et_especes_sans_caisse_ouverte_refusees()
    {
        var due = Vente("credit");
        var (credit, stock) = Monter(due with { Vente = due.Vente with { SoldeCreditPatient = 590 } }, new SessionsEnMemoire { Ouverte = null });
        var r = await credit.ExecuterAsync(20, "Erreur", [new(1, 3, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal((0m, 350m, 350m), (r.ARendre, stock.Ecrit!.Montant, stock.Ecrit.ReductionDette));
        var (especes, _) = Monter(Vente(), new SessionsEnMemoire { Ouverte = null });
        await Assert.ThrowsAsync<RegleMetierException>(() => especes.ExecuterAsync(20, "Erreur", [new(1, 1, "stock", null)], 8, "Caisse 1", null, null));
    }

    [Fact]
    public async Task Credit_deja_rembourse_la_part_payee_est_rendue_en_especes()
    {
        // Dette restante 100 DA : elle est effacée, les 250 DA déjà remboursés par le patient lui sont rendus
        var vente = Vente("credit");
        var partielle = vente with { Vente = vente.Vente with { SoldeCreditPatient = 100, PayeSurVente = 490 } };
        var (cas, stock) = Monter(partielle);
        var r = await cas.ExecuterAsync(20, "Erreur", [new(1, 3, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal((250m, "especes"), (r.ARendre, r.Mode));
        Assert.Equal((350m, 100m), (stock.Ecrit!.Montant, stock.Ecrit.ReductionDette));

        // L'argent rendu sort d'une caisse ouverte
        var (sansCaisse, _) = Monter(partielle, new SessionsEnMemoire { Ouverte = null });
        await Assert.ThrowsAsync<RegleMetierException>(() => sansCaisse.ExecuterAsync(20, "Erreur", [new(1, 3, "stock", null)], 8, "Caisse 1", null, null));
    }

    [Fact]
    public async Task Patient_endette_le_caissier_choisit_de_solder_la_dette_ou_de_rendre()
    {
        // Vente payée en espèces à un patient qui doit 200 DA par ailleurs ; retour de 350 DA
        var vente = Vente();
        var endette = vente with { Vente = vente.Vente with { Patient = "Patient Fictif", SoldeCreditPatient = 200 } };
        LigneRetourDemandee[] l = [new(1, 3, "stock", null)];

        // Sans l'accord du client : refusé
        var (sansAccord, _) = Monter(endette);
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => sansAccord.ExecuterAsync(20, "Erreur", l, 8, "Caisse 1", null, null, solderDette: true));
        Assert.Equal("accord_client", e.Champ);

        var (solder, stock) = Monter(endette);
        var r = await solder.ExecuterAsync(20, "Erreur", l, 8, "Caisse 1", null, null, solderDette: true, accordClient: true);
        Assert.True(stock.Ecrit!.AccordClient);
        Assert.Equal((150m, 200m, "especes"), (r.ARendre, r.DetteDiminuee, r.Mode));   // dette soldée, surplus rendu
        // Script 158 : imputé sur ses dettes comme un encaissement de dette
        Assert.Equal((350m, 0m, 200m), (stock.Ecrit!.Montant, stock.Ecrit.ReductionDette, stock.Ecrit.ImputationDettes));

        // Script 158 : patient endetté, le vendeur doit choisir ; « rendre quand même »
        var (sansChoix, _) = Monter(endette);
        var e2 = await Assert.ThrowsAsync<RegleMetierException>(() => sansChoix.ExecuterAsync(20, "Erreur", l, 8, "Caisse 1", null, null, solderDette: false));
        Assert.Equal("dettes_patient", e2.Champ);
        var (rendre, stock2) = Monter(endette);
        var r2 = await rendre.ExecuterAsync(20, "Erreur", l, 8, "Caisse 1", null, null, dettes: RegleRetour.DettesRendre);
        Assert.Equal((350m, 0m, 0m, 0m), (r2.ARendre, r2.DetteDiminuee, stock2.Ecrit!.ReductionDette, stock2.Ecrit.ImputationDettes));
    }

    [Fact]
    public async Task Vente_sans_patient_le_patient_se_nomme_et_sa_dette_est_verifiee()
    {
        var patients = new PatientsEnMemoire(new Patient { Id = 3, Nom = "Fictif", SoldeCredit = 1000 });
        LigneRetourDemandee[] l = [new(1, 3, "stock", null)];

        // Dette de 1000 DA : les 350 DA du retour en sont déduits, rien ne sort de la caisse (même fermée)
        var (cas, stock) = Monter(Vente(), new SessionsEnMemoire { Ouverte = null }, patients);
        var r = await cas.ExecuterAsync(20, "Erreur", l, 8, "Caisse 1", null, null, solderDette: true, patientDetteId: 3, accordClient: true);
        Assert.Equal((0m, 350m), (r.ARendre, r.DetteDiminuee));
        Assert.Equal((350m, 3L), (stock.Ecrit!.ReductionDette, stock.Ecrit.PatientDetteId));

        // Patient inconnu, ou vente qui a déjà son patient : refusé
        var (inconnu, _) = Monter(Vente(), null, patients);
        await Assert.ThrowsAsync<RegleMetierException>(() => inconnu.ExecuterAsync(20, "Erreur", l, 8, "Caisse 1", null, null, solderDette: true, patientDetteId: 99, accordClient: true));
        var nommee = Vente();
        var (dejaNommee, _) = Monter(nommee with { Vente = nommee.Vente with { Patient = "Autre Patient" } }, null, patients);
        await Assert.ThrowsAsync<RegleMetierException>(() => dejaNommee.ExecuterAsync(20, "Erreur", l, 8, "Caisse 1", null, null, solderDette: true, patientDetteId: 3, accordClient: true));
    }

    [Fact]
    public async Task Vendeur_sans_le_droit_valide_seul_un_retour_entierement_deduit_de_la_dette_avec_l_accord_du_client()
    {
        var vente = Vente();
        var endette = vente with { Vente = vente.Vente with { Patient = "Patient Fictif", SoldeCreditPatient = 1000 } };
        LigneRetourDemandee[] l = [new(1, 3, "stock", null)];

        // Employé 5 : pas le droit de rembourser. Tout est déduit (dette 1000 DA, retour 350 DA) : aucun code demandé
        var (cas, stock) = Monter(endette);
        var r = await cas.ExecuterAsync(20, "Erreur", l, 5, "Caisse 1", null, null, solderDette: true, accordClient: true);
        Assert.Equal((0m, 350m, (string?)null), (r.ARendre, r.DetteDiminuee, r.ValidePar));
        Assert.Equal(5L, stock.Ecrit!.ValidePar);

        // Rendu en espèces (« rendre quand même », script 158) : la validation reste demandée
        var (rendre, _) = Monter(endette);
        await Assert.ThrowsAsync<RegleMetierException>(() => rendre.ExecuterAsync(20, "Erreur", l, 5, "Caisse 1", null, null, dettes: RegleRetour.DettesRendre));
        // Dette plus petite que le retour : une partie est rendue en espèces, validation demandée
        var petite = vente with { Vente = vente.Vente with { Patient = "Patient Fictif", SoldeCreditPatient = 100 } };
        var (surplus, _) = Monter(petite);
        await Assert.ThrowsAsync<RegleMetierException>(() => surplus.ExecuterAsync(20, "Erreur", l, 5, "Caisse 1", null, null, solderDette: true, accordClient: true));
    }
}

public class RetourBoitesNonScanneesTests
{
    // Ligne de 3 boîtes dont une seule scannée à la vente (scan facultatif)
    private static VenteRetournable Vente() => new(
        new VenteAnnulable(30, "T-000030", DateTime.UtcNow, "Vendeur", null, 300, 300, 0, "especes", "validee", false, false, []),
        [new LigneRetournable(5, 15, "Gélule fictive", "G1", 3, 0, 300, [new BoiteVendue(200, "SN-C")])]);

    private static EnregistrerRetourClient Monter()
    {
        var codes = new CodesEnMemoire();
        var droits = new DroitsParEmploye(new() { [8] = ["caisse.vendre", "caisse.retour_especes"] });
        var journal = new JournalEspion();
        return new EnregistrerRetourClient(new RetoursEnMemoire(Vente()), new SessionsEnMemoire(), droits,
            new VerificationCodeComptoir(codes, new VerificateurFactice(), new HorlogeFixe(new DateOnly(2026, 11, 9)), journal));
    }

    [Fact]
    public async Task Boites_vendues_sans_identite_rendues_sans_les_cocher()
    {
        var r = await Monter().ExecuterAsync(30, "Traitement arrêté", [new(5, 2, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal(2, r.RemisEnStock);
    }

    [Fact]
    public async Task Boite_identifiee_a_cocher_au_dela_des_boites_sans_identite()
    {
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            Monter().ExecuterAsync(30, "Traitement arrêté", [new(5, 3, "stock", null)], 8, "Caisse 1", null, null));
        var r = await Monter().ExecuterAsync(30, "Traitement arrêté", [new(5, 3, "stock", [200])], 8, "Caisse 1", null, null);
        Assert.Equal(3, r.RemisEnStock);
    }
}
