using Pharma.Application.Achats;
using Pharma.Application.Administration;
using Pharma.Application.Catalogue;
using Pharma.Application.Commun;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

/// <summary>Patients en attente en mémoire (lot 7) : aucun SMS réel, le modem est une doublure.</summary>
internal sealed class DemandesPatientsEnMemoire : IDemandesPatients
{
    public List<DemandePatient> Demandes { get; } = [];
    public List<ProduitDuEnAttente> ProduitsDus { get; } = [];
    public Dictionary<long, (bool Accord, DateTime? Le)> Consentements { get; } = [];
    public string? ErreurSms { get; private set; }

    public Task<bool> PatientExisteAsync(long patientId, CancellationToken ct = default) => Task.FromResult(Consentements.ContainsKey(patientId));
    public Task<bool> ProduitExisteAsync(long produitId, CancellationToken ct = default) => Task.FromResult(produitId > 0);
    public Task<long?> DemandeOuverteAsync(long patientId, long produitId, CancellationToken ct = default) =>
        Task.FromResult(Demandes.FirstOrDefault(d => d.PatientId == patientId && d.ProduitId == produitId && d.Statut is "en_attente" or "prevenu")?.Id);
    public Task<long> CreerAsync(long patientId, long produitId, int quantite, long utilisateurId, CancellationToken ct = default)
    {
        var id = Demandes.Count + 1L;
        Demandes.Add(Demande(id, patientId, produitId, stock: 0, grossiste: false, consentement: Consentements[patientId].Accord) with { Quantite = quantite });
        return Task.FromResult(id);
    }
    // Comme la requête SQL : l'accord SMS est lu sur la fiche du patient au moment de la lecture (pas copié dans la demande)
    public Task<DemandePatient?> LireAsync(long id, CancellationToken ct = default) =>
        Task.FromResult(Demandes.FirstOrDefault(d => d.Id == id) is { } d && Consentements.TryGetValue(d.PatientId, out var c)
            ? d with { ConsentementSms = c.Accord, ConsentementSmsLe = c.Le } : Demandes.FirstOrDefault(d => d.Id == id));
    public Task<Page<DemandePatient>> PageAsync(string? recherche, string? statut, DemandePage page, CancellationToken ct = default) =>
        throw new NotSupportedException();
    // Même définition que la requête SQL : demande non encore prévenue dont le produit est disponible
    public Task<IReadOnlyList<DemandePatient>> DisponiblesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DemandePatient>>(Demandes.Where(d => d.Statut == StatutDemandePatient.EnAttente && d.Disponible).ToList());
    public Task<IReadOnlyList<ProduitDuEnAttente>> ProduitsDusDisponiblesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProduitDuEnAttente>>(ProduitsDus.Where(p => p.Disponible).ToList());
    public Task MarquerPrevenuAsync(long id, string canal, long utilisateurId, DateTime quandUtc, CancellationToken ct = default)
    {
        var i = Demandes.FindIndex(d => d.Id == id);
        Demandes[i] = Demandes[i] with { Statut = StatutDemandePatient.Prevenu, PrevenuCanal = canal, PrevenuLe = quandUtc };
        return Task.CompletedTask;
    }
    public Task NoterEchecSmsAsync(long id, string erreur, CancellationToken ct = default) { ErreurSms = erreur; return Task.CompletedTask; }
    public Task<bool> CloturerAsync(long id, string statut, long utilisateurId, DateTime quandUtc, CancellationToken ct = default) => Task.FromResult(true);
    public Task<(bool Accord, DateTime? Le)?> ConsentementAsync(long patientId, CancellationToken ct = default) =>
        Task.FromResult(Consentements.TryGetValue(patientId, out var c) ? c : ((bool, DateTime?)?)null);
    public Task EnregistrerConsentementAsync(long patientId, bool accord, DateTime quandUtc, CancellationToken ct = default)
    {
        Consentements[patientId] = (accord, quandUtc);
        return Task.CompletedTask;
    }

    public static DemandePatient Demande(long id, long patient, long produit, int stock, bool grossiste, bool consentement, string? telephone = "0550000000") =>
        new(id, patient, "Patient " + patient, telephone, consentement, consentement ? new DateTime(2026, 11, 1) : null, produit, "Produit " + produit, 1,
            new DateTime(2026, 11, 10), StatutDemandePatient.EnAttente, null, null, null, stock, grossiste);
}

internal sealed class SmsEnPanneLot7 : Pharma.Application.Notifications.IEnvoiSms
{
    public Task EnvoyerAsync(string port, string numero, string texte, CancellationToken ct = default) => throw new InvalidOperationException("modem absent");
}

internal sealed class SubstitutionEnMemoire(bool exactSeulement) : ISubstitution
{
    public Task<ParametreSubstitution> ParametreAsync(CancellationToken ct = default) => Task.FromResult(new ParametreSubstitution(exactSeulement));
    public Task EnregistrerParametreAsync(bool e, long u, CancellationToken ct = default) => Task.CompletedTask;
    public Task<Page<LienListeBlanche>> ListeBlancheAsync(string? r, DemandePage p, CancellationToken ct = default) => throw new NotSupportedException();
    public List<(long, long)> Liens { get; } = [];
    public Task<bool> AjouterListeBlancheAsync(long produitId, long remplacantId, long u, CancellationToken ct = default)
    {
        if (Liens.Contains((produitId, remplacantId))) return Task.FromResult(false);
        Liens.Add((produitId, remplacantId));
        return Task.FromResult(true);
    }
    public Task<bool> RetirerListeBlancheAsync(long produitId, long remplacantId, CancellationToken ct = default) => Task.FromResult(Liens.Remove((produitId, remplacantId)));
    public Task<bool> ProduitExisteAsync(long produitId, CancellationToken ct = default) => Task.FromResult(produitId > 0);
    public Task<Page<ProduitFabrication>> FabricationAsync(string? r, bool? l, DemandePage p, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> ModifierFabricationAsync(long produitId, bool? f, CancellationToken ct = default) => Task.FromResult(true);
}

public class RupturesSubstitutionTests
{
    private static readonly DateOnly Jour = new(2026, 11, 16);

    // ---- Historique des ruptures -------------------------------------------------

    [Theory]
    [InlineData(false, false, TransitionRupture.Ouvrir)]    // devient indisponible : la rupture commence
    [InlineData(true, false, TransitionRupture.Aucune)]     // déjà en rupture : rien
    [InlineData(true, true, TransitionRupture.Fermer)]      // de nouveau disponible : retour en stock
    [InlineData(false, true, TransitionRupture.Aucune)]
    [InlineData(true, null, TransitionRupture.Aucune)]      // disponibilité inconnue : rien ne change
    [InlineData(false, null, TransitionRupture.Aucune)]
    public void Historique_des_ruptures_passages_disponible_indisponible(bool ouverte, bool? disponible, TransitionRupture attendu) =>
        Assert.Equal(attendu, RegleHistoriqueRuptures.Transition(ouverte, disponible));

    // ---- Retour en stock → alerte ---------------------------------------------------

    [Fact]
    public async Task Retour_en_stock_ou_grossiste_disponible_declenche_l_alerte()
    {
        var depot = new DemandesPatientsEnMemoire();
        depot.Demandes.Add(DemandesPatientsEnMemoire.Demande(1, 10, 100, stock: 0, grossiste: false, consentement: true));   // toujours en rupture
        depot.Demandes.Add(DemandesPatientsEnMemoire.Demande(2, 11, 101, stock: 3, grossiste: false, consentement: false));  // stock reçu
        depot.Demandes.Add(DemandesPatientsEnMemoire.Demande(3, 12, 102, stock: 0, grossiste: true, consentement: false));   // grossiste disponible
        depot.ProduitsDus.Add(new ProduitDuEnAttente(7, "Vente de 10 h", 103, "Produit 103", 1, DateTime.UtcNow, 2, false));
        depot.ProduitsDus.Add(new ProduitDuEnAttente(8, "Vente de 11 h", 104, "Produit 104", 1, DateTime.UtcNow, 0, false));

        var alertes = await new ConsulterDemandesPatients(depot).AlertesAsync();

        Assert.Equal([2L, 3L], alertes.Patients.Select(d => d.Id));
        Assert.Equal(103L, Assert.Single(alertes.VentesEnAttente).ProduitId);
        Assert.Equal(3, alertes.Nombre);
    }

    [Fact]
    public async Task Demande_notee_une_seule_fois_par_patient_et_produit_sans_donnee_personnelle_au_journal()
    {
        var depot = new DemandesPatientsEnMemoire();
        depot.Consentements[10] = (false, null);
        var journal = new JournalEspion();
        var cas = new DemanderPrevenirPatient(depot, new DroitsFactices("caisse.vendre"), journal);

        var id = await cas.ExecuterAsync(10, 100, 2, 1);
        Assert.Equal(id, await cas.ExecuterAsync(10, 100, 1, 1));
        Assert.Single(depot.Demandes);
        Assert.DoesNotContain(journal.Details.SelectMany(d => d.Values), v => v is string s && s.Contains("Patient"));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(99, 100, 1, 1));   // patient inconnu
    }

    // ---- SMS : consentement requis -----------------------------------------------------

    private static PrevenirPatient Prevenir(DemandesPatientsEnMemoire depot, Pharma.Application.Notifications.IEnvoiSms sms, NotificationsEnMemoire? reglages = null)
    {
        var pharmacie = new PharmacieEnMemoire();
        pharmacie.EnregistrerAsync(new IdentitePharmacie("Pharmacie El Amel", null, null, null, null, null, "fr")).GetAwaiter().GetResult();
        return new PrevenirPatient(depot, reglages ?? new NotificationsEnMemoire(), sms, pharmacie, new DroitsFactices("caisse.vendre"),
            new JournalEspion(), new HorlogeFixe(Jour));
    }

    [Fact]
    public async Task Sms_refuse_sans_consentement_du_patient()
    {
        var depot = new DemandesPatientsEnMemoire();
        depot.Demandes.Add(DemandesPatientsEnMemoire.Demande(1, 10, 100, stock: 2, grossiste: false, consentement: false));
        var sms = new SmsEspion();

        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => Prevenir(depot, sms).ExecuterAsync(1, parSms: true, 1));

        Assert.Contains("accord", ex.Message);
        Assert.Empty(sms.Envoyes);
        Assert.Equal(StatutDemandePatient.EnAttente, depot.Demandes[0].Statut);
        // Prévenu au comptoir : toujours possible
        Assert.Equal("comptoir", await Prevenir(depot, sms).ExecuterAsync(1, parSms: false, 1));
        Assert.Empty(sms.Envoyes);
    }

    [Fact]
    public async Task Sms_avec_consentement_sans_donnee_de_sante()
    {
        var depot = new DemandesPatientsEnMemoire();
        depot.Demandes.Add(DemandesPatientsEnMemoire.Demande(1, 10, 100, stock: 2, grossiste: false, consentement: true));
        var sms = new SmsEspion();

        Assert.Equal("sms", await Prevenir(depot, sms).ExecuterAsync(1, parSms: true, 1));

        var (numero, texte) = Assert.Single(sms.Envoyes);
        Assert.Equal("0550000000", numero);
        Assert.Equal("Votre commande est disponible a la pharmacie Pharmacie El Amel.", texte);
        Assert.DoesNotContain("Produit", texte);
        Assert.Equal((StatutDemandePatient.Prevenu, "sms"), (depot.Demandes[0].Statut, depot.Demandes[0].PrevenuCanal));
    }

    [Fact]
    public async Task Sms_refuse_sans_modem_et_echec_note_sans_prevenir()
    {
        var depot = new DemandesPatientsEnMemoire();
        depot.Demandes.Add(DemandesPatientsEnMemoire.Demande(1, 10, 100, stock: 2, grossiste: false, consentement: true));
        var sansModem = new NotificationsEnMemoire { Reglages = new NotificationsEnMemoire().Reglages with { ModemPort = null } };
        await Assert.ThrowsAsync<RegleMetierException>(() => Prevenir(depot, new SmsEspion(), sansModem).ExecuterAsync(1, true, 1));

        await Assert.ThrowsAsync<RegleMetierException>(() => Prevenir(depot, new SmsEnPanneLot7()).ExecuterAsync(1, true, 1));
        Assert.Equal("modem absent", depot.ErreurSms);
        Assert.Equal(StatutDemandePatient.EnAttente, depot.Demandes[0].Statut);
    }

    // ---- 2026-11-16 : accès depuis la Caisse (caisse.vendre sans droit achats) et accord SMS de la fiche patient ----

    [Fact]
    public async Task Caissier_sans_droit_achats_note_previent_et_enregistre_l_accord()
    {
        var depot = new DemandesPatientsEnMemoire();
        depot.Consentements[10] = (false, null);
        var caissier = new DroitsFactices("caisse.vendre");   // aucun droit achats.*
        var journal = new JournalEspion();

        var id = await new DemanderPrevenirPatient(depot, caissier, journal).ExecuterAsync(10, 100, 1, 1);
        await new ConsentementSmsPatient(depot, caissier, journal, new HorlogeFixe(Jour)).EnregistrerAsync(10, true, 1);
        Assert.Equal("comptoir", await new PrevenirPatient(depot, new NotificationsEnMemoire(), new SmsEspion(), new PharmacieEnMemoire(), caissier,
            journal, new HorlogeFixe(Jour)).ExecuterAsync(id, false, 1));
        await new CloturerDemandePatient(depot, caissier, journal, new HorlogeFixe(Jour)).ExecuterAsync(id, true, 1);

        // Droit achats seul, sans caisse.vendre : refusé
        var acheteur = new DroitsFactices("achats.commander");
        await Assert.ThrowsAnyAsync<Exception>(() => new DemanderPrevenirPatient(depot, acheteur, journal).ExecuterAsync(10, 101, 1, 1));
    }

    [Fact]
    public async Task Accord_sms_coche_sur_la_fiche_patient_permet_l_envoi_et_son_retrait_le_bloque()
    {
        var depot = new DemandesPatientsEnMemoire();
        depot.Demandes.Add(DemandesPatientsEnMemoire.Demande(1, 10, 100, stock: 2, grossiste: false, consentement: false));
        depot.Consentements[10] = (false, null);
        var journal = new JournalEspion();
        var fiche = new ConsentementSmsPatient(depot, new DroitsFactices("caisse.vendre"), journal, new HorlogeFixe(Jour));

        await fiche.EnregistrerAsync(10, true, 1);   // case cochée sur la fiche patient
        var (accord, le) = await fiche.LireAsync(10);
        Assert.True(accord);
        Assert.NotNull(le);
        var sms = new SmsEspion();
        Assert.Equal("sms", await Prevenir(depot, sms).ExecuterAsync(1, parSms: true, 1));
        Assert.Single(sms.Envoyes);

        // Décochée : plus de SMS ; journal sans donnée personnelle (identifiant et accord seulement)
        depot.Demandes[0] = depot.Demandes[0] with { Statut = StatutDemandePatient.EnAttente };
        await fiche.EnregistrerAsync(10, false, 1);
        await Assert.ThrowsAsync<RegleMetierException>(() => Prevenir(depot, new SmsEspion()).ExecuterAsync(1, parSms: true, 1));
        Assert.DoesNotContain(journal.Details.SelectMany(d => d.Values), v => v is string s && (s.Contains("Patient") || s.Contains("0550")));
    }

    private sealed class FormulaireEnMemoire : IFormulaireAccordSmsDepot
    {
        public (bool Recu, DateTime? Le) Recu { get; set; }
        public string? TexteRegle { get; set; }
        public Task<(string Nom, string? Telephone)?> PatientAsync(long patientId, CancellationToken ct = default) =>
            Task.FromResult<(string, string?)?>(patientId == 10 ? ("Patient Fictif", "0550000000") : null);
        public Task<(bool Recu, DateTime? Le)?> RecuAsync(long patientId, CancellationToken ct = default) =>
            Task.FromResult<(bool, DateTime?)?>(patientId == 10 ? Recu : null);
        public Task EnregistrerRecuAsync(long patientId, bool recu, DateTime quandUtc, CancellationToken ct = default) { Recu = (recu, quandUtc); return Task.CompletedTask; }
        public Task<string?> TexteAsync(CancellationToken ct = default) => Task.FromResult(TexteRegle);
        public Task EnregistrerTexteAsync(string? texte, long utilisateurId, CancellationToken ct = default) { TexteRegle = texte; return Task.CompletedTask; }
    }

    private sealed class DocumentsAccordEspion : IDocumentsAccordSms
    {
        public ContenuFormulaireAccordSms? Dernier { get; private set; }
        public byte[] FormulaireAccordSms(ContenuFormulaireAccordSms contenu) { Dernier = contenu; return [1]; }
    }

    [Fact]
    public async Task Formulaire_accord_sms_imprime_recu_note_et_journal_sans_donnee_personnelle()
    {
        var depot = new FormulaireEnMemoire();
        var docs = new DocumentsAccordEspion();
        var journal = new JournalEspion();
        var cas = new FormulaireAccordSmsPatient(depot, docs, new PharmacieEnMemoire(), new DroitsFactices("caisse.vendre"), journal, new HorlogeFixe(Jour));

        var (_, nom) = await cas.ImprimerAsync(10, 1);
        Assert.Equal("accord_sms_10.pdf", nom);
        Assert.Equal(("Patient Fictif", RegleFormulaireAccordSms.TexteParDefaut), (docs.Dernier!.Patient, docs.Dernier.Texte));
        await cas.EnregistrerRecuAsync(10, true, 1);
        var (recu, le) = await cas.LireRecuAsync(10);
        Assert.True(recu);
        Assert.NotNull(le);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ImprimerAsync(99, 1));
        Assert.DoesNotContain(journal.Details.SelectMany(d => d.Values), v => v is string s && (s.Contains("Fictif") || s.Contains("0550")));

        // Texte réglé : réservé au titulaire (admin.parametres) ; vide = texte par défaut
        await Assert.ThrowsAnyAsync<Exception>(() => cas.ModifierTexteAsync("Autre texte", 1));
        var titulaire = new FormulaireAccordSmsPatient(depot, docs, new PharmacieEnMemoire(), new DroitsFactices("admin.parametres"), journal, new HorlogeFixe(Jour));
        await titulaire.ModifierTexteAsync("Autre texte", 1);
        Assert.Equal(("Autre texte", false), await titulaire.TexteAsync());
        await titulaire.ModifierTexteAsync("  ", 1);
        Assert.True((await titulaire.TexteAsync()).ParDefaut);
    }

    [Fact]
    public void Texte_sms_tient_en_un_message() =>
        Assert.True(RegleDemandesPatients.TexteSms(new string('x', 300)).Length <= Pharma.Domain.Notifications.TexteSms.LongueurMax);

    // ---- Substitution ---------------------------------------------------------------------

    private static ProduitResume Resume(long id, int stock) => new(id, "P" + id, "Amoxicilline", "1 g", "cp", 300, stock, ClasseReglementaire.Libre);
    private static ProduitPrixServi Info(long id, int stock, decimal prix, string dosage = "1 g", long dci = 1, bool local = false) =>
        new(id, "P" + id, dci, dosage, "cp", stock, prix, null, local);

    [Fact]
    public void Substitution_exacte_par_defaut_autre_dosage_seulement_si_desactivee()
    {
        var p = Resume(1, 0);
        var autreDosage = new[] { Info(4, 50, 150, dosage: "500 mg") };

        Assert.Null(RechercherAuComptoir.Proposer(p, Info(1, 0, 300), autreDosage).Remplacant);
        var aValider = RechercherAuComptoir.Proposer(p, Info(1, 0, 300), autreDosage, equivalentExactSeulement: false).Remplacant;
        Assert.Equal((4L, false), (aValider!.ProduitId, aValider.Exact));
    }

    [Fact]
    public async Task Lister_alternatives_exactes_seulement_par_defaut()
    {
        var lecture = new AlternativesEnMemoire(new ProduitAlternatif(5, "Autre dosage", 100, 50, Exact: false));
        Assert.Empty(await new ListerAlternatives(lecture).ExecuterAsync(1));
        Assert.Empty(await new ListerAlternatives(lecture, new SubstitutionEnMemoire(true)).ExecuterAsync(1));
        Assert.Single(await new ListerAlternatives(lecture, new SubstitutionEnMemoire(false)).ExecuterAsync(1));
    }

    [Fact]
    public void Liste_blanche_remplacant_autorise_meme_hors_equivalent_exact()
    {
        var p = Resume(1, 0);
        var autreDci = Info(9, 4, 500, dosage: "250 mg", dci: 7);

        var c = RechercherAuComptoir.Proposer(p, Info(1, 0, 300), [], listeBlanche: [autreDci]);

        Assert.Equal(9L, c.Remplacant!.ProduitId);
        Assert.True(c.Remplacant.ListeBlanche);
        Assert.False(c.Remplacant.Exact);
    }

    [Fact]
    public async Task Liste_blanche_refuse_un_produit_pour_lui_meme_et_les_doublons()
    {
        var subst = new SubstitutionEnMemoire(true);
        var cas = new GererListeBlanche(subst, new DroitsFactices("admin.parametres"), new JournalEspion());
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.AjouterAsync(3, 3, 1));
        await cas.AjouterAsync(3, 4, 1);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.AjouterAsync(3, 4, 1));
        await Assert.ThrowsAnyAsync<Exception>(() => new GererListeBlanche(subst, new DroitsFactices(), new JournalEspion()).AjouterAsync(3, 5, 1));
    }

    [Fact]
    public void Generiques_fabriques_en_Algerie_en_tete()
    {
        var p = Resume(1, 0);
        // 2 : importé moins cher ; 3 : fabriqué en Algérie plus cher → 3 proposé
        var c = RechercherAuComptoir.Proposer(p, Info(1, 0, 300), [Info(2, 10, 200), Info(3, 5, 260, local: true)]);
        Assert.Equal(3L, c.Remplacant!.ProduitId);
        Assert.True(c.Remplacant.FabricationLocale);

        var ordre = RegleSubstitution.Choisir([
            new ProduitAlternatif(2, "Importé", 200, 10), new ProduitAlternatif(3, "Local cher", 260, 5, FabricationLocale: true),
            new ProduitAlternatif(4, "Local", 240, 5, FabricationLocale: true)], equivalentExactSeulement: true);
        Assert.Equal([4L, 3L, 2L], ordre.Select(a => a.ProduitId));
    }
}
