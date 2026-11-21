using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pharma.Api.Demo;
using Pharma.Application.Administration;
using Pharma.Application.Demo;
using Pharma.Domain.Commun;
using Pharma.Infrastructure;
using Pharma.Infrastructure.Demo;
using Pharma.Infrastructure.Impression;
using PdfSharp.Pdf.IO;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Démo cloud, étape 2 (docs/demo-cloud.md) : mode démonstration de l'API. Refus de démarrer sans marqueur, fonctions coupées (422),
/// bandeau exposé, filigrane des PDF, marqueur posé par le seul script d'init, compose de la remise à zéro sans secret en clair.
/// Aucun conteneur n'est lancé (Docker absent du poste) : contenu des fichiers et code seulement.
/// </summary>
public class ModeDemoTests
{
    private static string RacineDepot()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    private static string Chemin(params string[] parties) => Path.Combine([RacineDepot(), .. parties]);
    private static string Lire(params string[] parties) => File.ReadAllText(Chemin(parties));

    private static IEnumerable<string> Instructions(string texte) =>
        texte.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'));

    // ---- Démarrage ----

    [Fact]
    public void Program_refuse_le_demarrage_avant_toute_commande_et_coupe_les_taches_de_fond()
    {
        var program = Lire("src", "Pharma.Api", "Program.cs");
        var refus = program.IndexOf("ModeDemo.RefusDemarrage(demo.Actif, marqueurDemo)", StringComparison.Ordinal);
        Assert.True(refus > 0);
        Assert.True(refus < program.IndexOf("ConsoleSuperAdmin.ExecuterAsync", StringComparison.Ordinal));
        Assert.True(refus < program.IndexOf("ConsoleCleScellement.Executer(", StringComparison.Ordinal));
        Assert.True(refus < program.IndexOf("app.Run();", StringComparison.Ordinal));
        Assert.Contains("MarqueurDemo.LireAsync(", program);
        Assert.Contains("if (demo.Actif) app.Use(Pharma.Api.Demo.FonctionsCoupeesDemo.FiltrerAsync);", program);
        // Tâches qui sortent de l'officine : seulement hors démonstration
        var bloc = Regex.Match(program, @"if \(!demo\.Actif\)\s*\{(?<corps>[^}]*)\}").Groups["corps"].Value;
        foreach (var tache in new[] { "EnvoiSynchronisation", "SurveillanceSynchronisation", "PurgeSynchronisation", "EnvoiNotifications",
                     "SauvegardesAutomatiques", "SurveillanceSecondServeur" })
        {
            Assert.Contains($"AddHostedService<{(tache.Contains("Synchronisation") ? "Pharma.Api.Taches." : "")}{tache}>", bloc);
            Assert.Single(Regex.Matches(program, $@"AddHostedService<[\w.]*\b{tache}>"));
        }
    }

    [Fact]
    public void Demo_inactif_par_defaut()
    {
        Assert.False(new ReglagesDemo().Actif);
        using var json = JsonDocument.Parse(Lire("src", "Pharma.Api", "appsettings.json"));
        Assert.False(json.RootElement.GetProperty("Demo").GetProperty("Actif").GetBoolean());
        Assert.DoesNotContain("\"Demo\"", Lire("src", "Pharma.Api", "appsettings.Development.json"));
    }

    // ---- Fonctions coupées (422) ----

    [Theory]
    [InlineData("POST", "/api/super-admin/connexion")]
    [InlineData("GET", "/api/super-admin/restauration")]
    [InlineData("post", "/API/Super-Admin/restauration/base")]
    [InlineData("POST", "/api/admin/reprise/sources")]
    [InlineData("GET", "/api/admin/reprise/pilotes")]
    [InlineData("POST", "/api/admin/synchronisation/activation")]
    [InlineData("PUT", "/api/admin/synchronisation/reglages")]
    [InlineData("POST", "/api/admin/sauvegardes/lancer")]
    [InlineData("PUT", "/api/admin/sauvegardes/reglages")]
    [InlineData("PUT", "/api/admin/sauvegardes/secours")]
    [InlineData("POST", "/api/admin/sauvegardes/secours/tester")]
    [InlineData("PUT", "/api/admin/second-serveur/copie-ged")]
    [InlineData("POST", "/api/admin/second-serveur/cle/exporter")]
    [InlineData("POST", "/api/admin/notifications/destinataires/12/code")]
    [InlineData("POST", "/api/admin/notifications/destinataires/12/test")]
    [InlineData("POST", "/api/admin/notifications/rapport")]
    [InlineData("POST", "/api/achats/commandes/7/envoyer-email")]
    public void Route_coupee_en_demonstration(string methode, string chemin) =>
        Assert.NotNull(FonctionsCoupeesDemo.FonctionCoupee(methode, chemin));

    [Theory]
    [InlineData("GET", "/api/admin/sauvegardes")]
    [InlineData("GET", "/api/admin/synchronisation")]
    [InlineData("GET", "/api/admin/second-serveur")]
    [InlineData("POST", "/api/caisse/ventes")]
    [InlineData("POST", "/api/auth/connexion")]
    [InlineData("GET", "/api/demo")]
    [InlineData("GET", "/api/healthcheck")]
    [InlineData("POST", "/api/admin/nomenclature")]
    [InlineData("PUT", "/api/admin/notifications/reglages")]
    [InlineData("GET", "/api/admin/notifications/file")]
    public void Route_permise_en_demonstration(string methode, string chemin) =>
        Assert.Null(FonctionsCoupeesDemo.FonctionCoupee(methode, chemin));

    [Fact]
    public async Task Fonction_coupee_repond_422_avec_un_message_clair()
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        http.Request.Method = "POST";
        http.Request.Path = "/api/super-admin/connexion";
        http.Response.Body = new MemoryStream();
        var suiteAppelee = false;

        await FonctionsCoupeesDemo.FiltrerAsync(http, () => { suiteAppelee = true; return Task.CompletedTask; });

        Assert.False(suiteAppelee);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, http.Response.StatusCode);
        http.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(http.Response.Body);
        Assert.StartsWith("Indisponible en démonstration : super admin", json.RootElement.GetProperty("message").GetString());

        var permise = new DefaultHttpContext();
        permise.Request.Method = "GET";
        permise.Request.Path = "/api/demo";
        await FonctionsCoupeesDemo.FiltrerAsync(permise, () => { suiteAppelee = true; return Task.CompletedTask; });
        Assert.True(suiteAppelee);
    }

    [Fact]
    public async Task Adaptateurs_factices_refusent_l_envoi_sans_rien_envoyer()
    {
        var connexion = new Pharma.Application.Notifications.ConnexionSmtp("smtp.exemple.invalid", 587, true, "officine@exemple.invalid", null, null);
        var sms = await Assert.ThrowsAsync<RegleMetierException>(() =>
            new EnvoiSmsDemo(NullLogger<EnvoiSmsDemo>.Instance).EnvoyerAsync("COM9", "0550000000", "Texte fictif"));
        Assert.StartsWith("Indisponible en démonstration : envoi de SMS", sms.Message);
        var email = new EnvoiEmailDemo(NullLogger<EnvoiEmailDemo>.Instance);
        var e1 = await Assert.ThrowsAsync<RegleMetierException>(() => email.EnvoyerAsync(connexion, "x@exemple.invalid", "Objet", "Texte", null));
        var e2 = await Assert.ThrowsAsync<RegleMetierException>(() =>
            email.EnvoyerAsync(connexion, "x@exemple.invalid", "Objet", "Texte", "bon.pdf", [1, 2], "application/pdf"));
        Assert.All([e1.Message, e2.Message], m => Assert.StartsWith("Indisponible en démonstration : envoi d'e-mails", m));
        Assert.Empty(await new ChifaConnecteurDemo(NullLogger<ChifaConnecteurDemo>.Instance).LireFacturesSigneesAsync(new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void AddInfrastructure_remplace_les_adaptateurs_en_demonstration_seulement()
    {
        static ServiceCollection Services(bool demo)
        {
            var s = new ServiceCollection();
            s.AddInfrastructure(new InfrastructureOptions("Host=localhost;Database=jamais_ouverte", "", Path.GetTempPath(), ModeDemo: demo));
            return s;
        }
        Type? Implementation<T>(ServiceCollection s) => s.Last(d => d.ServiceType == typeof(T)).ImplementationType;

        var demo = Services(true);
        Assert.Equal(typeof(EnvoiSmsDemo), Implementation<Pharma.Application.Notifications.IEnvoiSms>(demo));
        Assert.Equal(typeof(EnvoiEmailDemo), Implementation<Pharma.Application.Notifications.IEnvoiEmail>(demo));
        Assert.Equal(typeof(EnvoiEmailDemo), Implementation<Pharma.Application.Achats.IEnvoiEmailPieceJointe>(demo));
        Assert.Equal(typeof(ChifaConnecteurDemo), Implementation<Pharma.Application.Chifa.IChifaConnecteur>(demo));
        Assert.True(((ModeDemo)demo.Last(d => d.ServiceType == typeof(ModeDemo)).ImplementationInstance!).Actif);
        Assert.True(((FiligranePdf)demo.Last(d => d.ServiceType == typeof(FiligranePdf)).ImplementationInstance!).Actif);

        var officine = Services(false);
        Assert.Equal(typeof(Pharma.Infrastructure.Notifications.EnvoiSmsModemGsm), Implementation<Pharma.Application.Notifications.IEnvoiSms>(officine));
        Assert.Equal(typeof(Pharma.Infrastructure.Notifications.EnvoiEmailMailKit), Implementation<Pharma.Application.Notifications.IEnvoiEmail>(officine));
        Assert.False(((ModeDemo)officine.Last(d => d.ServiceType == typeof(ModeDemo)).ImplementationInstance!).Actif);
    }

    // ---- Bandeau ----

    [Fact]
    public void Bandeau_expose_par_l_API_et_affiche_par_le_poste_et_la_page_mobile()
    {
        var routes = Lire("src", "Pharma.Api", "Endpoints", "PharmaEndpoints.cs");
        Assert.Contains("app.MapGet(\"/api/demo\"", routes);
        Assert.Contains("new Pharma.Shared.EtatDemoDto(demo.Value.Actif", routes);
        Assert.Equal("Démonstration — données fictives", ModeDemo.Libelle);

        // Poste : barre d'état (toujours affichée, avant et après connexion), aucun bouton pour masquer la mention
        var barre = Lire("src", "Pharma.Desktop", "Composants", "Mise", "BarreEtat.razor");
        var element = Regex.Match(barre, @"@if \(demo is \{ Actif: true \}\)\s*\{(?<corps>.*?)\n    \}", RegexOptions.Singleline).Groups["corps"].Value;
        Assert.Contains("data-zone=\"demonstration\"", element);
        Assert.Contains("class=\"element-etat demo\"", element);
        Assert.DoesNotContain("<button", element);
        Assert.DoesNotContain("style=", element);
        Assert.Contains("Api.EtatDemoAsync()", barre);
        Assert.Contains("\"api/demo\"", Lire("src", "Pharma.Desktop", "Services", "ApiClient.cs"));
        Assert.Contains(".element-etat.demo", Lire("src", "Pharma.Desktop", "wwwroot", "css", "app.css"));

        // Page mobile : dans l'en-tête collé en haut
        var page = Lire("src", "Pharma.Api", "wwwroot", "mobile", "index.html");
        var entete = page[page.IndexOf("<header", StringComparison.Ordinal)..page.IndexOf("</header>", StringComparison.Ordinal)];
        Assert.Contains("id=\"bandeau-demo\"", entete);
        Assert.Contains("fetch(\"/api/demo\"", Lire("src", "Pharma.Api", "wwwroot", "mobile", "mobile.js"));
        Assert.Contains(".bandeau-demo", Lire("src", "Pharma.Api", "wwwroot", "mobile", "mobile.css"));
    }

    // ---- Filigrane des PDF ----

    private static Pharma.Application.Achats.ContenuFormulaireAccordSms Contenu() =>
        new(new IdentitePharmacie("Pharmacie Fictive", "Dr Fictif", "1 rue Fictive", "000", null, null, "fr"), "Patient Fictif", "0550000000",
            Pharma.Application.Achats.RegleFormulaireAccordSms.TexteParDefaut, new DateOnly(2026, 11, 21));

    [Fact]
    public void Filigrane_present_dans_un_PDF_genere_en_demonstration_seulement()
    {
        var avec = new DocumentsPdf(new FiligranePdf(true)).FormulaireAccordSms(Contenu());
        var sans = new DocumentsPdf().FormulaireAccordSms(Contenu());

        using var lu = PdfReader.Open(new MemoryStream(avec), PdfDocumentOpenMode.Import);
        Assert.True(lu.PageCount >= 1);
        Assert.Equal(FiligranePdf.Texte, lu.Info.Keywords);
        Assert.Equal(FiligranePdf.Mention, lu.Info.Subject);
        using var normal = PdfReader.Open(new MemoryStream(sans), PdfDocumentOpenMode.Import);
        Assert.NotEqual(FiligranePdf.Texte, normal.Info.Keywords);
        // Le filigrane ajoute un contenu dessiné sur chaque page
        Assert.True(avec.Length > sans.Length);
        Assert.Equal(lu.PageCount, normal.PageCount);
    }

    [Fact]
    public void Generateurs_de_PDF_posent_le_filigrane()
    {
        foreach (var fichier in new[] { "DocumentsPdf.cs", "DocumentsAchats.cs" })
        {
            var code = Lire("src", "Pharma.Infrastructure", "Impression", fichier);
            var sauvegardes = Regex.Matches(code, @"\.Save\(flux, false\)").Count;
            Assert.True(sauvegardes > 0);
            Assert.Equal(sauvegardes, Regex.Matches(code, @"filigrane\.Poser\(").Count);
        }
    }

    // ---- Marqueur : script d'init seulement ----

    [Fact]
    public void Script_d_init_pose_le_marqueur_puis_cree_le_gabarit()
    {
        var init = string.Join('\n', Instructions(Lire("deploy", "demo", "init", "10_base_officine.sh")));
        var donnees = init.IndexOf("for fichier in \"${DONNEES[@]}\"", StringComparison.Ordinal);
        var marqueur = init.IndexOf("CREATE TABLE IF NOT EXISTS base_demonstration", StringComparison.Ordinal);
        var insertion = init.IndexOf("INSERT INTO base_demonstration (id, libelle) VALUES (1,", StringComparison.Ordinal);
        var gabarit = init.IndexOf("CREATE DATABASE :\"gabarit\" TEMPLATE :\"base\" OWNER pharma_app;", StringComparison.Ordinal);
        Assert.True(donnees > 0 && marqueur > donnees && insertion > marqueur && gabarit > insertion, "Ordre : données, marqueur, gabarit.");
        Assert.Contains("ALTER DATABASE :\"gabarit\" WITH IS_TEMPLATE true ALLOW_CONNECTIONS false;", init);
        Assert.Contains("SELECT count(*) FROM base_demonstration", init);
        Assert.Equal(ModeDemo.TableMarqueur, "base_demonstration");
    }

    [Fact]
    public void Aucune_migration_ni_seed_ni_donnee_ne_pose_le_marqueur()
    {
        var fichiers = Directory.EnumerateFiles(Chemin("database"), "*.*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Chemin("scripts"), "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".sh", StringComparison.OrdinalIgnoreCase));
        var fautifs = fichiers.Where(f => File.ReadAllText(f).Contains(ModeDemo.TableMarqueur, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(fautifs.Count == 0, "Marqueur de démonstration hors du script d'init : " + string.Join(", ", fautifs));
        // Ni dans le code de production en dehors de sa lecture (MarqueurDemo) et de sa constante
        var src = Directory.EnumerateFiles(Chemin("src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"(?i)(CREATE TABLE|INSERT INTO)[^;""]*base_demonstration"));
        Assert.Empty(src);
    }

    // ---- Compose et remise à zéro ----

    [Fact]
    public void Compose_mode_demo_jeton_deux_heures_et_remise_a_zero_sans_secret_en_clair()
    {
        var compose = string.Join('\n', Instructions(Lire("deploy", "demo", "docker-compose.yml")));
        Assert.Contains("Demo__Actif: \"true\"", compose);
        Assert.Contains("Jwt__DureeHeures: \"2\"", compose);
        Assert.Contains("Demo__FichierNomenclature: \"/pharma/data/NOMENCLATURE NATIONALE - VERSION JUIN 2026.xlsx\"", compose);
        Assert.True(File.Exists(Chemin("database", "data", "NOMENCLATURE NATIONALE - VERSION JUIN 2026.xlsx")));

        var debut = compose.IndexOf("\n  reinit:", StringComparison.Ordinal);
        Assert.True(debut > 0, "Service reinit absent du compose.");
        var fin = compose.IndexOf("\nsecrets:", debut, StringComparison.Ordinal);
        var reinit = compose[debut..fin];
        Assert.Contains("image: postgres:18", reinit);
        Assert.Contains("entrypoint: [\"bash\", \"/pharma/reinit/remise_a_zero.sh\"]", reinit);
        Assert.Contains("POSTGRES_MDP_FILE: /run/secrets/postgres_mdp", reinit);
        Assert.Contains("PHARMA_APP_MDP_FILE: /run/secrets/pharma_app_mdp", reinit);
        Assert.Contains("PHARMA_BASE: pharmacie_demo", reinit);
        Assert.DoesNotMatch(@"(?i)(PASSWORD|PGPASSWORD|_MDP)\s*:\s*(?!/run/secrets/)", reinit.Replace("_MDP_FILE", "_FICHIER"));
        Assert.DoesNotMatch(@"(?i)password=", reinit);
        Assert.DoesNotContain("A-REMPLACER", reinit);
        Assert.DoesNotContain("pharmacie_dev", compose);
        Assert.DoesNotContain("pharmacie_test", compose);
        Assert.Contains("- ./reinit:/pharma/reinit:ro", reinit);
    }

    [Fact]
    public void Script_de_remise_a_zero_en_LF_garde_fous_et_secrets_lus_dans_les_fichiers()
    {
        var chemin = Chemin("deploy", "demo", "reinit", "remise_a_zero.sh");
        var octets = File.ReadAllBytes(chemin);
        Assert.DoesNotContain((byte)'\r', octets);
        var texte = File.ReadAllText(chemin);
        Assert.StartsWith("#!/usr/bin/env bash\n", texte);
        Assert.Matches(@"(?m)^set -euo pipefail$", texte);
        Assert.Contains("*.sh text eol=lf", Lire("deploy", "demo", "reinit", ".gitattributes"));
        var code = string.Join('\n', Instructions(texte));
        // Jamais les bases du poste de développement ; marqueur vérifié avant la suppression
        Assert.Contains("pharmacie_dev|pharmacie_test|pharmacie) erreur", code);
        var verification = code.IndexOf("verifier_marqueur\n", code.IndexOf("remettre_a_zero() {", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(verification > 0 && verification < code.IndexOf("DROP DATABASE IF EXISTS :\"base\" WITH (FORCE);", StringComparison.Ordinal));
        Assert.Contains("CREATE DATABASE :\"base\" TEMPLATE :\"gabarit\" OWNER pharma_app;", code);
        Assert.Contains("ALTER DATABASE :\"base\" SET timezone = 'Africa/Algiers';", code);
        Assert.Contains("905_jeu_de_test.sql", code);
        // Secrets : fichiers compose, transmis à psql par l'environnement du seul processus
        Assert.Contains("lire_secret \"${POSTGRES_MDP_FILE:-}\"", code);
        Assert.Contains("lire_secret \"${PHARMA_APP_MDP_FILE:-}\"", code);
        Assert.DoesNotMatch(@"PGPASSWORD=[""']?[A-Za-z0-9]", code);
        Assert.DoesNotMatch(@"(?i)password\s*[:=]\s*'", code);
    }
}
