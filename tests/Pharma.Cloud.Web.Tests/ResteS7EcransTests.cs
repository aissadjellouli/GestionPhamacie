using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pharma.Cloud.Web.Codes;
using Pharma.Cloud.Web.Donnees;
using Pharma.Cloud.Web.Points;
using Pharma.Cloud.Web.Securite;
using Pharma.Domain.Reglages;

namespace Pharma.Cloud.Web.Tests;

/// <summary>
/// Reste des lots S7.2 et S7.3 (script central 038) : remise à zéro du mot de passe d'un compte par l'éditeur (montré une fois, jamais
/// au journal, provisoire, sessions fermées, jamais son propre compte), retrait d'un rattachement par le titulaire (accès coupé, ordres
/// annulés), message à afficher sur les postes (bornes contrôlées par l'écran et la base, droit « ordres », non-fuite). Règles pures,
/// puis de bout en bout en HTTPS sur une base centrale JETABLE.
/// </summary>
public class ResteS7EcransTests
{
    private const string MotDePasse = "Essai-Ecrans-2026";

    private static SessionEcran S(string role, Guid? compte = null) => new(1, compte ?? Guid.NewGuid(), "a.b", "A", role, "ouverte", DateTime.UtcNow.AddHours(1), false);

    [Fact]
    public void Acces_remise_a_l_editeur_retrait_et_message_au_titulaire()
    {
        var id = Guid.NewGuid();
        foreach (var c in new[] { "/comptes/mot-de-passe-affiche", $"/actions/comptes/{id}/mot-de-passe", "/actions/comptes/creer" })
        {
            Assert.Null(ControleAcces.Redirection(c, S("editeur")));
            Assert.Equal("/acces-refuse", ControleAcces.Redirection(c, S("titulaire")));
            Assert.Equal("/connexion", ControleAcces.Redirection(c, null));
        }
        foreach (var c in new[] { $"/mes-officines/{id}/retirer", $"/actions/mes-officines/{id}/retrait", $"/actions/mes-officines/{id}/message" })
        {
            Assert.Null(ControleAcces.Redirection(c, S("titulaire")));
            Assert.Equal("/acces-refuse", ControleAcces.Redirection(c, S("editeur")));   // l'éditeur ne retire aucun rattachement
            Assert.Equal("/connexion", ControleAcces.Redirection(c, null));
        }
        Assert.Equal("/acces-refuse", ControleAcces.Redirection($"/actions/mes-officines/{id}/supprimer", S("titulaire")));
        Assert.Equal("/acces-refuse", ControleAcces.Redirection($"/mes-officines/{id}/retrait", S("titulaire")));
    }

    [Fact]
    public void Regles_du_message_identiques_a_la_base_et_messages_de_chaque_refus()
    {
        var sql = File.ReadAllText(Path.Combine(Depot.Racine(), "database", "cloud", "038_remise_mot_de_passe_retrait_message.sql"));
        Assert.Contains($"t ~ '{MessagePostes.MotifInterdits}'", sql);
        Assert.Contains($"t ~* '{MessagePostes.MotifLien}'", sql);
        Assert.Contains($"!~ '{MessagePostes.MotifDate}'", sql);
        Assert.Contains($"length(t) > {MessagePostes.LongueurMax}", sql);
        Assert.Contains($"p_jours NOT BETWEEN {MessagePostes.JoursMin} AND {MessagePostes.JoursMax}", sql);
        Assert.Contains($"interval '{MessagePostes.JoursMax} days'", sql);
        // Chaque statut des fonctions du 038 a un message à l'écran (jamais un texte saisi renvoyé)
        foreach (var s in new[] { "vide", "trop_long", "texte", "lien", "duree", "format", "refuse", "partie", "deja_en_cours", "trop_d_ordres" })
            Assert.NotNull(Messages.Erreur("message_" + s));
        foreach (var s in new[] { "inconnu", "soi_meme", "inactif", "motif", "confirmation" })
            Assert.NotNull(Messages.Erreur("remise_" + s));
        foreach (var s in new[] { "refuse", "confirmation" })
            Assert.NotNull(Messages.Erreur("retrait_" + s));
        foreach (var s in new[] { "identifiant", "existe", "nom", "role", "contact", "email", "telephone" })
            Assert.NotNull(Messages.Erreur("creation_" + s));
        foreach (var s in new[] { "faux", "expire", "epuise", "aucun" }) Assert.NotNull(Messages.Erreur(s));
        Assert.Equal("Message sur les postes", Rubriques.Afficher("type", MessagePostes.TypeOrdre));
        // Le mot de passe provisoire ne s'affiche jamais sur la page du code d'activation
        var page = File.ReadAllText(Path.Combine(Depot.Racine(), "src", "Pharma.Cloud.Web", "Composants", "Pages", "CodeAffiche.razor"));
        Assert.Contains("a.Serveur != CodesAffiches.MotDePasse", page);
    }

    [Fact]
    public async Task Bout_en_bout_remise_a_zero_retrait_et_message()
    {
        using var b = BaseCentraleJetable.Creer();
        if (b is null) return;
        var dossier = Path.Combine(Path.GetTempPath(), "pharma_s7r_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dossier);
        try
        {
            var empreinte = BCrypt.Net.BCrypt.HashPassword(MotDePasse, 12);
            await b.ExecuterAsync("SELECT cloud_compte_distant_creer('editeur.s7r', 'Éditeur essai', 'editeur', $1, NULL, 'e7r@exemple.invalid', 'tests')", empreinte);
            await b.ExecuterAsync("SELECT cloud_compte_distant_creer('titulaire.s7r', 'Titulaire essai', 'titulaire', $1, NULL, 't7r@exemple.invalid', 'tests')", empreinte);
            await b.ExecuterAsync("SELECT cloud_compte_distant_creer('titulaire.b7r', 'Titulaire B', 'titulaire', $1, NULL, 'b7r@exemple.invalid', 'tests')", empreinte);
            await b.ExecuterAsync("UPDATE compte_distant SET mot_de_passe_provisoire = false");
            var (o1, o2) = (Guid.NewGuid(), Guid.NewGuid());
            await b.ExecuterAsync("INSERT INTO officine (id, libelle) VALUES ($1, 'Officine S7R Un'), ($2, 'Officine S7R Deux')", o1, o2);
            await b.ExecuterAsync("""
                INSERT INTO compte_officine (compte_id, officine_id, accorde_par, droits)
                SELECT c.id, x.o, 'tests', '{consulter,journal_acces,ordres}' FROM compte_distant c,
                       (VALUES ('titulaire.s7r', $1::uuid), ('titulaire.b7r', $2::uuid)) x(i, o) WHERE c.identifiant = x.i
                """, o1, o2);
            var titulaire = (Guid)(await b.ScalaireAsync("SELECT id FROM compte_distant WHERE identifiant = 'titulaire.s7r'"))!;
            var editeur = (Guid)(await b.ScalaireAsync("SELECT id FROM compte_distant WHERE identifiant = 'editeur.s7r'"))!;
            var (crt, tls) = Certificats(dossier);
            using var _tls = tls;
            var codes = Path.Combine(dossier, "codes");
            await using var app = await DemarrerAsync(b, crt, tls, codes, Path.Combine(dossier, "cles-protection"));

            // Le titulaire est connecté (sa session sera fermée par la remise à zéro)
            using var avant = Client(app, tls);
            await ConnecterAsync(avant, "titulaire.s7r", MotDePasse, codes, "/mes-officines");

            using var ed = Client(app, tls);
            await ConnecterAsync(ed, "editeur.s7r", MotDePasse, codes, "/officines");
            // Son propre compte : aucun formulaire, refus avant tout
            var propre = await Texte(ed, $"/comptes?compte={editeur}&action=mot-de-passe");
            Assert.Contains("votre propre compte", propre);
            Assert.DoesNotContain("Remettre à zéro le mot de passe", propre);
            Assert.Equal($"/comptes?compte={editeur}&action=mot-de-passe&erreur=remise_soi_meme",
                (await Poster(ed, $"/comptes?compte={titulaire}&action=mot-de-passe", $"/actions/comptes/{editeur}/mot-de-passe",
                    new() { ["motif"] = "Oubli du mot de passe", ["confirmation"] = "editeur.s7r", ["code"] = "000000" })).Headers.Location?.OriginalString);

            // Remise à zéro du titulaire : identifiant mal ressaisi, puis code, puis réussite
            var page = $"/comptes?compte={titulaire}&action=mot-de-passe";
            Assert.Contains("Remettre à zéro le mot de passe", await Texte(ed, page));
            await Poster(ed, page, "/actions/code-action", new() { ["retour"] = page });
            Assert.EndsWith("&erreur=remise_confirmation", (await Poster(ed, page, $"/actions/comptes/{titulaire}/mot-de-passe",
                new() { ["motif"] = "Oubli du mot de passe", ["confirmation"] = "titulaire.b7r", ["code"] = Code(codes, "editeur.s7r") })).Headers.Location?.OriginalString);
            var r = await Poster(ed, page, $"/actions/comptes/{titulaire}/mot-de-passe",
                new() { ["motif"] = "Oubli du mot de passe (appel)", ["confirmation"] = "titulaire.s7r", ["code"] = Code(codes, "editeur.s7r") });
            var affiche = r.Headers.Location?.OriginalString ?? "";
            Assert.StartsWith($"/comptes/mot-de-passe-affiche?compte={titulaire}&ref=", affiche);
            var html = await Texte(ed, affiche);
            var provisoire = Regex.Match(html, "id=\"mot-de-passe-provisoire\">([^<]+)<").Groups[1].Value;
            Assert.Equal(16, provisoire.Length);
            Assert.DoesNotContain("style=\"", html);
            Assert.Contains("déjà été affiché", await Texte(ed, affiche));                        // une seule fois
            Assert.DoesNotContain(provisoire, await Texte(ed, $"/comptes?compte={titulaire}"));
            var compte = (string)(await b.ScalaireAsync("SELECT empreinte_mot_de_passe || '|' || mot_de_passe_provisoire FROM compte_distant WHERE id = $1", titulaire))!;
            Assert.True(BCrypt.Net.BCrypt.Verify(provisoire, compte.Split('|')[0]));
            Assert.EndsWith("|true", compte);
            // Jamais le mot de passe ni son empreinte dans un journal
            Assert.Equal(0L, await b.ScalaireAsync("""
                SELECT (SELECT count(*) FROM journal_securite WHERE detail LIKE '%' || $1 || '%' OR detail LIKE '%$2%')
                     + (SELECT count(*) FROM journal_acces_distant WHERE objet LIKE '%' || $1 || '%')
                """, provisoire));
            Assert.Equal(1L, await b.ScalaireAsync("SELECT count(*) FROM journal_securite WHERE evenement = 'mot_de_passe_remis'"));
            // Session ouverte du titulaire fermée : renvoyé à la connexion
            Assert.Equal("/connexion", (await avant.GetAsync("/mes-officines")).Headers.Location?.OriginalString);
            // Ancien mot de passe refusé ; mot de passe provisoire : changement obligatoire
            using var apres = Client(app, tls);
            Assert.Equal("/connexion?erreur=identifiants", (await Poster(apres, "/connexion", "/actions/connexion",
                new() { ["identifiant"] = "titulaire.s7r", ["mot_de_passe"] = MotDePasse })).Headers.Location?.OriginalString);
            await ConnecterAsync(apres, "titulaire.s7r", provisoire, codes, "/mot-de-passe");
            Assert.Equal("/mot-de-passe", (await apres.GetAsync("/mes-officines")).Headers.Location?.OriginalString);
            const string Nouveau = "Nouveau-Secret-2027";
            Assert.Equal("/mes-officines?ok=mdp", (await Poster(apres, "/mot-de-passe", "/actions/mot-de-passe",
                new() { ["actuel"] = provisoire, ["nouveau"] = Nouveau, ["confirmation"] = Nouveau })).Headers.Location?.OriginalString);

            // Message : hors bornes refusé par l'écran (sans code), puis envoyé avec le code
            var ordres = $"/mes-officines/{o1}/ordres";
            Assert.Contains("Message sur les postes", await Texte(apres, ordres));
            Assert.Equal($"{ordres}?erreur=message_trop_long", (await Poster(apres, ordres, $"/actions/mes-officines/{o1}/message",
                new() { ["texte"] = new string('x', 201), ["jours"] = "2", ["code"] = "000000" })).Headers.Location?.OriginalString);
            Assert.Equal($"{ordres}?erreur=message_lien", (await Poster(apres, ordres, $"/actions/mes-officines/{o1}/message",
                new() { ["texte"] = "Voir www.exemple", ["jours"] = "2", ["code"] = "000000" })).Headers.Location?.OriginalString);
            Assert.Equal($"{ordres}?erreur=message_duree", (await Poster(apres, ordres, $"/actions/mes-officines/{o1}/message",
                new() { ["texte"] = "Inventaire lundi", ["jours"] = "8", ["code"] = "000000" })).Headers.Location?.OriginalString);
            await Poster(apres, ordres, "/actions/code-action", new() { ["retour"] = ordres });
            Assert.Equal($"{ordres}?ok=message_envoye", (await Poster(apres, ordres, $"/actions/mes-officines/{o1}/message",
                new() { ["texte"] = "Inventaire lundi : caisse fermée à 17 h", ["jours"] = "3", ["code"] = Code(codes, "titulaire.s7r") })).Headers.Location?.OriginalString);
            Assert.Contains("Message sur les postes", await Texte(apres, ordres));               // ligne du tableau
            // Non-fuite : B n'envoie rien vers O1
            using var autre = Client(app, tls);
            await ConnecterAsync(autre, "titulaire.b7r", MotDePasse, codes, "/mes-officines");
            await Poster(autre, "/mes-officines", "/actions/code-action", new() { ["retour"] = "/mes-officines" });
            Assert.Equal($"{ordres}?erreur=message_refuse", (await Poster(autre, "/mes-officines", $"/actions/mes-officines/{o1}/message",
                new() { ["texte"] = "Message de B", ["jours"] = "1", ["code"] = Code(codes, "titulaire.b7r") })).Headers.Location?.OriginalString);
            Assert.Contains("Officine introuvable", await Texte(autre, $"/mes-officines/{o1}/retirer"));

            // Retrait par le titulaire : nom mal ressaisi, puis réussite ; accès coupé, message annulé
            var retirer = $"/mes-officines/{o1}/retirer";
            Assert.Contains("Officine S7R Un", await Texte(apres, retirer));
            await Poster(apres, retirer, "/actions/code-action", new() { ["retour"] = retirer });
            Assert.Equal($"{retirer}?erreur=retrait_confirmation", (await Poster(apres, retirer, $"/actions/mes-officines/{o1}/retrait",
                new() { ["confirmation"] = "Officine S7R Deux", ["code"] = Code(codes, "titulaire.s7r") })).Headers.Location?.OriginalString);
            Assert.Equal("/mes-officines?ok=rattachement_retire", (await Poster(apres, retirer, $"/actions/mes-officines/{o1}/retrait",
                new() { ["confirmation"] = "officine s7r un", ["code"] = Code(codes, "titulaire.s7r") })).Headers.Location?.OriginalString);
            Assert.DoesNotContain("Officine S7R Un", await Texte(apres, "/mes-officines"));
            Assert.Contains("Officine introuvable", await Texte(apres, $"/mes-officines/{o1}"));
            // Le message encore ouvert est annulé (et republié) : journaux de sécurité et des accès (ordre_central est sous RLS)
            Assert.Equal(1L, await b.ScalaireAsync("SELECT count(*) FROM journal_securite WHERE evenement = 'ordre_annule' AND officine_id = $1", o1));
            Assert.Equal(1L, await b.ScalaireAsync(
                "SELECT count(*) FROM journal_acces_distant WHERE evenement = 'retrait_rattachement' AND resultat = 'ok' AND objet = 'ordres_annules:1' AND officine_id = $1", o1));
            Assert.Equal(DBNull.Value, await b.ScalaireAsync("SELECT premiere_rupture FROM cloud_ecran_verifier_journal_acces()"));
            await app.StopAsync();

            // Seconde instance (limites de débit neuves) : création d'un compte par l'éditeur, script central 039
            await using var app2 = await DemarrerAsync(b, crt, tls, codes, Path.Combine(dossier, "cles-protection"));
            using var ed2 = Client(app2, tls);
            await ConnecterAsync(ed2, "editeur.s7r", MotDePasse, codes, "/officines");
            // Script central 039 : création d'un compte par l'éditeur (identifiant pris refusé avant le code, puis réussite)
            Assert.Contains("Ajouter un compte", await Texte(ed2, "/comptes"));
            var creer = "/comptes?action=creer";
            Assert.Contains("Créer le compte", await Texte(ed2, creer));
            Assert.Equal($"{creer}&erreur=creation_existe", (await Poster(ed2, creer, "/actions/comptes/creer",
                new() { ["role"] = "titulaire", ["identifiant"] = "titulaire.s7r", ["nom"] = "Doublon", ["email"] = "d@exemple.invalid", ["code"] = "000000" })).Headers.Location?.OriginalString);
            await Poster(ed2, creer, "/actions/code-action", new() { ["retour"] = creer });
            var cree = (await Poster(ed2, creer, "/actions/comptes/creer", new()
            {
                ["role"] = "titulaire", ["identifiant"] = "nouveau.s7r", ["nom"] = "Nouveau titulaire", ["email"] = "n7r@exemple.invalid",
                ["code"] = Code(codes, "editeur.s7r"),
            })).Headers.Location?.OriginalString ?? "";
            Assert.Matches("^/comptes/mot-de-passe-affiche\\?compte=[0-9a-f-]{36}&ref=[^&]+&cree=1$", cree);
            var htmlCree = await Texte(ed2, cree);
            Assert.Contains("Compte créé", htmlCree);
            var provisoireCree = Regex.Match(htmlCree, "id=\"mot-de-passe-provisoire\">([^<]+)<").Groups[1].Value;
            Assert.Equal(16, provisoireCree.Length);
            Assert.Equal("titulaire|n7r@exemple.invalid|true", await b.ScalaireAsync(
                "SELECT role || '|' || email || '|' || mot_de_passe_provisoire FROM compte_distant WHERE identifiant = 'nouveau.s7r'"));
            Assert.True(BCrypt.Net.BCrypt.Verify(provisoireCree, (string)(await b.ScalaireAsync(
                "SELECT empreinte_mot_de_passe FROM compte_distant WHERE identifiant = 'nouveau.s7r'"))!));
            Assert.Equal(0L, await b.ScalaireAsync("""
                SELECT (SELECT count(*) FROM journal_securite WHERE evenement = 'compte_cree' AND (detail LIKE '%@%' OR detail LIKE '%nouveau%'))
                     + (SELECT count(*) FROM journal_acces_distant WHERE objet LIKE '%' || $1 || '%')
                """, provisoireCree));
            Assert.Contains("deux saisies", Messages.Erreur("mdp_confirmation"));   // plus écrasé par le message de la remise à zéro

            Assert.Equal(DBNull.Value, await b.ScalaireAsync("SELECT premiere_rupture FROM cloud_ecran_verifier_journal_acces()"));
            await app2.StopAsync();
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            Directory.Delete(dossier, true);
        }
    }

    // ---- Outils (mêmes que EnvoiCodesEcransTests) ----
    private static async Task<WebApplication> DemarrerAsync(BaseCentraleJetable b, string crt, X509Certificate2 tls, string codes, string cles)
    {
        var reglages = new ReglagesEcrans(IPAddress.Loopback, 0, ModeCodes.Fichier, b.ConnexionEcrans, crt, codes, cles);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ApplicationName = "Pharma.Cloud.Web" });
        var app = ServeurEcrans.Construire(builder, reglages, new OptionsServeurEcrans { Certificat = tls });
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app, X509Certificate2 tls) =>
        new(new HttpClientHandler
        {
            CookieContainer = new CookieContainer(), AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, c, _, _) => c?.Thumbprint == tls.Thumbprint
        }) { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()) };

    private static async Task ConnecterAsync(HttpClient client, string identifiant, string motDePasse, string codes, string attendu)
    {
        var r = await Poster(client, "/connexion", "/actions/connexion", new() { ["identifiant"] = identifiant, ["mot_de_passe"] = motDePasse });
        Assert.Equal("/code", r.Headers.Location?.OriginalString);
        var c = await Poster(client, "/code", "/actions/code", new() { ["code"] = Code(codes, identifiant) });
        Assert.Equal(attendu, c.Headers.Location?.OriginalString);
    }

    private static string Code(string dossier, string identifiant) =>
        Regex.Match(File.ReadAllText(Path.Combine(dossier, identifiant + ".txt")), "[0-9]{6}").Value;

    private static (string Crt, X509Certificate2 Tls) Certificats(string dossier)
    {
        using var cleAutorite = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var autorite = new CertificateRequest("CN=Autorite essai", cleAutorite, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var crt = Path.Combine(dossier, "autorite.crt");
        File.WriteAllText(crt, autorite.ExportCertificatePem());
        using var cleTls = RSA.Create(2048);
        var demande = new CertificateRequest("CN=127.0.0.1", cleTls, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var noms = new SubjectAlternativeNameBuilder();
        noms.AddIpAddress(IPAddress.Loopback);
        demande.CertificateExtensions.Add(noms.Build());
        using var temp = demande.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return (crt, X509CertificateLoader.LoadPkcs12(temp.Export(X509ContentType.Pfx), null));
    }

    private static async Task<string> Texte(HttpClient client, string page) => WebUtility.HtmlDecode(await (await client.GetAsync(page)).Content.ReadAsStringAsync());

    private static async Task<HttpResponseMessage> Poster(HttpClient client, string page, string action, Dictionary<string, string> champs)
    {
        var html = await (await client.GetAsync(page)).Content.ReadAsStringAsync();
        var jeton = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(jeton);
        champs["__RequestVerificationToken"] = WebUtility.HtmlDecode(jeton);
        return await client.PostAsync(action, new FormUrlEncodedContent(champs));
    }
}
