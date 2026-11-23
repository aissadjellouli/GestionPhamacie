using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Pharma.Api.Securite;
using Pharma.Application.SecondServeur;
using Pharma.Shared;

namespace Pharma.Architecture.Tests;

/// <summary>Vague 2, lot V2-1 : barrage 503 « serveur_en_veille », rôle du healthcheck, officine à un seul serveur inchangée.</summary>
public class BarrageServeurVeilleTests
{
    private sealed class RelaisFictif(bool repond) : IRelaisVeille
    {
        public int Appels;
        public Task<bool> TenterAsync(HttpContext ctx, CancellationToken ct) { Appels++; return Task.FromResult(repond); }
    }

    private static (DefaultHttpContext Ctx, MemoryStream Corps) Requete(string chemin, bool veille, IRelaisVeille? relais = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        services.AddSingleton<IVeilleServeur>(new VeilleServeur(veille));
        if (relais is not null) services.AddSingleton(relais);
        var corps = new MemoryStream();
        var ctx = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        ctx.Request.Path = chemin;
        ctx.Response.Body = corps;
        return (ctx, corps);
    }

    [Theory]
    [InlineData("/api/catalogue/produits")]
    [InlineData("/api/caisse/ventes")]
    [InlineData("/api/auth/connexion")]
    [InlineData("/api/super-admin/connexion")]
    [InlineData("/api/demo")]
    [InlineData("/api/healthcheck/autre")]
    [InlineData("/api/vigiex")]
    public async Task En_veille_toute_route_est_refusee_en_503_serveur_en_veille(string chemin)
    {
        var (ctx, corps) = Requete(chemin, veille: true);
        var suite = false;
        await BarrageServeurVeille.Filtrer(ctx, _ => { suite = true; return Task.CompletedTask; });

        Assert.False(suite);
        Assert.Equal(503, ctx.Response.StatusCode);
        Assert.StartsWith("application/problem+json", ctx.Response.ContentType);
        corps.Position = 0;
        using var doc = JsonDocument.Parse(corps);
        Assert.Equal("serveur_en_veille", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(503, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("urn:pharmaofficine:erreur:serveur_en_veille", doc.RootElement.GetProperty("type").GetString());
        Assert.False(doc.RootElement.TryGetProperty("motif", out _));
    }

    [Theory]
    [InlineData("/api/healthcheck")]
    [InlineData("/API/HealthCheck")]
    [InlineData("/api/vigie/rapport")]
    [InlineData("/api/inter-serveurs/etat")]
    [InlineData("/api/inter-serveurs/retrait")]
    public async Task En_veille_healthcheck_vigie_et_inter_serveurs_passent(string chemin)
    {
        var (ctx, _) = Requete(chemin, veille: true);
        var suite = false;
        await BarrageServeurVeille.Filtrer(ctx, _ => { suite = true; return Task.CompletedTask; });
        Assert.True(suite);
    }

    [Theory]
    [InlineData("/api/caisse/ventes")]
    [InlineData("/api/healthcheck")]
    [InlineData("/api/vigie/rapport")]
    [InlineData("/api/inter-serveurs/etat")]
    public async Task Hors_veille_rien_ne_change(string chemin)
    {
        var (ctx, _) = Requete(chemin, veille: false);
        var suite = false;
        await BarrageServeurVeille.Filtrer(ctx, _ => { suite = true; return Task.CompletedTask; });
        Assert.True(suite);
        Assert.Equal(200, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Le_relais_branche_par_V2_5_repond_a_la_place_du_503_seulement_s_il_a_traite_la_requete()
    {
        var oui = new RelaisFictif(true);
        var (ctx, _) = Requete("/api/caisse/ventes", veille: true, oui);
        var suite = false;
        await BarrageServeurVeille.Filtrer(ctx, _ => { suite = true; return Task.CompletedTask; });
        Assert.Equal(1, oui.Appels);
        Assert.False(suite);
        Assert.Equal(200, ctx.Response.StatusCode);

        var non = new RelaisFictif(false);
        var (ctx2, _) = Requete("/api/caisse/ventes", veille: true, non);
        await BarrageServeurVeille.Filtrer(ctx2, _ => Task.CompletedTask);
        Assert.Equal(1, non.Appels);
        Assert.Equal(503, ctx2.Response.StatusCode);

        // Route permise : le relais n'est jamais consulté
        var (ctx3, _) = Requete("/api/healthcheck", veille: true, non);
        await BarrageServeurVeille.Filtrer(ctx3, _ => Task.CompletedTask);
        Assert.Equal(1, non.Appels);
    }

    [Theory]
    [InlineData(false, false, "principal")]
    [InlineData(false, true, "veille")]
    [InlineData(true, false, "ecarte")]
    [InlineData(true, true, "ecarte")]
    public void Role_du_healthcheck(bool ecarte, bool veille, string attendu) => Assert.Equal(attendu, BarrageServeurVeille.Role(ecarte, veille));

    [Fact]
    public void Code_du_barrage_conforme_au_contrat() => Assert.Equal(CodesBasculeVague2.ServeurEnVeille, BarrageServeurVeille.Code);
}
