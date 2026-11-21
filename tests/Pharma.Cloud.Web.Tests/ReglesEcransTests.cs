using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Pharma.Cloud.Web.Codes;
using Pharma.Cloud.Web.Donnees;
using Pharma.Cloud.Web.Points;
using Pharma.Cloud.Web.Securite;
using Pharma.Shared;

namespace Pharma.Cloud.Web.Tests;

/// <summary>Règles pures des écrans : démarrage, second facteur, contrôle d'accès, mots de passe.</summary>
public class ReglesEcransTests
{
    private static IConfiguration Config(params (string Cle, string Valeur)[] v) =>
        new ConfigurationBuilder().AddInMemoryCollection(v.Select(x => new KeyValuePair<string, string?>(x.Cle, x.Valeur))).Build();

    private static string? Variables(string nom) => nom switch
    {
        ReglagesEcrans.VariableConnexion => "Host=localhost;Database=x;Username=y",
        ReglagesEcrans.VariableDossier => @"C:\dossier",
        _ => null,
    };

    [Fact]
    public void Developpement_en_boucle_locale_et_mode_fichier_demarre()
    {
        var (r, refus) = ReglagesEcrans.Evaluer(true, Config(("Ecrans:ModeCodes", "fichier")), Variables);
        Assert.Null(refus);
        Assert.Equal(IPAddress.Loopback, r!.Adresse);
        Assert.Equal(5094, r.Port);
        Assert.Equal(ModeCodes.Fichier, r.Mode);
        Assert.EndsWith("autorite.crt", r.CertificatAutorite);
        Assert.EndsWith(Path.Combine("PharmaOfficine", "central-dev", "codes"), r.DossierCodes);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.10")]
    [InlineData("::")]
    public void Adresse_non_locale_refusee_en_developpement(string adresse)
    {
        var (r, refus) = ReglagesEcrans.Evaluer(true, Config(("Ecrans:Adresse", adresse), ("Ecrans:ModeCodes", "courriel")), Variables);
        Assert.Null(r);
        Assert.Contains("boucle locale", refus);
    }

    [Fact]
    public void Mode_fichier_refuse_hors_developpement_meme_en_boucle_locale()
    {
        var (r, refus) = ReglagesEcrans.Evaluer(false, Config(("Ecrans:ModeCodes", "fichier")), Variables);
        Assert.Null(r);
        Assert.Contains("réservé au développement", refus);
    }

    [Fact]
    public void Sms_sans_fournisseur_refuse_et_cle_de_l_autorite_jamais_lue()
    {
        Assert.Contains("SMS non disponible", ReglagesEcrans.Evaluer(false, Config(("Ecrans:ModeCodes", "sms")), Variables).Refus);
        Assert.Contains("jamais sa clé", ReglagesEcrans.Evaluer(true, Config(("Ecrans:ModeCodes", "fichier"), ("Ecrans:CertificatAutorite", @"C:\x\autorite.cle")), Variables).Refus);
        Assert.Contains(ReglagesEcrans.VariableConnexion, ReglagesEcrans.Evaluer(true, Config(("Ecrans:ModeCodes", "fichier")), _ => null).Refus);
        Assert.Contains("port 5090", ReglagesEcrans.Evaluer(true, Config(("Ecrans:Port", "5090")), Variables).Refus);
    }

    [Fact]
    public void Codes_de_six_chiffres_empreinte_hmac_propre_au_processus()
    {
        for (var i = 0; i < 200; i++) Assert.Matches("^[0-9]{6}$", CodesSecondFacteur.Tirer());
        var a = new CodesSecondFacteur(new byte[32]);
        var b = new CodesSecondFacteur(Enumerable.Repeat((byte)1, 32).ToArray());
        Assert.Equal(a.Empreinte("connexion", "123456"), a.Empreinte("connexion", "123 456"));
        Assert.NotEqual(a.Empreinte("connexion", "123456"), a.Empreinte("action", "123456"));
        Assert.NotEqual(a.Empreinte("connexion", "123456"), b.Empreinte("connexion", "123456"));
        Assert.Null(a.Empreinte("connexion", "12345"));
        Assert.Null(a.Empreinte("connexion", "abcdef"));
        Assert.Throws<ArgumentException>(() => new CodesSecondFacteur(new byte[16]));
    }

    [Fact]
    public async Task Mode_fichier_ecrit_dans_le_dossier_des_codes_et_refuse_un_chemin()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "pharma_codes_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var envoi = new EnvoiCodeFichier(dossier, TimeProvider.System);
            await envoi.EnvoyerAsync(new DestinataireCode("editeur.dev", null, null), "042137", CancellationToken.None);
            Assert.Contains("042137", await File.ReadAllTextAsync(Path.Combine(dossier, "editeur.dev.txt")));
            await Assert.ThrowsAsync<ArgumentException>(() => envoi.EnvoyerAsync(new DestinataireCode(@"..\..\x", null, null), "1", CancellationToken.None));
            await Assert.ThrowsAsync<NotSupportedException>(() => new EnvoiCodeSmsIndisponible().EnvoyerAsync(new DestinataireCode("a.b", null, "0555"), "1", CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(dossier)) Directory.Delete(dossier, true);
        }
    }

    [Fact]
    public void Controle_d_acces_selon_l_etape_de_la_session()
    {
        SessionEcran S(string etape, string role = "editeur") => new(1, Guid.NewGuid(), "a.b", "A", role, etape, DateTime.UtcNow.AddHours(1), false);
        Assert.Equal("/connexion", ControleAcces.Redirection("/officines", null));
        Assert.Null(ControleAcces.Redirection("/connexion", null));
        Assert.Null(ControleAcces.Redirection("/css/app.css", null));
        Assert.Equal("/connexion", ControleAcces.Redirection("/actions/creer-officine", null));
        Assert.Equal("/code", ControleAcces.Redirection("/officines", S("code")));
        Assert.Null(ControleAcces.Redirection("/actions/code", S("code")));
        Assert.Equal("/mot-de-passe", ControleAcces.Redirection("/officines", S("mot_de_passe")));
        Assert.Equal("/mot-de-passe", ControleAcces.Redirection("/actions/code-action", S("mot_de_passe")));
        Assert.Null(ControleAcces.Redirection("/officines", S("ouverte")));
        Assert.Equal("/officines", ControleAcces.Redirection("/connexion", S("ouverte")));
        Assert.Equal("/mes-officines", ControleAcces.Redirection("/officines", S("ouverte", "titulaire")));
    }

    [Fact]
    public void Roles_croises_refuses_titulaire_et_editeur()
    {
        SessionEcran S(string role) => new(1, Guid.NewGuid(), "a.b", "A", role, "ouverte", DateTime.UtcNow.AddHours(1), false);
        var officine = Guid.NewGuid();
        // Titulaire : ses écrans et les écrans communs ; aucun écran ni aucune action de l'éditeur
        foreach (var permis in new[] { "/mes-officines", "/rattacher", "/actions/rattacher", $"/mes-officines/{officine}", $"/mes-officines/{officine}/journal",
                     "/mon-journal", "/mot-de-passe", "/actions/mot-de-passe", "/actions/deconnexion", "/actions/theme", "/acces-refuse" })
            Assert.Null(ControleAcces.Redirection(permis, S("titulaire")));
        foreach (var editeur in new[] { $"/officines/{officine}", "/officines/nouvelle", $"/officines/{officine}/nouveau-code", "/actions/creer-officine",
                     $"/actions/officines/{officine}/revoquer-serveur", $"/officines/{officine}/code-affiche", $"/mes-officines/{officine}/autre",
                     "/mes-officines/pas-un-uuid" })
            Assert.Equal("/acces-refuse", ControleAcces.Redirection(editeur, S("titulaire")));
        Assert.Equal("/mes-officines", ControleAcces.Redirection("/", S("titulaire")));
        Assert.Equal("/mes-officines", ControleAcces.Redirection("/connexion", S("titulaire")));
        Assert.Equal("/mes-officines", ControleAcces.Accueil(S("titulaire")));
        // Éditeur : aucun écran du titulaire (tableau de bord, journal d'une officine, rattachement)
        foreach (var titulaire in new[] { "/mes-officines", "/rattacher", "/actions/rattacher", $"/mes-officines/{officine}", $"/mes-officines/{officine}/journal" })
            Assert.Equal("/acces-refuse", ControleAcces.Redirection(titulaire, S("editeur")));
        Assert.Null(ControleAcces.Redirection($"/officines/{officine}", S("editeur")));
        Assert.Null(ControleAcces.Redirection("/mon-journal", S("editeur")));
        Assert.Equal("/officines", ControleAcces.Accueil(S("editeur")));
        // Rôle inconnu : écrans communs seulement
        Assert.Equal("/acces-refuse", ControleAcces.Redirection("/officines", S("autre")));
        Assert.Equal("/acces-refuse", ControleAcces.Redirection("/mes-officines", S("autre")));
    }

    [Fact]
    public void Code_de_rattachement_et_valeurs_non_disponibles()
    {
        for (var i = 0; i < 100; i++)
        {
            var code = ComptesDistants.CodeRattachement();
            Assert.Matches("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", code);
            Assert.Equal(ComptesDistants.EmpreinteCodeRattachement(code), ComptesDistants.EmpreinteCodeRattachement(code.ToLowerInvariant().Replace("-", " ")));
        }
        Assert.Matches("^[0-9a-f]{64}$", ComptesDistants.EmpreinteCodeRattachement("ABCD-EFGH-JKLM")!);
        Assert.NotEqual(ComptesDistants.EmpreinteCodeRattachement("ABCD-EFGH-JKLM"), ComptesDistants.EmpreinteCodeRattachement("ABCD-EFGH-JKLN"));
        Assert.Null(ComptesDistants.EmpreinteCodeRattachement("ABCD-EFGH-JKL"));
        Assert.Null(ComptesDistants.EmpreinteCodeRattachement("ABCD-EFGH-JKL0"));   // 0 hors alphabet
        Assert.Null(ComptesDistants.EmpreinteCodeRattachement(null));
        Assert.Null(ComptesDistants.EmpreinteCodeRattachement(new string('A', 60)));
        // NULL de la base : « non disponible », jamais 0
        Assert.Equal(Tableau.NonDisponible, Tableau.Montant(null));
        Assert.Equal(Tableau.NonDisponible, Tableau.Nombre(null));
        Assert.Equal("1 400,00 DA", Tableau.Montant(1400m).Replace(' ', ' ').Replace(' ', ' '));
        Assert.Equal("0", Tableau.Nombre(0));
    }

    [Fact]
    public void Lot_s7_3_vue_d_ensemble_et_ordres_du_titulaire_jamais_pour_l_editeur()
    {
        SessionEcran S(string role) => new(1, Guid.NewGuid(), "a.b", "A", role, "ouverte", DateTime.UtcNow.AddHours(1), false);
        var officine = Guid.NewGuid();
        // Titulaire : vue d'ensemble, ordres d'une officine, envoi d'un ordre, code de confirmation (second facteur de l'ordre)
        foreach (var permis in new[] { "/mes-officines/ensemble", $"/mes-officines/{officine}/ordres", $"/actions/mes-officines/{officine}/ordre", "/actions/code-action" })
            Assert.Null(ControleAcces.Redirection(permis, S("titulaire")));
        foreach (var refuse in new[] { "/actions/mes-officines/pas-un-uuid/ordre", $"/actions/mes-officines/{officine}/autre", $"/mes-officines/{officine}/ordres/x" })
            Assert.Equal("/acces-refuse", ControleAcces.Redirection(refuse, S("titulaire")));
        // Éditeur : rien du titulaire ; il garde le code de ses propres actions
        foreach (var titulaire in new[] { "/mes-officines/ensemble", $"/mes-officines/{officine}/ordres", $"/actions/mes-officines/{officine}/ordre" })
            Assert.Equal("/acces-refuse", ControleAcces.Redirection(titulaire, S("editeur")));
        Assert.Null(ControleAcces.Redirection("/actions/code-action", S("editeur")));
        // Liste fermée affichée = liste fermée de la base (script 034)
        Assert.Equal(["essai", "sauvegarde", "verification_integrite"], Rubriques.OrdresTitulaire.Select(o => o.Type));
        var sql = File.ReadAllText(Path.Combine(Depot.Racine(), "database", "cloud", "034_titulaire_multi_officines_ordres.sql"));
        Assert.Contains("SELECT coalesce(p_type IN ('essai', 'sauvegarde', 'verification_integrite'), false)", sql);
        Assert.Equal("ordres à distance", Rubriques.Afficher("droits", "ordres"));
        Assert.Equal("À confirmer sur le poste", Rubriques.Afficher("statut", "a_confirmer"));
    }

    [Fact]
    public void Lot_s7_3_total_du_groupe_jamais_un_non_disponible_compte_comme_zero()
    {
        var t = new TableEcran(["id", "ca_jour", "ventes_jour"],
            [[Guid.NewGuid(), 100.50m, 3], [Guid.NewGuid(), null, null], [Guid.NewGuid(), 20m, 2]], 3);
        var ca = VueEnsemble.Total(t, "ca_jour");
        Assert.Equal(new TotalGroupe(120.50m, 2, 1), ca);
        Assert.True(ca.Partiel);
        Assert.Equal("120,50 DA (partiel : 2 sur 3 officines)", VueEnsemble.Texte(ca, true).Replace(' ', ' ').Replace(' ', ' '));
        // Toutes disponibles : total simple ; aucune : non disponible (jamais 0)
        var complet = new TableEcran(["id", "ventes_jour"], [[Guid.NewGuid(), 3], [Guid.NewGuid(), 0]], 2);
        Assert.Equal("3", VueEnsemble.Texte(VueEnsemble.Total(complet, "ventes_jour"), false));
        Assert.False(VueEnsemble.Total(complet, "ventes_jour").Partiel);
        var vide = new TableEcran(["id", "ca_jour"], [[Guid.NewGuid(), null], [Guid.NewGuid(), null]], 2);
        Assert.Equal(Tableau.NonDisponible, VueEnsemble.Texte(VueEnsemble.Total(vide, "ca_jour"), true));
        Assert.Equal(Tableau.NonDisponible, VueEnsemble.Texte(VueEnsemble.Total(TableEcran.Vide, "ca_jour"), true));
    }

    [Fact]
    public void Acteur_poste_de_l_officine_au_journal_des_acces()
    {
        // Lot S7.2b (script central 033) : demandes de code faites par le poste de l'officine
        Assert.Equal("Poste de l’officine", Rubriques.Afficher("acteur", "poste"));
        Assert.Equal("Console du central", Rubriques.Afficher("acteur", "console"));
    }

    [Fact]
    public void Mots_de_passe_et_retours_locaux()
    {
        Assert.NotNull(ComptesDistants.RefusMotDePasse("court1", "a.b"));
        Assert.NotNull(ComptesDistants.RefusMotDePasse("sanschiffresici", "a.b"));
        Assert.NotNull(ComptesDistants.RefusMotDePasse("editeur.dev2026x", "editeur.dev"));
        Assert.NotNull(ComptesDistants.RefusMotDePasse("aaaaaa111111", "a.b"));
        Assert.Null(ComptesDistants.RefusMotDePasse("Clinique-Sud-2026", "editeur.dev"));
        var p = ComptesDistants.MotDePasseProvisoire();
        Assert.Equal(16, p.Length);
        Assert.Equal("/officines", ActionsEcrans.RetourLocal("//exemple.org/x", "/officines"));
        Assert.Equal("/officines", ActionsEcrans.RetourLocal("https://exemple.org", "/officines"));
        Assert.Equal("/officines", ActionsEcrans.RetourLocal(@"/\exemple.org", "/officines"));
        Assert.Equal("/officines/nouvelle", ActionsEcrans.RetourLocal("/officines/nouvelle", "/officines"));
        var (code, empreinte) = ActionsEcrans.CodeActivation(new string('a', 64));
        Assert.EndsWith("." + new string('a', CodeActivationSync.LongueurEpinglage), code);
        Assert.Equal(CodeActivationSync.EmpreinteSecret(CodeActivationSync.Lire(code)!.Value.Secret), empreinte);
    }
}

/// <summary>Architecture des écrans : charte, pas d'API des officines, jamais la clé de l'autorité, pas d'interactivité.</summary>
public class ArchitectureEcransTests
{
    private static string Projet => Path.Combine(Depot.Racine(), "src", "Pharma.Cloud.Web");

    private static IEnumerable<string> Sources(params string[] motifs) =>
        motifs.SelectMany(m => Directory.GetFiles(Projet, m, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    [Fact]
    public void Aucun_style_en_ligne_de_taille_couleur_ou_hauteur()
    {
        var ecarts = Sources("*.razor").Where(f => Regex.IsMatch(File.ReadAllText(f), @"style\s*=\s*""[^""]*(font-size|color|height)", RegexOptions.IgnoreCase))
            .Select(Path.GetFileName).ToList();
        Assert.True(ecarts.Count == 0, "Style en ligne interdit (charte) : " + string.Join(", ", ecarts));
        Assert.DoesNotContain(Sources("*.razor"), f => File.ReadAllText(f).Contains("style=\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Aucune_route_de_l_api_des_officines()
    {
        Assert.DoesNotContain(Sources("*.cs", "*.razor"), f => File.ReadAllText(f).Contains("/api/v1", StringComparison.Ordinal));
        Assert.DoesNotContain(Sources("*.razor"), f => Regex.IsMatch(File.ReadAllText(f), @"@page\s+""/api"));
    }

    [Fact]
    public void Jamais_la_cle_de_l_autorite_ni_le_serveur_central()
    {
        var csproj = File.ReadAllText(Path.Combine(Projet, "Pharma.Cloud.Web.csproj"));
        Assert.DoesNotContain("ProjectReference Include=\"..\\Pharma.Cloud.Api", csproj);
        Assert.DoesNotContain(typeof(ServeurEcrans).Assembly.GetReferencedAssemblies(), a => a.Name == "Pharma.Cloud.Api");
        foreach (var f in Sources("*.cs", "*.razor", "*.json"))
        {
            var texte = File.ReadAllText(f);
            foreach (var interdit in new[] { "autorite.cle", "FichierCle", "ImportFromEncryptedPem", "ImportPkcs8", "jetons.cle", "serveur.pfx", "PHARMA_CLOUD_MOT_DE_PASSE" })
                Assert.False(texte.Contains(interdit, StringComparison.Ordinal), $"{Path.GetFileName(f)} contient « {interdit} »");
        }
        // Seul fichier lié du serveur central : l'envoi SMTP (sans autorité ni base)
        var lies = Regex.Matches(csproj, @"Compile Include=""([^""]+)""").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal([@"..\Pharma.Cloud.Api\Depart\CourrielSmtp.cs"], lies);
        Assert.DoesNotContain("Autorite", File.ReadAllText(Path.Combine(Depot.Racine(), "src", "Pharma.Cloud.Api", "Depart", "CourrielSmtp.cs")));
    }

    [Fact]
    public void Rendu_statique_sans_script_blazor_ni_interactivite()
    {
        foreach (var f in Sources("*.razor", "*.cs"))
        {
            var t = File.ReadAllText(f);
            Assert.False(t.Contains("@rendermode", StringComparison.Ordinal) || t.Contains("blazor.web.js", StringComparison.Ordinal)
                || t.Contains("AddInteractiveServerComponents", StringComparison.Ordinal) || t.Contains("@onclick", StringComparison.Ordinal),
                $"{Path.GetFileName(f)} : interactivité interdite (rendu serveur statique)");
        }
        Assert.Contains("script-src 'self'", ServeurEcrans.Csp);
        Assert.DoesNotContain("unsafe", ServeurEcrans.Csp);
        Assert.StartsWith("__Host-", Jetons.Cookie);
        Assert.StartsWith("__Host-", Jetons.CookieAntiFalsification);
        var o = Jetons.Options();
        Assert.True(o.Secure && o.HttpOnly && o.SameSite == Microsoft.AspNetCore.Http.SameSiteMode.Strict && o.Path == "/" && o.Domain is null);
    }

    [Fact]
    public void Formulaires_post_avec_jeton_anti_falsification()
    {
        foreach (var f in Sources("*.razor"))
        {
            var t = File.ReadAllText(f);
            var formulaires = Regex.Matches(t, @"<form[^>]*method=""post""", RegexOptions.IgnoreCase).Count;
            Assert.True(formulaires <= Regex.Matches(t, "<AntiforgeryToken />").Count, $"{Path.GetFileName(f)} : formulaire POST sans <AntiforgeryToken />");
        }
    }

    [Fact]
    public void Lecture_par_fonctions_cloud_ecran_seulement()
    {
        var depot = File.ReadAllText(Path.Combine(Projet, "Donnees", "DepotEcrans.cs"));
        var tables = new[] { "FROM officine", "FROM certificat", "FROM lot_recu", "FROM ordre_central", "FROM compte_distant", "FROM journal_", "INSERT ", "UPDATE ", "DELETE " };
        foreach (var t in tables) Assert.DoesNotContain(t, depot, StringComparison.OrdinalIgnoreCase);
        Assert.All(Rubriques.Toutes, r => Assert.StartsWith("cloud_ecran_", r.Fonction));
        var fonctions = Regex.Matches(depot, @"FROM (\w+)\(").Select(m => m.Groups[1].Value).Concat(Regex.Matches(depot, @"SELECT (cloud_\w+)\(").Select(m => m.Groups[1].Value));
        Assert.All(fonctions, f => Assert.StartsWith("cloud_ecran_", f));
        Assert.Null(typeof(DepotEcrans).GetMethod("Executer", BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void La_balise_base_est_permise_pour_les_adresses_relatives_des_pages_imbriquees()
    {
        // Constaté le 2026-11-21 : avec base-uri 'none', <base href="/"> était ignorée ; sur /officines/{id}, css/, js/ et img/ étaient
        // cherchés sous /officines/ (404) et le script de l'écran fixe ne tournait pas (rubrique « Lots reçus » défilant de 532 px)
        Assert.Contains("base-uri 'self'", ServeurEcrans.Csp);
        Assert.DoesNotContain("base-uri 'none'", ServeurEcrans.Csp);
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx"))) dossier = dossier.Parent;
        Assert.NotNull(dossier);
        var app = File.ReadAllText(Path.Combine(dossier!.FullName, "src", "Pharma.Cloud.Web", "Composants", "App.razor"));
        Assert.Contains("<base href=\"/\" />", app);
    }
}
