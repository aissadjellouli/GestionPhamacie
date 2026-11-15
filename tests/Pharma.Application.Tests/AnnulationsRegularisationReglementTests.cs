using Pharma.Application.Achats;
using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Caisse;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

/// <summary>Annulation d'une vente régularisée (script 098, règles du titulaire 2026-11-15).</summary>
public class AnnulationVenteRegulariseeTests
{
    // Vente payée en espèces, patient 3, régularisation de 100 DA mise à sa charge ; il doit 100 DA (s'il n'a rien payé de la régularisation)
    private static VenteAnnulable Vente(string sort = RegularisationPartOrganisme.ChargePatient, decimal paye = 0, decimal solde = 100, bool bordereau = false) =>
        new(12, "T-000012", DateTime.UtcNow, "Vendeur", "Patient fictif", 470, 300, 170, "especes", "validee", true, bordereau,
            [new LigneVenteAnnulable("Produit fictif", 1, 470)], 0, solde, null, 5, 3,
            Regularisation: new RegularisationVente(9, sort, sort == RegularisationPartOrganisme.ChargePatient ? 100 : 0, paye));

    private static (AnnulerVente Cas, AnnulationsEnMemoire Stock, JournalEspion Journal) Monter(VenteAnnulable v, decimal especes = 10000)
    {
        var stock = new AnnulationsEnMemoire(v);
        var journal = new JournalEspion();
        var cas = new AnnulerVente(stock, new SessionsEnMemoire { Especes = especes, Parametres = new(true, 5000, 500, 0, SeuilSortieTiroir: 2000) },
            new DroitsParEmploye(new() { [5] = ["caisse.vendre"] }),
            new VerificationCodeComptoir(new CodesEnMemoire(), new VerificateurFactice(), new HorlogeFixe(new DateOnly(2026, 11, 15)), journal), journal);
        return (cas, stock, journal);
    }

    [Fact]
    public async Task Charge_patient_impayee_la_dette_est_annulee_sans_choix_ni_remboursement()
    {
        var (cas, stock, journal) = Monter(Vente());
        var r = await cas.ExecuterAsync(12, "Erreur de saisie", 5, "Caisse 1", null, null);
        Assert.Equal((100m, 0m, 0m), (r.RegularisationDetteAnnulee, r.RegularisationImputee, r.RegularisationRembourse));
        Assert.Equal((9L, 100m, 0m), (stock.Ecrite!.Regularisation!.Id, stock.Ecrite.Regularisation.DetteAnnulee, stock.Ecrite.Regularisation.Rembourse));
        Assert.Contains(AnnulerVente.ActionRegularisationAnnulee, journal.Actions);
    }

    [Fact]
    public async Task Trop_paye_choix_obligatoire_puis_impute_sur_les_autres_dettes_et_le_reste_rembourse()
    {
        // Le patient a payé 60 des 100 DA ; il doit 50 DA sur la vente 30 (et 40 encore dus sur la régularisation, annulés)
        var v = Vente(paye: 60, solde: 90);
        var (cas, stock, journal) = Monter(v);
        stock.AutresDettes[30] = 50;
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(12, "Erreur de saisie", 5, "Caisse 1", null, null));
        Assert.Equal("trop_paye", e.Champ);
        Assert.Null(stock.Ecrite);
        // Plus que le reste de la vente, ou répartition qui ne fait pas le trop-payé : refusés
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(12, "Erreur", 5, "Caisse 1", null, null, tropPayeImputations: [(30, 60)], tropPayeRembourse: 0));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(12, "Erreur", 5, "Caisse 1", null, null, tropPayeImputations: [(30, 50)], tropPayeRembourse: 20));
        // La vente annulée elle-même ne se solde pas avec son propre trop-payé
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(12, "Erreur", 5, "Caisse 1", null, null, tropPayeImputations: [(12, 10)], tropPayeRembourse: 50));

        var r = await cas.ExecuterAsync(12, "Erreur de saisie", 5, "Caisse 1", null, null, tropPayeImputations: [(30, 50)], tropPayeRembourse: 10);
        Assert.Equal((40m, 50m, 10m), (r.RegularisationDetteAnnulee, r.RegularisationImputee, r.RegularisationRembourse));
        Assert.Equal("BS-000041", r.BonSortie!.Numero);
        Assert.Equal([(30L, 50m)], stock.Ecrite!.Regularisation!.Imputations);
        Assert.Equal("reparti", journal.Details.First(d => d.ContainsKey("trop_paye_impute"))["choix"]);
    }

    [Fact]
    public async Task Trop_paye_sans_autre_dette_seul_le_remboursement_jamais_au_dela_de_l_encaisse()
    {
        var (cas, stock, _) = Monter(Vente(paye: 100, solde: 0));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(12, "Erreur", 5, "Caisse 1", null, null, tropPayeImputations: [(30, 100)]));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(12, "Erreur", 5, "Caisse 1", null, null, tropPayeRembourse: 150));
        var r = await cas.ExecuterAsync(12, "Erreur", 5, "Caisse 1", null, null, tropPayeRembourse: 100);
        Assert.Equal((0m, 0m, 100m), (r.RegularisationDetteAnnulee, r.RegularisationImputee, r.RegularisationRembourse));
        Assert.Equal(100m, stock.Ecrite!.Regularisation!.Rembourse);
    }

    [Fact]
    public async Task Remboursement_du_trop_paye_au_plus_l_argent_du_tiroir()
    {
        var (cas, _, _) = Monter(Vente(paye: 100, solde: 0), especes: -4950);   // fond 5 000 : 50 DA dans le tiroir
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(12, "Erreur", 5, "Caisse 1", null, null, tropPayeRembourse: 100));
    }

    [Theory]
    [InlineData(RegularisationPartOrganisme.PertePharmacie)]
    [InlineData(RegularisationPartOrganisme.GainOrganisme)]
    [InlineData(RegularisationPartOrganisme.AvoirPatient)]
    public async Task Perte_pharmacie_ou_gain_organisme_neutralises_sans_mouvement(string sort)
    {
        var (cas, stock, journal) = Monter(Vente(sort, solde: 0));
        var r = await cas.ExecuterAsync(12, "Erreur de saisie", 5, "Caisse 1", null, null);
        Assert.Equal((0m, 0m, 0m), (r.RegularisationDetteAnnulee, r.RegularisationImputee, r.RegularisationRembourse));
        Assert.Equal((sort, 0m), (stock.Ecrite!.Regularisation!.Sort, stock.Ecrite.Regularisation.DetteAnnulee));
        Assert.Contains(AnnulerVente.ActionRegularisationAnnulee, journal.Actions);
    }

    [Fact]
    public async Task Vente_sur_un_bordereau_envoye_toujours_refusee()
    {
        var (cas, stock, _) = Monter(Vente(bordereau: true));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(12, "Erreur", 5, "Caisse 1", null, null));
        Assert.Null(stock.Ecrite);
    }

    [Fact]
    public void Regle_du_domaine_du_impaye_annule_paye_soumis_au_choix()
    {
        Assert.Equal(new AnnulationRegularisation(40, 60), RegularisationPartOrganisme.Annuler(RegularisationPartOrganisme.ChargePatient, 100, 60, 90));
        Assert.Equal(new AnnulationRegularisation(0, 0), RegularisationPartOrganisme.Annuler(RegularisationPartOrganisme.PertePharmacie, 0, 0, 90));
        Assert.Empty(RegularisationPartOrganisme.Repartir(0, null, null, new Dictionary<long, decimal>(), 12).Imputations);
    }
}

/// <summary>Annulation d'un règlement de grossiste payé depuis le tiroir et droit accordé par code pour cette seule action (script 098).</summary>
public class AnnulationReglementTiroirTests
{
    private sealed class ReglementsTiroirEnMemoire : IAnnulationsReglementTiroir
    {
        public ReglementTiroir Reglement { get; set; } = new(99, "RG-000099", 7, "GROSSISTE SUD", 1000, 1000, "especes", false, 1, true);
        public AnnulationReglementAEcrire? Ecrite { get; private set; }
        public decimal DetteGrossiste { get; private set; } = 29000;
        public Task<ReglementTiroir?> LireAsync(long id, CancellationToken ct = default) => Task.FromResult<ReglementTiroir?>(id == Reglement.Id ? Reglement : null);
        public Task<MouvementTiroirEcrit> AnnulerAsync(AnnulationReglementAEcrire a, CancellationToken ct = default)
        {
            (Ecrite, DetteGrossiste, Reglement) = (a, DetteGrossiste + Reglement.Montant, Reglement with { Annule = true });
            return Task.FromResult(new MouvementTiroirEcrit(2, "BS-000002"));
        }
    }

    private static MouvementTiroir Paiement() => new(1, "BS-000001", DateTime.UtcNow, 5, "Vendeur", "Caisse 1", 1, "paiement_fournisseur",
        "Paiement d’un livreur ou d’un fournisseur", RegleTiroir.Sortie, "fournisseur", 1000, null, null, "GROSSISTE SUD", "BL-1", null, null, null,
        null, null, false, 99);

    private static (AnnulerReglementGrossisteTiroir Cas, ReglementsTiroirEnMemoire Reglements, JournalEspion Journal) Monter(IPermissionService droits,
        AutorisationCourante? autorisation = null)
    {
        var tiroir = new TiroirEnMemoire(new SessionsEnMemoire());
        tiroir.Mouvements.Add(Paiement());
        var reglements = new ReglementsTiroirEnMemoire();
        var journal = new JournalEspion();
        return (new AnnulerReglementGrossisteTiroir(tiroir, reglements, droits, journal, autorisation), reglements, journal);
    }

    [Fact]
    public async Task Annulation_retablit_la_dette_et_credite_le_tiroir_avec_journal()
    {
        var (cas, reglements, journal) = Monter(new DroitsFactices("caisse.vendre", Permissions.AchatsRegler));
        var bon = await cas.ExecuterAsync(1, "Mauvais grossiste", 5, "Caisse 1");
        Assert.Equal("BS-000002", bon.Numero);
        Assert.Equal((30000m, true), (reglements.DetteGrossiste, reglements.Reglement.Annule));
        Assert.Equal((99L, 1L, (long?)null), (reglements.Ecrite!.ReglementId, reglements.Ecrite.Mouvement.Id, reglements.Ecrite.ValidePar));
        Assert.Contains(AnnulerReglementGrossisteTiroir.Action, journal.Actions);
        // Une seule annulation
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(1, "Encore", 5, "Caisse 1"));
    }

    [Fact]
    public async Task Motif_obligatoire()
    {
        var (cas, reglements, _) = Monter(new DroitsFactices(Permissions.AchatsRegler));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(1, "  ", 5, "Caisse 1"));
        Assert.Null(reglements.Ecrite);
    }

    [Fact]
    public async Task Caisse_d_origine_fermee_refusee_correction_en_tresorerie()
    {
        var (cas, reglements, _) = Monter(new DroitsFactices(Permissions.AchatsRegler));
        reglements.Reglement = reglements.Reglement with { SessionOuverte = false };
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(1, "Mauvais grossiste", 5, "Caisse 1"));
        Assert.Contains("trésorerie", e.Message);
        Assert.Null(reglements.Ecrite);
    }

    [Fact]
    public async Task Vendeur_sans_droit_refuse_sans_code_accepte_avec_le_code_d_un_ayant_droit()
    {
        var (sans, reglements, _) = Monter(new DroitsFactices("caisse.vendre"));
        var refus = await Assert.ThrowsAsync<DroitRefuseException>(() => sans.ExecuterAsync(1, "Mauvais grossiste", 5, "Caisse 1"));
        Assert.Equal(CodeComptoir.ActionReglementGrossisteTiroir, refus.Permission);
        Assert.Null(reglements.Ecrite);

        // Code accepté (PermissionsComptoir accorde l'action, AutorisationCourante nomme le valideur) : vendeur et valideur journalisés
        var (avec, r2, journal) = Monter(new DroitsFactices("caisse.vendre", CodeComptoir.ActionReglementGrossisteTiroir),
            new AutorisationCourante { AutoriseParId = 8, AutoriseParNom = "Assistante Fictive" });
        await avec.ExecuterAsync(1, "Mauvais grossiste", 5, "Caisse 1");
        Assert.Equal((long?)8, r2.Ecrite!.ValidePar);
        var d = journal.Details.Last();
        Assert.Equal((5L, 8L), ((long)d["vendeur"]!, (long)d["valide_par"]!));
    }

    [Fact]
    public void Achats_regler_hors_liste_blanche_seule_l_action_du_tiroir_est_accordable_par_code()
    {
        // Le reste du module Achats n'est jamais ouvert par code ; l'action du tiroir exige un valideur qui a achats.regler
        Assert.False(CodeComptoir.AutorisablesParCollegue.Contains(Permissions.AchatsRegler));
        Assert.True(CodeComptoir.AutorisableParCollegue(CodeComptoir.ActionReglementGrossisteTiroir));
        Assert.Equal(Permissions.AchatsRegler, CodeComptoir.DroitDuValideur(CodeComptoir.ActionReglementGrossisteTiroir));
        Assert.Equal(Permissions.AchatsReceptionner, CodeComptoir.DroitDuValideur(Permissions.AchatsReceptionner));
    }

    [Fact]
    public async Task Valideurs_proposes_seulement_ceux_qui_ont_achats_regler()
    {
        var codes = new CodesEnMemoire();
        codes.Comptes[8] = new CompteComptoir(8, "assistante", "Assistante Fictive", "hash:4826", 0, null);
        codes.Comptes[9] = new CompteComptoir(9, "vendeur.b", "Vendeur B", "hash:2580", 0, null);
        var droits = new DroitsParEmploye(new() { [8] = [Permissions.AchatsRegler], [9] = ["caisse.vendre"] });
        var lister = new ListerValidateurs(codes, droits);
        var liste = await lister.AutorisablesAsync(CodeComptoir.ActionReglementGrossisteTiroir, 5, null);
        Assert.Equal(["assistante"], liste.Select(v => v.Identifiant));
        // achats.regler demandé directement (autre action Achats) : personne n'est proposé
        Assert.Empty(await lister.AutorisablesAsync(Permissions.AchatsRegler, 5, null));
    }

    [Fact]
    public async Task Paiement_depuis_le_tiroir_accepte_avec_le_code_d_un_ayant_droit_valideur_journalise()
    {
        var sessions = new SessionsEnMemoire { Especes = 10000, Parametres = new(true, 5000, 500, 0, SeuilSortieTiroir: 20000) };
        var tiroir = new TiroirEnMemoire(sessions);
        var journal = new JournalEspion();
        var motifs = new Pharma.Application.Referentiels.MotifsActifs(new MotifsEnMemoire
        {
            Motifs = { new Pharma.Domain.Referentiels.MotifReglable(21, "tiroir", "paiement_fournisseur", "Paiement d’un livreur ou d’un fournisseur", true, false, 70, "sortie", "fournisseur") }
        });
        var grossiste = new SituationGrossiste(7, "GROSSISTE SUD", 30000, [new FactureFournisseur(70, "F-1", "BL-1", new DateTime(2026, 10, 1), 10000, 0, 10000)], []);
        var cas = new OuvrirTiroirAvecEffet(tiroir, sessions, new DroitsFactices("caisse.vendre", CodeComptoir.ActionReglementGrossisteTiroir), journal, motifs,
            new ReglementsEnMemoire(grossiste), new AutorisationCourante { AutoriseParId = 8 }, new HorlogeFixe(new DateOnly(2026, 11, 15)));
        var motif = (await motifs.ExecuterAsync("tiroir")).Single(m => m.Code == "paiement_fournisseur");
        await cas.ExecuterAsync(motif, motif.Libelle, new SaisieTiroir(1000, FournisseurId: 7, Reference: "BL-1"), 5, "Caisse 1");
        var d = journal.Details.Last();
        Assert.Equal((5L, 8L), ((long)d["vendeur"]!, (long)d["valide_par"]!));
    }
}
