using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Pharma.Api.Securite;
using Pharma.Application.Commun;
using Pharma.Application.SecondServeur;

namespace Pharma.Architecture.Tests;

/// <summary>Vague 2, lot V2-1b : intergiciel de présence des postes (en-tête X-Pharma-Poste) et motif « isole » du 503 serveur_ecarte.</summary>
public class PresencePostesMiddlewareTests
{
    private sealed class Horloge : IHorloge
    {
        public DateTime MaintenantUtc => new(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc);
        public DateOnly Aujourdhui => DateOnly.FromDateTime(MaintenantUtc);
    }

    private sealed class Releves : IReleveServeurs
    {
        public bool AutreServeurRegle => true;
        public string? AutreServeur => "AUTRE";
        public Task<ReleveServeur?> LocalAsync(CancellationToken ct = default) => Task.FromResult<ReleveServeur?>(null);
        public Task<ReleveServeur?> AutreAsync(CancellationToken ct = default) => Task.FromResult<ReleveServeur?>(null);
    }

    private sealed class Depot : IPresenceDepot
    {
        public List<string> Ecrits { get; } = [];
        public Task NoterAsync(string poste, CancellationToken ct = default) { Ecrits.Add(poste); return Task.CompletedTask; }
    }

    private static (PresencePostes Presence, RegistrePostesVus Registre, Depot Depot, HttpContext Ctx) Banc(string chemin, params string[] enTetes)
    {
        var registre = new RegistrePostesVus();
        var depot = new Depot();
        var presence = new PresencePostes(registre, depot, new SuiviServeurEcarte(), new VeilleServeur(false), new Releves(), new Horloge());
        var services = new ServiceCollection().AddSingleton(presence).BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Request.Path = chemin;
        foreach (var e in enTetes) ctx.Request.Headers.Append(PresencePostesMiddleware.EnTete, e);
        return (presence, registre, depot, ctx);
    }

    [Theory]
    [InlineData("/api/healthcheck")]
    [InlineData("/api/caisse/ventes")]
    public async Task L_en_tete_de_toute_requete_est_note_healthcheck_compris_et_la_suite_est_appelee(string chemin)
    {
        var (presence, registre, depot, ctx) = Banc(chemin, "CAISSE-1");
        var suite = false;

        await new PresencePostesMiddleware(_ => { suite = true; return Task.CompletedTask; }).InvokeAsync(ctx);
        await presence.Derniere;

        Assert.True(suite);
        Assert.Equal(["CAISSE-1"], depot.Ecrits);
        Assert.Contains("CAISSE-1", registre.Vus(new Horloge().MaintenantUtc, TimeSpan.FromSeconds(45)));
    }

    [Fact]
    public async Task Sans_en_tete_ou_avec_un_en_tete_double_ou_invalide_rien_n_est_note_et_la_requete_passe()
    {
        foreach (var enTetes in new[] { Array.Empty<string>(), ["A", "B"], [new string('x', 101)], ["  "] })
        {
            var (presence, registre, depot, ctx) = Banc("/api/healthcheck", enTetes);
            var suite = false;
            await new PresencePostesMiddleware(_ => { suite = true; return Task.CompletedTask; }).InvokeAsync(ctx);
            await presence.Derniere;
            Assert.True(suite);
            Assert.Empty(depot.Ecrits);
            Assert.Empty(registre.Vus(new Horloge().MaintenantUtc, TimeSpan.FromMinutes(5)));
        }
    }

    [Fact]
    public async Task Serveur_isole_503_serveur_ecarte_motif_isole_sans_serveur_retenu()
    {
        var suivi = new SuiviServeurEcarte();
        var depuis = new DateTime(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc);
        suivi.Restaurer(new EcartDetecte(depuis, null, 0, 0, Isole: true));
        var services = new ServiceCollection().AddLogging().AddProblemDetails().AddSingleton<IServeurEcarteDetecte>(suivi).BuildServiceProvider();
        var corps = new MemoryStream();
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Request.Path = "/api/caisse/ventes";
        ctx.Response.Body = corps;

        await BarrageServeurEcarte.Filtrer(ctx, _ => Task.CompletedTask);

        Assert.Equal(503, ctx.Response.StatusCode);
        corps.Position = 0;
        using var doc = JsonDocument.Parse(corps);
        Assert.Equal("serveur_ecarte", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("isole", doc.RootElement.GetProperty("motif").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("serveurRetenu").ValueKind);
        Assert.True(BarrageServeurEcarte.Permise("/api/healthcheck"));
    }
}
