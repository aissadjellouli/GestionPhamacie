using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pharma.Cloud.Api.Depart;
using Pharma.Cloud.Web.Donnees;
using Pharma.Cloud.Web.Points;
using Pharma.Cloud.Web.Securite;
using Pharma.Shared;

namespace Pharma.Cloud.Web.Tests;

/// <summary>
/// Script central 040 : import de comptes par fichier CSV (aperçu sans code, UN code pour tout le fichier, tout ou rien, liste montrée
/// une fois), identifiant envoyé par e-mail (jamais le mot de passe), durée de vie du mot de passe provisoire réglée par l'éditeur et
/// contrôlée à la connexion, régénération d'un provisoire expiré. Règles pures, puis de bout en bout en HTTPS sur une base JETABLE.
/// </summary>
public class ImportComptesEcransTests
{
    private const string MotDePasse = "Essai-Ecrans-2026";
    private const string AdresseEcrans = "https://ecrans.exemple.invalid/";

    private static SessionEcran S(string role) => new(1, Guid.NewGuid(), "a.b", "A", role, "ouverte", DateTime.UtcNow.AddHours(1), false);

    [Fact]
    public void Acces_a_l_editeur_seulement_et_message_de_chaque_refus()
    {
        foreach (var c in new[] { "/actions/comptes/importer/apercu", "/actions/comptes/importer", "/actions/envoi-codes/duree", "/comptes/mot-de-passe-affiche" })
        {
            Assert.Null(ControleAcces.Redirection(c, S("editeur")));
            Assert.Equal("/acces-refuse", ControleAcces.Redirection(c, S("titulaire")));
            Assert.Equal("/connexion", ControleAcces.Redirection(c, null));
        }
        Assert.Equal(ActionsImportComptes.RouteApercu, "/actions/comptes/importer/apercu");
        foreach (var r in ImportComptesCsv.RefusFichier) Assert.NotNull(Messages.Erreur("import_" + r));
        foreach (var s in new[] { "lignes", "apercu", "existe", "erreur" }) Assert.NotNull(Messages.Erreur("import_" + s));
        foreach (var s in ImportComptesCsv.StatutsLigne) Assert.True(Messages.LignesImport.ContainsKey(s), s);
        Assert.Equal("Mot de passe provisoire expiré : demandez à l'éditeur une remise à zéro.", Messages.Erreur("provisoire_expire"));
        Assert.NotNull(Messages.Erreur("duree_duree"));
        Assert.NotNull(Messages.Reussite("duree_enregistree"));
        // État du mot de passe dans le tableau « Comptes »
        Assert.Equal("provisoire expiré", EtatMotDePasse.Texte("provisoire_expire", DateTime.UtcNow));
        Assert.StartsWith("provisoire (expire le ", EtatMotDePasse.Texte("provisoire", DateTime.UtcNow));
        Assert.Equal("", EtatMotDePasse.Texte(null, null));
        // Le texte copié de la liste ne passe que par la page (jamais un journal) ; la page n'a aucun style en ligne
        var page = File.ReadAllText(Path.Combine(Depot.Racine(), "src", "Pharma.Cloud.Web", "Composants", "Pages", "MotDePasseAffiche.razor"));
        Assert.DoesNotContain("style=", page);
        Assert.Contains("data-copier=\"comptes-transmis-texte\"", page);
        // Seul le dépôt du fichier dépasse 16 Ko
        Assert.True(ActionsImportComptes.TailleRequeteMax > ImportComptesCsv.OctetsMax);
    }

    private sealed class CourrielFactice : IEnvoiCourriel
    {
        public List<(string Adresse, string Objet, string Texte)> Envoyes { get; } = [];

        public Task EnvoyerAsync(string adresse, string objet, string texte, CancellationToken ct)
        {
            lock (Envoyes) Envoyes.Add((adresse, objet, texte));
            return Task.CompletedTask;
        }
    }

    private sealed class SmsFactice : IEnvoiSmsMotDePasse
    {
        public List<(string Telephone, string MotDePasse)> Envoyes { get; } = [];
        public bool Disponible => true;

        public Task EnvoyerAsync(string telephone, string identifiant, string motDePasse, CancellationToken ct)
        {
            lock (Envoyes) Envoyes.Add((telephone, motDePasse));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Bout_en_bout_import_duree_expiration_et_regeneration()
    {
        using var b = BaseCentraleJetable.Creer();
        if (b is null) return;
        var dossier = Path.Combine(Path.GetTempPath(), "pharma_imp_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dossier);
        try
        {
            var empreinte = BCrypt.Net.BCrypt.HashPassword(MotDePasse, 12);
            await b.ExecuterAsync("SELECT cloud_compte_distant_creer('editeur.imp', 'Éditeur essai', 'editeur', $1, NULL, 'eimp@exemple.invalid', 'tests')", empreinte);
            await b.ExecuterAsync("UPDATE compte_distant SET mot_de_passe_provisoire = false");
            var cles = Path.Combine(dossier, "cles-protection");
            // Réglage « Envoi des codes » complet (mot de passe SMTP chiffré avec les clés des écrans) : l'identifiant part par l'envoi factice
            await b.ExecuterAsync("""
                UPDATE parametre_envoi_codes SET canal = 'courriel', smtp_serveur = 'smtp.exemple.invalid', smtp_port = 587, smtp_tls = 'starttls',
                       smtp_identifiant = 'envoi@exemple.invalid', smtp_expediteur = 'envoi@exemple.invalid', smtp_mot_de_passe_chiffre = $1
                """, new ProtectionSmtpCentral(cles).Proteger("mot-de-passe-smtp-essai"));
            var (crt, tls) = Certificats(dossier);
            using var _tls = tls;
            var codes = Path.Combine(dossier, "codes");
            var courriel = new CourrielFactice();
            await using var app = await DemarrerAsync(b, crt, tls, codes, cles, courriel, null);
            using var ed = Client(app, tls);
            await ConnecterAsync(ed, "editeur.imp", MotDePasse, codes, "/officines");

            // Dépôt, confirmation, création et remise à zéro sans jeton anti-falsification : refusés
            using (var brut = new MultipartFormDataContent { { new ByteArrayContent("x"u8.ToArray()), "fichier", "c.csv" } })
                Assert.Equal(HttpStatusCode.BadRequest, (await ed.PostAsync("/actions/comptes/importer/apercu", brut)).StatusCode);
            foreach (var action in new[] { "/actions/comptes/importer", "/actions/comptes/creer", $"/actions/comptes/{Guid.NewGuid()}/mot-de-passe" })
                Assert.Equal(HttpStatusCode.BadRequest, (await ed.PostAsync(action, new FormUrlEncodedContent([new("code", "000000")]))).StatusCode);

            const string page = "/comptes?action=importer";
            var html = await Texte(ed, page);
            Assert.Contains("Lire le fichier (aperçu)", html);
            Assert.Contains("enctype=\"multipart/form-data\"", html);
            // En-tête invalide : refus du fichier entier
            Assert.Equal($"{page}&erreur=import_entete", (await Deposer(ed, page, "role;identifiant\nt;a.b.c\n")).Headers.Location?.OriginalString);
            // Lignes en erreur (identifiant déjà pris, doublon, e-mail) : aperçu sans bouton de création
            var refuse = (await Deposer(ed, page, "role;identifiant;nom;email;telephone\ntitulaire;editeur.imp;Pris;p@exemple.invalid;\n"
                + "titulaire;double.imp;A;a@exemple.invalid;\ntitulaire;double.imp;B;b@exemple.invalid;\ntitulaire;mauvais.imp;C;pas-une-adresse;\n")).Headers.Location?.OriginalString ?? "";
            Assert.Matches("^/comptes\\?action=importer&apercu=[0-9a-f]{32}$", refuse);
            html = await Texte(ed, refuse);
            Assert.Contains("identifiant déjà pris", html);
            Assert.Contains("identifiant déjà présent plus haut dans le fichier", html);
            Assert.Contains("e-mail invalide", html);
            Assert.DoesNotContain("Créer les", html);

            // Fichier valable (BOM, « , », 3 comptes) : aperçu, rien de créé, aucun code consommé
            var csv = "﻿role,identifiant,nom,email,telephone\ntitulaire,import.un,Titulaire Un,un@exemple.invalid,\n"
                      + "éditeur,import.deux,Éditeur Deux,,0555 000 402\ntitulaire,import.trois,\"Trois, Titulaire\",trois@exemple.invalid,+213555000403\n";
            var apercu = (await Deposer(ed, page, csv)).Headers.Location?.OriginalString ?? "";
            var reference = apercu.Split("apercu=")[1];
            html = await Texte(ed, apercu);
            Assert.Contains("Créer les 3 comptes", html);
            Assert.Contains("Trois, Titulaire", html);
            Assert.Contains("name=\"envoyer_identifiant\" value=\"1\" checked", html);
            Assert.Equal(0L, await b.ScalaireAsync("SELECT count(*) FROM compte_distant WHERE identifiant LIKE 'import.%'"));
            // Sans code demandé : refus, rien de créé ; puis UN code pour tout le fichier
            Assert.Equal($"{apercu}&erreur=aucun", (await Poster(ed, apercu, "/actions/comptes/importer",
                new() { ["apercu"] = reference, ["code"] = "000000", ["envoyer_identifiant"] = "1" })).Headers.Location?.OriginalString);
            await Poster(ed, apercu, "/actions/code-action", new() { ["retour"] = apercu });
            var resultat = (await Poster(ed, apercu, "/actions/comptes/importer",
                new() { ["apercu"] = reference, ["code"] = Code(codes, "editeur.imp"), ["envoyer_identifiant"] = "1" })).Headers.Location?.OriginalString ?? "";
            Assert.Matches("^/comptes/mot-de-passe-affiche\\?ref=[0-9a-f]{32}&import=1$", resultat);
            html = await Texte(ed, resultat);
            Assert.Contains("3 comptes créés", html);
            var mdp = Regex.Matches(html, "class=\"mdp\">([^<]+)<").Select(m => m.Groups[1].Value).ToList();
            Assert.Equal(3, mdp.Count);
            Assert.All(mdp, m => Assert.Equal(16, m.Length));
            Assert.Contains("non (aucune adresse)", html);
            Assert.Contains("à transmettre par SMS (manuel)", html);
            Assert.Contains("data-copier=\"comptes-transmis-texte\"", html);
            Assert.Contains($"import.un\tTitulaire\t{mdp[0]}\tidentifiant par e-mail : oui", html);
            Assert.DoesNotContain("style=\"", html);
            Assert.Contains("déjà été affiché", await Texte(ed, resultat));                       // une seule fois
            Assert.Equal($"{page}&erreur=import_apercu", (await Poster(ed, "/comptes", "/actions/comptes/importer",
                new() { ["apercu"] = reference, ["code"] = "000000" })).Headers.Location?.OriginalString);   // aperçu retiré après la création
            // Comptes créés : provisoires, empreintes seulement
            Assert.Equal(3L, await b.ScalaireAsync("SELECT count(*) FROM compte_distant WHERE identifiant LIKE 'import.%' AND mot_de_passe_provisoire"));
            Assert.True(BCrypt.Net.BCrypt.Verify(mdp[0], (string)(await b.ScalaireAsync("SELECT empreinte_mot_de_passe FROM compte_distant WHERE identifiant = 'import.un'"))!));
            Assert.Equal("editeur|0555000402", await b.ScalaireAsync("SELECT role || '|' || telephone FROM compte_distant WHERE identifiant = 'import.deux'"));
            // Identifiant par e-mail (envoi factice) : deux adresses, l'adresse des écrans, JAMAIS le mot de passe
            Assert.Equal(["trois@exemple.invalid", "un@exemple.invalid"], courriel.Envoyes.Where(e => e.Objet == MessageIdentifiant.Objet).Select(e => e.Adresse).Order());
            var mailUn = courriel.Envoyes.Single(e => e.Adresse == "un@exemple.invalid");
            Assert.Contains("Identifiant : import.un", mailUn.Texte);
            Assert.Contains(AdresseEcrans, mailUn.Texte);
            Assert.Contains("Le mot de passe provisoire vous sera transmis séparément", mailUn.Texte);
            foreach (var m in mdp) Assert.DoesNotContain(courriel.Envoyes, e => e.Texte.Contains(m, StringComparison.Ordinal) || e.Objet.Contains(m, StringComparison.Ordinal));
            // Journaux : jamais l'identifiant, le contact ni le mot de passe
            Assert.Equal(0L, await b.ScalaireAsync("""
                SELECT (SELECT count(*) FROM journal_securite WHERE detail LIKE '%import.%' OR detail LIKE '%un@%' OR detail LIKE '%' || $1 || '%')
                     + (SELECT count(*) FROM journal_acces_distant WHERE objet LIKE '%@%' OR objet LIKE '%import.%' OR objet LIKE '%' || $1 || '%')
                """, mdp[0]));
            Assert.Equal(2L, await b.ScalaireAsync("SELECT count(*) FROM journal_acces_distant WHERE evenement = 'identifiant_envoi' AND objet = 'email:ok'"));

            // Durée de vie : hors bornes refusée avant le code, puis enregistrée avec le code
            Assert.Contains("value=\"7\"", await Texte(ed, "/envoi-codes"));
            Assert.Equal("/envoi-codes?erreur=duree_duree", (await Poster(ed, "/envoi-codes", "/actions/envoi-codes/duree",
                new() { ["duree_jours"] = "40", ["code"] = "000000" })).Headers.Location?.OriginalString);
            await Poster(ed, "/envoi-codes", "/actions/code-action", new() { ["retour"] = "/envoi-codes" });
            Assert.Equal("/envoi-codes?ok=duree_enregistree", (await Poster(ed, "/envoi-codes", "/actions/envoi-codes/duree",
                new() { ["duree_jours"] = "3", ["code"] = Code(codes, "editeur.imp") })).Headers.Location?.OriginalString);
            Assert.Equal(3, await b.ScalaireAsync("SELECT duree_provisoire_jours FROM parametre_envoi_codes"));

            // Provisoire non utilisé depuis 4 jours (durée 3) : refusé à la connexion avec un message clair ; mot de passe faux : message habituel
            await b.ExecuterAsync("UPDATE compte_distant SET cree_le = now() - interval '4 days' WHERE identifiant = 'import.un'");
            using (var perime = Client(app, tls))
            {
                Assert.Equal("/connexion?erreur=identifiants", (await Poster(perime, "/connexion", "/actions/connexion",
                    new() { ["identifiant"] = "import.un", ["mot_de_passe"] = "faux-mot-de-passe" })).Headers.Location?.OriginalString);
                Assert.Equal("/connexion?erreur=provisoire_expire", (await Poster(perime, "/connexion", "/actions/connexion",
                    new() { ["identifiant"] = "import.un", ["mot_de_passe"] = mdp[0] })).Headers.Location?.OriginalString);
                Assert.Contains("Mot de passe provisoire expiré : demandez à l'éditeur une remise à zéro.", await Texte(perime, "/connexion?erreur=provisoire_expire"));
            }
            Assert.Equal(1L, await b.ScalaireAsync("SELECT count(*) FROM journal_acces_distant WHERE evenement = 'connexion' AND objet = 'provisoire_expire'"));
            // Tableau « Comptes » : état du mot de passe et lien « Régénérer le mot de passe »
            var un = (Guid)(await b.ScalaireAsync("SELECT id FROM compte_distant WHERE identifiant = 'import.un'"))!;
            html = await Texte(ed, "/comptes");
            Assert.Contains("provisoire expiré", html);
            Assert.Contains("provisoire (expire le ", html);
            Assert.Contains($"comptes?compte={un}&page=1&action=mot-de-passe&motif=provisoire_expire", html);
            await app.StopAsync();

            // Seconde instance (limites de débit neuves), fournisseur SMS factice : régénération du provisoire expiré, puis création unitaire
            // Limite de 5 codes par compte et par quart d'heure (inchangée) : les codes déjà demandés sont vieillis
            await b.ExecuterAsync("UPDATE code_second_facteur SET cree_le = cree_le - interval '20 minutes'");
            var sms = new SmsFactice();
            await using var app2 = await DemarrerAsync(b, crt, tls, codes, cles, courriel, sms);
            using var ed2 = Client(app2, tls);
            await ConnecterAsync(ed2, "editeur.imp", MotDePasse, codes, "/officines");
            var regen = $"/comptes?compte={un}&page=1&action=mot-de-passe&motif=provisoire_expire";
            html = await Texte(ed2, regen);
            Assert.Contains("value=\"Mot de passe provisoire expiré\"", html);                     // motif prérempli, modifiable
            Assert.Contains("name=\"envoyer_identifiant\" value=\"1\" checked", html);
            await Poster(ed2, regen, "/actions/code-action", new() { ["retour"] = regen });
            courriel.Envoyes.Clear();
            var affiche = (await Poster(ed2, regen, $"/actions/comptes/{un}/mot-de-passe", new()
            {
                ["motif"] = "Mot de passe provisoire expiré", ["confirmation"] = "import.un", ["code"] = Code(codes, "editeur.imp"), ["envoyer_identifiant"] = "1",
            })).Headers.Location?.OriginalString ?? "";
            Assert.StartsWith($"/comptes/mot-de-passe-affiche?compte={un}&ref=", affiche);
            html = await Texte(ed2, affiche);
            var nouveau = Regex.Match(html, "id=\"mot-de-passe-provisoire\">([^<]+)<").Groups[1].Value;
            Assert.Equal(16, nouveau.Length);
            Assert.Contains("Mot de passe remis à zéro", html);
            Assert.Contains("<dd>oui</dd>", html);                                                 // identifiant renvoyé par e-mail
            Assert.Contains("à transmettre (manuel, aucun téléphone)", html);
            var mailRemise = Assert.Single(courriel.Envoyes);
            Assert.Contains("remis à zéro", mailRemise.Texte);
            Assert.DoesNotContain(nouveau, mailRemise.Texte);
            // La durée de vie repart à zéro : connexion acceptée, changement du mot de passe exigé
            Assert.Equal(true, await b.ScalaireAsync("SELECT mot_de_passe_change_le > now() - interval '1 minute' FROM compte_distant WHERE id = $1", un));
            using (var apres = Client(app2, tls))
                await ConnecterAsync(apres, "import.un", nouveau, codes, "/mot-de-passe");

            // Création unitaire : case décochée (aucun e-mail), téléphone et fournisseur SMS en service : mot de passe envoyé par SMS
            const string creer = "/comptes?action=creer";
            Assert.Contains("Envoyer l'identifiant par e-mail", await Texte(ed2, creer));
            await Poster(ed2, creer, "/actions/code-action", new() { ["retour"] = creer });
            courriel.Envoyes.Clear();
            var cree = (await Poster(ed2, creer, "/actions/comptes/creer", new()
            {
                ["role"] = "titulaire", ["identifiant"] = "unitaire.imp", ["nom"] = "Unitaire", ["email"] = "u@exemple.invalid", ["telephone"] = "0555 000 499",
                ["code"] = Code(codes, "editeur.imp"),
            })).Headers.Location?.OriginalString ?? "";
            Assert.Matches("^/comptes/mot-de-passe-affiche\\?compte=[0-9a-f-]{36}&ref=[^&]+&cree=1$", cree);
            html = await Texte(ed2, cree);
            var unitaire = Regex.Match(html, "id=\"mot-de-passe-provisoire\">([^<]+)<").Groups[1].Value;
            Assert.Contains("non (case décochée)", html);
            Assert.Contains("envoyé par SMS", html);
            Assert.Empty(courriel.Envoyes);
            Assert.Contains(("0555000499", unitaire), sms.Envoyes);
            Assert.Equal(1L, await b.ScalaireAsync("SELECT count(*) FROM journal_acces_distant WHERE evenement = 'provisoire_envoi' AND objet = 'sms:ok'"));
            Assert.Equal(DBNull.Value, await b.ScalaireAsync("SELECT premiere_rupture FROM cloud_ecran_verifier_journal_acces()"));
            await app2.StopAsync();
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            Directory.Delete(dossier, true);
        }
    }

    [Fact]
    public void Inactivite_regles_de_la_page_et_du_controle_d_acces()
    {
        // Signal d'activité : session ouverte des deux rôles ; déconnexion permise sans session (la base a pu la fermer juste avant)
        Assert.Null(ControleAcces.Redirection(ActionsImportComptes.RouteActivite, S("editeur")));
        Assert.Null(ControleAcces.Redirection(ActionsImportComptes.RouteActivite, S("titulaire")));
        Assert.Equal("/connexion", ControleAcces.Redirection(ActionsImportComptes.RouteActivite, null));
        Assert.Null(ControleAcces.Redirection("/actions/deconnexion", null));
        Assert.Equal("/acces-refuse", ControleAcces.Redirection("/actions/envoi-codes/inactivite", S("titulaire")));
        var maintenant = DateTime.UtcNow;
        Assert.Equal(600, ActionsImportComptes.SecondesRestantes(S("editeur") with { ExpireLe = maintenant.AddMinutes(10) }, maintenant));
        Assert.Equal(0, ActionsImportComptes.SecondesRestantes(S("editeur") with { ExpireLe = maintenant.AddMinutes(-1) }, maintenant));
        Assert.Equal("Session fermée après inactivité : reconnectez-vous.", Messages.Erreur("session_inactivite"));
        foreach (var s in new[] { "inactivite_editeur", "inactivite_titulaire" }) Assert.NotNull(Messages.Erreur(s));
        Assert.NotNull(Messages.Reussite("inactivite_enregistree"));
        // Page : aucun script en ligne ; le signal ne part qu'après une action de l'utilisateur, une fois par minute au plus
        var js = File.ReadAllText(Path.Combine(Depot.Racine(), "src", "Pharma.Cloud.Web", "wwwroot", "js", "ecrans.js"));
        Assert.Contains("data-session-restant", js);
        Assert.Contains("Date.now() - dernierSignal < UNE_MINUTE", js);
        Assert.Contains("\"keydown\", \"mousedown\", \"wheel\", \"touchstart\", \"scroll\"", js);
        Assert.Contains("motif.value = \"inactivite\"", js);
        var mise = File.ReadAllText(Path.Combine(Depot.Racine(), "src", "Pharma.Cloud.Web", "Composants", "Mise", "MiseEnPage.razor"));
        Assert.DoesNotContain("<script", mise);
        Assert.Contains("Rester connecté", mise);
    }

    [Fact]
    public async Task Bout_en_bout_inactivite_reglee_signal_d_activite_et_deconnexion()
    {
        using var b = BaseCentraleJetable.Creer();
        if (b is null) return;
        var dossier = Path.Combine(Path.GetTempPath(), "pharma_inact_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dossier);
        try
        {
            await b.ExecuterAsync("SELECT cloud_compte_distant_creer('editeur.ina', 'Éditeur essai', 'editeur', $1, NULL, 'eina@exemple.invalid', 'tests')",
                BCrypt.Net.BCrypt.HashPassword(MotDePasse, 12));
            await b.ExecuterAsync("UPDATE compte_distant SET mot_de_passe_provisoire = false");
            var (crt, tls) = Certificats(dossier);
            using var _tls = tls;
            var codes = Path.Combine(dossier, "codes");
            await using var app = await DemarrerAsync(b, crt, tls, codes, Path.Combine(dossier, "cles-protection"), new CourrielFactice(), null);
            using var ed = Client(app, tls);
            await ConnecterAsync(ed, "editeur.ina", MotDePasse, codes, "/officines");

            // Page : secondes restantes (15 min par défaut pour l'éditeur), signal d'activité et bandeau (caché)
            var html = await Texte(ed, "/officines");
            var restant = int.Parse(Regex.Match(html, "data-session-restant=\"([0-9]+)\"").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(restant, 14 * 60, 15 * 60);
            Assert.Contains("id=\"form-activite\"", html);
            Assert.Contains("id=\"bandeau-inactivite\"", html);

            // Réglage : hors bornes refusé avant le code, puis enregistré avec le code
            html = await Texte(ed, "/envoi-codes");
            Assert.Contains("Sécurité des sessions", html);
            Assert.Contains("id=\"inactivite_editeur\" name=\"inactivite_editeur\" type=\"number\" min=\"5\" max=\"60\" step=\"1\" value=\"15\"", html);
            Assert.Equal("/envoi-codes?erreur=inactivite_editeur", (await Poster(ed, "/envoi-codes", "/actions/envoi-codes/inactivite",
                new() { ["inactivite_editeur"] = "4", ["inactivite_titulaire"] = "30", ["code"] = "000000" })).Headers.Location?.OriginalString);
            await Poster(ed, "/envoi-codes", "/actions/code-action", new() { ["retour"] = "/envoi-codes" });
            Assert.Equal("/envoi-codes?ok=inactivite_enregistree", (await Poster(ed, "/envoi-codes", "/actions/envoi-codes/inactivite",
                new() { ["inactivite_editeur"] = "10", ["inactivite_titulaire"] = "20", ["code"] = Code(codes, "editeur.ina") })).Headers.Location?.OriginalString);
            Assert.Equal("10|20", await b.ScalaireAsync("SELECT inactivite_editeur_minutes || '|' || inactivite_titulaire_minutes FROM parametre_envoi_codes"));

            // Signal d'activité : jeton anti-falsification exigé ; rend les secondes restantes et note l'activité
            Assert.Equal(HttpStatusCode.BadRequest, (await ed.PostAsync(ActionsImportComptes.RouteActivite, new FormUrlEncodedContent([]))).StatusCode);
            await b.ExecuterAsync("UPDATE session_distante SET derniere_activite_le = now() - interval '8 minutes' WHERE fin_le IS NULL");
            var signal = await Poster(ed, "/officines", ActionsImportComptes.RouteActivite, new());
            Assert.Equal(HttpStatusCode.OK, signal.StatusCode);
            var json = await signal.Content.ReadAsStringAsync();
            var secondes = int.Parse(Regex.Match(json, "\"restant\":([0-9]+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(secondes, 9 * 60, 10 * 60);
            Assert.Equal(true, await b.ScalaireAsync("SELECT bool_and(derniere_activite_le > now() - interval '1 minute') FROM session_distante WHERE fin_le IS NULL"));

            // Utilisateur actif depuis plus de 2 h : jamais déconnecté (plus de durée maximale)
            await b.ExecuterAsync("UPDATE session_distante SET creee_le = now() - interval '5 hours', expire_le = now() - interval '1 hour' WHERE fin_le IS NULL");
            Assert.Equal(HttpStatusCode.OK, (await ed.GetAsync("/officines")).StatusCode);

            // Inactif plus longtemps que la durée réglée : session fermée, renvoyé à la connexion ; inactivité journalisée
            await b.ExecuterAsync("UPDATE session_distante SET derniere_activite_le = now() - interval '11 minutes' WHERE fin_le IS NULL");
            var jeton = await Texte(ed, "/connexion");   // la page de connexion fournit un jeton anti-falsification
            Assert.Equal("/connexion", (await ed.GetAsync("/officines")).Headers.Location?.OriginalString);
            Assert.Equal(1L, await b.ScalaireAsync("SELECT count(*) FROM session_distante WHERE motif_fin = 'inactivite'"));
            Assert.Equal(1L, await b.ScalaireAsync("SELECT count(*) FROM journal_acces_distant WHERE evenement = 'session_expiree' AND objet = 'inactivite'"));
            // La page qui se ferme d'elle-même : déconnexion (sans session) puis message à la connexion
            Assert.Equal("/connexion?erreur=session_inactivite", (await Poster(ed, "/connexion", "/actions/deconnexion",
                new() { ["motif"] = "inactivite" })).Headers.Location?.OriginalString);
            Assert.Contains("Session fermée après inactivité : reconnectez-vous.", await Texte(ed, "/connexion?erreur=session_inactivite"));
            Assert.NotNull(jeton);
            Assert.Equal(DBNull.Value, await b.ScalaireAsync("SELECT premiere_rupture FROM cloud_ecran_verifier_journal_acces()"));
            await app.StopAsync();
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            Directory.Delete(dossier, true);
        }
    }

    // ---- Outils (mêmes que ResteS7EcransTests) ----
    private static async Task<WebApplication> DemarrerAsync(BaseCentraleJetable b, string crt, X509Certificate2 tls, string codes, string cles, CourrielFactice courriel,
        IEnvoiSmsMotDePasse? sms)
    {
        var reglages = new ReglagesEcrans(IPAddress.Loopback, 0, ModeCodes.Fichier, b.ConnexionEcrans, crt, codes, cles, AdresseEcrans);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ApplicationName = "Pharma.Cloud.Web" });
        var app = ServeurEcrans.Construire(builder, reglages, new OptionsServeurEcrans { Certificat = tls, FabriqueCourriel = _ => courriel, SmsMotDePasse = sms });
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

    private static async Task<string> Jeton(HttpClient client, string page)
    {
        var html = await (await client.GetAsync(page)).Content.ReadAsStringAsync();
        var jeton = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(jeton);
        return WebUtility.HtmlDecode(jeton);
    }

    private static async Task<HttpResponseMessage> Poster(HttpClient client, string page, string action, Dictionary<string, string> champs)
    {
        champs["__RequestVerificationToken"] = await Jeton(client, page);
        return await client.PostAsync(action, new FormUrlEncodedContent(champs));
    }

    /// <summary>Dépôt d'un fichier CSV (formulaire multipart, jeton anti-falsification de la page).</summary>
    private static async Task<HttpResponseMessage> Deposer(HttpClient client, string page, string csv)
    {
        var fichier = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        fichier.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        using var contenu = new MultipartFormDataContent
        {
            { new StringContent(await Jeton(client, page)), "__RequestVerificationToken" },
            { fichier, "fichier", "comptes.csv" },
        };
        return await client.PostAsync(ActionsImportComptes.RouteApercu, contenu);
    }
}
