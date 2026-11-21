using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pharma.Cloud.Api.Etat;
using BaseCentraleJetable = Pharma.Cloud.Tests.DescendantsMutuV1Tests.BaseCentraleJetable;

namespace Pharma.Cloud.Tests;

/// <summary>
/// Page d'état du central (outil de développement, 127.0.0.1:5092) : démarrage conditionnel, adresse imposée, routes en lecture
/// seulement, requêtes SELECT sur une liste blanche de tables techniques, HTML encodé, script 029 (rôle pharma_cloud_etat).
/// </summary>
public class PageEtatCentralTests
{
    private static readonly string[] TablesPermises = ["officine", "certificat", "code_activation", "journal_securite", "lot_recu", "officine_epoque", "chargement"];
    private static readonly string[] Catalogues = ["pg_roles", "pg_tables"];

    private static IConfiguration Config(params (string Cle, string Valeur)[] valeurs) =>
        new ConfigurationBuilder().AddInMemoryCollection(valeurs.Select(v => new KeyValuePair<string, string?>(v.Cle, v.Valeur))).Build();

    // ------------------------------------------------------------------ démarrage
    [Fact]
    public void Hors_Development_la_page_ne_demarre_pas()
    {
        var (r, refus) = PageEtatCentral.Evaluer(false, Config(("Cloud:PageEtat", "true")), "Host=x");
        Assert.Null(r);
        Assert.Null(refus);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void Sans_Cloud_PageEtat_la_page_ne_demarre_pas(string? valeur)
    {
        var config = valeur is null ? Config() : Config(("Cloud:PageEtat", valeur));
        var (r, refus) = PageEtatCentral.Evaluer(true, config, "Host=x");
        Assert.Null(r);
        Assert.Null(refus);
    }

    [Fact]
    public void En_Development_avec_Cloud_PageEtat_la_page_ecoute_127_0_0_1_sur_5092()
    {
        var (r, refus) = PageEtatCentral.Evaluer(true, Config(("Cloud:PageEtat", "true")), "Host=x");
        Assert.Null(refus);
        Assert.NotNull(r);
        Assert.Equal(IPAddress.Loopback, r.Adresse);
        Assert.Equal(5092, r.Port);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("localhost")]
    [InlineData("192.168.1.10")]
    [InlineData("::1")]
    [InlineData("*")]
    public void Une_adresse_autre_que_127_0_0_1_est_refusee(string adresse)
    {
        var (r, refus) = PageEtatCentral.Evaluer(true, Config(("Cloud:PageEtat", "true"), ("Cloud:AdressePageEtat", adresse)), "Host=x");
        Assert.Null(r);
        Assert.Contains("127.0.0.1 seulement", refus);
        Assert.Throws<ArgumentException>(() => PageEtatCentral.Construire(IPAddress.Any, 0, new LecteurFactice()));
    }

    [Fact]
    public void Sans_connexion_de_lecture_la_page_ne_demarre_pas_avec_un_message_clair()
    {
        var (r, refus) = PageEtatCentral.Evaluer(true, Config(("Cloud:PageEtat", "true")), null);
        Assert.Null(r);
        Assert.Contains(PageEtatCentral.VariableConnexion, refus);
    }

    [Fact]
    public void Le_port_du_central_est_refuse_pour_la_page()
    {
        var (r, refus) = PageEtatCentral.Evaluer(true, Config(("Cloud:PageEtat", "true"), ("Cloud:PortPageEtat", "5090")), "Host=x");
        Assert.Null(r);
        Assert.Contains("port", refus);
    }

    [Fact]
    public async Task Refus_journalise_sans_lever_le_serveur_principal_continue()
    {
        var page = await PageEtatCentral.DemarrerSiDemandeeAsync(true, Config(("Cloud:PageEtat", "true")), null, NullLogger.Instance);
        Assert.Null(page);
    }

    // ------------------------------------------------------------------ routes
    [Fact]
    public async Task Le_serveur_de_la_page_n_expose_que_deux_routes_GET_et_aucune_route_api_v1()
    {
        await using var app = PageEtatCentral.Construire(IPAddress.Loopback, 0, new LecteurFactice());
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
        Assert.Equal(["/", "/etat.json"], routes.Select(r => r.RoutePattern.RawText ?? "").Order(StringComparer.Ordinal));
        foreach (var r in routes)
            Assert.Equal(["GET"], r.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);

        await app.StartAsync();
        try
        {
            var adresse = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            Assert.StartsWith("http://127.0.0.1:", adresse);
            using var client = new HttpClient { BaseAddress = new Uri(adresse) };
            var page = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
            Assert.Contains("no-store", page.Headers.CacheControl!.ToString());
            Assert.Contains("default-src 'none'", string.Join(" ", page.Headers.GetValues("Content-Security-Policy")));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/etat.json")).StatusCode);
            foreach (var chemin in new[] { "/api/v1/etat", "/api/v1/lots", "/api/v1/activation", "/api/v1/echange", "/api/v1/maximums" })
            {
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(chemin)).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(chemin, new StringContent("{}"))).StatusCode);
            }
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/", new StringContent(""))).StatusCode);
            // Autre nom d'hôte (reliaison DNS) : refusé
            using var autre = new HttpRequestMessage(HttpMethod.Get, "/");
            autre.Headers.Host = "exemple.invalid";
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(autre)).StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    // ------------------------------------------------------------------ requêtes
    [Fact]
    public void Les_requetes_de_la_page_sont_des_SELECT_sur_la_liste_blanche_de_tables_techniques()
    {
        var miroir = TablesMiroir();
        Assert.True(miroir.Count > 50, "liste des tables miroir non lue (002)");
        foreach (var sql in LecteurEtatCentral.Requetes)
        {
            var s = sql.Trim();
            Assert.True(s.StartsWith("SELECT ", StringComparison.Ordinal) || s == "SET TRANSACTION READ ONLY", $"requête non SELECT : {s}");
            Assert.DoesNotMatch(new Regex(@"\b(INSERT|UPDATE|DELETE|TRUNCATE|MERGE|ALTER|DROP|CREATE|GRANT|REVOKE|COPY|CALL|DO|EXECUTE|LOCK|SET\s+ROLE|RESET)\b",
                RegexOptions.IgnoreCase), s.Replace("SET TRANSACTION READ ONLY", ""));
            Assert.DoesNotMatch(new Regex(@"\b(contenu|empreinte_code|motif_revocation|lignes_vues|lignes_attendues)\b", RegexOptions.IgnoreCase), s);
            Assert.DoesNotContain("{", s);   // pas d'interpolation : paramètres $n seulement
            foreach (Match m in Regex.Matches(s, @"\b(?:FROM|JOIN)\s+([a-z_][a-z0-9_.""]*)", RegexOptions.IgnoreCase))
            {
                var table = m.Groups[1].Value.Replace("public.", "").Trim('"');
                Assert.True(TablesPermises.Contains(table) || Catalogues.Contains(table), $"table hors liste blanche : {table}");
                Assert.DoesNotContain(table, miroir);
            }
        }
        // Tables cloisonnées : filtre explicite sur l'officine posée, en plus de la RLS
        foreach (var q in LecteurEtatCentral.Requetes.Where(q => Regex.IsMatch(q, @"\bFROM\s+(lot_recu|officine_epoque|chargement)\b")))
            Assert.Matches(@"\b(l|e|c)\.officine_id = \$1\b", q);
        Assert.All(TablesPermises, t => Assert.DoesNotContain(t, miroir));
    }

    [Fact]
    public void Le_script_029_accorde_seulement_la_lecture_par_colonnes_des_tables_techniques()
    {
        var texte = File.ReadAllText(Path.Combine(Depot.Racine(), "database", "cloud", "029_role_lecture_etat.sql"));
        var sql = string.Join('\n', texte.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        Assert.Contains("CREATE ROLE pharma_cloud_etat NOLOGIN", sql);
        Assert.DoesNotMatch(new Regex(@"\bLOGIN\s+PASSWORD\b|\bPASSWORD\b", RegexOptions.IgnoreCase), sql);
        var octrois = Regex.Matches(sql, @"GRANT\s+(\w+)\s*(\(([^)]*)\))?\s*ON\s+(?:FUNCTION\s+)?([a-z_]+)[^;]*TO\s+pharma_cloud_etat", RegexOptions.IgnoreCase);
        Assert.Equal(8, octrois.Count);
        foreach (Match g in octrois)
        {
            var (droit, colonnes, objet) = (g.Groups[1].Value.ToUpperInvariant(), g.Groups[3].Value, g.Groups[4].Value);
            if (droit == "EXECUTE") { Assert.Equal("cloud_officine_courante", objet); continue; }
            Assert.Equal("SELECT", droit);
            Assert.True(g.Groups[2].Success, $"{objet} : droits par colonnes attendus");
            Assert.Contains(objet, TablesPermises);
            Assert.DoesNotMatch(new Regex(@"\b(contenu|empreinte_code|motif_revocation)\b"), colonnes);
        }
        Assert.DoesNotMatch(new Regex(@"GRANT\s+(ALL|INSERT|UPDATE|DELETE|TRUNCATE)[^;]*pharma_cloud_etat", RegexOptions.IgnoreCase), sql);
    }

    // ------------------------------------------------------------------ HTML
    [Fact]
    public void Le_HTML_encode_libelles_et_details_venus_de_la_base()
    {
        var id = Guid.NewGuid();
        var piege = "<script>alert('x')</script>\"&<img src=x onerror=1>";
        var etat = new EtatCentral(DateTimeOffset.UtcNow, "role<b>", [new OfficineEtat(id, piege, DateTimeOffset.UtcNow, null)],
            [new CertificatEtat(id, "principal", "abcdef012345", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1), null)],
            [new CodeActivationEtat(id, "principal", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), null)],
            [new DetailOfficineEtat(id, [new ChargementEtat(1, "activation", "en_cours", 1, DateTimeOffset.UtcNow, null, 3, 42, 1000, null)],
                [new EpoqueEtat(1, 3, DateTimeOffset.UtcNow)], [new LotEtat(1, 3, 14, 2048, DateTimeOffset.UtcNow)])],
            [new JournalEtat(1, DateTimeOffset.UtcNow, id, "abcdef012345", "activation", piege)]);
        var html = RenduPageEtat.Html(etat, "<erreur>", DateTimeOffset.UtcNow);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b>", html);
        Assert.DoesNotContain("<erreur>", html);
        Assert.Contains("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;&quot;&amp;", html);
        Assert.Contains("http-equiv=\"refresh\" content=\"10\"", html);
        Assert.Contains("prefers-color-scheme:dark", html);
        Assert.Contains("en cours", html);
    }

    // ------------------------------------------------------------------ bout en bout (base jetable)
    [Fact]
    public async Task Bout_en_bout_sur_une_base_jetable_role_de_lecture_et_cloisonnement()
    {
        using var base_ = BaseCentraleJetable.Creer();
        if (base_ is null) return;   // pas de serveur, de psql ou de droit CREATEDB : rien à vérifier ici
        base_.Rejouer(Path.Combine(Depot.Racine(), "database", "cloud", "tests", "029_role_lecture_etat_tests.sql"));

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await using (var proprietaire = NpgsqlDataSource.Create(base_.Connexion))
        {
            foreach (var sql in new[]
                     {
                         "INSERT INTO officine (id, libelle) VALUES ($1, '<script>A</script>'), ($2, 'Officine B')",
                         "INSERT INTO code_activation (empreinte_code, officine_id, serveur, expire_le) VALUES (repeat('c', 64), $1, 'principal', now() + interval '1 day')",
                         "INSERT INTO journal_securite (officine_id, evenement, detail) VALUES ($1, 'essai_page_etat', '<i>détail</i>')"
                     })
            {
                await using var cmd = proprietaire.CreateCommand(sql);
                cmd.Parameters.AddWithValue(a);
                if (sql.Contains("$2", StringComparison.Ordinal)) cmd.Parameters.AddWithValue(b);
                await cmd.ExecuteNonQueryAsync();
            }
            await using var cnx = await proprietaire.OpenConnectionAsync();
            await using var tx = await cnx.BeginTransactionAsync();
            await using (var role = new NpgsqlCommand("SET LOCAL ROLE pharma_cloud_api", cnx, tx)) await role.ExecuteNonQueryAsync();
            await using (var poser = new NpgsqlCommand("SELECT set_config('app.officine_id', $1, true)", cnx, tx))
            {
                poser.Parameters.AddWithValue(a.ToString("D"));
                await poser.ExecuteScalarAsync();
            }
            await using (var lot = new NpgsqlCommand("""
                SELECT cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1',
                  convert_to('{"lignes":[{"n":1,"table":"vente","id":"1","op":"creation","donnees":{"id":1,"numero":"T-000001"}}]}', 'UTF8'))
                """, cnx, tx))
                await lot.ExecuteScalarAsync();
            await tx.CommitAsync();

            // Connexion du propriétaire : refusée par la page (propriétaire des tables)
            await Assert.ThrowsAsync<InvalidOperationException>(() => new LecteurEtatCentral(proprietaire, TimeProvider.System).LireAsync(CancellationToken.None));
        }

        var lecture = new NpgsqlConnectionStringBuilder(base_.Connexion) { Options = "-c role=pharma_cloud_etat", Pooling = false }.ConnectionString;
        await using var db = NpgsqlDataSource.Create(lecture);
        var etat = await new LecteurEtatCentral(db, TimeProvider.System).LireAsync(CancellationToken.None);
        Assert.Equal("pharma_cloud_etat", etat.Role);
        Assert.Contains(etat.Officines, o => o.Id == a && o.Libelle == "<script>A</script>");
        Assert.Contains(etat.Codes, c => c.Officine == a && c.UtiliseLe is null);
        Assert.Contains(etat.Journal, j => j.Evenement == "essai_page_etat");
        var detailA = etat.Details.Single(d => d.Officine == a);
        var detailB = etat.Details.Single(d => d.Officine == b);
        Assert.Equal([1L], detailA.Lots.Select(l => l.Numero));
        Assert.Equal(1, detailA.Lots[0].Lignes);
        Assert.Equal([1L], detailA.Epoques.Select(e => e.DernierLot));
        Assert.Empty(detailB.Lots);
        Assert.Empty(detailB.Epoques);
        var html = RenduPageEtat.Html(etat, null, DateTimeOffset.UtcNow);
        Assert.DoesNotContain("<script>A", html);
        Assert.DoesNotContain("<i>", html);
        Assert.Contains("&lt;script&gt;A&lt;/script&gt;", html);
    }

    private static List<string> TablesMiroir() =>
        Regex.Matches(File.ReadAllText(Path.Combine(Depot.Racine(), "database", "cloud", "002_tables_miroir.sql")), @"CREATE TABLE IF NOT EXISTS public\.""(\w+)""")
            .Select(m => m.Groups[1].Value).ToList();

    private sealed class LecteurFactice : ILecteurEtatCentral
    {
        public Task<EtatCentral> LireAsync(CancellationToken ct) =>
            Task.FromResult(new EtatCentral(DateTimeOffset.UtcNow, "pharma_cloud_etat", [], [], [], [], []));
    }
}
