using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pharma.Api.Securite;
using Pharma.Application.Commun;
using Pharma.Application.SecondServeur;
using Pharma.Shared;

namespace Pharma.Architecture.Tests;

/// <summary>Bascule vague 2, lot V2-5 : relais du 2e serveur (veille) vers le serveur 1, en-têtes signés, adresse d'origine, aucun privilège local par relais.</summary>
public class RelaisVeilleTests
{
    private const string Cle = "cle-des-jetons-d-essai-0123456789-abcdefghijklmnopqrstuvwxyz-0123456789-ABCDEF";
    private const string AutreCle = "une-AUTRE-cle-des-jetons-d-essai-0123456789-abcdefghijklmnopqrstuvwxyz-ABCDEF";
    private const string AdresseDeuxiemeServeur = "192.168.1.20";
    private const string AdressePoste = "192.168.1.50";
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    private sealed class Temps(DateTimeOffset debut) : TimeProvider
    {
        public DateTimeOffset Maintenant { get; set; } = debut;
        public override DateTimeOffset GetUtcNow() => Maintenant;
    }

    private sealed class HorlogeFictive(DateTime debut) : IHorloge
    {
        public DateTime MaintenantUtc { get; set; } = debut;
        public DateOnly Aujourdhui => DateOnly.FromDateTime(MaintenantUtc);
    }

    private sealed class SituationFictive(RefusRelais? refus = null) : ISituationRelais
    {
        public RefusRelais? Refus { get; set; } = refus;
        public string? Dernier { get; private set; }
        public RefusRelais? Evaluer(string poste) { Dernier = poste; return Refus; }
        public List<string> Relayees { get; } = [];
        public void Relayee(string poste) => Relayees.Add(poste);
        public bool AlerteOk { get; set; } = true;
        public bool AlerteAutorisee(string poste) => AlerteOk;
    }

    private sealed class AlertesFictives(bool reussit = true) : IAlertesVersServeur1
    {
        public List<(string Type, string? Poste, string Message)> Envoyees { get; } = [];
        public bool Reussit { get; set; } = reussit;
        public Task<bool> EnvoyerAsync(string type, string? poste, string message, DateTime survenueUtc, CancellationToken ct = default)
        {
            lock (Envoyees) Envoyees.Add((type, poste, message));
            return Task.FromResult(Reussit);
        }
    }

    private sealed class Fabrique(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> repondre) : IHttpClientFactory
    {
        public int Appels;
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Repondre { get; } = repondre;
        private sealed class Gestionnaire(Fabrique f) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref f.Appels);
                return f.Repondre(request, ct);
            }
        }

        public HttpClient CreateClient(string name) => new(new Gestionnaire(this));
    }

    private static IConfiguration Config(string? autre = "192.168.1.10") => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["SecondServeur:AutreServeur"] = autre, ["Jwt:Cle"] = Cle }).Build();

    // ===== Serveur 1 (simulé) : intergiciel d'origine + terminal qui note ce qu'il voit =====
    private sealed class Principal
    {
        public Temps Temps { get; } = new(T0);
        public SignatureInterServeurs Signature { get; }
        public IPAddress? AdresseVue;
        public bool? Via;
        public string? PosteVu, Autorisation, CorpsVu, MethodeVue, CibleVue;
        public IHeaderDictionary? EnTetesVus;
        public int Passages;
        public int StatutReponse = 201;
        private readonly RelaisOrigineMiddleware _mw;
        private readonly IServiceProvider _services;

        public Principal(string? autre = AdresseDeuxiemeServeur, bool veille = false, string cle = Cle, bool sansCle = false)
        {
            Signature = new SignatureInterServeurs(cle, "SERVEUR1", 60, Temps);
            var sc = new ServiceCollection();
            sc.AddLogging();
            sc.AddProblemDetails();
            if (!sansCle) sc.AddSingleton(Signature);
            sc.AddSingleton(new ControleAppelantInterServeurs(autre));
            sc.AddSingleton<IVeilleServeur>(new VeilleServeur(veille));
            _services = sc.BuildServiceProvider();
            _mw = new RelaisOrigineMiddleware(async ctx =>
            {
                Passages++;
                AdresseVue = ctx.Connection.RemoteIpAddress;
                Via = RelaisOrigineMiddleware.ViaRelais(ctx);
                PosteVu = ctx.Request.Headers["X-Pharma-Poste"];
                Autorisation = ctx.Request.Headers.Authorization;
                MethodeVue = ctx.Request.Method;
                CibleVue = ctx.Request.Path + ctx.Request.QueryString.ToString();
                EnTetesVus = ctx.Request.Headers;
                using var lu = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                CorpsVu = await lu.ReadToEndAsync();
                ctx.Response.StatusCode = StatutReponse;
                ctx.Response.Headers["X-Test"] = "oui";
                ctx.Response.Headers.Connection = "close";
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"ok\":true}");
            }, NullLogger<RelaisOrigineMiddleware>.Instance);
        }

        public DefaultHttpContext Contexte(string ip, string methode, string cible, IEnumerable<KeyValuePair<string, string>>? entetes = null, string corps = "")
        {
            var ctx = new DefaultHttpContext { RequestServices = _services };
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
            ctx.Request.Method = methode;
            var i = cible.IndexOf('?');
            ctx.Request.Path = i < 0 ? cible : cible[..i];
            if (i >= 0) ctx.Request.QueryString = new QueryString(cible[i..]);
            ctx.Features.Get<IHttpRequestFeature>()!.RawTarget = cible;
            foreach (var (k, v) in entetes ?? []) ctx.Request.Headers[k] = v;
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(corps));
            ctx.Response.Body = new MemoryStream();
            return ctx;
        }

        public Task Executer(HttpContext ctx) => _mw.InvokeAsync(ctx);

        /// <summary>Transport en mémoire : la requête du relais arrive au serveur 1 depuis l'adresse du 2e serveur.</summary>
        public async Task<HttpResponseMessage> Repondre(HttpRequestMessage req, string ipAppel = AdresseDeuxiemeServeur)
        {
            IEnumerable<KeyValuePair<string, IEnumerable<string>>> contenu = (IEnumerable<KeyValuePair<string, IEnumerable<string>>>?)req.Content?.Headers ?? [];
            var entetes = req.Headers.Concat(contenu)
                .Select(h => new KeyValuePair<string, string>(h.Key, string.Join(",", h.Value))).ToList();
            var corps = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
            var ctx = Contexte(ipAppel, req.Method.Method, req.RequestUri!.PathAndQuery, entetes, corps);
            await Executer(ctx);
            ctx.Response.Body.Position = 0;
            var reponse = new HttpResponseMessage((HttpStatusCode)ctx.Response.StatusCode) { Content = new StreamContent(ctx.Response.Body) };
            foreach (var (k, v) in ctx.Response.Headers)
                if (!reponse.Headers.TryAddWithoutValidation(k, v.ToArray())) reponse.Content.Headers.TryAddWithoutValidation(k, v.ToArray());
            return reponse;
        }
    }

    // ===== Serveur 2 (relais) =====
    private sealed class Veille
    {
        public SignatureInterServeurs Signature { get; }
        public Temps Temps { get; }
        public SituationFictive Situation { get; } = new();
        public AlertesFictives Alertes { get; } = new();
        public HorlogeFictive Horloge { get; } = new(T0.UtcDateTime);
        public AlerteRelaisPoste Alerte { get; }
        public Fabrique Fabrique { get; }
        public RelaisVeille Relais { get; }
        public LimiteRelais Limite { get; }
        public HttpRequestMessage? Vue;
        public byte[]? CorpsVu;

        public Veille(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? repondre = null, Temps? temps = null, string? autre = "192.168.1.10", bool sansCle = false, int parMinute = LimiteRelais.ParMinuteParDefaut)
        {
            Temps = temps ?? new Temps(T0);
            Signature = new SignatureInterServeurs(Cle, "SERVEUR2", 60, Temps);
            Alerte = new AlerteRelaisPoste(Alertes, Horloge);
            Fabrique = new Fabrique(async (req, ct) =>
            {
                Vue = req;
                if (req.Content is not null) CorpsVu = await req.Content.ReadAsByteArrayAsync(ct);
                return repondre is null ? new HttpResponseMessage(HttpStatusCode.OK) : await repondre(req, ct);
            });
            Limite = new LimiteRelais(Horloge, parMinute);
            Relais = new RelaisVeille(Fabrique, sansCle ? null : Signature, Config(autre), Situation, Alerte, Limite, NullLogger<RelaisVeille>.Instance);
        }

        public DefaultHttpContext Poste(string methode = "GET", string cible = "/api/caisse/ventes?du=2026-11-01", string poste = "CAISSE-1", string corps = "",
            Action<IHeaderDictionary>? entetes = null, string ip = AdressePoste)
        {
            var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider() };
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
            ctx.Request.Method = methode;
            var i = cible.IndexOf('?');
            ctx.Request.Path = i < 0 ? cible : cible[..i];
            if (i >= 0) ctx.Request.QueryString = new QueryString(cible[i..]);
            if (poste.Length > 0) ctx.Request.Headers["X-Pharma-Poste"] = poste;
            ctx.Request.Headers.Authorization = "Bearer jeton-du-poste";
            var octets = Encoding.UTF8.GetBytes(corps);
            ctx.Request.Body = new MemoryStream(octets);
            if (octets.Length > 0) { ctx.Request.ContentLength = octets.Length; ctx.Request.ContentType = "application/json"; }
            entetes?.Invoke(ctx.Request.Headers);
            ctx.Response.Body = new MemoryStream();
            return ctx;
        }

        public Task<bool> Tenter(HttpContext ctx) => Relais.TenterAsync(ctx, CancellationToken.None);
    }

    private static string Texte(HttpContext c)
    {
        c.Response.Body.Position = 0;
        return new StreamReader(c.Response.Body).ReadToEnd();
    }

    // ===================================================================================================================
    // Signature des en-têtes de relais
    // ===================================================================================================================
    private static EnTetesRelaisRecus Recus(EnTetesRelaisSignes s) => new(s.Serveur, s.Poste, s.Origine, s.Horodatage, s.Nonce, s.Signature);

    private static (SignatureInterServeurs Emetteur, SignatureInterServeurs Recepteur, Temps H) Paire(string cleRecepteur = Cle)
    {
        var h = new Temps(T0);
        return (new SignatureInterServeurs(Cle, "SERVEUR2", 60, h), new SignatureInterServeurs(cleRecepteur, "SERVEUR1", 60, h), h);
    }

    [Fact]
    public void En_tetes_de_relais_valides_acceptes_une_seule_fois()
    {
        var (e, r, _) = Paire();
        var s = e.SignerRelais("CAISSE-1", AdressePoste, "post", "/api/caisse/ventes?x=1");
        Assert.Null(r.VerifierRelais("POST", "/api/caisse/ventes?x=1", Recus(s)));
        Assert.Equal(RefusSignature.NonceRejoue, r.VerifierRelais("POST", "/api/caisse/ventes?x=1", Recus(s)));
    }

    [Fact]
    public void Signature_fausse_poste_ou_origine_ou_methode_ou_cible_modifies()
    {
        var (e, r, _) = Paire();
        var s = e.SignerRelais("CAISSE-1", AdressePoste, "GET", "/api/a");
        Assert.Equal(RefusSignature.SignatureFausse, r.VerifierRelais("GET", "/api/a", Recus(s) with { Origine = "192.168.1.51" }));
        Assert.Equal(RefusSignature.SignatureFausse, r.VerifierRelais("GET", "/api/a", Recus(s) with { Poste = "CAISSE-2" }));
        Assert.Equal(RefusSignature.SignatureFausse, r.VerifierRelais("POST", "/api/a", Recus(s)));
        Assert.Equal(RefusSignature.SignatureFausse, r.VerifierRelais("GET", "/api/b", Recus(s)));
        Assert.Equal(RefusSignature.SignatureFausse, r.VerifierRelais("GET", "/api/a", Recus(s) with { Serveur = "AUTRE" }));
        // Une signature fausse ne consomme pas le nonce : l'annonce authentique passe encore
        Assert.Null(r.VerifierRelais("GET", "/api/a", Recus(s)));
    }

    [Fact]
    public void Signature_d_une_autre_cle_refusee()
    {
        var (e, r, _) = Paire(AutreCle);
        var s = e.SignerRelais("CAISSE-1", AdressePoste, "GET", "/api/a");
        Assert.Equal(RefusSignature.SignatureFausse, r.VerifierRelais("GET", "/api/a", Recus(s)));
    }

    [Fact]
    public void Annonce_hors_fenetre_ou_mal_formee_refusee()
    {
        var (e, r, h) = Paire();
        var s = e.SignerRelais("CAISSE-1", AdressePoste, "GET", "/api/a");
        h.Maintenant = T0.AddSeconds(61);
        Assert.Equal(RefusSignature.HorodatageHorsFenetre, r.VerifierRelais("GET", "/api/a", Recus(s)));
        h.Maintenant = T0;
        Assert.Equal(RefusSignature.EnTetesInvalides, r.VerifierRelais("GET", "/api/a", Recus(s) with { Origine = null }));
        Assert.Equal(RefusSignature.EnTetesInvalides, r.VerifierRelais("GET", "/api/a", Recus(s) with { Poste = "a\nb" }));
        Assert.Equal(RefusSignature.EnTetesInvalides, r.VerifierRelais("GET", "/api/a", Recus(s) with { Signature = null }));
        Assert.Equal(RefusSignature.EnTetesInvalides, r.VerifierRelais("GET", "/api/a", Recus(s) with { Origine = new string('1', 46) }));
    }

    [Fact]
    public void La_chaine_de_relais_est_distincte_de_celle_des_appels_inter_serveurs()
    {
        var relais = SignatureInterServeurs.ChaineRelais("GET", "/api/a", "P", "1.2.3.4", "1", "n", "S");
        var inter = SignatureInterServeurs.ChaineRequete("GET", "/api/a", "1", "n", [], "S");
        Assert.StartsWith("PHARMA-RELAIS-V1\n", relais);
        Assert.NotEqual(inter, relais);
    }

    // ===================================================================================================================
    // Décision de relayer
    // ===================================================================================================================
    private static readonly ReglagesBascule Reglages = new ReglagesBascule().Borner();
    private static readonly DateTime Maintenant = T0.UtcDateTime;
    private static readonly Serveur1Vu Serveur1Joint = new(true, true, null, null);

    private static RapportRetenu Rapport(bool joint, double age) => new("CAISSE-1", joint, Maintenant.AddSeconds(-age), joint ? null : Maintenant.AddSeconds(-age - 40), false);

    [Fact]
    public void Decision_relais_permis_seulement_si_perdu_recent_verdict_relais_et_serveur1_joint()
    {
        Assert.Null(SituationRelais.Decider(VerdictPromotion.Relais, Rapport(false, 5), Serveur1Joint, Maintenant, Reglages));
        Assert.Null(SituationRelais.Decider(VerdictPromotion.Relais, Rapport(false, 44), Serveur1Joint, Maintenant, Reglages));
    }

    [Theory]
    [InlineData(VerdictPromotion.Attendre)]
    [InlineData(VerdictPromotion.Rester)]
    [InlineData(VerdictPromotion.Promotion)]
    [InlineData(VerdictPromotion.RetraitPuisPromotion)]
    [InlineData(VerdictPromotion.ApiServeur1Muette)]
    [InlineData(VerdictPromotion.Egalite)]
    [InlineData(VerdictPromotion.Inactif)]
    public void Decision_verdict_autre_que_relais_refuse(VerdictPromotion verdict) =>
        Assert.Equal(RefusRelais.VerdictNonRelais, SituationRelais.Decider(verdict, Rapport(false, 5), Serveur1Joint, Maintenant, Reglages));

    [Fact]
    public void Decision_sans_verdict_sans_rapport_poste_joint_rapport_perime_ou_serveur1_muet_refuse()
    {
        Assert.Equal(RefusRelais.VerdictNonRelais, SituationRelais.Decider(null, Rapport(false, 5), Serveur1Joint, Maintenant, Reglages));
        Assert.Equal(RefusRelais.PosteSansRapport, SituationRelais.Decider(VerdictPromotion.Relais, null, Serveur1Joint, Maintenant, Reglages));
        Assert.Equal(RefusRelais.PosteJoint, SituationRelais.Decider(VerdictPromotion.Relais, Rapport(true, 5), Serveur1Joint, Maintenant, Reglages));
        Assert.Equal(RefusRelais.RapportPerime, SituationRelais.Decider(VerdictPromotion.Relais, Rapport(false, 45), Serveur1Joint, Maintenant, Reglages));
        Assert.Equal(RefusRelais.RapportPerime, SituationRelais.Decider(VerdictPromotion.Relais, Rapport(false, 300), Serveur1Joint, Maintenant, Reglages));
        Assert.Equal(RefusRelais.Serveur1Injoignable, SituationRelais.Decider(VerdictPromotion.Relais, Rapport(false, 5), new Serveur1Vu(true, false, null, Maintenant), Maintenant, Reglages));
        Assert.Equal(RefusRelais.Serveur1Injoignable, SituationRelais.Decider(VerdictPromotion.Relais, Rapport(false, 5), null, Maintenant, Reglages));
    }

    // ===================================================================================================================
    // Transmission
    // ===================================================================================================================
    [Fact]
    public async Task Transmission_methode_corps_statut_et_reponse_en_flux()
    {
        var v = new Veille((req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{\"numero\":42}", Encoding.UTF8, "application/json")
        }));
        var ctx = v.Poste("POST", "/api/caisse/ventes?x=a%20b", corps: "{\"panier\":[1,2]}");

        Assert.True(await v.Tenter(ctx));

        Assert.NotNull(v.Vue);
        Assert.Equal(HttpMethod.Post, v.Vue!.Method);
        Assert.Equal("https://192.168.1.10:5443/api/caisse/ventes?x=a%20b", v.Vue.RequestUri!.AbsoluteUri);
        Assert.Equal("{\"panier\":[1,2]}", Encoding.UTF8.GetString(v.CorpsVu!));
        Assert.Equal(201, ctx.Response.StatusCode);
        Assert.Equal("{\"numero\":42}", Texte(ctx));
        Assert.StartsWith("application/json", ctx.Response.ContentType);
        Assert.Equal("no-store", ctx.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task Transmission_de_toutes_les_methodes(string methode)
    {
        var v = new Veille();
        Assert.True(await v.Tenter(v.Poste(methode, "/api/catalogue/produits/5")));
        Assert.Equal(methode, v.Vue!.Method.Method);
    }

    [Fact]
    public async Task Le_statut_d_erreur_du_serveur_1_est_rendu_tel_quel()
    {
        var v = new Veille((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"message\":\"x\"}") }));
        var ctx = v.Poste();
        Assert.True(await v.Tenter(ctx));
        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Authorization_et_poste_inchanges_en_tetes_de_saut_a_saut_et_forges_retires()
    {
        var v = new Veille();
        var ctx = v.Poste("GET", "/api/catalogue/produits", entetes: h =>
        {
            h["Connection"] = "keep-alive, X-Secret";
            h["X-Secret"] = "a-retirer-car-nomme-dans-Connection";
            h["Keep-Alive"] = "timeout=5";
            h["Upgrade"] = "websocket";
            h["X-Forwarded-For"] = "10.9.9.9";
            h["Forwarded"] = "for=10.9.9.9";
            h["X-Real-IP"] = "10.9.9.9";
            h["X-Pharma-Relais-Origine"] = "127.0.0.1";
            h["X-Pharma-Relais-Poste"] = "FAUX";
            h["X-Pharma-Relais-Signature"] = "forge";
            h["X-Pharma-Signature"] = "forge";
            h["Accept-Language"] = "fr-DZ";
            h["X-Pharma-Autorise-Par"] = "#12";
        });
        Assert.True(await v.Tenter(ctx));
        var h = v.Vue!.Headers;
        Assert.Equal("Bearer jeton-du-poste", h.GetValues("Authorization").Single());
        Assert.Equal("CAISSE-1", h.GetValues("X-Pharma-Poste").Single());
        Assert.Equal("fr-DZ", h.GetValues("Accept-Language").Single());
        Assert.Equal("#12", h.GetValues("X-Pharma-Autorise-Par").Single());
        foreach (var interdit in new[] { "Connection", "Keep-Alive", "Upgrade", "X-Secret", "X-Forwarded-For", "Forwarded", "X-Real-IP", "X-Pharma-Signature" })
            Assert.False(h.Contains(interdit), interdit);
        Assert.Null(v.Vue.Headers.Connection.FirstOrDefault());
        // Les en-têtes de relais reçus du poste sont écrasés par ceux, signés, du relais
        Assert.Equal(AdressePoste, h.GetValues(EnTetesRelais.Origine).Single());
        Assert.Equal("CAISSE-1", h.GetValues(EnTetesRelais.Poste).Single());
        Assert.NotEqual("forge", h.GetValues(EnTetesRelais.Signature).Single());
    }

    [Fact]
    public async Task Les_en_tetes_de_relais_envoyes_sont_signes_et_verifiables_par_le_serveur_1()
    {
        var v = new Veille();
        Assert.True(await v.Tenter(v.Poste("POST", "/api/caisse/ventes?x=1", corps: "{}")));
        var h = v.Vue!.Headers;
        var recus = new EnTetesRelaisRecus(h.GetValues(EnTetesRelais.Serveur).Single(), h.GetValues(EnTetesRelais.Poste).Single(), h.GetValues(EnTetesRelais.Origine).Single(),
            h.GetValues(EnTetesRelais.Horodatage).Single(), h.GetValues(EnTetesRelais.Nonce).Single(), h.GetValues(EnTetesRelais.Signature).Single());
        var serveur1 = new SignatureInterServeurs(Cle, "SERVEUR1", 60, v.Temps);
        Assert.Equal("SERVEUR2", recus.Serveur);
        Assert.Null(serveur1.VerifierRelais("POST", v.Vue.RequestUri!.PathAndQuery, recus));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(119, true)]
    [InlineData(120, false)]
    [InlineData(121, false)]
    public void Tolerance_Q24_poste_deja_relaye_reste_relaye_moins_de_120_s_meme_si_son_rapport_dit_joint(int secondes, bool permis)
    {
        var dernier = Maintenant.AddSeconds(-secondes);
        // Rapport « joint », verdict qui n'est plus Relais : refusé sans tolérance, permis avec
        Assert.Equal(RefusRelais.VerdictNonRelais, SituationRelais.Decider(VerdictPromotion.Rester, Rapport(true, 5), Serveur1Joint, Maintenant, Reglages));
        var r = SituationRelais.Decider(VerdictPromotion.Rester, Rapport(true, 5), Serveur1Joint, Maintenant, Reglages, dernier);
        Assert.Equal(permis, r is null);
        // Même chose avec le verdict Relais et un rapport « joint », ou sans aucun rapport
        Assert.Equal(permis, SituationRelais.Decider(VerdictPromotion.Relais, Rapport(true, 5), Serveur1Joint, Maintenant, Reglages, dernier) is null);
        Assert.Equal(permis, SituationRelais.Decider(null, null, Serveur1Joint, Maintenant, Reglages, dernier) is null);
    }

    [Fact]
    public void Tolerance_Q24_refusee_si_le_serveur_1_n_est_plus_joint_ou_sans_relais_recent()
    {
        var recent = Maintenant.AddSeconds(-10);
        Assert.Equal(RefusRelais.VerdictNonRelais, SituationRelais.Decider(VerdictPromotion.Rester, Rapport(true, 5), new Serveur1Vu(true, false, null, Maintenant), Maintenant, Reglages, recent));
        Assert.Equal(RefusRelais.VerdictNonRelais, SituationRelais.Decider(VerdictPromotion.Rester, Rapport(true, 5), null, Maintenant, Reglages, recent));
        Assert.Equal(RefusRelais.Serveur1Injoignable, SituationRelais.Decider(VerdictPromotion.Relais, Rapport(false, 5), null, Maintenant, Reglages, recent));
        Assert.Equal(RefusRelais.PosteJoint, SituationRelais.Decider(VerdictPromotion.Relais, Rapport(true, 5), Serveur1Joint, Maintenant, Reglages, null));
    }

    [Fact]
    public void Tolerance_Q24_memoire_vive_notee_a_chaque_relais_et_bornee()
    {
        var horloge = new HorlogeFictive(Maintenant);
        var vigie = new VigieSecondServeur(new RegistreRapportsPostes(Reglages), null!, null!, null!, null!, horloge, Reglages);
        var situation = new SituationRelais(vigie, new RegistreRapportsPostes(Reglages), new SondeServeur1(null!, null!), horloge, Reglages);
        // Aucun rapport ni sonde : refusé ; après un relais noté, le refus ne dépend plus que de la sonde (ici absente : serveur 1 injoignable)
        Assert.Equal(RefusRelais.VerdictNonRelais, situation.Evaluer("P1"));
        situation.Relayee("P1");
        Assert.Equal(RefusRelais.VerdictNonRelais, situation.Evaluer("P1"));   // sonde absente : la tolérance exige le serveur 1 joint
        for (var i = 0; i < SituationRelais.PostesMax + 20; i++) situation.Relayee("Q" + i);   // mémoire bornée, sans exception
    }

    [Fact]
    public async Task Le_relais_note_chaque_requete_relayee_et_pas_les_refus()
    {
        var v = new Veille();
        Assert.True(await v.Tenter(v.Poste(poste: "Caisse%20%C3%89t%C3%A9%201")));
        Assert.Equal(["Caisse Été 1"], v.Situation.Relayees);
        v.Situation.Refus = RefusRelais.RapportPerime;
        Assert.False(await v.Tenter(v.Poste(poste: "Caisse%20%C3%89t%C3%A9%201")));
        Assert.Single(v.Situation.Relayees);
    }

    [Fact]
    public async Task Nom_de_poste_encode_URL_le_rapport_est_cherche_sous_le_nom_decode()
    {
        var v = new Veille();
        Assert.True(await v.Tenter(v.Poste(poste: "CAISSE%20n%C2%B01")));
        Assert.Equal("CAISSE n°1", v.Situation.Dernier);
        Assert.Equal("CAISSE%20n%C2%B01", v.Vue!.Headers.GetValues("X-Pharma-Poste").Single());
        Assert.Equal("CAISSE%20n%C2%B01", v.Vue.Headers.GetValues(EnTetesRelais.Poste).Single());
        await AttendreAlertes(v, 1);
        Assert.Equal("CAISSE n°1", v.Alertes.Envoyees.Single().Poste);
    }

    [Fact]
    public async Task Une_requete_chunked_sans_longueur_est_transmise()
    {
        var v = new Veille();
        var ctx = v.Poste("POST", "/api/ged/ordonnances", entetes: h => h["Transfer-Encoding"] = "chunked");
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("contenu"));
        ctx.Request.ContentLength = null;
        Assert.True(await v.Tenter(ctx));
        Assert.Equal("contenu", Encoding.UTF8.GetString(v.CorpsVu!));
        Assert.False(v.Vue!.Headers.Contains("Transfer-Encoding"));
    }

    [Fact]
    public async Task En_tetes_de_reponse_conserves_sauf_saut_a_saut()
    {
        var v = new Veille((_, _) =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
            r.Headers.TryAddWithoutValidation("X-Application", "valeur");
            r.Headers.TryAddWithoutValidation("Connection", "close");
            r.Headers.TryAddWithoutValidation("Keep-Alive", "timeout=5");
            r.Headers.TryAddWithoutValidation("Cache-Control", "private, max-age=0");
            return Task.FromResult(r);
        });
        var ctx = v.Poste();
        Assert.True(await v.Tenter(ctx));
        Assert.Equal("valeur", ctx.Response.Headers["X-Application"].ToString());
        Assert.False(ctx.Response.Headers.ContainsKey("Keep-Alive"));
        Assert.False(ctx.Response.Headers.ContainsKey("Connection"));
        Assert.Contains("private", ctx.Response.Headers.CacheControl.ToString());
        Assert.DoesNotContain("no-store", ctx.Response.Headers.CacheControl.ToString());   // celui du serveur 1 n'est pas remplacé
    }

    // ===================================================================================================================
    // Refus
    // ===================================================================================================================
    [Theory]
    [InlineData(RefusRelais.PosteSansRapport)]
    [InlineData(RefusRelais.PosteJoint)]
    [InlineData(RefusRelais.RapportPerime)]
    [InlineData(RefusRelais.VerdictNonRelais)]
    [InlineData(RefusRelais.Serveur1Injoignable)]
    public async Task Refus_aucun_appel_au_serveur_1_et_503_serveur_en_veille_par_le_barrage(RefusRelais refus)
    {
        var v = new Veille();
        v.Situation.Refus = refus;
        var ctx = v.Poste();
        Assert.False(await v.Tenter(ctx));
        Assert.Equal(0, v.Fabrique.Appels);
        Assert.Empty(v.Alertes.Envoyees);

        // Dans le barrage : 503 « serveur_en_veille », sans motif
        var barrage = Barrage(v.Relais);
        await BarrageServeurVeille.Filtrer(barrage, _ => Task.CompletedTask);
        Assert.Equal(503, barrage.Response.StatusCode);
        Assert.Contains("\"code\":\"serveur_en_veille\"", Texte(barrage));
    }

    private static DefaultHttpContext Barrage(IRelaisVeille relais, string chemin = "/api/caisse/ventes", string poste = "CAISSE-1")
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddProblemDetails();
        sc.AddSingleton<IVeilleServeur>(new VeilleServeur(true));
        sc.AddSingleton(relais);
        var ctx = new DefaultHttpContext { RequestServices = sc.BuildServiceProvider() };
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(AdressePoste);
        ctx.Request.Path = chemin;
        ctx.Request.Headers["X-Pharma-Poste"] = poste;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    [Theory]
    [InlineData("/api/healthcheck")]
    [InlineData("/api/vigie/rapport")]
    [InlineData("/api/inter-serveurs/retrait")]
    [InlineData("/api/inter-serveurs/alerte")]
    [InlineData("/index.html")]
    [InlineData("/mobile/")]
    [InlineData("/apix/test")]
    public async Task Routes_non_relayables_jamais_transmises(string chemin)
    {
        var v = new Veille();
        Assert.False(await v.Tenter(v.Poste("GET", chemin)));
        Assert.Equal(0, v.Fabrique.Appels);
    }

    [Fact]
    public async Task Poste_sans_en_tete_ou_avec_nom_douteux_ou_methode_trace_connect_refuse()
    {
        var v = new Veille();
        Assert.False(await v.Tenter(v.Poste(poste: "")));
        Assert.False(await v.Tenter(v.Poste(poste: new string('x', 101))));
        Assert.False(await v.Tenter(v.Poste(poste: "a\u0001b")));
        Assert.False(await v.Tenter(v.Poste("TRACE")));
        Assert.False(await v.Tenter(v.Poste("CONNECT")));
        var deux = v.Poste();
        deux.Request.Headers["X-Pharma-Poste"] = new Microsoft.Extensions.Primitives.StringValues(["A", "B"]);
        Assert.False(await v.Tenter(deux));
        Assert.Equal(0, v.Fabrique.Appels);
    }

    [Fact]
    public async Task Serveur_1_injoignable_pendant_l_appel_503_standard_et_une_seule_tentative()
    {
        var v = new Veille((_, _) => throw new HttpRequestException("connexion refusée"));
        var ctx = v.Poste("POST", "/api/caisse/ventes", corps: "{}");
        Assert.False(await v.Tenter(ctx));
        Assert.Equal(1, v.Fabrique.Appels);   // aucun renvoi automatique
        Assert.Equal(200, ctx.Response.StatusCode);   // rien d'écrit : le barrage répond 503

        var barrage = Barrage(v.Relais);
        await BarrageServeurVeille.Filtrer(barrage, _ => Task.CompletedTask);
        Assert.Equal(503, barrage.Response.StatusCode);
    }

    [Fact]
    public void Limite_de_debit_du_relais_dictionnaire_borne_meme_sous_un_flot_de_noms_et_d_adresses()
    {
        var h = new HorlogeFictive(T0.UtcDateTime);
        var l = new LimiteRelais(h);
        for (var i = 0; i < 20_000; i++) Assert.True(l.Autoriser("P" + i, "10.0." + (i / 250) + "." + (i % 250)));
        Assert.True(l.Taille <= 2000, "taille " + l.Taille);
    }

    [Fact]
    public async Task Jeton_sur_le_healthcheck_base_illisible_pas_d_erreur_le_jeton_est_simplement_tenu_pour_non_valide()
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<Pharma.Application.Securite.IEtatConnexions>(new EtatIllisible());
        var http = new DefaultHttpContext { RequestServices = sc.BuildServiceProvider() };
        var schema = new Microsoft.AspNetCore.Authentication.AuthenticationScheme("Bearer", null, typeof(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler));
        TokenValidatedContext Contexte(string chemin)
        {
            http.Request.Path = chemin;
            return new Microsoft.AspNetCore.Authentication.JwtBearer.TokenValidatedContext(http, schema, new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions())
            {
                Principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "7")], "Bearer"))
            };
        }
        // Healthcheck : aucune exception, jeton refusé (le poste n'est pas compté « vu »)
        var hc = Contexte("/api/healthcheck");
        await ValidationJeton.VerifierAsync(hc);
        Assert.NotNull(hc.Result);
        Assert.False(hc.Result!.Succeeded);
        // Toute autre route : comportement inchangé (l'erreur de base remonte)
        await Assert.ThrowsAsync<InvalidOperationException>(() => ValidationJeton.VerifierAsync(Contexte("/api/caisse/ventes")));
    }

    private sealed class EtatIllisible : Pharma.Application.Securite.IEtatConnexions
    {
        public Task<Pharma.Application.Securite.EtatConnexion?> LireAsync(long utilisateurId, CancellationToken ct = default) => throw new InvalidOperationException("base illisible");
    }

    [Fact]
    public async Task Poste_parti_pendant_l_appel_rien_a_repondre()
    {
        var v = new Veille(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); });
        using var parti = new CancellationTokenSource();
        var ctx = v.Poste();
        var tache = v.Relais.TenterAsync(ctx, parti.Token);
        await Task.Delay(50);
        await parti.CancelAsync();
        Assert.True(await tache);   // le poste a abandonné : traité, rien à écrire
        Assert.Equal(200, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Officine_a_un_seul_serveur_relais_inactif_et_non_enregistre()
    {
        // Pas de 2e serveur déclaré : aucune adresse d'API, rien n'est envoyé
        var v = new Veille(autre: null);
        Assert.False(await v.Tenter(v.Poste()));
        Assert.Equal(0, v.Fabrique.Appels);
        // Pas de clé : inactif aussi
        var s = new Veille(sansCle: true);
        Assert.False(await s.Tenter(s.Poste()));
        Assert.Equal(0, s.Fabrique.Appels);

        // Enregistrement : rien sans 2e serveur déclaré, ni sans clé
        var sc = new ServiceCollection().AjouterRelaisVeille(Config(autre: null));
        Assert.DoesNotContain(sc, d => d.ServiceType == typeof(IRelaisVeille));
        var sansCle = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["SecondServeur:AutreServeur"] = "192.168.1.10" }).Build();
        Assert.DoesNotContain(new ServiceCollection().AjouterRelaisVeille(sansCle), d => d.ServiceType == typeof(IRelaisVeille));
        // Deux serveurs et clé : enregistré
        Assert.Contains(new ServiceCollection().AjouterRelaisVeille(Config()), d => d.ServiceType == typeof(IRelaisVeille));

        // Barrage sans relais enregistré : 503 comme avant
        var sc2 = new ServiceCollection();
        sc2.AddLogging(); sc2.AddProblemDetails(); sc2.AddSingleton<IVeilleServeur>(new VeilleServeur(true));
        var ctx = new DefaultHttpContext { RequestServices = sc2.BuildServiceProvider() };
        ctx.Request.Path = "/api/caisse/ventes";
        ctx.Response.Body = new MemoryStream();
        await BarrageServeurVeille.Filtrer(ctx, _ => Task.CompletedTask);
        Assert.Equal(503, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Sur_un_seul_serveur_les_en_tetes_de_relais_sont_ignores_par_le_serveur_1()
    {
        var p = new Principal(autre: null);
        var origine = new Veille().Signature.SignerRelais("CAISSE-1", AdressePoste, "GET", "/api/a");
        var ctx = p.Contexte(AdresseDeuxiemeServeur, "GET", "/api/a", Entetes(origine));
        await p.Executer(ctx);
        Assert.Equal(1, p.Passages);
        Assert.Equal(IPAddress.Parse(AdresseDeuxiemeServeur), p.AdresseVue);
        Assert.False(p.Via);
    }

    // ===================================================================================================================
    // Alerte « reseau_poste »
    // ===================================================================================================================
    [Fact]
    public async Task Alerte_reseau_poste_une_fois_par_heure_et_par_poste()
    {
        var v = new Veille();
        Assert.True(await v.Tenter(v.Poste(poste: "CAISSE-1")));
        Assert.True(await v.Tenter(v.Poste(poste: "CAISSE-1")));
        Assert.True(await v.Tenter(v.Poste(poste: "CAISSE-2")));
        await AttendreAlertes(v, 2);
        Assert.Equal(2, v.Alertes.Envoyees.Count);
        Assert.All(v.Alertes.Envoyees, a => Assert.Equal("reseau_poste", a.Type));
        Assert.Equal(["CAISSE-1", "CAISSE-2"], v.Alertes.Envoyees.Select(a => a.Poste).Order());
        Assert.Contains("Problème réseau entre le poste CAISSE-1 et le serveur 1 : ce poste passe par le 2e serveur", v.Alertes.Envoyees.Select(a => a.Message));
        await Task.Delay(100);   // l'heure de l'alerte partie est notée juste après l'envoi

        v.Horloge.MaintenantUtc = T0.UtcDateTime.AddMinutes(59);
        Assert.True(await v.Tenter(v.Poste(poste: "CAISSE-1")));
        await Task.Delay(100);
        Assert.Equal(2, v.Alertes.Envoyees.Count);

        v.Horloge.MaintenantUtc = T0.UtcDateTime.AddMinutes(61);
        Assert.True(await v.Tenter(v.Poste(poste: "CAISSE-1")));
        await AttendreAlertes(v, 3);
        Assert.Equal(3, v.Alertes.Envoyees.Count);
    }

    private static async Task AttendreAlertes(Veille v, int n)
    {
        for (var i = 0; i < 100 && v.Alertes.Envoyees.Count < n; i++) await Task.Delay(20);
    }

    [Fact]
    public async Task Alerte_seulement_pour_un_poste_connu_dont_le_rapport_est_authentifie()
    {
        var v = new Veille();
        v.Situation.AlerteOk = false;   // poste inconnu de la copie, ou rapport anonyme
        Assert.True(await v.Tenter(v.Poste(poste: "CAISSE-1")));   // le relais lui-même n'est pas refusé
        await Task.Delay(100);
        Assert.Empty(v.Alertes.Envoyees);
    }

    [Fact]
    public async Task Plafond_global_de_cinq_alertes_par_heure_tous_postes_confondus()
    {
        var v = new Veille();
        for (var i = 0; i < 12; i++) Assert.True(await v.Tenter(v.Poste(poste: "CAISSE-" + i)));
        await AttendreAlertes(v, 5);
        await Task.Delay(100);
        Assert.Equal(AlerteRelaisPoste.PlafondParHeure, v.Alertes.Envoyees.Count);
        v.Horloge.MaintenantUtc = T0.UtcDateTime.AddMinutes(61);
        Assert.True(await v.Tenter(v.Poste(poste: "CAISSE-20")));
        await AttendreAlertes(v, 6);
        Assert.Equal(6, v.Alertes.Envoyees.Count);
    }

    // ===================================================================================================================
    // Cache de nonces distinct et limite de débit du relais
    // ===================================================================================================================
    [Fact]
    public void Un_flot_d_annonces_de_relais_ne_sature_pas_le_cache_des_appels_etat_retrait_alerte()
    {
        var (e, r, _) = Paire();
        var avant = r.TaillesCachesNonces;
        for (var i = 0; i < 2000; i++) Assert.Null(r.VerifierRelais("GET", "/api/a", Recus(e.SignerRelais("P", AdressePoste, "GET", "/api/a"))));
        var apres = r.TaillesCachesNonces;
        Assert.Equal(avant.Appels, apres.Appels);   // aucun nonce de relais dans le cache des appels inter-serveurs
        Assert.Equal(avant.Relais + 2000, apres.Relais);
        // Les appels signés (état, retrait, alerte) restent acceptés
        var appel = e.SignerRequete("POST", "/api/inter-serveurs/alerte", []);
        Assert.Null(r.VerifierRequete("POST", "/api/inter-serveurs/alerte", new EnTetesRecus(appel.Serveur, appel.Horodatage, appel.Nonce, appel.Signature), []));
    }

    [Fact]
    public void Le_cache_du_relais_plein_refuse_les_annonces_de_relais_mais_jamais_les_appels_inter_serveurs()
    {
        var h = new Temps(T0);
        var emetteur = new SignatureInterServeurs(Cle, "SERVEUR2", 60, h);
        var recepteur = new SignatureInterServeurs(Cle, "SERVEUR1", 60, h);
        for (var i = 0; i < SignatureInterServeurs.CapaciteNoncesRelais; i++)
            if (recepteur.VerifierRelais("GET", "/api/a", Recus(emetteur.SignerRelais("P", AdressePoste, "GET", "/api/a"))) is not null) Assert.Fail("annonce refusée avant saturation");
        // Cache du relais saturé : l'annonce suivante est refusée (traitée sans adresse d'origine par le serveur 1)
        Assert.NotNull(recepteur.VerifierRelais("GET", "/api/a", Recus(emetteur.SignerRelais("P", AdressePoste, "GET", "/api/a"))));
        // état, retrait, alerte : toujours acceptés
        foreach (var route in new[] { "/api/inter-serveurs/etat", "/api/inter-serveurs/retrait", "/api/inter-serveurs/alerte" })
        {
            var s = emetteur.SignerRequete("POST", route, []);
            Assert.Null(recepteur.VerifierRequete("POST", route, new EnTetesRecus(s.Serveur, s.Horodatage, s.Nonce, s.Signature), []));
        }
    }

    [Fact]
    public async Task Cache_de_nonces_du_relais_plein_le_serveur_1_refuse_la_requete_en_503_au_lieu_de_la_traiter_avec_l_adresse_du_2e_serveur()
    {
        var p = new Principal();
        var emetteur = new SignatureInterServeurs(Cle, "SERVEUR2", 60, p.Temps);
        for (var i = 0; i < SignatureInterServeurs.CapaciteNoncesRelais; i++)
            if (p.Signature.VerifierRelais("GET", "/api/a", Recus(emetteur.SignerRelais("P", AdressePoste, "GET", "/api/a"))) is not null) Assert.Fail("annonce refusée avant saturation");
        var ctx = p.Contexte(AdresseDeuxiemeServeur, "GET", "/api/catalogue/produits", Entetes(Annonce(p)));
        await p.Executer(ctx);
        Assert.Equal(0, p.Passages);   // jamais traitée
        Assert.Equal(503, ctx.Response.StatusCode);
        Assert.Contains("relais_indisponible", Texte(ctx));
    }

    [Fact]
    public async Task Limite_de_debit_du_relais_par_poste_429_puis_reprise_apres_une_minute()
    {
        var v = new Veille(parMinute: 3);
        for (var i = 0; i < 3; i++) Assert.True(await v.Tenter(v.Poste()));
        var refuse = v.Poste();
        Assert.True(await v.Tenter(refuse));   // traité par le relais : 429, rien n'est transmis
        Assert.Equal(429, refuse.Response.StatusCode);
        Assert.Equal(3, v.Fabrique.Appels);
        v.Horloge.MaintenantUtc = T0.UtcDateTime.AddSeconds(61);
        Assert.True(await v.Tenter(v.Poste()));
        Assert.Equal(4, v.Fabrique.Appels);
    }

    [Fact]
    public async Task Limite_de_debit_du_relais_par_adresse_d_origine_meme_en_changeant_de_nom_de_poste()
    {
        var v = new Veille(parMinute: 3);
        for (var i = 0; i < 3; i++) Assert.True(await v.Tenter(v.Poste(poste: "P" + i)));
        var refuse = v.Poste(poste: "P-nouveau");
        Assert.True(await v.Tenter(refuse));
        Assert.Equal(429, refuse.Response.StatusCode);
        // Une autre adresse d'origine n'est pas pénalisée
        var autre = v.Poste(poste: "P-autre", ip: "192.168.1.51");
        Assert.True(await v.Tenter(autre));
        Assert.Equal(200, autre.Response.StatusCode);
    }

    [Fact]
    public async Task Alerte_non_envoyee_n_est_retentee_qu_apres_une_minute()
    {
        var horloge = new HorlogeFictive(T0.UtcDateTime);
        var alertes = new AlertesFictives(reussit: false);
        var a = new AlerteRelaisPoste(alertes, horloge);
        await a.Signaler("P1")!;
        Assert.Null(a.Signaler("P1"));
        horloge.MaintenantUtc = T0.UtcDateTime.AddSeconds(61);
        alertes.Reussit = true;
        await a.Signaler("P1")!;
        Assert.Equal(2, alertes.Envoyees.Count);
        horloge.MaintenantUtc = T0.UtcDateTime.AddMinutes(10);
        Assert.Null(a.Signaler("P1"));   // partie : plus rien pendant une heure
    }

    [Fact]
    public async Task L_alerte_ne_retarde_jamais_le_relais_meme_si_l_envoi_echoue()
    {
        var v = new Veille();
        v.Alertes.Reussit = false;
        Assert.True(await v.Tenter(v.Poste()));
    }

    // ===================================================================================================================
    // Serveur 1 : adresse d'origine
    // ===================================================================================================================
    private static IEnumerable<KeyValuePair<string, string>> Entetes(EnTetesRelaisSignes s) =>
    [
        new(EnTetesRelais.Serveur, s.Serveur), new(EnTetesRelais.Poste, s.Poste), new(EnTetesRelais.Origine, s.Origine),
        new(EnTetesRelais.Horodatage, s.Horodatage), new(EnTetesRelais.Nonce, s.Nonce), new(EnTetesRelais.Signature, s.Signature)
    ];

    private static EnTetesRelaisSignes Annonce(Principal p, string poste = "CAISSE-1", string origine = AdressePoste, string methode = "GET", string cible = "/api/catalogue/produits",
        string cle = Cle, DateTimeOffset? quand = null)
    {
        var t = new Temps(quand ?? T0);
        return new SignatureInterServeurs(cle, "SERVEUR2", 60, t).SignerRelais(poste, origine, methode, cible);
    }

    [Fact]
    public async Task Serveur_1_signature_valide_et_appel_du_2e_serveur_l_adresse_d_origine_est_reprise()
    {
        var p = new Principal();
        var ctx = p.Contexte(AdresseDeuxiemeServeur, "GET", "/api/catalogue/produits", Entetes(Annonce(p)));
        await p.Executer(ctx);
        Assert.Equal(IPAddress.Parse(AdressePoste), p.AdresseVue);
        Assert.True(p.Via);
    }

    [Fact]
    public async Task Serveur_1_origine_ipv6_mappee_reprise_en_ipv4()
    {
        var p = new Principal();
        var annonce = Annonce(p, origine: "::ffff:192.168.1.77");
        var ctx = p.Contexte(AdresseDeuxiemeServeur, "GET", "/api/catalogue/produits", Entetes(annonce));
        await p.Executer(ctx);
        Assert.Equal(IPAddress.Parse("192.168.1.77"), p.AdresseVue);
    }

    [Fact]
    public async Task Serveur_1_signature_fausse_en_tete_ignore_requete_traitee_avec_l_adresse_reelle_sans_erreur()
    {
        var p = new Principal();
        var annonce = Annonce(p) with { Origine = "192.168.1.99" };   // origine modifiée après signature
        var ctx = p.Contexte(AdresseDeuxiemeServeur, "GET", "/api/catalogue/produits", Entetes(annonce));
        await p.Executer(ctx);
        Assert.Equal(1, p.Passages);
        Assert.Equal(201, ctx.Response.StatusCode);   // la requête est traitée normalement : aucune erreur qui révèle quoi que ce soit
        Assert.Equal(IPAddress.Parse(AdresseDeuxiemeServeur), p.AdresseVue);
        Assert.False(p.Via);
    }

    [Fact]
    public async Task Serveur_1_signature_d_une_autre_cle_ignoree()
    {
        var p = new Principal();
        var ctx = p.Contexte(AdresseDeuxiemeServeur, "GET", "/api/catalogue/produits", Entetes(Annonce(p, cle: AutreCle)));
        await p.Executer(ctx);
        Assert.Equal(IPAddress.Parse(AdresseDeuxiemeServeur), p.AdresseVue);
        Assert.False(p.Via);
    }

    [Fact]
    public async Task Serveur_1_annonce_rejouee_ignoree()
    {
        var p = new Principal();
        var annonce = Annonce(p);
        await p.Executer(p.Contexte(AdresseDeuxiemeServeur, "GET", "/api/catalogue/produits", Entetes(annonce)));
        Assert.True(p.Via);
        await p.Executer(p.Contexte(AdresseDeuxiemeServeur, "GET", "/api/catalogue/produits", Entetes(annonce)));
        Assert.False(p.Via);
        Assert.Equal(IPAddress.Parse(AdresseDeuxiemeServeur), p.AdresseVue);
        Assert.Equal(2, p.Passages);
    }

    [Fact]
    public async Task Serveur_1_annonce_perimee_ignoree()
    {
        var p = new Principal();
        var annonce = Annonce(p, quand: T0.AddSeconds(-120));
        var ctx = p.Contexte(AdresseDeuxiemeServeur, "GET", "/api/catalogue/produits", Entetes(annonce));
        await p.Executer(ctx);
        Assert.False(p.Via);
        Assert.Equal(IPAddress.Parse(AdresseDeuxiemeServeur), p.AdresseVue);
    }

    [Fact]
    public async Task Serveur_1_annonce_reutilisee_sur_une_autre_route_ou_methode_ignoree()
    {
        var p = new Principal();
        var annonce = Annonce(p, cible: "/api/catalogue/produits");
        await p.Executer(p.Contexte(AdresseDeuxiemeServeur, "GET", "/api/caisse/ventes", Entetes(annonce)));
        Assert.False(p.Via);
        await p.Executer(p.Contexte(AdresseDeuxiemeServeur, "DELETE", "/api/catalogue/produits", Entetes(annonce)));
        Assert.False(p.Via);
    }

    [Theory]
    [InlineData("192.168.1.99")]   // autre poste du réseau
    [InlineData("10.0.0.5")]
    [InlineData("203.0.113.9")]    // adresse publique
    [InlineData("127.0.0.1")]      // le PC serveur lui-même
    public async Task Serveur_1_appel_ne_venant_pas_de_l_adresse_declaree_du_2e_serveur_en_tete_ignore(string appelant)
    {
        var p = new Principal();
        var ctx = p.Contexte(appelant, "GET", "/api/catalogue/produits", Entetes(Annonce(p, origine: "192.168.1.77")));
        await p.Executer(ctx);
        Assert.Equal(1, p.Passages);
        Assert.Equal(IPAddress.Parse(appelant), p.AdresseVue);
        Assert.False(p.Via);
    }

    [Fact]
    public async Task Serveur_1_en_tetes_absents_incomplets_ou_doubles_ignores_sans_erreur()
    {
        var p = new Principal();
        var annonce = Annonce(p);
        var sans = Entetes(annonce).Where(h => h.Key != EnTetesRelais.Nonce).ToList();
        var ctx = p.Contexte(AdresseDeuxiemeServeur, "GET", "/api/catalogue/produits", sans);
        await p.Executer(ctx);
        Assert.False(p.Via);

        var double_ = p.Contexte(AdresseDeuxiemeServeur, "GET", "/api/catalogue/produits", Entetes(Annonce(p)));
        double_.Request.Headers[EnTetesRelais.Origine] = new Microsoft.Extensions.Primitives.StringValues([AdressePoste, "10.1.1.1"]);
        await p.Executer(double_);
        Assert.False(p.Via);
        Assert.Equal(IPAddress.Parse(AdresseDeuxiemeServeur), p.AdresseVue);
    }

    [Fact]
    public async Task Serveur_1_en_veille_ou_sans_cle_ignore_les_en_tetes()
    {
        var veille = new Principal(veille: true);
        await veille.Executer(veille.Contexte(AdresseDeuxiemeServeur, "GET", "/api/catalogue/produits", Entetes(Annonce(veille))));
        Assert.False(veille.Via);

        var sansCle = new Principal(sansCle: true);
        await sansCle.Executer(sansCle.Contexte(AdresseDeuxiemeServeur, "GET", "/api/catalogue/produits", Entetes(Annonce(sansCle))));
        Assert.False(sansCle.Via);
        Assert.Equal(IPAddress.Parse(AdresseDeuxiemeServeur), sansCle.AdresseVue);
    }

    // ===================================================================================================================
    // Aucun privilège local par relais
    // ===================================================================================================================
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("255.255.255.255")]
    [InlineData("224.0.0.1")]
    [InlineData("ff02::1")]
    public async Task Origine_locale_annoncee_jamais_reprise_et_la_requete_reste_marquee_via_relais(string origine)
    {
        var p = new Principal();
        var ctx = p.Contexte(AdresseDeuxiemeServeur, "GET", "/api/catalogue/produits", Entetes(Annonce(p, origine: origine)));
        await p.Executer(ctx);
        Assert.True(p.Via);   // authentique, mais marquée : jamais de privilège « PC serveur »
        Assert.False(p.AdresseVue is { } a && IPAddress.IsLoopback(a));
        Assert.Equal(IPAddress.Parse(AdresseDeuxiemeServeur), p.AdresseVue);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task Le_2e_serveur_sur_le_meme_PC_ne_donne_pas_le_privilege_local_aux_requetes_relayees(string adresse2eServeur)
    {
        var p = new Principal(autre: adresse2eServeur);
        var ctx = p.Contexte(adresse2eServeur, "GET", "/mobile/autorite-pharmacie.crt", Entetes(Annonce(p, cible: "/mobile/autorite-pharmacie.crt")));
        await p.Executer(ctx);
        Assert.True(p.Via);
        Assert.Equal(IPAddress.Parse(AdressePoste), p.AdresseVue);   // origine reprise, non locale

        // Origine locale annoncée : l'adresse réelle reste la boucle locale, mais le marquage interdit le privilège
        var ctx2 = p.Contexte(adresse2eServeur, "GET", "/mobile/autorite-pharmacie.crt", Entetes(Annonce(p, origine: "127.0.0.1", cible: "/mobile/autorite-pharmacie.crt")));
        await p.Executer(ctx2);
        Assert.True(p.Via);
        Assert.True(ReseauSecurise.CertificatAutoriteRefuse(new PathString("/mobile/autorite-pharmacie.crt"), IPAddress.Loopback, viaRelais: true));
    }

    [Fact]
    public void Certificat_de_l_autorite_toujours_servi_au_PC_serveur_hors_relais_et_refuse_par_relais()
    {
        var chemin = new PathString(ReseauSecurise.CheminCertificatAutorite);
        Assert.False(ReseauSecurise.CertificatAutoriteRefuse(chemin, IPAddress.Loopback));
        Assert.False(ReseauSecurise.CertificatAutoriteRefuse(chemin, IPAddress.IPv6Loopback, viaRelais: false));
        Assert.True(ReseauSecurise.CertificatAutoriteRefuse(chemin, IPAddress.Loopback, viaRelais: true));
        Assert.True(ReseauSecurise.CertificatAutoriteRefuse(chemin, IPAddress.Parse(AdressePoste)));
        Assert.True(ReseauSecurise.CertificatAutoriteRefuse(chemin, null));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("192.168.1.5")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:10.0.0.1")]
    public void Lecture_de_l_origine_annoncee(string texte)
    {
        var a = OrigineRelais.Lire(texte);
        if (IPAddress.TryParse(texte, out var brute) && IPAddress.IsLoopback(brute.IsIPv4MappedToIPv6 ? brute.MapToIPv4() : brute)) Assert.Null(a);
        else Assert.NotNull(a);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pas-une-adresse")]
    [InlineData("192.168.1.5; DROP")]
    public void Origine_illisible_refusee(string? texte) => Assert.Null(OrigineRelais.Lire(texte));

    /// <summary>
    /// Garde-fou : les seuls fichiers de l'API qui lisent l'adresse de l'appelant ou testent la boucle locale sont ceux listés ici, tous revus pour le relais
    /// (limite de débit, certificat de l'autorité, redirection HTTPS, appels signés, vigie, relais). Un nouveau fichier qui donnerait un privilège selon
    /// l'adresse fait échouer ce test : il doit alors refuser les requêtes relayées (RelaisOrigineMiddleware.ViaRelais).
    /// </summary>
    [Fact]
    public void Aucune_regle_d_adresse_hors_des_fichiers_revus_pour_le_relais()
    {
        var revus = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ReseauSecurise.cs", "InterServeurs.cs", "VigieEndpoints.cs", "RelaisOrigineMiddleware.cs", "RelaisVeille.cs", "ProxysInverses.cs", "Program.cs"
        };
        var dossier = Path.Combine(Racine(), "src", "Pharma.Api");
        var motif = new Regex(@"RemoteIpAddress|IsLoopback|LocalIpAddress|IPAddress\.Loopback", RegexOptions.Compiled);
        var intrus = Directory.EnumerateFiles(dossier, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => !revus.Contains(Path.GetFileName(f)) && motif.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(dossier, f)).ToList();
        Assert.True(intrus.Count == 0, "Règle d'adresse non revue pour le relais : " + string.Join(", ", intrus));
        // Program.cs : le seul usage est le certificat de l'autorité, qui reçoit le marquage « via relais »
        var program = File.ReadAllText(Path.Combine(dossier, "Program.cs"));
        Assert.Contains("CertificatAutoriteRefuse(ctx.Request.Path, ctx.Connection.RemoteIpAddress, Pharma.Api.Securite.RelaisOrigineMiddleware.ViaRelais(ctx))", program);
        Assert.Single(Regex.Matches(program, "RemoteIpAddress"));
    }

    // ===================================================================================================================
    // Limite de débit sur l'adresse d'origine
    // ===================================================================================================================
    [Fact]
    public async Task Limite_de_debit_appliquee_a_l_adresse_d_origine_et_non_a_celle_du_2e_serveur()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AjouterLimiteConnexions(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["LimiteConnexions:ParMinute"] = "2" }).Build());
        var limiteur = sc.BuildServiceProvider().GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter!;
        var p = new Principal();

        async Task<bool> Tente(string origine)
        {
            var ctx = p.Contexte(AdresseDeuxiemeServeur, "POST", "/api/auth/connexion", Entetes(Annonce(p, origine: origine, methode: "POST", cible: "/api/auth/connexion")));
            await p.Executer(ctx);
            using var bail = limiteur.AttemptAcquire(ctx);
            return bail.IsAcquired;
        }

        Assert.True(await Tente("192.168.1.51"));
        Assert.True(await Tente("192.168.1.51"));
        Assert.False(await Tente("192.168.1.51"));   // 3e essai du même poste : refusé
        Assert.True(await Tente("192.168.1.52"));    // un autre poste relayé n'est pas pénalisé
        Assert.True(await Tente("192.168.1.52"));
        Assert.False(await Tente("192.168.1.52"));

        // Sans signature valide, le même flot est limité sur l'adresse réelle (celle du 2e serveur), jamais sur une adresse annoncée
        var sans = p.Contexte(AdresseDeuxiemeServeur, "POST", "/api/auth/connexion");
        await p.Executer(sans);
        using var b1 = limiteur.AttemptAcquire(sans);
        Assert.True(b1.IsAcquired);
    }

    // ===================================================================================================================
    // De bout en bout en mémoire : poste -> 2e serveur (relais) -> serveur 1
    // ===================================================================================================================
    private static (Veille Veille, Principal Principal) Chaine()
    {
        var principal = new Principal();
        var veille = new Veille((req, _) => principal.Repondre(req), principal.Temps);
        return (veille, principal);
    }

    [Fact]
    public async Task Bout_en_bout_la_requete_arrive_au_serveur_1_avec_l_adresse_du_poste_son_jeton_et_son_corps()
    {
        var (v, p) = Chaine();
        var ctx = v.Poste("POST", "/api/caisse/ventes?du=2026-11-01", corps: "{\"clef\":\"abc\"}");

        Assert.True(await v.Tenter(ctx));

        Assert.Equal(1, p.Passages);
        Assert.True(p.Via);
        Assert.Equal(IPAddress.Parse(AdressePoste), p.AdresseVue);   // limites et règles d'adresse du serveur 1 voient le poste
        Assert.Equal("POST", p.MethodeVue);
        Assert.Equal("/api/caisse/ventes?du=2026-11-01", p.CibleVue);
        Assert.Equal("CAISSE-1", p.PosteVu);                         // la présence est notée comme en direct (X-Pharma-Poste inchangé)
        Assert.Equal("Bearer jeton-du-poste", p.Autorisation);       // vérifié par le serveur 1 lui-même
        Assert.Equal("{\"clef\":\"abc\"}", p.CorpsVu);
        Assert.Equal(201, ctx.Response.StatusCode);
        Assert.Equal("{\"ok\":true}", Texte(ctx));
        Assert.Equal("oui", ctx.Response.Headers["X-Test"].ToString());
        Assert.False(ctx.Response.Headers.ContainsKey("Connection"));
    }

    [Fact]
    public async Task Bout_en_bout_le_poste_ne_peut_pas_forger_son_adresse_d_origine()
    {
        var (v, p) = Chaine();
        var ctx = v.Poste(entetes: h =>
        {
            h["X-Pharma-Relais-Origine"] = "127.0.0.1";
            h["X-Pharma-Relais-Poste"] = "AUTRE";
            h["X-Forwarded-For"] = "127.0.0.1";
        });
        Assert.True(await v.Tenter(ctx));
        Assert.Equal(IPAddress.Parse(AdressePoste), p.AdresseVue);
        Assert.Equal("CAISSE-1", p.PosteVu);
        Assert.False(p.EnTetesVus!.ContainsKey("X-Forwarded-For"));
    }

    [Fact]
    public async Task Bout_en_bout_le_poste_qui_s_adresse_directement_au_serveur_1_ne_peut_pas_se_faire_passer_pour_un_relais()
    {
        var principal = new Principal();
        // Un poste du réseau envoie lui-même des en-têtes de relais : même avec une signature valide (clé connue), l'appel ne vient pas du 2e serveur
        var annonce = Annonce(principal, origine: "192.168.1.77");
        var ctx = principal.Contexte(AdressePoste, "GET", "/api/catalogue/produits", Entetes(annonce));
        await principal.Executer(ctx);
        Assert.Equal(IPAddress.Parse(AdressePoste), principal.AdresseVue);
        Assert.False(principal.Via);
    }

    [Fact]
    public async Task Bout_en_bout_serveur_1_avec_une_autre_cle_ignore_l_annonce_et_traite_quand_meme()
    {
        var principal = new Principal(cle: AutreCle);
        var veille = new Veille((req, _) => principal.Repondre(req), principal.Temps);
        var ctx = veille.Poste();
        Assert.True(await veille.Tenter(ctx));
        Assert.Equal(201, ctx.Response.StatusCode);
        Assert.False(principal.Via);
        Assert.Equal(IPAddress.Parse(AdresseDeuxiemeServeur), principal.AdresseVue);
    }

    // ===================================================================================================================
    // Délais et taille de corps
    // ===================================================================================================================
    [Theory]
    [InlineData("/api/caisse/ventes", 30)]
    [InlineData("/api/catalogue/produits", 30)]
    [InlineData("/api/factures/12/pdf", 120)]
    [InlineData("/api/stock/export", 120)]
    [InlineData("/api/catalogue/etiquettes", 120)]
    [InlineData("/api/analyses/rapport", 120)]
    [InlineData("/api/ged/ordonnances", 600)]
    [InlineData("/api/admin/sauvegardes/lancer", 600)]
    [InlineData("/api/admin/nomenclature", 600)]
    [InlineData("/api/admin/reprise/importer", 600)]
    [InlineData("/api/super-admin/restauration", 600)]
    [InlineData("/api/mobile/reception/scans/importer", 600)]
    public void Delais_du_relais_identiques_a_ceux_du_poste(string chemin, int secondes) =>
        Assert.Equal(TimeSpan.FromSeconds(secondes), DelaisRelais.Pour(chemin));

    [Fact]
    public void Les_delais_du_relais_restent_ceux_du_poste_lus_dans_le_code_du_poste()
    {
        var source = File.ReadAllText(Path.Combine(Racine(), "src", "Pharma.Desktop", "Services", "AppelsEnCours.cs"));
        string[] Liste(string nom)
        {
            var bloc = Regex.Match(source, nom + @"\s*=\s*\[(?<l>.*?)\];", RegexOptions.Singleline).Groups["l"].Value;
            return Regex.Matches(bloc, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
        }
        Assert.Equal(Liste("Lourds"), DelaisRelais.Lourds);
        Assert.Equal(Liste("Documents"), DelaisRelais.Documents);
        Assert.Contains("TimeSpan.FromSeconds(30)", source);
        Assert.Contains("Document = TimeSpan.FromMinutes(2)", source);
        Assert.Contains("Lourd = TimeSpan.FromMinutes(10)", source);
    }

    [Fact]
    public void Taille_de_corps_relevee_comme_sur_l_api()
    {
        Assert.Equal(Pharma.Application.Ged.DeposerDocumentGed.TailleMax + 1L, DelaisRelais.TailleCorpsMax("/api/ged/ordonnances"));
        Assert.Equal(Pharma.Application.Catalogue.ImporterNomenclature.TailleMax, DelaisRelais.TailleCorpsMax("/api/admin/nomenclature"));
        Assert.Null(DelaisRelais.TailleCorpsMax("/api/caisse/ventes"));
    }

    private static string Racine()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } racine) return racine;
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }
}
