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
