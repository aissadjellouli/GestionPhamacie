using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Pharma.Api.Securite;
using Pharma.Application.SecondServeur;
using Pharma.Shared;

namespace Pharma.Architecture.Tests;

/// <summary>Bascule vague 2, lot V2-2 : signature entre serveurs (requête et réponse), anti-rejeu, appelant, empreinte, client signé, officine à un seul serveur.</summary>
public class InterServeursSignatureTests
{
    private const string Cle = "cle-des-jetons-d-essai-0123456789-abcdefghijklmnopqrstuvwxyz-0123456789-ABCDEF";
    private const string AutreCle = "une-AUTRE-cle-des-jetons-d-essai-0123456789-abcdefghijklmnopqrstuvwxyz-ABCDEF";

    private sealed class Horloge(DateTimeOffset debut) : TimeProvider
    {
        public DateTimeOffset Maintenant { get; set; } = debut;
        public override DateTimeOffset GetUtcNow() => Maintenant;
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Octets = Encoding.UTF8.GetBytes("{\"a\":1}");

    private static (SignatureInterServeurs Emetteur, SignatureInterServeurs Recepteur, Horloge H) Paire(string cleRecepteur = Cle)
    {
        var h = new Horloge(T0);
        return (new SignatureInterServeurs(Cle, "SERVEUR1", 60, h), new SignatureInterServeurs(cleRecepteur, "SERVEUR2", 60, h), h);
    }

    private static EnTetesRecus Recus(EnTetesSignes s) => new(s.Serveur, s.Horodatage, s.Nonce, s.Signature);

    // ---- Requête ----
    [Fact]
    public void Une_requete_signee_est_acceptee_une_seule_fois()
    {
        var (e, r, _) = Paire();
        var s = e.SignerRequete("post", "/api/inter-serveurs/alerte?x=1", Octets);
        Assert.Null(r.VerifierRequete("POST", "/api/inter-serveurs/alerte?x=1", Recus(s), Octets));
        Assert.Equal(RefusSignature.NonceRejoue, r.VerifierRequete("POST", "/api/inter-serveurs/alerte?x=1", Recus(s), Octets));
    }

    [Fact]
    public void Le_rejeu_reste_refuse_tant_que_l_horodatage_serait_accepte_meme_avec_une_grande_fenetre()
    {
        var h = new Horloge(T0);
        var e = new SignatureInterServeurs(Cle, "S1", 300, h);
        var r = new SignatureInterServeurs(Cle, "S2", 300, h);
        var s = e.SignerRequete("GET", "/api/inter-serveurs/etat", []);
        Assert.Null(r.VerifierRequete("GET", "/api/inter-serveurs/etat", Recus(s), []));
        h.Maintenant = T0.AddSeconds(299);
        Assert.Equal(RefusSignature.NonceRejoue, r.VerifierRequete("GET", "/api/inter-serveurs/etat", Recus(s), []));
    }

    [Theory]
    [InlineData(61, RefusSignature.HorodatageHorsFenetre)]
    [InlineData(-61, RefusSignature.HorodatageHorsFenetre)]
    [InlineData(60, null)]
    [InlineData(-60, null)]
    public void Horloge_decalee_la_fenetre_est_de_60_secondes(int decalage, RefusSignature? attendu)
    {
        var (e, r, h) = Paire();
        var s = e.SignerRequete("GET", "/api/inter-serveurs/etat", []);
        h.Maintenant = T0.AddSeconds(decalage);
        Assert.Equal(attendu, r.VerifierRequete("GET", "/api/inter-serveurs/etat", Recus(s), []));
    }

    [Fact]
    public void Mauvaise_cle_jwt_la_signature_est_fausse()
    {
        var (e, r, _) = Paire(AutreCle);
        var s = e.SignerRequete("GET", "/api/inter-serveurs/etat", []);
        Assert.Equal(RefusSignature.SignatureFausse, r.VerifierRequete("GET", "/api/inter-serveurs/etat", Recus(s), []));
        Assert.NotEqual(e.EmpreinteJeton, r.EmpreinteJeton);
    }

    [Fact]
    public void Tout_element_modifie_invalide_la_signature()
    {
        var (e, r, _) = Paire();
        var s = e.SignerRequete("POST", "/api/inter-serveurs/retrait", Octets);
        Assert.Equal(RefusSignature.SignatureFausse, r.VerifierRequete("POST", "/api/inter-serveurs/retrait", Recus(s), Encoding.UTF8.GetBytes("{\"a\":2}")));
        Assert.Equal(RefusSignature.SignatureFausse, r.VerifierRequete("POST", "/api/inter-serveurs/alerte", Recus(s), Octets));
        Assert.Equal(RefusSignature.SignatureFausse, r.VerifierRequete("GET", "/api/inter-serveurs/retrait", Recus(s), Octets));
        Assert.Equal(RefusSignature.SignatureFausse, r.VerifierRequete("POST", "/api/inter-serveurs/retrait", Recus(s) with { Serveur = "AUTRE" }, Octets));
        Assert.Equal(RefusSignature.SignatureFausse,
            r.VerifierRequete("POST", "/api/inter-serveurs/retrait", Recus(s) with { Horodatage = (long.Parse(s.Horodatage) + 1).ToString() }, Octets));
    }

    [Theory]
    [InlineData(null, "1", "AAAAAAAAAAAAAAAAAAAAAA", "AAAA")]
    [InlineData("S", null, "AAAAAAAAAAAAAAAAAAAAAA", "AAAA")]
    [InlineData("S", "1", "trop-court", "AAAA")]
    [InlineData("S", "abc", "AAAAAAAAAAAAAAAAAAAAAA", "AAAA")]
    [InlineData("S", "1", "AAAAAAAAAAAAAAAAAAAAAA", null)]
    [InlineData("S\nX", "1", "AAAAAAAAAAAAAAAAAAAAAA", "AAAA")]
    public void En_tetes_absents_ou_mal_formes_sont_refuses_sans_exception(string? serveur, string? horo, string? nonce, string? signature)
    {
        var (_, r, _) = Paire();
        Assert.Equal(RefusSignature.EnTetesInvalides, r.VerifierRequete("GET", "/x", new EnTetesRecus(serveur, horo, nonce, signature), []));
    }

    [Fact]
    public void Signature_mal_encodee_est_fausse_sans_exception()
    {
        var (e, r, _) = Paire();
        var s = e.SignerRequete("GET", "/api/inter-serveurs/etat", []);
        Assert.Equal(RefusSignature.SignatureFausse, r.VerifierRequete("GET", "/api/inter-serveurs/etat", Recus(s) with { Signature = "!!!pas-du-base64url!!!" }, []));
        Assert.Equal(RefusSignature.SignatureFausse, r.VerifierRequete("GET", "/api/inter-serveurs/etat", Recus(s) with { Signature = "AAAA" }, []));
    }

    [Fact]
    public void Un_appel_non_authentique_ne_pollue_pas_le_cache_de_nonces()
    {
        var (e, r, _) = Paire(AutreCle);
        var s = e.SignerRequete("GET", "/x", []);
        Assert.Equal(RefusSignature.SignatureFausse, r.VerifierRequete("GET", "/x", Recus(s), []));
        Assert.Equal(RefusSignature.SignatureFausse, r.VerifierRequete("GET", "/x", Recus(s), []));   // refusé pour la signature, pas pour le rejeu : rien n'a été retenu
    }

    // ---- Réponse ----
    [Fact]
    public void Une_reponse_signee_est_acceptee_et_liee_a_sa_requete()
    {
        var (demandeur, repondant, _) = Paire();
        var req = demandeur.SignerRequete("GET", "/api/inter-serveurs/etat", []);
        var rep = repondant.SignerReponse(200, req.Nonce, Octets);
        Assert.Null(demandeur.VerifierReponse(200, req.Nonce, Recus(rep), Octets));
        Assert.Equal(req.Nonce, rep.Nonce);
        var autre = demandeur.SignerRequete("GET", "/api/inter-serveurs/etat", []);
        Assert.Equal(RefusSignature.EnTetesInvalides, demandeur.VerifierReponse(200, autre.Nonce, Recus(rep), Octets));
    }

    [Fact]
    public void Une_reponse_falsifiee_est_refusee()
    {
        var (demandeur, repondant, _) = Paire();
        var req = demandeur.SignerRequete("POST", "/api/inter-serveurs/retrait", []);
        var rep = repondant.SignerReponse(409, req.Nonce, Octets);
        Assert.Equal(RefusSignature.SignatureFausse, demandeur.VerifierReponse(409, req.Nonce, Recus(rep), Encoding.UTF8.GetBytes("{\"a\":2}")));
        Assert.Equal(RefusSignature.SignatureFausse, demandeur.VerifierReponse(200, req.Nonce, Recus(rep), Octets));
        Assert.Equal(RefusSignature.SignatureFausse, demandeur.VerifierReponse(409, req.Nonce, Recus(rep) with { Serveur = "FAUX" }, Octets));
        Assert.Equal(RefusSignature.EnTetesInvalides, demandeur.VerifierReponse(409, req.Nonce, new EnTetesRecus(null, null, null, null), Octets));
        var faux = new SignatureInterServeurs(AutreCle, "SERVEUR2", 60, new Horloge(T0));
        Assert.Equal(RefusSignature.SignatureFausse, demandeur.VerifierReponse(200, req.Nonce, Recus(faux.SignerReponse(200, req.Nonce, Octets)), Octets));
    }

    [Fact]
    public void Une_reponse_ancienne_est_refusee()
    {
        var (demandeur, repondant, h) = Paire();
        var req = demandeur.SignerRequete("GET", "/x", []);
        var rep = repondant.SignerReponse(200, req.Nonce, Octets);
        h.Maintenant = T0.AddSeconds(120);
        Assert.Equal(RefusSignature.HorodatageHorsFenetre, demandeur.VerifierReponse(200, req.Nonce, Recus(rep), Octets));
    }

    [Fact]
    public void Une_requete_ne_peut_pas_passer_pour_une_reponse()
    {
        var (e, _, _) = Paire();
        var s = e.SignerRequete("GET", "/x", Octets);
        Assert.Equal(RefusSignature.SignatureFausse, e.VerifierReponse(200, s.Nonce, Recus(s), Octets));
    }

    // ---- Dérivation et empreinte ----
    [Fact]
    public void La_cle_de_signature_est_derivee_par_hkdf_sha256_selon_le_contrat()
    {
        var prk = HMACSHA256.HashData(new byte[32], Encoding.UTF8.GetBytes(Cle));
        var attendue = HMACSHA256.HashData(prk, Encoding.UTF8.GetBytes("pharmaofficine/inter-serveurs/v1").Append((byte)1).ToArray());
        Assert.Equal(attendue, CleInterServeurs.Deriver(Cle, CleInterServeurs.InfoSignature));
        Assert.NotEqual(Encoding.UTF8.GetBytes(Cle).Take(32).ToArray(), attendue);
    }

    [Fact]
    public void L_empreinte_est_stable_courte_et_ne_contient_pas_la_cle()
    {
        var emp = CleInterServeurs.Empreinte(Cle);
        Assert.Matches("^[0-9a-f]{16}$", emp);
        Assert.Equal(emp, CleInterServeurs.Empreinte(Cle));
        Assert.NotEqual(emp, CleInterServeurs.Empreinte(AutreCle));
        Assert.NotEqual(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Cle)))[..16], emp);
        Assert.NotEqual(Convert.ToHexStringLower(CleInterServeurs.Deriver(Cle, CleInterServeurs.InfoSignature))[..16], emp);
        Assert.True(CleInterServeurs.EmpreintesEgales(emp, emp));
        Assert.False(CleInterServeurs.EmpreintesEgales(emp, null));
        Assert.False(CleInterServeurs.EmpreintesEgales(emp, CleInterServeurs.Empreinte(AutreCle)));
    }

    [Fact]
    public void L_empreinte_correspond_au_calcul_des_scripts_d_installation()
    {
        // Valeur calculée par Get-EmpreinteJeton (scripts/second-serveur/commun.ps1, HKDF écrit à la main) sur la même clé d'essai
        Assert.Equal("7bfbbe003192de62", CleInterServeurs.Empreinte(Cle));
    }

    // ---- Appelant ----
    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.20", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::ffff:192.168.1.5", true)]
    [InlineData("fd00::1", true)]
    [InlineData("2001:db8::1", false)]
    public void Seules_les_adresses_privees_sont_admises(string ip, bool attendu) =>
        Assert.Equal(attendu, ControleAppelantInterServeurs.EstPrivee(IPAddress.Parse(ip)));

    [Fact]
    public async Task L_appelant_doit_etre_l_adresse_declaree_de_l_autre_serveur()
    {
        var c = new ControleAppelantInterServeurs("192.168.1.20");
        Assert.True(await c.AutoriseAsync(IPAddress.Parse("192.168.1.20"), default));
        Assert.True(await c.AutoriseAsync(IPAddress.Parse("::ffff:192.168.1.20"), default));
        Assert.False(await c.AutoriseAsync(IPAddress.Parse("192.168.1.21"), default));
        Assert.False(await new ControleAppelantInterServeurs("8.8.8.8").AutoriseAsync(IPAddress.Parse("8.8.8.8"), default));
        Assert.False(await c.AutoriseAsync(null, default));
        var nom = new ControleAppelantInterServeurs("pc2:5432", (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.7") }));
        Assert.True(await nom.AutoriseAsync(IPAddress.Parse("10.0.0.7"), default));
        Assert.False(await nom.AutoriseAsync(IPAddress.Parse("10.0.0.8"), default));
    }

    // ---- Intergiciel ----
    private sealed class Banc
    {
        public SignatureInterServeurs Local { get; }
        public SignatureInterServeurs Distant { get; }
        public SuiviCleJetons Suivi { get; } = new();
        public bool RouteAppelee { get; private set; }
        public int StatutRoute { get; set; } = 200;
        private readonly IServiceProvider _services;
        private readonly InterServeursMiddleware _mw;

        public Banc(string? autreServeur = "192.168.1.20", string cleDistante = Cle, bool sansCle = false)
        {
            var h = new Horloge(T0);
            Local = new SignatureInterServeurs(Cle, "SERVEUR1", 60, h);
            Distant = new SignatureInterServeurs(cleDistante, "SERVEUR2", 60, h);
            var sc = new ServiceCollection();
            sc.AddLogging();
            if (!sansCle) sc.AddSingleton(Local);
            sc.AddSingleton(new ControleAppelantInterServeurs(autreServeur));
            _services = sc.BuildServiceProvider();
            _mw = new InterServeursMiddleware(async ctx =>
            {
                RouteAppelee = true;
                ctx.Response.StatusCode = StatutRoute;
                await ctx.Response.WriteAsync("{\"etat\":\"ok\"}");
            }, NullLogger<InterServeursMiddleware>.Instance);
        }

        public DefaultHttpContext Contexte(string ip = "192.168.1.20", EnTetesSignes? s = null, string methode = "GET", string cible = "/api/inter-serveurs/etat")
        {
            var ctx = new DefaultHttpContext { RequestServices = _services };
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
            ctx.Request.Method = methode;
            ctx.Request.Path = cible;
            ctx.Features.Get<IHttpRequestFeature>()!.RawTarget = cible;
            ctx.Request.Body = new MemoryStream();
            ctx.Response.Body = new MemoryStream();
            if (s is not null)
            {
                ctx.Request.Headers[EnTetesInterServeurs.Serveur] = s.Serveur;
                ctx.Request.Headers[EnTetesInterServeurs.Horodatage] = s.Horodatage;
                ctx.Request.Headers[EnTetesInterServeurs.Nonce] = s.Nonce;
                ctx.Request.Headers[EnTetesInterServeurs.Signature] = s.Signature;
            }
            return ctx;
        }

        public Task Executer(HttpContext ctx) => _mw.InvokeAsync(ctx, Suivi);
    }

    private static string Texte(HttpContext c)
    {
        c.Response.Body.Position = 0;
        return new StreamReader(c.Response.Body).ReadToEnd();
    }

    private static EnTetesRecus EntetesReponse(HttpContext c) => new(c.Response.Headers[EnTetesInterServeurs.Serveur], c.Response.Headers[EnTetesInterServeurs.Horodatage],
        c.Response.Headers[EnTetesInterServeurs.Nonce], c.Response.Headers[EnTetesInterServeurs.Signature]);

    [Fact]
    public async Task Appel_signe_valide_la_route_repond_et_la_reponse_est_signee_et_verifiable()
    {
        var b = new Banc();
        var req = b.Distant.SignerRequete("GET", "/api/inter-serveurs/etat", []);
        var ctx = b.Contexte(s: req);
        await b.Executer(ctx);
        Assert.True(b.RouteAppelee);
        Assert.Equal(200, ctx.Response.StatusCode);
        var corps = Encoding.UTF8.GetBytes(Texte(ctx));
        Assert.Null(b.Distant.VerifierReponse(200, req.Nonce, EntetesReponse(ctx), corps));
        Assert.Equal(corps.Length, ctx.Response.ContentLength);
    }

    [Fact]
    public async Task La_reponse_d_une_route_en_erreur_est_aussi_signee()
    {
        var b = new Banc { StatutRoute = 409 };
        var req = b.Distant.SignerRequete("GET", "/api/inter-serveurs/etat", []);
        var ctx = b.Contexte(s: req);
        await b.Executer(ctx);
        Assert.Null(b.Distant.VerifierReponse(409, req.Nonce, EntetesReponse(ctx), Encoding.UTF8.GetBytes(Texte(ctx))));
    }

    [Fact]
    public async Task Requete_rejouee_401_et_la_route_n_est_pas_rappelee()
    {
        var b = new Banc();
        var req = b.Distant.SignerRequete("GET", "/api/inter-serveurs/etat", []);
        await b.Executer(b.Contexte(s: req));
        Assert.True(b.RouteAppelee);
        var rejeu = b.Contexte(s: req);
        var avant = b.RouteAppelee;
        await b.Executer(rejeu);
        Assert.Equal(401, rejeu.Response.StatusCode);
        Assert.Contains("signature_invalide", Texte(rejeu));
        Assert.Equal(avant, b.RouteAppelee);
        Assert.False(rejeu.Response.Headers.ContainsKey(EnTetesInterServeurs.Signature));
    }

    [Fact]
    public async Task Sans_signature_ou_avec_mauvaise_cle_401_message_identique_et_aucun_detail()
    {
        var sans = new Banc();
        var c1 = sans.Contexte();
        await sans.Executer(c1);
        var mauvaise = new Banc(cleDistante: AutreCle);
        var c2 = mauvaise.Contexte(s: mauvaise.Distant.SignerRequete("GET", "/api/inter-serveurs/etat", []));
        await mauvaise.Executer(c2);
        Assert.Equal(401, c1.Response.StatusCode);
        Assert.Equal(401, c2.Response.StatusCode);
        Assert.False(sans.RouteAppelee || mauvaise.RouteAppelee);
        var (t1, t2) = (Texte(c1), Texte(c2));
        Assert.Equal(t1, t2);
        Assert.DoesNotContain(Cle, t2);
        Assert.Equal(1, mauvaise.Suivi.RefusEntrantsRecents());
        Assert.Equal(0, sans.Suivi.RefusEntrantsRecents());
    }

    [Fact]
    public async Task Appelant_inconnu_403_adresse_publique_ou_autre_que_l_autre_serveur()
    {
        var b = new Banc();
        foreach (var ip in new[] { "8.8.8.8", "192.168.1.99" })
        {
            var ctx = b.Contexte(ip, b.Distant.SignerRequete("GET", "/api/inter-serveurs/etat", []));
            await b.Executer(ctx);
            Assert.Equal(403, ctx.Response.StatusCode);
        }
        Assert.False(b.RouteAppelee);
    }

    [Fact]
    public async Task Officine_a_un_seul_serveur_le_mecanisme_est_inactif()
    {
        foreach (var banc in new[] { new Banc(autreServeur: null), new Banc(autreServeur: "  "), new Banc(sansCle: true) })
        {
            var ctx = banc.Contexte(s: banc.Distant.SignerRequete("GET", "/api/inter-serveurs/etat", []));
            await banc.Executer(ctx);
            Assert.Equal(404, ctx.Response.StatusCode);
            Assert.False(banc.RouteAppelee);
        }
        var client = new ClientInterServeurs(new FabriqueHttp(_ => throw new InvalidOperationException("aucun appel attendu")), null,
            new ConfigurationBuilder().Build(), NullLogger<ClientInterServeurs>.Instance);
        Assert.False(client.Disponible);
        Assert.Equal(IssueInterServeurs.NonDeclare, (await client.LireEtatAutreAsync(default)).Issue);
        var services = new ServiceCollection().BuildServiceProvider();
        var tache = new SurveillanceInterServeurs(services, new ConfigurationBuilder().Build(), new SuiviCleJetons(), NullLogger<SurveillanceInterServeurs>.Instance);
        await tache.StartAsync(default);
        await tache.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(VerdictCleJetons.Inconnu, new SuiviCleJetons().Verdict);
    }

    [Fact]
    public void Les_routes_inter_serveurs_restent_permises_serveur_ecarte()
    {
        Assert.True(BarrageServeurEcarte.Permise("/api/inter-serveurs/etat"));
        Assert.True(BarrageServeurEcarte.Permise("/api/inter-serveurs/retrait"));
        Assert.True(BarrageServeurEcarte.Permise("/api/healthcheck"));
        Assert.False(BarrageServeurEcarte.Permise("/api/ventes"));
        Assert.False(BarrageServeurEcarte.Permise("/api/inter-serveursX/etat"));
    }

    // ---- Client signé ----
    private sealed class FabriqueHttp(Func<HttpRequestMessage, Task<HttpResponseMessage>> repondre) : IHttpClientFactory
    {
        private sealed class Gestionnaire(Func<HttpRequestMessage, Task<HttpResponseMessage>> r) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => r(request);
        }

        public HttpClient CreateClient(string name) => new(new Gestionnaire(repondre));
    }

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["SecondServeur:AutreServeur"] = "192.168.1.20" }).Build();

    private static ClientInterServeurs Client(string cleDistante, Func<SignatureInterServeurs, string, HttpResponseMessage>? repondre = null, Action<HttpRequestMessage>? voir = null)
    {
        var h = new Horloge(T0);
        var local = new SignatureInterServeurs(Cle, "SERVEUR1", 60, h);
        var distant = new SignatureInterServeurs(cleDistante, "SERVEUR2", 60, h);
        var fabrique = new FabriqueHttp(req =>
        {
            voir?.Invoke(req);
            var nonce = req.Headers.GetValues(EnTetesInterServeurs.Nonce).Single();
            var etat = $"{{\"serveur\":\"SERVEUR2\",\"role\":\"veille\",\"timeline\":3,\"enRecovery\":true,\"versionApi\":\"1.0\",\"empreinteJeton\":\"{distant.EmpreinteJeton}\"}}";
            return Task.FromResult(repondre is null ? Signee(distant, 200, nonce, etat) : repondre(distant, nonce));
        });
        return new ClientInterServeurs(fabrique, local, Config(), NullLogger<ClientInterServeurs>.Instance);
    }

    private static HttpResponseMessage Signee(SignatureInterServeurs s, int statut, string nonce, string corps)
    {
        var octets = Encoding.UTF8.GetBytes(corps);
        var e = s.SignerReponse(statut, nonce, octets);
        var r = new HttpResponseMessage((HttpStatusCode)statut) { Content = new ByteArrayContent(octets) };
        r.Headers.TryAddWithoutValidation(EnTetesInterServeurs.Serveur, e.Serveur);
        r.Headers.TryAddWithoutValidation(EnTetesInterServeurs.Horodatage, e.Horodatage);
        r.Headers.TryAddWithoutValidation(EnTetesInterServeurs.Nonce, e.Nonce);
        r.Headers.TryAddWithoutValidation(EnTetesInterServeurs.Signature, e.Signature);
        return r;
    }

    [Fact]
    public async Task Client_la_requete_envoyee_est_signee_et_la_reponse_verifiee()
    {
        HttpRequestMessage? vue = null;
        var c = Client(Cle, voir: r => vue = r);
        var etat = await c.LireEtatAutreAsync(default);
        Assert.Equal(IssueInterServeurs.Ok, etat.Issue);
        Assert.Equal("veille", etat.Etat!.Role);
        Assert.True(etat.MemeCleJetons);
        Assert.Equal("https://192.168.1.20:5443/api/inter-serveurs/etat", vue!.RequestUri!.ToString());
        Assert.True(vue.Headers.Contains(EnTetesInterServeurs.Signature));
        Assert.Equal(VerdictCleJetons.Identique, SurveillanceInterServeurs.Verdict(etat, false));
    }

    [Fact]
    public async Task Client_reponse_signee_par_une_autre_cle_est_rejetee()
    {
        var etat = await Client(AutreCle).LireEtatAutreAsync(default);
        Assert.Equal(IssueInterServeurs.ReponseInvalide, etat.Issue);
        Assert.Null(etat.Etat);
        Assert.Equal(VerdictCleJetons.Different, SurveillanceInterServeurs.Verdict(etat, false));
    }

    [Fact]
    public async Task Client_reponse_falsifiee_ou_non_signee_est_rejetee_et_jamais_fiable()
    {
        var falsifiee = Client(Cle, (d, nonce) =>
        {
            var r = Signee(d, 200, nonce, "{\"serveur\":\"S\",\"role\":\"veille\"}");
            r.Content = new StringContent("{\"serveur\":\"S\",\"role\":\"principal\"}");
            return r;
        });
        var nonSignee = Client(Cle, (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"role\":\"principal\"}") });
        foreach (var c in new[] { falsifiee, nonSignee })
        {
            var r = await c.EnvoyerAsync(HttpMethod.Get, "/api/inter-serveurs/etat", null, TimeSpan.FromSeconds(3), default);
            Assert.Equal(IssueInterServeurs.ReponseInvalide, r.Issue);
            Assert.False(r.Fiable);
        }
    }

    [Fact]
    public async Task Client_reponse_destinee_a_une_autre_requete_est_rejetee()
    {
        var c = Client(Cle, (d, _) => Signee(d, 200, "AAAAAAAAAAAAAAAAAAAAAA", "{}"));
        Assert.Equal(IssueInterServeurs.ReponseInvalide, (await c.EnvoyerAsync(HttpMethod.Get, "/api/inter-serveurs/etat", null, TimeSpan.FromSeconds(3), default)).Issue);
    }

    [Fact]
    public async Task Client_refus_401_de_l_autre_serveur_et_injoignable()
    {
        var refus = Client(Cle, (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"code\":\"signature_invalide\"}") });
        var etat = await refus.LireEtatAutreAsync(default);
        Assert.Equal(IssueInterServeurs.Refuse, etat.Issue);
        Assert.Equal(VerdictCleJetons.Different, SurveillanceInterServeurs.Verdict(etat, false));

        var injoignable = new ClientInterServeurs(new FabriqueHttp(_ => throw new HttpRequestException("réseau")), new SignatureInterServeurs(Cle, "S1"), Config(),
            NullLogger<ClientInterServeurs>.Instance);
        var e2 = await injoignable.LireEtatAutreAsync(default);
        Assert.Equal(IssueInterServeurs.Injoignable, e2.Issue);
        Assert.Equal(VerdictCleJetons.Inconnu, SurveillanceInterServeurs.Verdict(e2, false));
    }

    [Fact]
    public async Task Client_n_envoie_que_vers_api_et_l_annulation_de_l_appelant_remonte()
    {
        Assert.Equal(IssueInterServeurs.NonDeclare, (await Client(Cle).EnvoyerAsync(HttpMethod.Get, "http://pirate/x", null, TimeSpan.FromSeconds(1), default)).Issue);
        using var cts = new CancellationTokenSource();
        var annule = new ClientInterServeurs(new FabriqueHttp(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        }), new SignatureInterServeurs(Cle, "S1"), Config(), NullLogger<ClientInterServeurs>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => annule.LireEtatAutreAsync(cts.Token));
    }

    // ---- Verdict, suivi, cache ----
    [Fact]
    public void Verdict_des_cles_et_refus_entrants()
    {
        static EtatAutreServeur Etat(IssueInterServeurs i, bool? meme = null) => new(i, null, null, meme);
        Assert.Equal(VerdictCleJetons.Identique, SurveillanceInterServeurs.Verdict(Etat(IssueInterServeurs.Ok, true), true));
        Assert.Equal(VerdictCleJetons.Different, SurveillanceInterServeurs.Verdict(Etat(IssueInterServeurs.Ok, false), false));
        Assert.Equal(VerdictCleJetons.Inconnu, SurveillanceInterServeurs.Verdict(Etat(IssueInterServeurs.Ok, null), false));
        Assert.Equal(VerdictCleJetons.Inconnu, SurveillanceInterServeurs.Verdict(Etat(IssueInterServeurs.Injoignable), false));
        Assert.Equal(VerdictCleJetons.Inconnu, SurveillanceInterServeurs.Verdict(Etat(IssueInterServeurs.HorlogeDecalee), false));
        Assert.Equal(VerdictCleJetons.Different, SurveillanceInterServeurs.Verdict(Etat(IssueInterServeurs.Injoignable), true));
    }

    [Fact]
    public void Suivi_alerte_une_fois_par_heure_et_refus_entrants_sur_5_minutes()
    {
        var h = new Horloge(T0);
        var s = new SuiviCleJetons(h);
        Assert.True(s.AlerteDue());
        Assert.False(s.AlerteDue());
        h.Maintenant = T0.AddMinutes(61);
        Assert.True(s.AlerteDue());
        for (var i = 0; i < SuiviCleJetons.RefusEntrantsSeuil; i++) s.NoterRefusEntrant();
        Assert.True(s.RefusEntrantsSuspects);
        h.Maintenant = T0.AddMinutes(61 + 6);
        Assert.False(s.RefusEntrantsSuspects);
    }

    [Fact]
    public void Le_cache_de_nonces_est_borne_et_oublie_apres_la_conservation()
    {
        var c = new CacheNonces(TimeSpan.FromMinutes(5), capaciteMax: 3);
        Assert.True(c.TryEnregistrer("a", T0));
        Assert.False(c.TryEnregistrer("a", T0.AddMinutes(4)));
        Assert.True(c.TryEnregistrer("b", T0));
        Assert.True(c.TryEnregistrer("c", T0));
        Assert.False(c.TryEnregistrer("d", T0));
        Assert.True(c.TryEnregistrer("a", T0.AddMinutes(6)));
    }
}
