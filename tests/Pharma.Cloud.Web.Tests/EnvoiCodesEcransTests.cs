using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pharma.Cloud.Api.Depart;
using Pharma.Cloud.Web.Codes;
using Pharma.Cloud.Web.Donnees;
using Pharma.Cloud.Web.Securite;

namespace Pharma.Cloud.Web.Tests;

/// <summary>
/// Lot S7.6 (script central 037) : envoi des codes réglé dans l'administration du site. Règles pures (canal respecté, SMS
/// indisponible, les deux, réglage incomplet, accès de l'éditeur seulement), puis de bout en bout en HTTPS sur une base centrale
/// JETABLE : mot de passe chiffré, jamais renvoyé ni écrit au journal, champ vide = inchangé, e-mail d'essai, canal lu en base à
/// chaque envoi, titulaire refusé, contact d'un compte. Aucun e-mail ne part : envoi SMTP factice.
/// </summary>
public class EnvoiCodesEcransTests
{
    private const string MotDePasse = "Essai-Ecrans-2026";
    private const string MotDePasseSmtp = "abcdEFGHijklMNOP";

    private sealed class CourrielFactice : IEnvoiCourriel
    {
        public List<(string Adresse, string Texte)> Envoyes { get; } = [];
        public bool EnPanne { get; set; }

        public Task EnvoyerAsync(string adresse, string objet, string texte, CancellationToken ct)
        {
            if (EnPanne) throw new IOException("serveur injoignable");
            lock (Envoyes) Envoyes.Add((adresse, texte));
            return Task.CompletedTask;
        }
    }

    private static ReglageEnvoiCodes Reglage(string canal, string? chiffre) =>
        new(canal, "smtp.gmail.com", 587, "starttls", "compte@exemple.invalid", "compte@exemple.invalid", chiffre, canal == "sms" || chiffre is not null);

    [Fact]
    public async Task Canal_choisi_respecte_sms_indisponible_les_deux_et_reglage_incomplet()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "pharma_cles_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var protection = new ProtectionSmtpCentral(dossier);
            var chiffre = protection.Proteger(MotDePasseSmtp);
            var factice = new CourrielFactice();
            ReglagesSmtpCentral? vu = null;
            var repli = new EnvoiCodeFichier(Path.Combine(dossier, "codes"), TimeProvider.System);
            ChoixEnvoiCodes Choix(IEnvoiCodeDistant? r, IEnvoiCodeDistant? sms = null) => new(null, r, (_, _) => Task.FromResult<ReglageEnvoiCodes?>(null), protection,
                s => { vu = s; return factice; }, TimeProvider.System, null, sms);
            var d = new DestinataireCode("compte.essai", "c@exemple.invalid", "+213555000001");

            // E-mail : à l'adresse du compte, par le SMTP du réglage (mot de passe déchiffré)
            var (courriel, _) = Choix(null).Construire(Reglage("courriel", chiffre));
            Assert.Equal("email", courriel!.Canal);
            Assert.Equal([new ResultatEnvoiCode("email", "ok")], await EnvoisCodes.EnvoyerAsync(courriel, d, "123456", CancellationToken.None));
            Assert.Equal("c@exemple.invalid", factice.Envoyes.Single().Adresse);
            Assert.Contains("123456", factice.Envoyes.Single().Texte);
            Assert.Equal((MotDePasseSmtp, "smtp.gmail.com", 587), (vu!.MotDePasse, vu.Hote, vu.Port));

            // Aucun fournisseur SMS (revue de code 2026-11-21) : « SMS » et « les deux » déjà en base passent par l'e-mail seul,
            // jamais un canal qui bloquerait toutes les connexions
            Assert.False(Choix(null).SmsDisponible);
            Assert.Equal("email", Choix(null).Construire(Reglage("sms", chiffre)).Envoi!.Canal);
            Assert.Equal("email", Choix(null).Construire(Reglage("les_deux", chiffre)).Envoi!.Canal);

            // Développement (mode fichier) : le code va dans le fichier ET par l'e-mail du réglage complet ; réglage incomplet : fichier seul
            var copie = new EnvoiCodeFichier(Path.Combine(dossier, "copie"), TimeProvider.System);
            ChoixEnvoiCodes ChoixDev(ReglageEnvoiCodes? r) => new(null, repli, (_, _) => Task.FromResult(r), protection,
                s => { vu = s; return factice; }, TimeProvider.System, copieFichier: copie);
            factice.Envoyes.Clear();
            var (dev, _) = await ChoixDev(Reglage("courriel", chiffre)).ChoisirAsync("jeton", CancellationToken.None);
            Assert.Equal("email", dev!.Canal);
            await dev.EnvoyerAsync(d, "246810", CancellationToken.None);
            Assert.Contains("246810", factice.Envoyes.Single().Texte);
            Assert.Contains("246810", await File.ReadAllTextAsync(Path.Combine(dossier, "copie", "compte.essai.txt")));
            factice.EnPanne = true;   // e-mail en panne : le fichier suffit en développement, aucune erreur
            await dev.EnvoyerAsync(d, "135791", CancellationToken.None);
            Assert.Contains("135791", await File.ReadAllTextAsync(Path.Combine(dossier, "copie", "compte.essai.txt")));
            factice.EnPanne = false;
            Assert.Same(copie, (await ChoixDev(Reglage("courriel", null)).ChoisirAsync("jeton", CancellationToken.None)).Envoi);
            Assert.Same(copie, (await ChoixDev(null).ChoisirAsync("jeton", CancellationToken.None)).Envoi);

            // Fournisseur SMS présent (ici en panne) : SMS respecté, refus clair (jamais un faux succès)
            var smsPanne = new SmsEnPanne();
            Assert.True(Choix(null, smsPanne).SmsDisponible);
            var (sms, _) = Choix(null, smsPanne).Construire(Reglage("sms", null));
            Assert.Equal([new ResultatEnvoiCode("sms", "indisponible", nameof(NotSupportedException))], await EnvoisCodes.EnvoyerAsync(sms!, d, "123456", CancellationToken.None));

            // Les deux : le SMS en échec n'empêche pas l'e-mail ; compte sans e-mail : rien ne part
            factice.Envoyes.Clear();
            var (deux, _) = Choix(null, smsPanne).Construire(Reglage("les_deux", chiffre));
            Assert.Equal("les_deux", deux!.Canal);
            var r = await EnvoisCodes.EnvoyerAsync(deux, d, "654321", CancellationToken.None);
            Assert.Equal(["email:ok", "sms:indisponible"], r.Select(x => x.Objet));
            Assert.Single(factice.Envoyes);
            await deux.EnvoyerAsync(d, "654321", CancellationToken.None);   // au moins un canal parti : pas d'erreur
            var sansMail = await EnvoisCodes.EnvoyerAsync(deux, d with { Email = null }, "1", CancellationToken.None);
            Assert.Equal(["email:sans_contact", "sms:indisponible"], sansMail.Select(x => x.Objet));
            await Assert.ThrowsAsync<InvalidOperationException>(() => deux.EnvoyerAsync(d with { Email = null }, "1", CancellationToken.None));
            factice.EnPanne = true;
            Assert.Equal(["email:echec", "sms:indisponible"], (await EnvoisCodes.EnvoyerAsync(deux, d, "1", CancellationToken.None)).Select(x => x.Objet));

            // Réglage incomplet ou illisible : repli (fichier du développement) sinon refus clair en production
            Assert.Equal((null, ChoixEnvoiCodes.RefusIncomplet), Choix(null).Construire(Reglage("courriel", null) with { Complet = false }));
            Assert.Equal((null, ChoixEnvoiCodes.RefusIncomplet), Choix(null).Construire(null));
            Assert.Same(repli, Choix(repli).Construire(Reglage("courriel", null) with { Complet = false }).Envoi);
            var autresCles = new ChoixEnvoiCodes(null, null, (_, _) => Task.FromResult<ReglageEnvoiCodes?>(null), new ProtectionSmtpCentral(dossier + "-autre"),
                _ => factice, TimeProvider.System);
            Assert.Equal((null, ChoixEnvoiCodes.RefusIncomplet), autresCles.Construire(Reglage("courriel", chiffre)));
            var sansCles = new ChoixEnvoiCodes(null, null, (_, _) => Task.FromResult<ReglageEnvoiCodes?>(null), null, _ => factice, TimeProvider.System);
            Assert.Equal((null, ChoixEnvoiCodes.RefusIncomplet), sansCles.Construire(Reglage("courriel", chiffre)));

            // Mode fichier (développement) prioritaire : la base n'est pas lue
            var lu = false;
            var fixe = new ChoixEnvoiCodes(repli, null, (_, _) => { lu = true; return Task.FromResult<ReglageEnvoiCodes?>(null); }, protection, _ => factice, TimeProvider.System);
            Assert.Same(repli, (await fixe.ChoisirAsync(new string('a', 64), CancellationToken.None)).Envoi);
            Assert.False(lu);

            // Jamais le mot de passe dans un texte affichable
            Assert.DoesNotContain(MotDePasseSmtp, Reglage("courriel", chiffre).ToString());
            Assert.DoesNotContain(chiffre, Reglage("courriel", chiffre).ToString());
        }
        finally
        {
            foreach (var x in new[] { dossier, dossier + "-autre" }) if (Directory.Exists(x)) Directory.Delete(x, true);
        }
    }

    [Fact]
    public void Ecrans_et_actions_de_l_envoi_des_codes_reserves_a_l_editeur()
    {
        SessionEcran S(string role) => new(1, Guid.NewGuid(), "a.b", "A", role, "ouverte", DateTime.UtcNow.AddHours(1), false);
        var compte = Guid.NewGuid();
        foreach (var c in new[] { "/envoi-codes", "/comptes", "/actions/envoi-codes", "/actions/envoi-codes/essai", $"/actions/comptes/{compte}/contact" })
        {
            Assert.Null(ControleAcces.Redirection(c, S("editeur")));
            Assert.Equal("/acces-refuse", ControleAcces.Redirection(c, S("titulaire")));
            Assert.Equal("/connexion", ControleAcces.Redirection(c, null));
        }
        // Le mode « sms » au démarrage reste refusé ; « courriel » (ancien nom) = réglage en base
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ecrans:ModeCodes"] = "courriel" }).Build();
        var (r, _) = ReglagesEcrans.Evaluer(false, config, n => n switch
        {
            ReglagesEcrans.VariableConnexion => "Host=localhost;Database=x;Username=y",
            ReglagesEcrans.VariableDossier => @"C:\dossier-central",
            _ => null,
        });
        Assert.Equal(ModeCodes.Reglage, r!.Mode);
        Assert.Equal(Path.Combine(@"C:\dossier-central", "cles-protection"), r.DossierCles);
    }

    [Fact]
    public async Task Bout_en_bout_reglage_chiffre_jamais_rendu_essai_canal_lu_en_base_et_contact_d_un_compte()
    {
        using var b = BaseCentraleJetable.Creer();
        if (b is null) return;
        var dossier = Path.Combine(Path.GetTempPath(), "pharma_envoi_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dossier);
        try
        {
            await b.ExecuterAsync("SELECT cloud_compte_distant_creer('editeur.s76', 'Éditeur essai', 'editeur', $1, NULL, 'e76@exemple.invalid', 'tests')", BCrypt.Net.BCrypt.HashPassword(MotDePasse, 12));
            await b.ExecuterAsync("SELECT cloud_compte_distant_creer('titulaire.s76', 'Titulaire essai', 'titulaire', $1, '0555000076', NULL, 'tests')", BCrypt.Net.BCrypt.HashPassword(MotDePasse, 12));
            await b.ExecuterAsync("UPDATE compte_distant SET mot_de_passe_provisoire = false");
            var (crt, tls) = Certificats(dossier);
            using var _tls = tls;
            var codes = Path.Combine(dossier, "codes");
            var cles = Path.Combine(dossier, "cles-protection");
            var factice = new CourrielFactice();

            // 1. Mode fichier (développement) : l'éditeur règle l'envoi des codes
            await using (var app = await DemarrerAsync(b, crt, tls, codes, cles, ModeCodes.Fichier, factice))
            {
                using var client = Client(app, tls);
                await ConnecterAsync(client, "editeur.s76", () => Code(codes, "editeur.s76"));
                var page = await Texte(client, "/envoi-codes");
                Assert.Contains("Envoi des codes", page);
                Assert.Contains("Non renseigné", page);
                Assert.Contains("Envoi des codes", await Texte(client, "/officines"));   // lien du menu de l'éditeur

                // E-mail d'essai avec les valeurs saisies (rien n'est enregistré), sans code de confirmation
                var essai = await Poster(client, "/envoi-codes", "/actions/envoi-codes/essai", Formulaire("essai@exemple.invalid"));
                Assert.Equal("/envoi-codes?ok=essai_envoye", essai.Headers.Location?.OriginalString);
                Assert.Equal("essai@exemple.invalid", factice.Envoyes.Single().Adresse);
                Assert.Equal(DBNull.Value, await b.ScalaireAsync("SELECT smtp_mot_de_passe_chiffre FROM parametre_envoi_codes"));
                factice.EnPanne = true;
                Assert.Equal("/envoi-codes?erreur=essai_echec", (await Poster(client, "/envoi-codes", "/actions/envoi-codes/essai", Formulaire("essai@exemple.invalid"))).Headers.Location?.OriginalString);
                factice.EnPanne = false;

                // Enregistrer : code de confirmation exigé ; mot de passe CHIFFRÉ en base, jamais réaffiché
                Assert.Equal("/envoi-codes?erreur=aucun", (await Poster(client, "/envoi-codes", "/actions/envoi-codes", Formulaire(code: "000000"))).Headers.Location?.OriginalString);
                await Poster(client, "/envoi-codes", "/actions/code-action", new() { ["retour"] = "/envoi-codes" });
                var ok = await Poster(client, "/envoi-codes", "/actions/envoi-codes", Formulaire(code: Code(codes, "editeur.s76")));
                Assert.Equal("/envoi-codes?ok=envoi_codes_enregistre", ok.Headers.Location?.OriginalString);
                var chiffre = (string)(await b.ScalaireAsync("SELECT smtp_mot_de_passe_chiffre FROM parametre_envoi_codes"))!;
                Assert.DoesNotContain(MotDePasseSmtp, chiffre);
                Assert.Equal(MotDePasseSmtp, new ProtectionSmtpCentral(cles).Deproteger(chiffre));
                page = await Texte(client, "/envoi-codes");
                Assert.DoesNotContain(MotDePasseSmtp, page);
                Assert.DoesNotContain(chiffre, page);
                Assert.Contains("Renseigné : vide = inchangé", page);
                Assert.DoesNotContain("style=\"", page);

                // Champ vide = inchangé (port changé, même texte chiffré)
                await Poster(client, "/envoi-codes", "/actions/code-action", new() { ["retour"] = "/envoi-codes" });
                var sansMdp = Formulaire(code: Code(codes, "editeur.s76"));
                sansMdp["mot_de_passe"] = "";
                sansMdp["port"] = "465";
                sansMdp["tls"] = "ssl";
                Assert.Equal("/envoi-codes?ok=envoi_codes_enregistre", (await Poster(client, "/envoi-codes", "/actions/envoi-codes", sansMdp)).Headers.Location?.OriginalString);
                Assert.Equal(chiffre, await b.ScalaireAsync("SELECT smtp_mot_de_passe_chiffre FROM parametre_envoi_codes WHERE smtp_port = 465 AND smtp_tls = 'ssl'"));

                // E-mail d'essai sans mot de passe saisi : le mot de passe enregistré ne part QUE vers le serveur enregistré
                factice.Envoyes.Clear();
                var essaiMeme = Formulaire("essai@exemple.invalid");
                (essaiMeme["mot_de_passe"], essaiMeme["port"], essaiMeme["tls"]) = ("", "465", "ssl");
                Assert.Equal("/envoi-codes?ok=essai_envoye", (await Poster(client, "/envoi-codes", "/actions/envoi-codes/essai", essaiMeme)).Headers.Location?.OriginalString);
                var essaiAutre = new Dictionary<string, string>(essaiMeme) { ["serveur"] = "smtp.autre.example" };
                Assert.Equal("/envoi-codes?erreur=essai_mot_de_passe", (await Poster(client, "/envoi-codes", "/actions/envoi-codes/essai", essaiAutre)).Headers.Location?.OriginalString);
                var essaiIdent = new Dictionary<string, string>(essaiMeme) { ["identifiant"] = "autre@exemple.invalid" };
                Assert.Equal("/envoi-codes?erreur=essai_mot_de_passe", (await Poster(client, "/envoi-codes", "/actions/envoi-codes/essai", essaiIdent)).Headers.Location?.OriginalString);
                Assert.Single(factice.Envoyes);

                // « SMS » seul sans fournisseur : refusé avant le code (plus aucun code ne partirait), option grisée
                var smsSeul = Formulaire(code: "000000");
                smsSeul["canal"] = "sms";
                Assert.Equal("/envoi-codes?erreur=envoi_codes_sms", (await Poster(client, "/envoi-codes", "/actions/envoi-codes", smsSeul)).Headers.Location?.OriginalString);
                Assert.Matches("<option value=\"sms\"[^>]*disabled", await Texte(client, "/envoi-codes"));

                // Contact d'un compte : e-mail ajouté au titulaire (téléphone gardé) ; compte sans aucun contact refusé
                var titulaire = (Guid)(await b.ScalaireAsync("SELECT id FROM compte_distant WHERE identifiant = 'titulaire.s76'"))!;
                Assert.Contains("***76", await Texte(client, $"/comptes?compte={titulaire}"));
                await Poster(client, $"/comptes?compte={titulaire}", "/actions/code-action", new() { ["retour"] = $"/comptes?compte={titulaire}" });
                var vide = await Poster(client, $"/comptes?compte={titulaire}", $"/actions/comptes/{titulaire}/contact",
                    new() { ["email"] = "", ["telephone"] = "-", ["code"] = Code(codes, "editeur.s76") });
                Assert.Equal($"/comptes?compte={titulaire}&erreur=contact_sans_contact", vide.Headers.Location?.OriginalString);
                var contact = await Poster(client, $"/comptes?compte={titulaire}", $"/actions/comptes/{titulaire}/contact",
                    new() { ["email"] = "t76@exemple.invalid", ["telephone"] = "", ["code"] = Code(codes, "editeur.s76") });
                Assert.Equal($"/comptes?compte={titulaire}&ok=contact_modifie", contact.Headers.Location?.OriginalString);
                Assert.Equal("t76@exemple.invalid|0555000076", await b.ScalaireAsync("SELECT email || '|' || telephone FROM compte_distant WHERE id = $1", titulaire));
                await app.StopAsync();
            }

            // Journaux : jamais le mot de passe (ni en clair ni chiffré), jamais l'adresse
            Assert.Equal(0L, await b.ScalaireAsync("""
                SELECT (SELECT count(*) FROM journal_securite WHERE detail LIKE '%' || $1 || '%' OR detail LIKE '%' || p.smtp_mot_de_passe_chiffre || '%' OR detail LIKE '%@%')
                     + (SELECT count(*) FROM journal_acces_distant WHERE objet LIKE '%' || $1 || '%' OR objet LIKE '%@%')
                FROM parametre_envoi_codes p
                """, MotDePasseSmtp));
            Assert.Equal(DBNull.Value, await b.ScalaireAsync("SELECT premiere_rupture FROM cloud_ecran_verifier_journal_acces()"));

            // 2. Réglage en base (canal lu à chaque envoi) : e-mail, puis SMS indisponible, puis les deux
            // (codes déjà demandés vieillis : 5 au plus par quart d'heure et par compte)
            const string Vieillir = "UPDATE code_second_facteur SET cree_le = now() - interval '2 hours', expire_le = now() - interval '1 hour'";
            await b.ExecuterAsync(Vieillir);
            await b.ExecuterAsync("UPDATE parametre_envoi_codes SET canal = 'courriel', smtp_port = 587, smtp_tls = 'starttls'");
            factice.Envoyes.Clear();
            await using (var app = await DemarrerAsync(b, crt, tls, codes, cles, ModeCodes.Reglage, factice))
            {
                using (var client = Client(app, tls))
                    await ConnecterAsync(client, "editeur.s76", () => Regex.Match(factice.Envoyes.Last().Texte, "[0-9]{6}").Value);
                Assert.Equal("e76@exemple.invalid", factice.Envoyes.Single().Adresse);

                // « SMS » en base (enregistré avant le contrôle) sans fournisseur : l'e-mail prend le relais, la connexion reste possible
                await b.ExecuterAsync("UPDATE parametre_envoi_codes SET canal = 'sms'");
                await b.ExecuterAsync("UPDATE compte_distant SET telephone = '0555000077' WHERE identifiant = 'editeur.s76'");
                app.Services.GetRequiredService<ChoixEnvoiCodes>().Oublier();
                factice.Envoyes.Clear();
                using (var client = Client(app, tls))
                    await ConnecterAsync(client, "editeur.s76", () => Regex.Match(factice.Envoyes.Last().Texte, "[0-9]{6}").Value);
                Assert.Equal("e76@exemple.invalid", factice.Envoyes.Single().Adresse);
                Assert.Equal(0L, await b.ScalaireAsync("SELECT count(*) FROM journal_acces_distant WHERE evenement = 'code_envoi' AND objet = 'sms:indisponible'"));

                // « E-mail et SMS » sans fournisseur : l'e-mail seul
                await b.ExecuterAsync("UPDATE parametre_envoi_codes SET canal = 'les_deux'");
                app.Services.GetRequiredService<ChoixEnvoiCodes>().Oublier();
                factice.Envoyes.Clear();
                using (var client = Client(app, tls))
                    await ConnecterAsync(client, "editeur.s76", () => Regex.Match(factice.Envoyes.Last().Texte, "[0-9]{6}").Value);
                Assert.Single(factice.Envoyes);
                Assert.Equal(0L, await b.ScalaireAsync("SELECT count(*) FROM code_second_facteur WHERE canal = 'les_deux'"));

                // Titulaire : écran et actions de l'envoi des codes refusés
                await b.ExecuterAsync("UPDATE parametre_envoi_codes SET canal = 'courriel'");
                app.Services.GetRequiredService<ChoixEnvoiCodes>().Oublier();
                using (var client = Client(app, tls))
                {
                    await ConnecterAsync(client, "titulaire.s76", () => Regex.Match(factice.Envoyes.Last(e => e.Adresse == "t76@exemple.invalid").Texte, "[0-9]{6}").Value);
                    Assert.Equal("/acces-refuse", (await client.GetAsync("/envoi-codes")).Headers.Location?.OriginalString);
                    Assert.Equal("/acces-refuse", (await client.GetAsync("/comptes")).Headers.Location?.OriginalString);
                }

                // Réglage incomplet en développement : repli sur le fichier des codes
                await b.ExecuterAsync(Vieillir);
                await b.ExecuterAsync("UPDATE parametre_envoi_codes SET smtp_mot_de_passe_chiffre = NULL");
                app.Services.GetRequiredService<ChoixEnvoiCodes>().Oublier();
                File.Delete(Path.Combine(codes, "editeur.s76.txt"));
                using (var client = Client(app, tls))
                    await ConnecterAsync(client, "editeur.s76", () => Code(codes, "editeur.s76"));
                await app.StopAsync();
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            Directory.Delete(dossier, true);
        }
    }

    private static Dictionary<string, string> Formulaire(string? essai = null, string code = "") => new()
    {
        ["canal"] = "courriel", ["serveur"] = "smtp.gmail.com", ["port"] = "587", ["tls"] = "starttls", ["identifiant"] = "compte@exemple.invalid",
        ["expediteur"] = "compte@exemple.invalid", ["mot_de_passe"] = MotDePasseSmtp, ["code"] = code, ["adresse_essai"] = essai ?? "",
    };

    private static async Task<WebApplication> DemarrerAsync(BaseCentraleJetable b, string crt, X509Certificate2 tls, string codes, string cles, ModeCodes mode,
        CourrielFactice factice)
    {
        var reglages = new ReglagesEcrans(IPAddress.Loopback, 0, mode, b.ConnexionEcrans, crt, codes, cles);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ApplicationName = "Pharma.Cloud.Web" });
        var app = ServeurEcrans.Construire(builder, reglages, new OptionsServeurEcrans { Certificat = tls, FabriqueCourriel = _ => factice });
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app, X509Certificate2 tls) =>
        new(new HttpClientHandler
        {
            CookieContainer = new CookieContainer(), AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, c, _, _) => c?.Thumbprint == tls.Thumbprint
        }) { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()) };

    private static async Task ConnecterAsync(HttpClient client, string identifiant, Func<string> code)
    {
        var r = await Poster(client, "/connexion", "/actions/connexion", new() { ["identifiant"] = identifiant, ["mot_de_passe"] = MotDePasse });
        Assert.Equal("/code", r.Headers.Location?.OriginalString);
        var c = await Poster(client, "/code", "/actions/code", new() { ["code"] = code() });
        Assert.Matches("^/(officines|mes-officines)$", c.Headers.Location?.OriginalString ?? "");
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

    /// <summary>Fournisseur SMS présent mais en panne (refus NotSupportedException, comme l'envoi indisponible).</summary>
    private sealed class SmsEnPanne : IEnvoiCodeDistant
    {
        public string Canal => "sms";

        public Task EnvoyerAsync(DestinataireCode destinataire, string code, CancellationToken ct) =>
            throw new NotSupportedException("SMS en panne (essai).");
    }
}
