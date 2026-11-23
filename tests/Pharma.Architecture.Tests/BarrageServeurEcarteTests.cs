using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pharma.Api.Securite;
using Pharma.Api.Taches;
using Pharma.Application.SecondServeur;

namespace Pharma.Architecture.Tests;

/// <summary>Bascule du 2e serveur, lot 3 : intergiciel 503 « serveur_ecarte » et tâches de fond suspendues.</summary>
public class BarrageServeurEcarteTests
{
    private static readonly EcartDetecte Bascule = new(new DateTime(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc), "SRV-2", 1, 2);

    private static (DefaultHttpContext Ctx, MemoryStream Corps) Requete(string chemin, string methode, SuiviServeurEcarte suivi)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        services.AddSingleton<IServeurEcarteDetecte>(suivi);
        var corps = new MemoryStream();
        var ctx = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        ctx.Request.Path = chemin;
        ctx.Request.Method = methode;
        ctx.Response.Body = corps;
        return (ctx, corps);
    }

    [Theory]
    [InlineData("GET", "/api/catalogue/produits")]
    [InlineData("POST", "/api/caisse/ventes")]
    [InlineData("POST", "/api/auth/connexion")]
    [InlineData("GET", "/api/auth/utilisateurs")]
    [InlineData("POST", "/api/super-admin/connexion")]
    [InlineData("GET", "/api/demo")]
    [InlineData("GET", "/api/healthcheck/autre")]
    public async Task Serveur_ecarte_refuse_toute_requete_consultation_et_connexion_comprises_en_503_problemdetails(string methode, string chemin)
    {
        var suivi = new SuiviServeurEcarte();
        suivi.Restaurer(Bascule);
        var (ctx, corps) = Requete(chemin, methode, suivi);
        var suiteAppelee = false;

        await BarrageServeurEcarte.Filtrer(ctx, _ => { suiteAppelee = true; return Task.CompletedTask; });

        Assert.False(suiteAppelee);
        Assert.Equal(503, ctx.Response.StatusCode);
        Assert.StartsWith("application/problem+json", ctx.Response.ContentType);
        corps.Position = 0;
        using var doc = JsonDocument.Parse(corps);
        var r = doc.RootElement;
        Assert.Equal(503, r.GetProperty("status").GetInt32());
        Assert.Equal("serveur_ecarte", r.GetProperty("code").GetString());
        Assert.Equal("bascule", r.GetProperty("motif").GetString());
        Assert.Equal("SRV-2", r.GetProperty("serveurRetenu").GetString());
        Assert.Equal(Bascule.DepuisUtc, r.GetProperty("ecarteDepuisUtc").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public async Task Serveur_ecarte_laisse_passer_l_etat_de_sante_seulement()
    {
        var suivi = new SuiviServeurEcarte();
        suivi.Restaurer(Bascule);
        var (ctx, _) = Requete("/api/healthcheck", "GET", suivi);
        var suiteAppelee = false;

        await BarrageServeurEcarte.Filtrer(ctx, _ => { suiteAppelee = true; return Task.CompletedTask; });

        Assert.True(suiteAppelee);
        Assert.True(BarrageServeurEcarte.Permise("/API/HealthCheck"));
        Assert.False(BarrageServeurEcarte.Permise("/api/healthcheck/x"));
    }

    [Fact]
    public async Task Etat_indecis_donne_le_motif_timelines_egales_sans_serveur_retenu()
    {
        var suivi = new SuiviServeurEcarte();
        suivi.Restaurer(new EcartDetecte(Bascule.DepuisUtc, null, 2, 2, Indecis: true));
        var (ctx, corps) = Requete("/api/caisse/ventes", "POST", suivi);

        await BarrageServeurEcarte.Filtrer(ctx, _ => Task.CompletedTask);

        corps.Position = 0;
        using var doc = JsonDocument.Parse(corps);
        Assert.Equal("timelines_egales", doc.RootElement.GetProperty("motif").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("serveurRetenu").ValueKind);
    }

    [Fact]
    public async Task Serveur_normal_ou_leve_laisse_tout_passer()
    {
        var suivi = new SuiviServeurEcarte();
        suivi.Restaurer(Bascule);
        suivi.Lever();
        var (ctx, _) = Requete("/api/caisse/ventes", "POST", suivi);
        var suiteAppelee = false;

        await BarrageServeurEcarte.Filtrer(ctx, _ => { suiteAppelee = true; return Task.CompletedTask; });

        Assert.True(suiteAppelee);
        Assert.NotEqual(503, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Tache_de_fond_arretee_des_que_le_serveur_est_ecarte_puis_relancee_a_la_levee()
    {
        var suivi = new SuiviServeurEcarte();
        var services = new ServiceCollection();
        services.AddSingleton<IHostedService, TacheTest>();
        services.AddSingleton<IServeurEcarteDetecte>(suivi);
        services.AddSingleton<ILogger<TacheSuspendueSiEcarte>>(NullLogger<TacheSuspendueSiEcarte>.Instance);
        TacheSuspendueSiEcarte.Envelopper(services, typeof(TacheTest));
        using var sp = services.BuildServiceProvider();
        var enveloppe = Assert.Single(sp.GetServices<IHostedService>());
        Assert.IsType<TacheSuspendueSiEcarte>(enveloppe);
        TacheTest.Demarrages = 0;
        TacheTest.EnMarche = false;

        await enveloppe.StartAsync(CancellationToken.None);
        await Attendre(() => TacheTest.EnMarche);
        Assert.Equal(1, TacheTest.Demarrages);

        suivi.Restaurer(Bascule);
        await Attendre(() => !TacheTest.EnMarche);

        suivi.Lever();
        await Attendre(() => TacheTest.EnMarche);
        Assert.Equal(2, TacheTest.Demarrages);

        await enveloppe.StopAsync(CancellationToken.None);
        Assert.False(TacheTest.EnMarche);
    }

    private static async Task Attendre(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(100);
        Assert.True(condition(), "condition non atteinte en 10 s");
    }

    private sealed class TacheTest : BackgroundService
    {
        public static int Demarrages;
        public static volatile bool EnMarche;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Interlocked.Increment(ref Demarrages);
            EnMarche = true;
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            finally { EnMarche = false; }
        }
    }
}
