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
using Pharma.Cloud.Web.Securite;

namespace Pharma.Cloud.Web.Tests;

/// <summary>
/// Écrans sur une base centrale JETABLE (supprimée à la fin) : par le dépôt (rôle pharma_cloud_ecrans), puis de bout en bout en
/// HTTPS (serveur des écrans démarré sur un port libre de 127.0.0.1, certificat de test, codes en mode fichier dans un dossier
/// temporaire). Sans PostgreSQL joignable, psql ou droit CREATEDB : rien n'est vérifié ici.
/// </summary>
public class BoutEnBoutEcransTests
{
    private const string MotDePasse = "Essai-Ecrans-2026";
    private static readonly string[] Interdites =
        ["donnees", "contenu", "attendu", "trouve", "cle_ligne", "empreinte_code", "code", "empreinte", "motif_revocation", "motif", "cible", "auteur",
         "fins_chaine", "titulaire_id", "montant_ttc", "telephone", "email", "nom", "identifiant", "empreinte_mot_de_passe"];

    private static async Task<(Guid A, Guid B)> PreparerAsync(BaseCentraleJetable b, string identifiant, bool provisoire)
    {
        var a = Guid.NewGuid();
        var o = Guid.NewGuid();
        await b.ExecuterAsync("INSERT INTO officine (id, libelle) VALUES ($1, 'Essai web A'), ($2, 'Essai web B')", a, o);
        await b.ExecuterAsync("INSERT INTO certificat (empreinte, officine_id, serveur, numero_serie, echeance) VALUES ('abcdef012345' || repeat('e', 52), $1, 'principal', '1', now() + interval '1 year')", a);
        await b.ExecuterAsync("SELECT cloud_compte_distant_creer($1, 'Éditeur essai', 'editeur', $2, NULL, 'e@exemple.invalid', 'tests')",
            identifiant, BCrypt.Net.BCrypt.HashPassword(MotDePasse, 12));
        if (!provisoire) await b.ExecuterAsync("UPDATE compte_distant SET mot_de_passe_provisoire = false WHERE identifiant = $1", identifiant);
        // Données sensibles posées par le propriétaire : jamais rendues aux écrans
        await using var db = NpgsqlDataSource.Create(b.Connexion);
        await using var cnx = await db.OpenConnectionAsync();
        await using var tx = await cnx.BeginTransactionAsync();
        foreach (var sql in new[]
                 {
                     "SELECT set_config('app.officine_id', $1::text, true)",
                     "INSERT INTO cloud_anomalie_integrite (officine_id, journal, cle_chaine, rang, cle_ligne, nature, detail, attendu, trouve) VALUES ($1, 'journal_vente', 'global', 3, 'SECRET_S71', 'chainage_rompu', 'SECRET_S71', 'SECRET_S71', 'SECRET_S71')",
                     "INSERT INTO ordre_central (officine_id, type, cible, donnees, auteur, expire_le) VALUES ($1, 'essai', 'SECRET_S71', '{\"nom\":\"SECRET_S71\"}', 'SECRET_S71', now() + interval '7 days')",
                     // Lot S7.5 : chargement complet terminé et une vente constatée absente (même époque : cause non expliquée) ; jamais montrée
                     "INSERT INTO chargement (officine_id, numero, epoque, repere, motif, tables, etat, fin_le) VALUES ($1, 1, 1, 5, 'activation', '{vente}', 'termine', now())",
                     "INSERT INTO vente (officine_id, id, sync_epoque, sync_numero, numero, statut, sync_absente_le) SELECT $1, 1, 1, 1, 'SECRET_S71', 'validee', fin_le FROM chargement WHERE officine_id = $1",
                 })
        {
            await using var cmd = new NpgsqlCommand(sql, cnx, tx);
            cmd.Parameters.AddWithValue(a);
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return (a, o);
    }

    [Fact]
    public async Task Depot_verrouillage_codes_sessions_et_colonnes_interdites()
    {
        using var b = BaseCentraleJetable.Creer();
        if (b is null) return;
        var (a, _) = await PreparerAsync(b, "editeur.depot", provisoire: false);
        await using var source = NpgsqlDataSource.Create(b.ConnexionEcrans);
        var depot = new DepotEcrans(source);
        var ct = CancellationToken.None;
        Assert.Equal("pharma_cloud_ecrans", await depot.ControlerRoleAsync(ct));
        await using (var proprietaire = NpgsqlDataSource.Create(b.Connexion))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new DepotEcrans(proprietaire).ControlerRoleAsync(ct));   // propriétaire refusé

        // Verrouillage : 5 échecs → verrouillé, empreinte plus rendue
        var (compte, empreinte) = await depot.DebutConnexionAsync("editeur.depot", ct);
        Assert.NotNull(empreinte);
        for (var i = 0; i < 4; i++) Assert.Equal("refuse", await depot.FinConnexionAsync(compte, false, null, ct));
        Assert.Equal("verrouille", await depot.FinConnexionAsync(compte, false, null, ct));
        Assert.Null((await depot.DebutConnexionAsync("editeur.depot", ct)).Empreinte);
        Assert.Equal("refuse", await depot.FinConnexionAsync(null, false, null, ct));
        await b.ExecuterAsync("SELECT cloud_compte_distant_deverrouiller('editeur.depot')");

        // Connexion, code faux, bon code, code réutilisé, code expiré
        var codes = new CodesSecondFacteur();
        var j1 = Jetons.Nouveau();
        Assert.Equal("code_attendu", await depot.FinConnexionAsync(compte, true, Jetons.Empreinte(j1), ct));
        Assert.Equal("envoye", (await depot.CreerCodeAsync(Jetons.Empreinte(j1), "connexion", codes.Empreinte("connexion", "111111")!, "fichier", ct)).Statut);
        var j2 = Jetons.Nouveau();
        Assert.Equal("faux", (await depot.ValiderCodeConnexionAsync(Jetons.Empreinte(j1), codes.Empreinte("connexion", "222222"), Jetons.Empreinte(j2), ct)).Statut);
        Assert.Equal(("ok", "ouverte"), await depot.ValiderCodeConnexionAsync(Jetons.Empreinte(j1), codes.Empreinte("connexion", "111111"), Jetons.Empreinte(j2), ct));
        Assert.Equal("session", (await depot.ValiderCodeConnexionAsync(Jetons.Empreinte(j1), codes.Empreinte("connexion", "111111"), Jetons.Empreinte(Jetons.Nouveau()), ct)).Statut);
        var j3 = Jetons.Nouveau();
        await depot.FinConnexionAsync(compte, true, Jetons.Empreinte(j3), ct);
        await depot.CreerCodeAsync(Jetons.Empreinte(j3), "connexion", codes.Empreinte("connexion", "333333")!, "fichier", ct);
        await b.ExecuterAsync("UPDATE code_second_facteur SET cree_le = now() - interval '10 minutes', expire_le = now() - interval '1 second' WHERE utilise_le IS NULL AND annule_le IS NULL");
        Assert.Equal("expire", (await depot.ValiderCodeConnexionAsync(Jetons.Empreinte(j3), codes.Empreinte("connexion", "333333"), Jetons.Empreinte(Jetons.Nouveau()), ct)).Statut);

        // Lecture : aucune colonne interdite, aucune valeur sensible, cloisonnement par officine
        var e2 = Jetons.Empreinte(j2);
        var tables = new List<TableEcran> { await depot.OfficinesAsync(e2, null, "toutes", 1, 20, ct), await depot.MonJournalAsync(e2, 1, 20, ct) };
        foreach (var r in Rubriques.Toutes) tables.Add((await depot.RubriqueAsync(e2, a, r, 1, 20, ct))!);
        foreach (var t in tables)
        {
            Assert.Empty(t.Colonnes.Intersect(Interdites));
            Assert.DoesNotContain(t.Lignes.SelectMany(l => l), v => v?.ToString()?.Contains("SECRET_S71", StringComparison.Ordinal) == true);
        }
        Assert.Equal(1, (await depot.RubriqueAsync(e2, a, Rubriques.Trouver("anomalies"), 1, 20, ct))!.Total);
        Assert.Null(await depot.RubriqueAsync(e2, Guid.NewGuid(), Rubriques.Trouver("lots"), 1, 20, ct));   // officine inconnue : ouverture refusée

        // Sessions : déconnexion (révocation), expiration
        await depot.DeconnexionAsync(e2, ct);
        Assert.Null(await depot.SessionAsync(e2, ct));
        await Assert.ThrowsAsync<PostgresException>(() => depot.OfficinesAsync(e2, null, "toutes", 1, 20, ct));
        var j4 = Jetons.Nouveau();
        // Script central 040 : une session ouverte n'a plus de durée maximale ; seule l'inactivité réglée (60 min au plus) la ferme
        await b.ExecuterAsync("""
            INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le, derniere_activite_le)
            VALUES ($1, $2, 'ouverte', now() - interval '1 second', now() - interval '61 minutes')
            """, Jetons.Empreinte(j4), compte!.Value);
        Assert.Null(await depot.SessionAsync(Jetons.Empreinte(j4), ct));
        Assert.Equal("inactivite", await b.ScalaireAsync("SELECT motif_fin FROM session_distante WHERE empreinte_jeton = $1", Jetons.Empreinte(j4)));
        Assert.Equal(DBNull.Value, await b.ScalaireAsync("SELECT premiere_rupture FROM cloud_ecran_verifier_journal_acces()"));
    }

    [Fact]
    public async Task Bout_en_bout_https_connexion_mot_de_passe_provisoire_liste_fiche_et_creation()
    {
        using var b = BaseCentraleJetable.Creer();
        if (b is null) return;
        var (a, _) = await PreparerAsync(b, "editeur.web", provisoire: true);
        var dossier = Path.Combine(Path.GetTempPath(), "pharma_ecrans_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dossier);
        try
        {
            // Certificat PUBLIC d'une autorité de test (jamais de clé lue par les écrans)
            using var cleAutorite = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var autorite = new CertificateRequest("CN=Autorite essai", cleAutorite, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var crt = Path.Combine(dossier, "autorite.crt");
            await File.WriteAllTextAsync(crt, autorite.ExportCertificatePem());
            using var cleTls = RSA.Create(2048);
            var demande = new CertificateRequest("CN=127.0.0.1", cleTls, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var noms = new SubjectAlternativeNameBuilder();
            noms.AddIpAddress(IPAddress.Loopback);
            demande.CertificateExtensions.Add(noms.Build());
            using var tlsTemp = demande.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var tls = X509CertificateLoader.LoadPkcs12(tlsTemp.Export(X509ContentType.Pfx), null);

            var codes = Path.Combine(dossier, "codes");
            var reglages = new ReglagesEcrans(IPAddress.Loopback, 0, ModeCodes.Fichier, b.ConnexionEcrans, crt, codes);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ApplicationName = "Pharma.Cloud.Web" });
            await using var app = ServeurEcrans.Construire(builder, reglages, new OptionsServeurEcrans { Certificat = tls });
            await app.StartAsync();
            var adresse = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

            var cookies = new CookieContainer();
            using var client = new HttpClient(new HttpClientHandler
            {
                CookieContainer = cookies, AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, c, _, _) => c?.Thumbprint == tls.Thumbprint
            }) { BaseAddress = new Uri(adresse) };

            // Sans session : renvoi vers la connexion ; en-têtes de sécurité
            var accueil = await client.GetAsync("/officines");
            Assert.Equal("/connexion", accueil.Headers.Location?.OriginalString);
            var connexion = await client.GetAsync("/connexion");
            Assert.Equal(HttpStatusCode.OK, connexion.StatusCode);
            Assert.Contains("no-store", connexion.Headers.CacheControl?.ToString());
            // Valeur posée par les écrans = celle de l'anti-falsification : rien remplacé, aucun avertissement à chaque page
            Assert.True(connexion.Headers.CacheControl is { NoCache: true, NoStore: true });
            Assert.Contains("no-cache", connexion.Headers.Pragma.ToString());
            Assert.Contains("frame-ancestors 'none'", string.Join(' ', connexion.Headers.GetValues("Content-Security-Policy")));

            // Formulaire sans jeton anti-falsification : refusé
            var sansJeton = await client.PostAsync("/actions/connexion", new FormUrlEncodedContent(new Dictionary<string, string> { ["identifiant"] = "editeur.web", ["mot_de_passe"] = MotDePasse }));
            Assert.Equal(HttpStatusCode.BadRequest, sansJeton.StatusCode);

            // Mot de passe faux puis bon : même message d'erreur que pour un identifiant inconnu
            var faux = await Poster(client, "/connexion", "/actions/connexion", new() { ["identifiant"] = "editeur.web", ["mot_de_passe"] = "faux" });
            Assert.Equal("/connexion?erreur=identifiants", faux.Headers.Location?.OriginalString);
            var inconnu = await Poster(client, "/connexion", "/actions/connexion", new() { ["identifiant"] = "personne.web", ["mot_de_passe"] = "faux" });
            Assert.Equal("/connexion?erreur=identifiants", inconnu.Headers.Location?.OriginalString);
            var bon = await Poster(client, "/connexion", "/actions/connexion", new() { ["identifiant"] = "editeur.web", ["mot_de_passe"] = MotDePasse });
            Assert.Equal("/code", bon.Headers.Location?.OriginalString);
            var cookie = bon.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(Jetons.Cookie + "=", StringComparison.Ordinal)).ToLowerInvariant();
            foreach (var attribut in new[] { "path=/", "secure", "samesite=strict", "httponly" }) Assert.Contains(attribut, cookie);
            Assert.DoesNotContain("domain=", cookie);
            Assert.DoesNotContain("expires=", cookie);

            var code = Regex.Match(await File.ReadAllTextAsync(Path.Combine(codes, "editeur.web.txt")), "[0-9]{6}").Value;
            var apresCode = await Poster(client, "/code", "/actions/code", new() { ["code"] = code });
            Assert.Equal("/mot-de-passe", apresCode.Headers.Location?.OriginalString);
            Assert.Equal("/mot-de-passe", (await client.GetAsync("/officines")).Headers.Location?.OriginalString);   // provisoire : rien d'autre

            var change = await Poster(client, "/mot-de-passe", "/actions/mot-de-passe",
                new() { ["actuel"] = MotDePasse, ["nouveau"] = "Nouveau-Essai-2027", ["confirmation"] = "Nouveau-Essai-2027" });
            Assert.Equal("/officines?ok=mdp", change.Headers.Location?.OriginalString);

            var liste = await (await client.GetAsync("/officines?texte=essai+web")).Content.ReadAsStringAsync();
            Assert.Contains("Essai web A", liste);
            Assert.Contains("Mot de passe changé.", await Texte(client, "/officines?ok=mdp"));
            foreach (var r in Rubriques.Toutes)
            {
                var page = await client.GetAsync($"/officines/{a}?rubrique={r.Cle}");
                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                var html = await page.Content.ReadAsStringAsync();
                Assert.DoesNotContain("SECRET_S71", html);
                Assert.DoesNotContain(new string('e', 52), html);
                Assert.DoesNotContain("style=\"", html);
            }
            Assert.Contains("Mon journal des accès", await Texte(client, "/mon-journal"));
            // Lot S7.5 : rubrique « Lignes absentes » : tuiles, groupe (table, n° du chargement, cause), pied de page ; aucune valeur de ligne
            var absentes = (await Texte(client, $"/officines/{a}?rubrique=absentes")).Replace(' ', ' ').Replace(' ', ' ');
            foreach (var attendu in new[] { "Lignes absentes au total", "Tables concernées", "Dernier chargement complet", "Cause probable la plus fréquente",
                                            "n° 1", "Non expliquée", "aucune ligne n'est montrée", "tuiles-absentes" })
                Assert.Contains(attendu, absentes);

            // Créer une officine : code de confirmation, motif, ressaisie du nom ; code d'activation affiché une seule fois
            var demandeCode = await Poster(client, "/officines/nouvelle", "/actions/code-action", new() { ["retour"] = "/officines/nouvelle" });
            Assert.Equal("/officines/nouvelle?ok=code_envoye", demandeCode.Headers.Location?.OriginalString);
            var codeAction = Regex.Match(await File.ReadAllTextAsync(Path.Combine(codes, "editeur.web.txt")), "[0-9]{6}").Value;
            var mauvaise = await Poster(client, "/officines/nouvelle", "/actions/creer-officine",
                new() { ["code"] = codeAction, ["motif"] = "Ouverture essai", ["libelle"] = "Officine créée", ["confirmation"] = "Autre nom", ["serveur"] = "principal" });
            Assert.Equal("/officines/nouvelle?erreur=confirmation", mauvaise.Headers.Location?.OriginalString);
            var creee = await Poster(client, "/officines/nouvelle", "/actions/creer-officine",
                new() { ["code"] = codeAction, ["motif"] = "Ouverture essai", ["libelle"] = "Officine créée", ["confirmation"] = "Officine créée", ["serveur"] = "principal" });
            var vers = creee.Headers.Location!.OriginalString;
            Assert.Matches("^/officines/[0-9a-f-]{36}/code-affiche\\?ref=[0-9a-f]{32}$", vers);
            var affiche = await (await client.GetAsync(vers)).Content.ReadAsStringAsync();
            var activation = Regex.Match(affiche, "[A-Z2-9]{5}-[A-Z2-9]{5}-[A-Z2-9]{5}-[A-Z2-9]{5}\\.[0-9a-f]{32}").Value;
            Assert.NotEmpty(activation);
            Assert.Equal(Pharma.Shared.CodeActivationSync.EmpreinteSecret(Pharma.Shared.CodeActivationSync.Lire(activation)!.Value.Secret),
                await b.ScalaireAsync("SELECT empreinte_code FROM code_activation c JOIN officine o ON o.id = c.officine_id WHERE o.libelle = 'Officine créée'"));
            Assert.DoesNotContain(activation, await (await client.GetAsync(vers)).Content.ReadAsStringAsync());   // une seule fois

            // Déconnexion : le jeton ne vaut plus rien
            var fin = await Poster(client, "/officines", "/actions/deconnexion", []);
            Assert.Equal("/connexion", fin.Headers.Location?.OriginalString);
            Assert.Equal("/connexion", (await client.GetAsync("/officines")).Headers.Location?.OriginalString);
            Assert.Equal(DBNull.Value, await b.ScalaireAsync("SELECT premiere_rupture FROM cloud_ecran_verifier_journal_acces()"));
            await app.StopAsync();
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            Directory.Delete(dossier, true);
        }
    }

    /// <summary>Page décodée (Blazor encode les caractères accentués en entités HTML).</summary>
    private static async Task<string> Texte(HttpClient client, string page) => WebUtility.HtmlDecode(await (await client.GetAsync(page)).Content.ReadAsStringAsync());

    /// <summary>Lit le jeton anti-falsification de la page, puis envoie le formulaire.</summary>
    private static async Task<HttpResponseMessage> Poster(HttpClient client, string page, string action, Dictionary<string, string> champs)
    {
        var html = await (await client.GetAsync(page)).Content.ReadAsStringAsync();
        var jeton = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(jeton);
        champs["__RequestVerificationToken"] = WebUtility.HtmlDecode(jeton);
        return await client.PostAsync(action, new FormUrlEncodedContent(champs));
    }
}
