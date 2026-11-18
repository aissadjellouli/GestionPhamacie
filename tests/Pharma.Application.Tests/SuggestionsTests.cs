using Pharma.Application.Commun;
using Pharma.Application.Patients;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

/// <summary>
/// Suggestions pendant la frappe (décision du titulaire, 2026-11-18) : liste blanche des champs libres, droit de l'écran, 10 valeurs au
/// plus ; recherche d'un patient hors caisse sans dette ni plafond.
/// </summary>
public class SuggestionsTests
{
    private sealed class DepotEspion : ISuggestionsDepot
    {
        public List<(ChampSuggestion Champ, string Texte, int Limite)> Appels { get; } = [];
        public int Valeurs { get; init; } = 3;
        public Task<IReadOnlyList<string>> ValeursAsync(ChampSuggestion champ, string texte, int limite, CancellationToken ct = default)
        {
            Appels.Add((champ, texte, limite));
            return Task.FromResult<IReadOnlyList<string>>(Enumerable.Range(1, Valeurs).Select(i => $"Valeur {i}").ToList());
        }
    }

    [Fact]
    public async Task Un_champ_hors_de_la_liste_blanche_est_refuse_sans_lire_la_base()
    {
        var depot = new DepotEspion();
        var proposer = new ProposerValeurs(depot, new DroitsFactices(Permissions.CaisseVendre) { Titulaire = true });
        foreach (var cle in new[] { "patient.nom", "nom", "medecin.specialite", "specialite_medecin; DROP TABLE patient", "", "SPECIALITE_MEDECIN" })
            await Assert.ThrowsAsync<RegleMetierException>(() => proposer.ExecuterAsync(cle, "ca", 1));
        await Assert.ThrowsAsync<RegleMetierException>(() => proposer.ExecuterAsync(null, "ca", 1));
        Assert.Empty(depot.Appels);
    }

    [Fact]
    public async Task Sans_le_droit_de_l_ecran_les_valeurs_sont_refusees()
    {
        var depot = new DepotEspion();
        var refus = await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new ProposerValeurs(depot, new DroitsFactices(Permissions.CaisseVendre)).ExecuterAsync("libelle_depense_tiroir", "caf", 1));
        Assert.Equal(Permissions.CaisseOuvrirTiroir, refus.Permission);
        // Prêteur d'un prêt : écran Financements, réservé au titulaire (même avec tous les droits de la matrice)
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new ProposerValeurs(depot, new DroitsFactices(Permissions.TresorerieGerer, Permissions.AdminParametres)).ExecuterAsync("preteur_pret", "ba", 1));
        Assert.Empty(depot.Appels);
    }

    [Fact]
    public async Task Un_seul_des_droits_du_champ_suffit()
    {
        var depot = new DepotEspion();
        Assert.Equal(3, (await new ProposerValeurs(depot, new DroitsFactices(Permissions.FacturesGerer)).ExecuterAsync("specialite_medecin", "card", 1)).Count);
        Assert.Equal(3, (await new ProposerValeurs(depot, new DroitsFactices() { Titulaire = true }).ExecuterAsync("preteur_pret", "ba", 1)).Count);
        Assert.Equal(["specialite_medecin", "preteur_pret"], depot.Appels.Select(a => a.Champ.Cle));
    }

    [Fact]
    public async Task Dix_valeurs_au_plus_et_rien_sous_deux_lettres()
    {
        var depot = new DepotEspion { Valeurs = 25 };
        var proposer = new ProposerValeurs(depot, new DroitsFactices(Permissions.CatalogueCreer));
        Assert.Empty(await proposer.ExecuterAsync("forme_produit", " c ", 1));
        Assert.Empty(depot.Appels);
        var valeurs = await proposer.ExecuterAsync("forme_produit", "  comp  ", 1);
        Assert.Equal(10, valeurs.Count);
        var appel = Assert.Single(depot.Appels);
        Assert.Equal(("comp", 10), (appel.Texte, appel.Limite));
    }

    [Fact]
    public async Task Les_rayons_sont_proposes_sans_lettre_tapee_et_tries_les_autres_champs_restent_a_deux_lettres()
    {
        // Rayon d'un emplacement (une lettre : A, B, C…) : tous les rayons dès l'entrée dans le champ, triés (décision du 2026-11-18)
        var rayon = ChampsSuggestions.Liste["rayon_emplacement"];
        Assert.Equal((0, true), (rayon.LettresMin, rayon.Alphabetique));
        var depot = new DepotEspion();
        var proposer = new ProposerValeurs(depot, new DroitsFactices(Permissions.AdminParametres));
        Assert.Equal(3, (await proposer.ExecuterAsync("rayon_emplacement", "", 1)).Count);
        Assert.Equal(3, (await proposer.ExecuterAsync("rayon_emplacement", null, 1)).Count);
        Assert.Equal(3, (await proposer.ExecuterAsync("rayon_emplacement", " b ", 1)).Count);
        Assert.Equal(["", "", "b"], depot.Appels.Select(a => a.Texte));
        // Le droit de l'écran reste exigé, même sans lettre tapée
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new ProposerValeurs(depot, new DroitsFactices(Permissions.StockConsulter)).ExecuterAsync("rayon_emplacement", "", 1));
        // Aucun autre champ n'est élargi : 2 lettres au moins, les plus fréquentes d'abord
        foreach (var c in ChampsSuggestions.Liste.Values.Where(c => c.Cle != "rayon_emplacement"))
            Assert.Equal((ChampsSuggestions.LettresMin, false), (c.LettresMin, c.Alphabetique));
        Assert.Equal(2, ChampsSuggestions.LettresMin);
    }

    [Fact]
    public void La_liste_blanche_ne_lit_jamais_la_table_patient_ni_un_texte_libre_nominatif()
    {
        Assert.Equal(11, ChampsSuggestions.Liste.Count);
        foreach (var c in ChampsSuggestions.Liste.Values)
        {
            Assert.NotEqual("patient", c.Table);
            Assert.DoesNotContain(c.Colonne, new[] { "motif", "raison", "commentaire", "motif_libelle", "motif_annulation", "motif_correction", "detail", "objet" });
            Assert.Matches("^[a-z_]+$", c.Table);
            Assert.Matches("^[a-z_]+$", c.Colonne);
            Assert.NotEmpty(c.Droits);
        }
        Assert.Equal(("mouvement_tiroir", "libelle", "detail = 'depense'"),
            (ChampsSuggestions.Liste["libelle_depense_tiroir"].Table, ChampsSuggestions.Liste["libelle_depense_tiroir"].Colonne, ChampsSuggestions.Liste["libelle_depense_tiroir"].Condition));
        Assert.Equal([ChampsSuggestions.Titulaire], ChampsSuggestions.Liste["preteur_pret"].Droits);
    }

    [Fact]
    public async Task Les_libelles_des_financements_sont_reserves_au_titulaire()
    {
        var depot = new DepotEspion();
        foreach (var cle in new[] { "libelle_usage_financement", "libelle_remboursement_titulaire" })
        {
            Assert.Equal([ChampsSuggestions.Titulaire], ChampsSuggestions.Liste[cle].Droits);
            await Assert.ThrowsAsync<DroitRefuseException>(() =>
                new ProposerValeurs(depot, new DroitsFactices(Permissions.TresorerieGerer, Permissions.AdminParametres)).ExecuterAsync(cle, "fa", 1));
            Assert.Equal(3, (await new ProposerValeurs(depot, new DroitsFactices() { Titulaire = true }).ExecuterAsync(cle, "fa", 1)).Count);
        }
        Assert.Equal(("utilisation_financement", "libelle"),
            (ChampsSuggestions.Liste["libelle_usage_financement"].Table, ChampsSuggestions.Liste["libelle_usage_financement"].Colonne));
        Assert.Equal(("remboursement_titulaire", "libelle"),
            (ChampsSuggestions.Liste["libelle_remboursement_titulaire"].Table, ChampsSuggestions.Liste["libelle_remboursement_titulaire"].Colonne));
    }

    [Fact]
    public void Les_libelles_ecrits_par_le_logiciel_ne_sont_pas_proposes()
    {
        // Trésorerie : dépense et encaissement saisis, hors caisse, hors automatique, hors mouvements inscrits par les financements
        var mouvement = ChampsSuggestions.Liste["libelle_mouvement_tresorerie"].Condition!;
        Assert.Contains("nature IN ('depense', 'encaissement')", mouvement);
        Assert.Contains("NOT automatique", mouvement);
        Assert.Contains("session_caisse_id IS NULL", mouvement);
        Assert.Contains("utilisation_financement", mouvement);
        Assert.Contains("paiement_echeance_pret", mouvement);
        // Financements : usages saisis seulement (pas les règlements de grossiste ni les traites), libellés par défaut exclus
        var usage = ChampsSuggestions.Liste["libelle_usage_financement"].Condition!;
        Assert.Contains("usage IN ('tresorerie', 'depense')", usage);
        Assert.Contains("'Apport du titulaire'", usage);
        Assert.Contains("'Versement du prêt'", usage);
        Assert.Contains("'Remboursement du titulaire'", ChampsSuggestions.Liste["libelle_remboursement_titulaire"].Condition!);
    }

    // ---- Recherche d'un patient hors caisse (Factures) et d'un médecin (Factures, registre des psychotropes) ----

    private sealed class PatientsTrouves : IPatients
    {
        public Task<IReadOnlyList<PatientResume>> RechercherAsync(string t, int l, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PatientResume>>([new PatientResume(1, "Benali", "Karim", "0550 12 34 56", "123456789012", 4, "CNAS", 5000, 1200,
                new DateOnly(2027, 3, 31))]);
        public Task<Patient?> TrouverAsync(long id, CancellationToken ct = default) => Task.FromResult<Patient?>(null);
        public Task<long> CreerAsync(NouveauPatient p, long? creePar, CancellationToken ct = default) => Task.FromResult(99L);
        public Task<FichePatient?> FicheAsync(long id, CancellationToken ct = default) => Task.FromResult<FichePatient?>(null);
        public Task<bool> ModifierAsync(FichePatient f, CancellationToken ct = default) => Task.FromResult(false);
    }

    [Fact]
    public async Task Sans_le_droit_de_vendre_la_recherche_patient_ne_donne_ni_organisme_ni_fin_de_droits_ni_dette_ni_plafond()
    {
        foreach (var droits in new[] { new DroitsFactices(Permissions.FacturesGerer), new DroitsFactices(Permissions.PsychotropesRegistre), null })
        {
            var p = Assert.Single(await new RechercherPatients(new PatientsTrouves(), droits).PourEmployeAsync("ben", 7));
            Assert.Equal((1L, "Benali", "Karim", "0550 12 34 56", "123456789012"), (p.Id, p.Nom, p.Prenom, p.Telephone, p.NumAssure));
            Assert.Null(p.OrganismeId);
            Assert.Null(p.Organisme);
            Assert.Null(p.DateFinDroit);
            Assert.Null(p.PlafondCredit);
            Assert.Null(p.SoldeCredit);
        }
        var vendeur = Assert.Single(await new RechercherPatients(new PatientsTrouves(), new DroitsFactices(Permissions.CaisseVendre)).PourEmployeAsync("ben", 7));
        Assert.Equal((4L, "CNAS", new DateOnly(2027, 3, 31), 5000m, 1200m),
            (vendeur.OrganismeId!.Value, vendeur.Organisme, vendeur.DateFinDroit!.Value, vendeur.PlafondCredit!.Value, vendeur.SoldeCredit!.Value));
    }

    [Fact]
    public void La_recherche_patient_suit_le_droit_de_l_ecran_et_le_registre_ne_cherche_que_les_medecins()
    {
        // Décision du titulaire (2026-11-18) : patient = caisse.vendre ou factures.gerer ; médecin = idem ou psychotropes.registre
        Assert.Equal([Permissions.CaisseVendre, Permissions.FacturesGerer], RechercherPatients.DroitsRecherche);
        Assert.Equal([Permissions.CaisseVendre, Permissions.FacturesGerer, Permissions.PsychotropesRegistre], RechercherPatients.DroitsRechercheMedecins);
    }

    [Fact]
    public async Task Le_seul_droit_du_registre_est_refuse_sur_les_patients_et_accepte_sur_les_medecins()
    {
        // Même règle que le filtre des routes : un seul des droits acceptés suffit
        var registre = new DroitsFactices(Permissions.PsychotropesRegistre);
        static async Task<bool> UnSuffit(IReadOnlyList<string> acceptes, DroitsFactices d)
        {
            foreach (var a in acceptes) if (await d.APermissionAsync(7, a)) return true;
            return false;
        }
        Assert.False(await UnSuffit(RechercherPatients.DroitsRecherche, registre));
        Assert.True(await UnSuffit(RechercherPatients.DroitsRechercheMedecins, registre));
        Assert.True(await UnSuffit(RechercherPatients.DroitsRecherche, new DroitsFactices(Permissions.FacturesGerer)));
    }
}
