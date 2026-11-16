using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Pharma.Api.Securite;

namespace Pharma.Architecture.Tests;

/// <summary>HTTP en clair sur le réseau, HTTPS, routes limitées en débit et fuseau horaire (revue de sécurité E2, M1, I-1).</summary>
public class ReseauSecuriseTests
{
    private static IConfiguration Config(params (string Cle, string Valeur)[] valeurs) =>
        new ConfigurationBuilder().AddInMemoryCollection(valeurs.Select(v => new KeyValuePair<string, string?>(v.Cle, v.Valeur))).Build();

    [Theory]
    [InlineData("http://0.0.0.0:5080")]
    [InlineData("http://*:5080")]
    [InlineData("http://192.168.1.10:5080")]
    [InlineData("http://localhost:5080;http://+:5081")]
    public void Http_sur_le_reseau_refuse_au_demarrage_en_production(string urls)
    {
        Assert.NotNull(ReseauSecurise.RefusDemarrage(Config(("Urls", urls)), developpement: false));
        Assert.Null(ReseauSecurise.RefusDemarrage(Config(("Urls", urls)), developpement: true));
        Assert.Null(ReseauSecurise.RefusDemarrage(Config(("Urls", urls), ("AutoriserHttpReseau", "true")), developpement: false));
    }

    [Theory]
    [InlineData("http://localhost:5080")]
    [InlineData("http://127.0.0.1:5080")]
    [InlineData("http://[::1]:5080")]
    [InlineData("https://0.0.0.0:5443")]
    public void Http_local_ou_https_accepte(string urls) =>
        Assert.Null(ReseauSecurise.RefusDemarrage(Config(("Urls", urls)), developpement: false));

    [Fact]
    public void Les_points_d_ecoute_kestrel_priment_sur_urls()
    {
        var config = Config(("Urls", "http://0.0.0.0:5080"),
            ("Kestrel:Endpoints:Http:Url", "http://localhost:5080"), ("Kestrel:Endpoints:Https:Url", "https://0.0.0.0:5443"));
        Assert.Null(ReseauSecurise.RefusDemarrage(config, developpement: false));
        Assert.True(ReseauSecurise.HttpsConfigure(config));
        var mobile = Config(("Kestrel:Endpoints:Http:Url", "http://0.0.0.0:5080"), ("Kestrel:Endpoints:Https:Url", "https://0.0.0.0:5443"));
        Assert.NotNull(ReseauSecurise.RefusDemarrage(mobile, developpement: false));
    }

    [Fact]
    public void Configuration_ecrite_par_le_script_donne_http_localhost_et_https_reseau_sans_autorisation_http_reseau()
    {
        var script = File.ReadAllText(Path.Combine(RacineDepot(), "scripts", "certificat_https.ps1"));
        Assert.Contains("Url = \"http://localhost:$PortHttp\"", script);
        Assert.Contains("Url         = \"https://0.0.0.0:$PortHttps\"", script);
        Assert.DoesNotContain("http://0.0.0.0", script);
        Assert.DoesNotContain("AutoriserHttpReseau=true", script);

        // Même forme que le fichier écrit par le script (appsettings.Https.json)
        const string json = """
            { "Kestrel": { "Endpoints": {
                "Http": { "Url": "http://localhost:5080" },
                "Https": { "Url": "https://0.0.0.0:5443", "Certificate": { "Subject": "PharmaOfficine Mobile", "Store": "My", "Location": "LocalMachine", "AllowInvalid": true } } } } }
            """;
        var config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json))).Build();
        Assert.Empty(ReseauSecurise.HttpSurLeReseau(config));
        Assert.True(ReseauSecurise.HttpsConfigure(config));
        Assert.Null(ReseauSecurise.RefusDemarrage(config, developpement: false));
        Assert.False(config.GetValue(ReseauSecurise.ReglageHttpReseau, false));
    }

    [Fact]
    public void Http_sur_le_reseau_reste_refuse_meme_avec_https_configure()
    {
        var config = Config(("Kestrel:Endpoints:Http:Url", "http://0.0.0.0:5080"), ("Kestrel:Endpoints:Https:Url", "https://0.0.0.0:5443"));
        Assert.NotNull(ReseauSecurise.RefusDemarrage(config, developpement: false));
    }

    [Theory]
    [InlineData("/mobile/autorite-pharmacie.crt", "192.168.1.20", true)]
    [InlineData("/Mobile/Autorite-Pharmacie.crt", "10.0.0.5", true)]
    [InlineData("/mobile/autorite-pharmacie.crt", null, true)]
    [InlineData("/mobile/autorite-pharmacie.crt", "127.0.0.1", false)]
    [InlineData("/mobile/autorite-pharmacie.crt", "::1", false)]
    [InlineData("/mobile/index.html", "192.168.1.20", false)]
    public void Certificat_de_l_autorite_servi_seulement_au_poste_local(string chemin, string? origine, bool refuse) =>
        Assert.Equal(refuse, ReseauSecurise.CertificatAutoriteRefuse(new PathString(chemin), origine is null ? null : System.Net.IPAddress.Parse(origine)));

    private static string RacineDepot()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    [Theory]
    [InlineData("/api/auth/connexion", true)]
    [InlineData("/api/auth/connexion-code", true)]
    [InlineData("/api/super-admin/connexion", true)]
    [InlineData("/api/auth/mot-de-passe", true)]
    [InlineData("/api/auth/permissions", false)]
    [InlineData("/api/caisse/ventes", false)]
    public void Limite_de_debit_sur_les_routes_de_connexion(string chemin, bool limitee) =>
        Assert.Equal(limitee, ReseauSecurise.EstRouteConnexion(new PathString(chemin)));

    [Fact]
    public void La_chaine_de_connexion_recoit_le_fuseau_de_l_officine_sauf_s_il_est_precise()
    {
        Assert.Contains("Timezone=Africa/Algiers", Pharma.Infrastructure.DependencyInjection.AvecFuseau("Host=localhost;Database=pharmacie_test"));
        Assert.Contains("Timezone=UTC", Pharma.Infrastructure.DependencyInjection.AvecFuseau("Host=localhost;Database=pharmacie_test;Timezone=UTC"));
    }

    [Fact]
    public void Fuseau_utc_plus_1_sans_heure_d_ete_accepte_sinon_avertissement()
    {
        var maintenant = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Null(ReseauSecurise.AvertissementFuseau(TimeZoneInfo.CreateCustomTimeZone("Alger", TimeSpan.FromHours(1), "Alger", "Alger"), maintenant));
        Assert.NotNull(ReseauSecurise.AvertissementFuseau(TimeZoneInfo.Utc, maintenant));
        var paris = TimeZoneInfo.CreateCustomTimeZone("Paris", TimeSpan.FromHours(1), "Paris", "Paris", "Paris été",
            [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday))]);
        Assert.NotNull(ReseauSecurise.AvertissementFuseau(paris, maintenant));
    }
}
