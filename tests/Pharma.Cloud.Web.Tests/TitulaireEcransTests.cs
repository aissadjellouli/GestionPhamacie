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
using Pharma.Shared;

namespace Pharma.Cloud.Web.Tests;

/// <summary>
/// Lot S7.2a (script central 032) : écrans du titulaire sur une base centrale JETABLE (supprimée à la fin). Deux titulaires, deux
/// officines : non-fuite, rattachement par code, rôles croisés refusés (l'éditeur n'a aucun écran du titulaire et inversement),
/// agrégats sans colonne nominative, « non disponible » quand la donnée n'est pas montée. Sans PostgreSQL, psql ou CREATEDB : rien.
/// </summary>
public class TitulaireEcransTests
{
    private const string MotDePasse = "Essai-Titulaire-2026";

    /// <summary>Colonnes nominatives ou sensibles : jamais rendues par une fonction du titulaire.</summary>
    private static readonly string[] Interdites =
        ["donnees", "contenu", "attendu", "trouve", "cle_ligne", "empreinte_code", "code", "empreinte", "motif", "cible", "auteur", "telephone", "email",
         "nom", "identifiant", "empreinte_mot_de_passe", "patient_id", "utilisateur_id", "nom_complet", "numero", "message", "titulaire", "adresse",
         "compte_id", "cree_par", "accorde_par", "produit_id", "lot_id"];

    private sealed record Essai(Guid A, Guid B, Guid CompteA, Guid CompteB, Guid Editeur);

    /// <summary>Officines A (chargement complet terminé, ventes, retour, crédit) et B (ventes reçues sans chargement terminé) ; deux titulaires, un éditeur.</summary>
    private static async Task<Essai> PreparerAsync(BaseCentraleJetable b, string prefixe)
    {
        var a = Guid.NewGuid();
        var o = Guid.NewGuid();
        await b.ExecuterAsync("INSERT INTO officine (id, libelle) VALUES ($1, 'Titulaire essai A'), ($2, 'Titulaire essai B')", a, o);
        await b.ExecuterAsync("INSERT INTO certificat (empreinte, officine_id, serveur, numero_serie, echeance) VALUES (repeat('a', 63) || '7', $1, 'principal', '1', now() + interval '1 year')", a);
        var empreinte = BCrypt.Net.BCrypt.HashPassword(MotDePasse, 12);
        foreach (var (id, role) in new[] { ($"{prefixe}.a", "titulaire"), ($"{prefixe}.b", "titulaire"), ($"{prefixe}.ed", "editeur") })
            await b.ExecuterAsync($"SELECT cloud_compte_distant_creer($1, 'Compte essai', '{role}', $2, NULL, 'x@exemple.invalid', 'tests')", id, empreinte);
        await b.ExecuterAsync("UPDATE compte_distant SET mot_de_passe_provisoire = false");
        await using var db = NpgsqlDataSource.Create(b.Connexion);
        await Officine(db, a,
            "INSERT INTO chargement (officine_id, numero, epoque, repere, motif, tables, etat, fin_le) VALUES ($1, 1, 1, 0, 'activation', '{vente,retour_client,patient,alerte}', 'termine', now())",
            "INSERT INTO officine_epoque (officine_id, epoque, dernier_lot, dernier_recu_le) VALUES ($1, 1, 5, now())",
            "INSERT INTO vente (officine_id, id, sync_epoque, sync_numero, numero, date_vente, montant_total, part_organisme, canal_organisme, statut, patient_id) VALUES ($1, 1, 1, 1, 'NOMINATIF_S72', now(), 1200, 700, 'chifa', 'validee', 'P-NOMINATIF_S72'), ($1, 2, 1, 2, 'T2', now(), 300, 0, 'logiciel', 'annulee', NULL)",
            "INSERT INTO retour_client (officine_id, id, sync_epoque, sync_numero, vente_id, montant_rembourse, cree_le, motif_texte) VALUES ($1, 1, 1, 3, 1, 200, now(), 'NOMINATIF_S72')",
            "INSERT INTO patient (officine_id, id, sync_epoque, sync_numero, solde_credit) VALUES ($1, 1, 1, 4, 450)",
            "INSERT INTO alerte (officine_id, id, sync_epoque, sync_numero, type, niveau, statut, message) VALUES ($1, 1, 1, 5, 'rupture', 'urgent', 'nouvelle', 'NOMINATIF_S72')");
        await Officine(db, o,
            "INSERT INTO vente (officine_id, id, sync_epoque, sync_numero, numero, date_vente, montant_total, part_organisme, canal_organisme, statut) VALUES ($1, 1, 1, 1, 'B1', now(), 98765.43, 9876.54, 'chifa', 'validee')",
            "INSERT INTO patient (officine_id, id, sync_epoque, sync_numero, solde_credit) VALUES ($1, 1, 1, 2, 87654.32)");
        async Task<Guid> Compte(string id) => (Guid)(await b.ScalaireAsync("SELECT id FROM compte_distant WHERE identifiant = $1", id))!;
        return new Essai(a, o, await Compte($"{prefixe}.a"), await Compte($"{prefixe}.b"), await Compte($"{prefixe}.ed"));
    }

    /// <summary>Lignes posées par le propriétaire pour une officine (officine courante posée dans la transaction : RLS forcée).</summary>
    private static async Task Officine(NpgsqlDataSource db, Guid officine, params string[] requetes)
    {
        await using var cnx = await db.OpenConnectionAsync();
        await using var tx = await cnx.BeginTransactionAsync();
        foreach (var sql in requetes.Prepend("SELECT set_config('app.officine_id', $1::text, true)"))
        {
            await using var cmd = new NpgsqlCommand(sql, cnx, tx);
            cmd.Parameters.AddWithValue(officine);
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    /// <summary>Code de rattachement créé comme par la console (empreinte seule en base) ; rend le code en clair.</summary>
    private static async Task<string> CodeAsync(BaseCentraleJetable b, string identifiant, Guid officine)
    {
        var code = ComptesDistants.CodeRattachement();
        Assert.Equal("ok", await b.ScalaireAsync("SELECT statut FROM cloud_compte_distant_code_rattachement($1, $2, $3, 'tests')",
            identifiant, officine, ComptesDistants.EmpreinteCodeRattachement(code)!));
        return code;
    }

    private static async Task<string> SessionAsync(BaseCentraleJetable b, Guid compte)
    {
        var jeton = Jetons.Nouveau();
        await b.ExecuterAsync("INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le) VALUES ($1, $2, 'ouverte', now() + interval '1 hour')",
            Jetons.Empreinte(jeton), compte);
        return Jetons.Empreinte(jeton);
    }

    [Fact]
    public async Task Depot_rattachement_non_fuite_roles_croises_et_agregats()
    {
        using var b = BaseCentraleJetable.Creer();
        if (b is null) return;
        var e = await PreparerAsync(b, "tit.depot");
        await using var source = NpgsqlDataSource.Create(b.ConnexionEcrans);
        var depot = new DepotEcrans(source);
        var ct = CancellationToken.None;
        var (sa, sb, se) = (await SessionAsync(b, e.CompteA), await SessionAsync(b, e.CompteB), await SessionAsync(b, e.Editeur));

        // Rattachement : code d'un autre compte (même réponse qu'inconnu), saisie invalide, bon code, usage unique, code expiré
        var codeA = await CodeAsync(b, "tit.depot.a", e.A);
        var codeB = await CodeAsync(b, "tit.depot.b", e.B);
        Assert.Equal("inconnu", (await depot.RattacherAsync(sb, ComptesDistants.EmpreinteCodeRattachement(codeA), ct)).Statut);
        Assert.Equal("inconnu", (await depot.RattacherAsync(sa, ComptesDistants.EmpreinteCodeRattachement("pas un code"), ct)).Statut);
        Assert.Equal(("ok", (Guid?)e.A), await depot.RattacherAsync(sa, ComptesDistants.EmpreinteCodeRattachement(codeA.ToLowerInvariant()), ct));
        Assert.Equal("utilise", (await depot.RattacherAsync(sa, ComptesDistants.EmpreinteCodeRattachement(codeA), ct)).Statut);
        await b.ExecuterAsync("UPDATE code_rattachement SET cree_le = now() - interval '40 minutes', expire_le = now() - interval '10 minutes' WHERE officine_id = $1", e.B);
        Assert.Equal("expire", (await depot.RattacherAsync(sb, ComptesDistants.EmpreinteCodeRattachement(codeB), ct)).Statut);
        var codeB2 = await CodeAsync(b, "tit.depot.b", e.B);
        Assert.Equal("ok", (await depot.RattacherAsync(sb, ComptesDistants.EmpreinteCodeRattachement(codeB2), ct)).Statut);

        // Mes officines : chacun la sienne seulement
        var listeA = await depot.TitulaireOfficinesAsync(sa, ct);
        Assert.Equal(new[] { e.A }, listeA.Lignes.Select(l => (Guid)listeA.Valeur(l, "id")!));
        var listeB = await depot.TitulaireOfficinesAsync(sb, ct);
        Assert.Equal(new[] { e.B }, listeB.Lignes.Select(l => (Guid)listeB.Valeur(l, "id")!));

        // Tableau de A : agrégats exacts ; B ne fuit jamais ; B sans chargement terminé : non disponible (NULL)
        var t = await depot.TitulaireTableauAsync(sa, e.A, ct);
        var ligne = Assert.Single(t.Lignes);
        Assert.Equal(1000m, t.Valeur(ligne, "ca_jour"));            // 1 200 validée − 200 de retour, sans l'annulée
        Assert.Equal(1, t.Valeur(ligne, "ventes_jour"));
        Assert.Equal(700m, t.Valeur(ligne, "part_chifa_mois"));
        Assert.Equal(450m, t.Valeur(ligne, "credits_en_cours"));
        Assert.Equal(1, t.Valeur(ligne, "alertes_stock"));
        Assert.Equal(5L, t.Valeur(ligne, "dernier_lot"));
        var tb = await depot.TitulaireTableauAsync(sb, e.B, ct);
        var ligneB = Assert.Single(tb.Lignes);
        foreach (var c in new[] { "ca_jour", "ventes_jour", "ca_mois", "ca_mois_precedent", "part_chifa_mois", "credits_en_cours", "alertes_stock", "alertes_peremption" })
            Assert.Null(tb.Valeur(ligneB, c));
        Assert.Empty((await depot.TitulaireTableauAsync(sa, e.B, ct)).Lignes);
        Assert.Empty((await depot.TitulaireTableauAsync(sb, e.A, ct)).Lignes);
        Assert.Empty((await depot.TitulaireJournalAsync(sa, e.B, 1, 20, ct)).Lignes);
        Assert.Empty((await depot.TitulaireTableauAsync(sa, Guid.NewGuid(), ct)).Lignes);

        // Agrégats sans colonne nominative, aucune valeur nominative ni valeur de l'autre officine
        var tables = new[] { t, listeA, await depot.TitulaireJournalAsync(sa, e.A, 1, 100, ct) };
        foreach (var x in tables)
        {
            Assert.Empty(x.Colonnes.Intersect(Interdites));
            var valeurs = x.Lignes.SelectMany(l => l).Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "").ToList();
            Assert.DoesNotContain(valeurs, v => v.Contains("NOMINATIF_S72", StringComparison.Ordinal) || v.Contains("tit.depot", StringComparison.Ordinal));
            Assert.DoesNotContain(valeurs, v => v.Contains("98765.43", StringComparison.Ordinal) || v.Contains("87654.32", StringComparison.Ordinal));
        }
        Assert.Contains(tables[2].Lignes, l => (string?)tables[2].Valeur(l, "acteur") == "vous" && (string?)tables[2].Valeur(l, "evenement") == "rattachement");

        // Rôles croisés : l'éditeur n'a aucune fonction du titulaire ; le titulaire aucune fonction de l'éditeur (refus de la base, 42501)
        foreach (var appel in new Func<Task>[]
                 {
                     () => depot.TitulaireOfficinesAsync(se, ct), () => depot.TitulaireTableauAsync(se, e.A, ct), () => depot.TitulaireJournalAsync(se, e.A, 1, 20, ct),
                     () => depot.RattacherAsync(se, ComptesDistants.EmpreinteCodeRattachement(codeA), ct),
                     () => depot.OfficinesAsync(sa, null, "toutes", 1, 20, ct), () => depot.RubriqueAsync(sa, e.A, Rubriques.Trouver("lots"), 1, 20, ct),
                 })
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(appel)).SqlState);
        Assert.Equal(DBNull.Value, await b.ScalaireAsync("SELECT premiere_rupture FROM cloud_ecran_verifier_journal_acces()"));
    }

    [Fact]
    public async Task Bout_en_bout_https_titulaire_rattachement_tableau_journal_et_ecrans_editeur_refuses()
    {
        using var b = BaseCentraleJetable.Creer();
        if (b is null) return;
        var e = await PreparerAsync(b, "tit.web");
        var dossier = Path.Combine(Path.GetTempPath(), "pharma_titulaire_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dossier);
        try
        {
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
            using var client = new HttpClient(new HttpClientHandler
            {
                CookieContainer = new CookieContainer(), AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, c, _, _) => c?.Thumbprint == tls.Thumbprint
            }) { BaseAddress = new Uri(adresse) };

            // Connexion du titulaire A : mot de passe, code, puis accueil « Mes officines »
            var bon = await Poster(client, "/connexion", "/actions/connexion", new() { ["identifiant"] = "tit.web.a", ["mot_de_passe"] = MotDePasse });
            Assert.Equal("/code", bon.Headers.Location?.OriginalString);
            var code = Regex.Match(await File.ReadAllTextAsync(Path.Combine(codes, "tit.web.a.txt")), "[0-9]{6}").Value;
            Assert.Equal("/mes-officines", (await Poster(client, "/code", "/actions/code", new() { ["code"] = code })).Headers.Location?.OriginalString);
            Assert.Contains("Aucune officine rattachée", await Texte(client, "/mes-officines"));

            // Rattachement : code faux, puis bon code (saisi en minuscules avec des espaces)
            var faux = await Poster(client, "/rattacher", "/actions/rattacher", new() { ["code"] = "AAAA-BBBB-CCCC" });
            Assert.Equal("/rattacher?erreur=rattachement_inconnu", faux.Headers.Location?.OriginalString);
            Assert.Contains("Code de rattachement inconnu", await Texte(client, "/rattacher?erreur=rattachement_inconnu"));
            var codeA = await CodeAsync(b, "tit.web.a", e.A);
            var ok = await Poster(client, "/rattacher", "/actions/rattacher", new() { ["code"] = codeA.ToLowerInvariant().Replace('-', ' ') });
            Assert.Equal($"/mes-officines/{e.A}?ok=rattachee", ok.Headers.Location?.OriginalString);

            // Tableau de bord : tuiles, montants, aucune donnée nominative ni de l'autre officine ; aucun style en ligne
            var tableau = (await Texte(client, $"/mes-officines/{e.A}?ok=rattachee")).Replace(' ', ' ').Replace(' ', ' ');
            Assert.Contains("Officine rattachée", tableau);
            Assert.Contains("class=\"tuiles tuiles-compactes\"", tableau);
            Assert.Contains("1 000,00 DA", tableau);
            Assert.Contains("450,00 DA", tableau);
            Assert.DoesNotContain("NOMINATIF_S72", tableau);
            Assert.DoesNotContain("98 765,43", tableau);
            Assert.DoesNotContain("style=\"", tableau);
            Assert.Contains("Titulaire essai A", await Texte(client, "/mes-officines"));
            Assert.Contains("Vous", await Texte(client, $"/mes-officines/{e.A}/journal"));

            // Lot S7.3 : vue d'ensemble (officines du compte seulement, total du groupe) ; ordres refusés sans le droit « ordres »
            var ensemble = (await Texte(client, "/mes-officines/ensemble")).Replace(' ', ' ').Replace(' ', ' ');
            Assert.Contains("Total du groupe", ensemble);
            Assert.Contains("1 000,00 DA", ensemble);
            Assert.DoesNotContain("98 765", ensemble);
            Assert.DoesNotContain("NOMINATIF_S72", ensemble);
            Assert.DoesNotContain("style=\"", ensemble);
            Assert.Contains("droit « ordres » non accordé", await Texte(client, $"/mes-officines/{e.A}/ordres"));
            // Droit accordé (au poste en service ; ici par le propriétaire) : page des ordres, code de confirmation, ordre envoyé
            Assert.Equal(1L, await b.ScalaireAsync("WITH u AS (UPDATE compte_officine SET droits = '{consulter,journal_acces,ordres}' WHERE officine_id = $1 RETURNING 1) SELECT count(*) FROM u", e.A));
            Assert.Contains("Aucun ordre envoyé", await Texte(client, $"/mes-officines/{e.A}/ordres"));
            var hors = await Poster(client, $"/mes-officines/{e.A}/ordres", $"/actions/mes-officines/{e.A}/ordre", new() { ["type"] = "prix_produit", ["code"] = "000000" });
            Assert.Equal($"/mes-officines/{e.A}/ordres?erreur=ordre_hors_liste", hors.Headers.Location?.OriginalString);
            var demandeCode = await Poster(client, $"/mes-officines/{e.A}/ordres", "/actions/code-action", new() { ["retour"] = $"/mes-officines/{e.A}/ordres" });
            Assert.Equal($"/mes-officines/{e.A}/ordres?ok=code_envoye", demandeCode.Headers.Location?.OriginalString);
            var codeOrdre = Regex.Match(await File.ReadAllTextAsync(Path.Combine(codes, "tit.web.a.txt")), "[0-9]{6}").Value;
            var envoi = await Poster(client, $"/mes-officines/{e.A}/ordres", $"/actions/mes-officines/{e.A}/ordre", new() { ["type"] = "sauvegarde", ["code"] = codeOrdre });
            Assert.Equal($"/mes-officines/{e.A}/ordres?ok=ordre_envoye", envoi.Headers.Location?.OriginalString);
            var ordres = await Texte(client, $"/mes-officines/{e.A}/ordres?ok=ordre_envoye");
            Assert.Contains("Demander une sauvegarde", ordres);
            Assert.Contains("En attente", ordres);
            Assert.DoesNotContain("style=\"", ordres);
            // Officine d'un autre titulaire : refus
            var autreOrdre = await Poster(client, $"/mes-officines/{e.A}/ordres", $"/actions/mes-officines/{e.B}/ordre", new() { ["type"] = "essai", ["code"] = "000000" });
            Assert.Equal($"/mes-officines/{e.B}/ordres?erreur=ordre_refuse", autreOrdre.Headers.Location?.OriginalString);
            Assert.Equal(1L, await b.ScalaireAsync("SELECT count(*) FROM journal_acces_distant WHERE evenement = 'ordre_titulaire' AND resultat = 'ok' AND officine_id = $1", e.A));

            // Lot S7.4 : réglages communs (liste fermée, droit « ordres » sur chaque officine, tout ou rien, code de confirmation)
            var pageReglages = await Texte(client, "/mes-officines/reglages");
            Assert.Contains("Réglages communs", pageReglages);
            Assert.Contains($"value=\"{e.A}\"", pageReglages);
            Assert.DoesNotContain($"value=\"{e.B}\"", pageReglages);
            Assert.DoesNotContain("style=\"", pageReglages);
            var horsListe = await Poster(client, "/mes-officines/reglages", "/actions/mes-officines/reglages",
                new() { ["reglage"] = "taux_tva", ["valeur"] = "19", ["officine"] = e.A.ToString(), ["code"] = "000000" });
            Assert.Equal($"/mes-officines/reglages?o={e.A}&erreur=reglages_hors_liste", horsListe.Headers.Location?.OriginalString);
            var horsBornes = await Poster(client, "/mes-officines/reglages", "/actions/mes-officines/reglages",
                new() { ["r.jours_orange"] = "5000", ["officine"] = e.A.ToString(), ["code"] = "000000" });
            Assert.Equal($"/mes-officines/reglages?v=jours_orange:5000&o={e.A}&erreur=reglages_hors_bornes", horsBornes.Headers.Location?.OriginalString);
            var versB = await Poster(client, "/mes-officines/reglages", "/actions/mes-officines/reglages",
                new() { ["r.jours_orange"] = "120", ["officine"] = e.B.ToString(), ["code"] = "000000" });
            Assert.Equal($"/mes-officines/reglages?v=jours_orange:120&o={e.B}&erreur=reglages_refuse", versB.Headers.Location?.OriginalString);
            Assert.Contains("Rien n'est envoyé", await Texte(client, versB.Headers.Location!.OriginalString));
            Assert.Equal("/mes-officines/reglages?ok=code_envoye",
                (await Poster(client, "/mes-officines/reglages", "/actions/code-action", new() { ["retour"] = "/mes-officines/reglages" })).Headers.Location?.OriginalString);
            var codeReglages = Regex.Match(await File.ReadAllTextAsync(Path.Combine(codes, "tit.web.a.txt")), "[0-9]{6}").Value;
            var propose = await Poster(client, "/mes-officines/reglages", "/actions/mes-officines/reglages",
                new() { ["reglage"] = "jours_orange", ["valeur"] = "120", ["officine"] = e.A.ToString(), ["code"] = codeReglages });
            Assert.Equal("/mes-officines/reglages?ok=reglages_envoyes&n=1", propose.Headers.Location?.OriginalString);
            var suivi = (await Texte(client, "/mes-officines/reglages?ok=reglages_envoyes&n=1")).Replace(' ', ' ').Replace(' ', ' ');
            Assert.Contains("Proposé", suivi);
            Assert.Contains("Péremption : alerte orange", suivi);
            Assert.Contains("120 jours", suivi);
            Assert.Contains("Propositions envoyées", suivi);
            Assert.Contains("Aucune donnée de patient ni de vente", suivi);
            Assert.DoesNotContain("style=\"", suivi);
            Assert.Equal(1L, await b.ScalaireAsync("SELECT count(*) FROM journal_acces_distant WHERE evenement = 'reglages_titulaire' AND resultat = 'ok' AND officine_id = $1", e.A));

            // Officine d'un autre titulaire : refus (rien de B) ; écrans de l'éditeur : accès refusé
            var autre = await Texte(client, $"/mes-officines/{e.B}");
            Assert.Contains("Officine introuvable ou non rattachée", autre);
            Assert.DoesNotContain("98 765", autre.Replace(' ', ' ').Replace(' ', ' '));
            Assert.Contains("Officine introuvable ou non rattachée", await Texte(client, $"/mes-officines/{e.B}/journal"));
            Assert.Equal("/mes-officines", (await client.GetAsync("/officines")).Headers.Location?.OriginalString);
            Assert.Equal("/acces-refuse", (await client.GetAsync($"/officines/{e.A}?rubrique=lots")).Headers.Location?.OriginalString);
            Assert.Equal("/acces-refuse", (await client.GetAsync("/officines/nouvelle")).Headers.Location?.OriginalString);
            var action = await client.PostAsync("/actions/creer-officine", new FormUrlEncodedContent(new Dictionary<string, string>()));
            Assert.Equal("/acces-refuse", action.Headers.Location?.OriginalString);

            // Déconnexion, puis l'éditeur : aucun écran du titulaire
            Assert.Equal("/connexion", (await Poster(client, "/mes-officines", "/actions/deconnexion", [])).Headers.Location?.OriginalString);
            await Poster(client, "/connexion", "/actions/connexion", new() { ["identifiant"] = "tit.web.ed", ["mot_de_passe"] = MotDePasse });
            var codeEd = Regex.Match(await File.ReadAllTextAsync(Path.Combine(codes, "tit.web.ed.txt")), "[0-9]{6}").Value;
            Assert.Equal("/officines", (await Poster(client, "/code", "/actions/code", new() { ["code"] = codeEd })).Headers.Location?.OriginalString);
            foreach (var ecran in new[] { "/mes-officines", $"/mes-officines/{e.A}", $"/mes-officines/{e.A}/journal", "/rattacher" })
                Assert.Equal("/acces-refuse", (await client.GetAsync(ecran)).Headers.Location?.OriginalString);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/officines/{e.A}?rubrique=resume")).StatusCode);
            Assert.Equal(DBNull.Value, await b.ScalaireAsync("SELECT premiere_rupture FROM cloud_ecran_verifier_journal_acces()"));
            await app.StopAsync();
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            Directory.Delete(dossier, true);
        }
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
