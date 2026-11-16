using System.Text.Json;
using Npgsql;
using Pharma.Cloud.Api.Reglages;
using Pharma.Domain.Reglages;

namespace Pharma.Cloud.Tests;

/// <summary>
/// Outil d'administration des réglages mutualisés (lot M6) : analyse des arguments, validation avant publication (D12), simulation, auteur obligatoire (D10),
/// confirmation d'un passage en « imposé », modèle fabriqué depuis le miroir d'une officine. Aucune base nécessaire, sauf l'essai réel (variable d'environnement).
/// </summary>
public class ReglagesMutualisesAdminTests
{
    private static readonly DateOnly Jour = new(2026, 11, 16);

    private const string InventaireValide =
        """{"auteur":"admin-test","format":"mutu-v1","mode":"impose","publie_le":"2026-11-20T09:12:00Z","valeur":{"seuil_boites":2,"seuil_montant":2000.50},"version_modele":4}""";

    private const string InventaireInvalide =
        """{"auteur":"admin-test","format":"mutu-v1","mode":"propose","publie_le":"2026-11-20T09:12:00Z","valeur":{"seuil_boites":-1,"seuil_montant":2000.5},"version_modele":1}""";

    // ---- Analyse des arguments ----------------------------------------------------------------

    [Fact]
    public void Publier_analyse_table_fichier_auteur_et_drapeaux()
    {
        var c = ArgumentsReglages.Analyser(["--publier-reglage", "parametre_inventaire", "--fichier", "m.json", "--auteur", "admin-1", "--simulation", "--motif", "essai"]);
        Assert.Equal("parametre_inventaire", c.Table());
        Assert.Equal("m.json", c.Option("--fichier"));
        Assert.Equal("admin-1", c.Auteur());
        Assert.Equal("essai", c.Motif());
        Assert.True(c.Drapeau("--simulation"));
        Assert.False(c.Drapeau("--oui"));
    }

    [Fact]
    public void Auteur_obligatoire_et_borne()
    {
        var sans = ArgumentsReglages.Analyser(["--mode-reglage", "parametre_inventaire", "impose"]);
        Assert.Contains("auteur est obligatoire", Assert.Throws<ArgumentException>(sans.Auteur).Message);
        var trop = ArgumentsReglages.Analyser(["--mode-reglage", "parametre_inventaire", "impose", "--auteur", new string('x', 101)]);
        Assert.Throws<ArgumentException>(trop.Auteur);
        var blanc = ArgumentsReglages.Analyser(["--mode-reglage", "parametre_inventaire", "impose", "--auteur", "   "]);
        Assert.Throws<ArgumentException>(blanc.Auteur);
    }

    [Theory]
    [InlineData("--publier-reglage")]                                                              // table absente
    [InlineData("--publier-reglage", "parametre_inventaire", "--fichier")]                          // valeur absente
    [InlineData("--publier-reglage", "parametre_inventaire", "--fichier", "--auteur", "a")]         // valeur = autre option
    [InlineData("--publier-reglage", "parametre_inventaire", "--inconnue", "x")]                    // option inconnue
    [InlineData("--mode-reglage", "parametre_inventaire")]                                          // mode absent
    [InlineData("--mode-reglage", "a", "impose", "b")]                                              // trop de positions
    [InlineData("--liberer-reglages")]
    [InlineData("--modele-depuis-officine", "00000000-0000-0000-0000-000000000001")]
    [InlineData("--journal-reglages", "a", "b")]
    [InlineData("--publier-reglage", "parametre_inventaire", "--auteur", "a", "--auteur", "b")]    // option donnée deux fois
    public void Arguments_incorrects_refuses(params string[] args) =>
        Assert.Throws<ArgumentException>(() => ArgumentsReglages.Analyser(args));

    [Fact]
    public void Table_hors_liste_refusee_avec_la_liste()
    {
        var e = Assert.Throws<ArgumentException>(() => ArgumentsReglages.ExigerTable("parametre_caisse"));
        Assert.Contains("parametre_inventaire", e.Message);
        Assert.Equal(TablesMutualisables.Toutes.Count, e.Message.Split(',').Length);
    }

    [Fact]
    public void Journal_depuis_et_limite()
    {
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), ArgumentsReglages.Depuis("2026-11-01"));
        Assert.Null(ArgumentsReglages.Depuis(null));
        Assert.Throws<ArgumentException>(() => ArgumentsReglages.Depuis("01/11/2026"));
        Assert.Equal(200, ArgumentsReglages.Limite(null));
        Assert.Equal(5, ArgumentsReglages.Limite("5"));
        Assert.Throws<ArgumentException>(() => ArgumentsReglages.Limite("0"));
    }

    [Fact]
    public void Toutes_les_commandes_sont_declarees_dans_l_outil()
    {
        foreach (var commande in ArgumentsReglages.Commandes)
            Assert.Contains(commande, Pharma.Cloud.Api.ConsoleAdministration.Commandes);
        Assert.Equal(7, ArgumentsReglages.Commandes.Count);
    }

    // ---- Validation avant publication (D12) ----------------------------------------------------

    [Fact]
    public void Modele_valide_prepare_en_forme_canonique()
    {
        var (modele, erreurs) = ModelePublication.Preparer("parametre_inventaire", InventaireValide, Jour);
        Assert.Empty(erreurs);
        Assert.NotNull(modele);
        Assert.Equal("impose", modele.Mode);
        Assert.Null(modele.AjoutsLocaux);
        Assert.Equal("""{"seuil_boites":2,"seuil_montant":2000.5}""", modele.ValeurJson);   // zéro final retiré, clés triées
        Assert.Equal(2, modele.Elements);
    }

    [Fact]
    public void Modele_invalide_refuse_avec_les_messages_du_domaine()
    {
        var (modele, erreurs) = ModelePublication.Preparer("parametre_inventaire", InventaireInvalide, Jour);
        Assert.Null(modele);
        Assert.NotEmpty(erreurs);
    }

    [Theory]
    [InlineData("pas du json")]
    [InlineData("""{"format":"mutu-v2","mode":"propose","version_modele":1,"publie_le":"2026-11-20T09:12:00Z","auteur":"a","valeur":{"seuil_boites":1,"seuil_montant":1}}""")]
    [InlineData("""{"format":"mutu-v1","mode":"propose","version_modele":1,"publie_le":"2026-11-20T09:12:00Z","auteur":"a","valeur":{"seuil_boites":1,"seuil_montant":1,"officine_id":"x"}}""")]
    [InlineData("""{"format":"mutu-v1","mode":"propose","version_modele":1,"publie_le":"2026-11-20T09:12:00Z","auteur":"a","valeur":{"seuil_boites":1}}""")]
    public void Format_ou_champ_incorrect_refuse(string json) =>
        Assert.NotEmpty(ModelePublication.Preparer("parametre_inventaire", json, Jour).Erreurs);

    [Fact]
    public void Mode_local_n_est_pas_une_publication()
    {
        var (modele, erreurs) = ModelePublication.Preparer("parametre_inventaire", """{"format":"mutu-v1","mode":"local","version_modele":2}""", Jour);
        Assert.Null(modele);
        Assert.Contains("--liberer-reglages", Assert.Single(erreurs));
    }

    [Fact]
    public void Remise_terminee_refusee_avec_la_date_du_jour()
    {
        const string json = """
            {"auteur":"a","format":"mutu-v1","mode":"propose","ajouts_locaux":true,"publie_le":"2026-11-20T09:12:00Z","version_modele":1,
             "valeur":{"remises":[{"cle":"c_abcdef012345","categorie":"cosmetique","pourcentage":10,"date_debut":"2026-01-01","date_fin":"2026-06-30","motif":null}]}}
            """;
        Assert.NotEmpty(ModelePublication.Preparer("remise_automatique", json, Jour).Erreurs);
        Assert.Empty(ModelePublication.Preparer("remise_automatique", json, new DateOnly(2026, 6, 1)).Erreurs);
    }

    // ---- Commandes sans base : simulation, refus, confirmation --------------------------------

    private sealed class Essai
    {
        public StringWriter Sortie { get; } = new();
        public StringWriter Erreur { get; } = new();
        public int Connexions { get; private set; }

        public AdministrationReglages Outil(string saisie = "", Func<NpgsqlDataSource>? connexion = null) =>
            new(connexion ?? (() => { Connexions++; throw new InvalidOperationException("connexion absente"); }),
                Sortie, Erreur, new StringReader(saisie), () => Jour, () => new DateTime(2026, 11, 16, 8, 0, 0, DateTimeKind.Utc), "TEST\\admin", "POSTE-ESSAI");
    }

    private static string Fichier(string contenu)
    {
        var f = Path.Combine(Path.GetTempPath(), $"mutu-m6-{Guid.NewGuid():N}.json");
        File.WriteAllText(f, contenu);
        return f;
    }

    [Fact]
    public async Task Publication_d_un_modele_invalide_refusee_sans_toucher_la_base()
    {
        var f = Fichier(InventaireInvalide);
        try
        {
            var essai = new Essai();
            var code = await essai.Outil().ExecuterAsync(["--publier-reglage", "parametre_inventaire", "--fichier", f, "--auteur", "admin-1"]);
            Assert.Equal(1, code);
            Assert.Equal(0, essai.Connexions);
            Assert.Contains("Modèle refusé", essai.Erreur.ToString());
            Assert.Contains("Rien n'a été publié", essai.Erreur.ToString());
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public async Task Simulation_d_un_modele_invalide_donne_un_code_non_nul()
    {
        var f = Fichier(InventaireInvalide);
        try
        {
            var essai = new Essai();
            var code = await essai.Outil().ExecuterAsync(["--publier-reglage", "parametre_inventaire", "--fichier", f, "--auteur", "admin-1", "--simulation"]);
            Assert.Equal(1, code);
            Assert.Contains("SIMULATION : rien n'a été écrit", essai.Erreur.ToString());
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public async Task Simulation_d_un_modele_valide_n_ecrit_rien()
    {
        var f = Fichier(InventaireValide);
        try
        {
            var essai = new Essai();
            var code = await essai.Outil().ExecuterAsync(["--publier-reglage", "parametre_inventaire", "--fichier", f, "--auteur", "admin-1", "--simulation"]);
            Assert.Equal(0, code);
            var sortie = essai.Sortie.ToString();
            Assert.Contains("modèle valide", sortie);
            Assert.Contains("imposé", sortie);
            Assert.Contains("remplacera celle de chaque officine", sortie);
            Assert.Contains("SIMULATION : rien n'a été écrit.", sortie);
            Assert.Contains("TEST\\admin sur POSTE-ESSAI", sortie);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public async Task Publication_sans_auteur_refusee()
    {
        var f = Fichier(InventaireValide);
        try
        {
            var essai = new Essai();
            var e = await Assert.ThrowsAsync<ArgumentException>(() => essai.Outil().ExecuterAsync(["--publier-reglage", "parametre_inventaire", "--fichier", f]));
            Assert.Contains("auteur", e.Message);
            Assert.Equal(0, essai.Connexions);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public async Task Publication_d_un_fichier_absent_ou_d_une_table_inconnue_refusee()
    {
        var essai = new Essai();
        await Assert.ThrowsAsync<ArgumentException>(() => essai.Outil().ExecuterAsync(["--publier-reglage", "parametre_inventaire", "--fichier", "absent.json", "--auteur", "a"]));
        await Assert.ThrowsAsync<ArgumentException>(() => essai.Outil().ExecuterAsync(["--publier-reglage", "parametre_caisse", "--fichier", "absent.json", "--auteur", "a"]));
    }

    [Fact]
    public async Task Passage_en_impose_sans_confirmation_refuse_sans_toucher_la_base()
    {
        var essai = new Essai();
        var code = await essai.Outil("non").ExecuterAsync(["--mode-reglage", "parametre_inventaire", "impose", "--auteur", "admin-1"]);
        Assert.Equal(1, code);
        Assert.Equal(0, essai.Connexions);
        Assert.Contains("REMPLACERA", essai.Sortie.ToString());
        Assert.Contains("prévenu", essai.Sortie.ToString());
        Assert.Contains("Annulé", essai.Erreur.ToString());
    }

    [Fact]
    public async Task Passage_en_impose_sans_saisie_possible_est_un_refus()
    {
        var essai = new Essai();
        Assert.Equal(1, await essai.Outil("").ExecuterAsync(["--mode-reglage", "parametre_inventaire", "impose", "--auteur", "admin-1"]));
        Assert.Equal(0, essai.Connexions);
    }

    [Theory]
    [InlineData("oui")]
    [InlineData("  OUI  ")]
    public async Task Passage_en_impose_confirme_par_la_saisie_atteint_la_base(string saisie)
    {
        var essai = new Essai();
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => essai.Outil(saisie).ExecuterAsync(["--mode-reglage", "parametre_inventaire", "impose", "--auteur", "admin-1"]));
        Assert.Equal("connexion absente", e.Message);
        Assert.Equal(1, essai.Connexions);
    }

    [Fact]
    public async Task Option_oui_dispense_de_la_saisie()
    {
        var essai = new Essai();
        await Assert.ThrowsAsync<InvalidOperationException>(() => essai.Outil().ExecuterAsync(["--mode-reglage", "parametre_inventaire", "impose", "--auteur", "a", "--oui"]));
        Assert.Equal(1, essai.Connexions);
    }

    [Fact]
    public async Task Passage_en_propose_ne_demande_pas_de_confirmation()
    {
        var essai = new Essai();
        await Assert.ThrowsAsync<InvalidOperationException>(() => essai.Outil().ExecuterAsync(["--mode-reglage", "parametre_inventaire", "propose", "--auteur", "a"]));
        Assert.DoesNotContain("Tapez", essai.Sortie.ToString());
    }

    [Fact]
    public async Task Mode_local_ou_inconnu_et_auteur_manquant_refuses()
    {
        var essai = new Essai();
        var local = await Assert.ThrowsAsync<ArgumentException>(() => essai.Outil().ExecuterAsync(["--mode-reglage", "parametre_inventaire", "local", "--auteur", "a"]));
        Assert.Contains("--liberer-reglages", local.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => essai.Outil().ExecuterAsync(["--mode-reglage", "parametre_inventaire", "autre", "--auteur", "a", "--oui"]));
        await Assert.ThrowsAsync<ArgumentException>(() => essai.Outil().ExecuterAsync(["--mode-reglage", "parametre_inventaire", "propose"]));
        Assert.Equal(0, essai.Connexions);
    }

    [Fact]
    public async Task Liberation_et_retablissement_exigent_auteur_et_officine()
    {
        var essai = new Essai();
        await Assert.ThrowsAsync<ArgumentException>(() => essai.Outil().ExecuterAsync(["--liberer-reglages", Guid.NewGuid().ToString()]));
        await Assert.ThrowsAsync<ArgumentException>(() => essai.Outil().ExecuterAsync(["--retablir-reglages", "pas-un-code", "--auteur", "a"]));
        Assert.Equal(0, essai.Connexions);
    }

    // ---- Modèle depuis les valeurs d'une officine ----------------------------------------------

    private static LigneMiroir Ligne(params (string, object?)[] valeurs) => new(valeurs.ToDictionary(v => v.Item1, v => v.Item2));

    [Fact]
    public void Modele_depuis_une_ligne_unique_est_valide_et_ne_porte_aucun_identifiant()
    {
        var (json, notes) = ModeleDepuisOfficine.Construire("parametre_inventaire", Guid.NewGuid(),
            [Ligne(("seuil_boites", 3), ("seuil_montant", 1500.00m))], "propose", Jour, new DateTime(2026, 11, 16, 8, 0, 0, DateTimeKind.Utc));
        Assert.Empty(notes);
        Assert.Empty(ModelePublication.Preparer("parametre_inventaire", json, Jour).Erreurs);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("propose", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal(1500, doc.RootElement.GetProperty("valeur").GetProperty("seuil_montant").GetDecimal());
        Assert.DoesNotContain("officine_id", json);
        Assert.DoesNotContain("\"id\"", json);
    }

    [Fact]
    public void Modele_depuis_une_table_vide_est_une_erreur_claire()
    {
        var e = Assert.Throws<InvalidOperationException>(() => ModeleDepuisOfficine.Construire("parametre_inventaire", Guid.NewGuid(), [], "propose", Jour, DateTime.UtcNow));
        Assert.Contains("rien envoyé", e.Message);
    }

    [Fact]
    public void Modele_depuis_une_colonne_vide_du_miroir_est_une_erreur_claire()
    {
        var e = Assert.Throws<InvalidOperationException>(() => ModeleDepuisOfficine.Construire("parametre_inventaire", Guid.NewGuid(),
            [Ligne(("seuil_boites", 3), ("seuil_montant", null))], "propose", Jour, DateTime.UtcNow));
        Assert.Contains("seuil_montant", e.Message);
    }

    [Fact]
    public void Modele_depuis_les_remises_garde_les_categories_en_cours_avec_une_cle_stable()
    {
        var officine = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var lignes = new[]
        {
            Ligne(("id", 1L), ("produit_id", null), ("categorie", "cosmetique"), ("pourcentage", 10.00m), ("date_debut", new DateTime(2026, 1, 1)), ("date_fin", new DateTime(2026, 12, 31)), ("motif", null), ("arretee_le", null), ("modele_cle", null)),
            Ligne(("id", 2L), ("produit_id", 99L), ("categorie", null), ("pourcentage", 5m), ("date_debut", new DateTime(2026, 1, 1)), ("date_fin", new DateTime(2026, 12, 31)), ("motif", null), ("arretee_le", null), ("modele_cle", null)),
            Ligne(("id", 3L), ("produit_id", null), ("categorie", "hygiene"), ("pourcentage", 5m), ("date_debut", new DateTime(2025, 1, 1)), ("date_fin", new DateTime(2025, 12, 31)), ("motif", null), ("arretee_le", null), ("modele_cle", null)),
            Ligne(("id", 4L), ("produit_id", null), ("categorie", "hygiene"), ("pourcentage", 5m), ("date_debut", new DateTime(2026, 1, 1)), ("date_fin", new DateTime(2026, 12, 31)), ("motif", null), ("arretee_le", DateTime.UtcNow), ("modele_cle", null))
        };
        var (json, notes) = ModeleDepuisOfficine.Construire("remise_automatique", officine, lignes, "propose", Jour, DateTime.UtcNow);
        Assert.Equal(3, notes.Count);
        Assert.Empty(ModelePublication.Preparer("remise_automatique", json, Jour).Erreurs);
        using var doc = JsonDocument.Parse(json);
        var remise = Assert.Single(doc.RootElement.GetProperty("valeur").GetProperty("remises").EnumerateArray());
        Assert.Equal("cosmetique", remise.GetProperty("categorie").GetString());
        Assert.Matches("^c_[0-9a-f]{12}$", remise.GetProperty("cle").GetString()!);
        Assert.True(doc.RootElement.GetProperty("ajouts_locaux").GetBoolean());
        // Même officine, même remise : même clé
        var (json2, _) = ModeleDepuisOfficine.Construire("remise_automatique", officine, lignes, "propose", Jour, DateTime.UtcNow);
        using var doc2 = JsonDocument.Parse(json2);
        Assert.Equal(remise.GetProperty("cle").GetString(),
            doc2.RootElement.GetProperty("valeur").GetProperty("remises")[0].GetProperty("cle").GetString());
    }

    [Fact]
    public void Modele_depuis_les_motifs_et_les_psychotropes_est_valide()
    {
        var (motifs, _) = ModeleDepuisOfficine.Construire("motif_reglable", Guid.NewGuid(),
            [Ligne(("liste", "sortie_stock"), ("code", "casse"), ("libelle", "Casse"), ("actif", true), ("ordre", 20), ("effet", null), ("detail", null))],
            "propose", Jour, DateTime.UtcNow);
        Assert.Empty(ModelePublication.Preparer("motif_reglable", motifs, Jour).Erreurs);

        var regles = new[] { "patient_repete", "ordonnance_reutilisee", "quantite_anormale", "prescripteur_inhabituel", "prescripteur_hors_zone" }
            .Select(code => Ligne(("code", code), ("active", true), ("seuil", 5), ("periode_jours", 30), ("exiger_valideur", false), ("unites_par_jour", null)))
            .ToList();
        var (psy, _) = ModeleDepuisOfficine.Construire("regle_alerte_psychotrope", Guid.NewGuid(), regles, "impose", Jour, DateTime.UtcNow);
        Assert.Empty(ModelePublication.Preparer("regle_alerte_psychotrope", psy, Jour).Erreurs);
    }

    // ---- Essai réel sur une base centrale jetable (variable PHARMA_ESSAI_M6_CONNEXION) ----------

    private sealed class FactEssaiBaseAttribute : FactAttribute
    {
        public FactEssaiBaseAttribute()
        {
            if (string.IsNullOrEmpty(Connexion))
                Skip = "Essai réel : définir PHARMA_ESSAI_M6_CONNEXION (connexion du propriétaire d'une base centrale JETABLE ET NEUVE pharmacie_cloud_jetable_m6_*, créée par creer_base_centrale.ps1).";
        }
    }

    private static string? Connexion => Environment.GetEnvironmentVariable("PHARMA_ESSAI_M6_CONNEXION");

    [FactEssaiBase]
    public async Task Essai_reel_publier_refuser_mode_journal_liberer_retablir_et_descendant()
    {
        var connexion = Connexion!;
        Assert.Contains("pharmacie_cloud_jetable_m6_", connexion);   // garde-fou : jamais une autre base
        var essai = new Essai();
        Func<NpgsqlDataSource> db = () => NpgsqlDataSource.Create(connexion);
        var f = Fichier(InventaireValide);
        var fInvalide = Fichier(InventaireInvalide);
        try
        {
            await using var admin = db();
            var officine = Guid.NewGuid();
            await using (var ins = admin.CreateCommand("INSERT INTO officine (id, libelle) VALUES ($1, 'Officine essai M6')"))
            {
                ins.Parameters.AddWithValue(officine);
                await ins.ExecuteNonQueryAsync();
            }

            // Un modèle invalide est refusé : rien d'écrit
            Assert.Equal(1, await essai.Outil(connexion: db).ExecuterAsync(["--publier-reglage", "parametre_inventaire", "--fichier", fInvalide, "--auteur", "admin-1"]));
            Assert.Equal(0L, await Compter(admin, "SELECT count(*) FROM cloud_journal_reglage"));

            // Simulation : rien d'écrit
            Assert.Equal(0, await essai.Outil(connexion: db).ExecuterAsync(["--publier-reglage", "parametre_inventaire", "--fichier", f, "--auteur", "admin-1", "--simulation"]));
            Assert.Equal(0L, await Compter(admin, "SELECT count(*) FROM cloud_journal_reglage"));

            // Publication imposée : refusée sans confirmation, puis acceptée avec --oui
            Assert.Equal(1, await essai.Outil("non", db).ExecuterAsync(["--publier-reglage", "parametre_inventaire", "--fichier", f, "--auteur", "admin-1"]));
            Assert.Equal(0, await essai.Outil(connexion: db).ExecuterAsync(["--publier-reglage", "parametre_inventaire", "--fichier", f, "--auteur", "admin-1", "--oui", "--motif", "essai M6"]));
            Assert.Equal(1L, await Compter(admin, "SELECT version_modele FROM cloud_reglage_modele WHERE nom_table = 'parametre_inventaire'"));
            Assert.Equal(1L, await Compter(admin, "SELECT count(*) FROM cloud_journal_reglage WHERE action = 'publication' AND auteur = 'admin-1' AND compte_systeme = 'TEST\\admin' AND poste = 'POSTE-ESSAI' AND motif = 'essai M6'"));

            // Même modèle : aucune nouvelle version
            Assert.Equal(0, await essai.Outil(connexion: db).ExecuterAsync(["--publier-reglage", "parametre_inventaire", "--fichier", f, "--auteur", "admin-1", "--oui"]));
            Assert.Equal(1L, await Compter(admin, "SELECT version_modele FROM cloud_reglage_modele WHERE nom_table = 'parametre_inventaire'"));

            // Mode : propose (sans confirmation) puis impose (confirmation)
            Assert.Equal(0, await essai.Outil(connexion: db).ExecuterAsync(["--mode-reglage", "parametre_inventaire", "propose", "--auteur", "admin-2"]));
            Assert.Equal(1, await essai.Outil("non", db).ExecuterAsync(["--mode-reglage", "parametre_inventaire", "impose", "--auteur", "admin-2"]));
            Assert.Equal(0, await essai.Outil("oui", db).ExecuterAsync(["--mode-reglage", "parametre_inventaire", "impose", "--auteur", "admin-2"]));
            Assert.Equal(3L, await Compter(admin, "SELECT version_modele FROM cloud_reglage_modele WHERE nom_table = 'parametre_inventaire'"));

            // État, journal
            Assert.Equal(0, await essai.Outil(connexion: db).ExecuterAsync(["--reglages-mutualises"]));
            Assert.Contains("parametre_inventaire", essai.Sortie.ToString());
            Assert.Contains("version 3", essai.Sortie.ToString());
            Assert.Equal(0, await essai.Outil(connexion: db).ExecuterAsync(["--journal-reglages", "parametre_inventaire", "--depuis", "2020-01-01"]));
            Assert.Contains("publication", essai.Sortie.ToString());

            // L'élément mutu_<table> est prêt à descendre
            var element = await Descendant(admin, officine);
            Assert.Equal("impose", element.GetProperty("mode").GetString());
            Assert.Equal(3, element.GetProperty("version_modele").GetInt32());
            Assert.Equal(2, element.GetProperty("valeur").GetProperty("seuil_boites").GetInt32());

            // Libération puis rétablissement
            Assert.Equal(0, await essai.Outil(connexion: db).ExecuterAsync(["--liberer-reglages", officine.ToString(), "--auteur", "admin-3"]));
            Assert.Equal("local", (await Descendant(admin, officine)).GetProperty("mode").GetString());
            Assert.Equal(0, await essai.Outil(connexion: db).ExecuterAsync(["--retablir-reglages", officine.ToString(), "--auteur", "admin-3"]));
            Assert.Equal("impose", (await Descendant(admin, officine)).GetProperty("mode").GetString());
            Assert.Equal(2L, await Compter(admin, $"SELECT count(*) FROM cloud_journal_reglage WHERE officine_id = '{officine}'"));

            // Sans auteur : refusé
            await Assert.ThrowsAsync<ArgumentException>(() => essai.Outil(connexion: db).ExecuterAsync(["--liberer-reglages", officine.ToString()]));
        }
        finally
        {
            File.Delete(f);
            File.Delete(fInvalide);
        }
    }

    private static async Task<long> Compter(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    /// <summary>Élément « mutu_parametre_inventaire » que cloud_descendants renverrait à l'officine (appel avec le compte propriétaire, officine posée).</summary>
    private static async Task<JsonElement> Descendant(NpgsqlDataSource db, Guid officine)
    {
        await using var cn = await db.OpenConnectionAsync();
        await using var tx = await cn.BeginTransactionAsync();
        await using (var pose = new NpgsqlCommand("SELECT set_config('app.officine_id', $1, true)", cn, tx))
        {
            pose.Parameters.AddWithValue(officine.ToString());
            await pose.ExecuteScalarAsync();
        }
        await using var cmd = new NpgsqlCommand("SELECT donnees::text FROM cloud_descendants(0) WHERE cle = 'mutu_parametre_inventaire'", cn, tx);
        var texte = (string?)await cmd.ExecuteScalarAsync() ?? throw new InvalidOperationException("élément mutu_parametre_inventaire absent");
        await tx.RollbackAsync();
        using var doc = JsonDocument.Parse(texte);
        return doc.RootElement.Clone();
    }
}
