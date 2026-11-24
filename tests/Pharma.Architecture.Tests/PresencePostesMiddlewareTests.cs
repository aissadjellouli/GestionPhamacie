using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Pharma.Api.Securite;
using Pharma.Application.Commun;
using Pharma.Application.SecondServeur;

namespace Pharma.Architecture.Tests;

/// <summary>Vague 2, lot V2-1b : intergiciel de présence des postes (jeton valide, poste du jeton) et motif « isole » du 503 serveur_ecarte.</summary>
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

    private static (PresencePostes Presence, RegistrePostesVus Registre, Depot Depot, HttpContext Ctx) Banc(string chemin, string? claimPoste = null, bool authentifie = true, params string[] enTetes)
    {
        var registre = new RegistrePostesVus();
        var depot = new Depot();
        var presence = new PresencePostes(registre, depot, new SuiviServeurEcarte(), new VeilleServeur(false), new Releves(), new Horloge());
        var services = new ServiceCollection().AddSingleton(presence).BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Request.Path = chemin;
        foreach (var e in enTetes) ctx.Request.Headers.Append(PresencePostesMiddleware.EnTete, e);
        if (authentifie)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "7") };
            if (claimPoste is not null) claims.Add(new Claim(PorteeConnexion.RevendicationPoste, claimPoste));
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        }
        return (presence, registre, depot, ctx);
    }

    private static async Task<bool> Passer(PresencePostes presence, HttpContext ctx)
    {
        var suite = false;
        await new PresencePostesMiddleware(_ => { suite = true; return Task.CompletedTask; }).InvokeAsync(ctx);
        await presence.Derniere;
        return suite;
    }

    [Theory]
    [InlineData("/api/healthcheck")]
    [InlineData("/api/caisse/ventes")]
    public async Task Une_requete_authentifiee_note_le_poste_du_jeton_healthcheck_compris_et_la_suite_est_appelee(string chemin)
    {
        // Le nom vient du claim du jeton, jamais de l'en-tête (ici volontairement différent)
        var (presence, registre, depot, ctx) = Banc(chemin, "  Caisse Été 1 ", true, "FAUX-POSTE");
        Assert.True(await Passer(presence, ctx));
        Assert.Equal(["Caisse Été 1"], depot.Ecrits);
        Assert.Contains("Caisse Été 1", registre.Vus(new Horloge().MaintenantUtc, TimeSpan.FromSeconds(45)));
        Assert.DoesNotContain("FAUX-POSTE", registre.Vus(new Horloge().MaintenantUtc, TimeSpan.FromSeconds(45)));
    }

    [Fact]
    public async Task Sans_jeton_valide_aucune_presence_meme_avec_un_en_tete_et_la_requete_passe()
    {
        var (presence, registre, depot, ctx) = Banc("/api/healthcheck", "CAISSE-1", authentifie: false, "CAISSE-1");
        Assert.True(await Passer(presence, ctx));
        Assert.Empty(depot.Ecrits);
        Assert.Empty(registre.Vus(new Horloge().MaintenantUtc, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Jeton_sans_poste_ou_avec_un_poste_inutilisable_aucune_presence()
    {
        foreach (var claim in new string?[] { null, "", "   ", new string('x', 101), "A\r\nB" })
        {
            var (presence, registre, depot, ctx) = Banc("/api/caisse/ventes", claim, true, "CAISSE-1");
            Assert.True(await Passer(presence, ctx));
            Assert.Empty(depot.Ecrits);
            Assert.Empty(registre.Vus(new Horloge().MaintenantUtc, TimeSpan.FromMinutes(5)));
        }
    }

    [Fact]
    public async Task Un_tiers_sans_jeton_qui_annonce_200_postes_n_ajoute_aucun_poste_connu_ni_vu()
    {
        var registre = new RegistrePostesVus();
        var depot = new Depot();
        var presence = new PresencePostes(registre, depot, new SuiviServeurEcarte(), new VeilleServeur(false), new Releves(), new Horloge());
        var services = new ServiceCollection().AddSingleton(presence).BuildServiceProvider();
        for (var i = 0; i < 200; i++)
        {
            var ctx = new DefaultHttpContext { RequestServices = services };
            ctx.Request.Path = "/api/healthcheck";
            ctx.Request.Headers[PresencePostesMiddleware.EnTete] = "FAUX%20" + i;
            await new PresencePostesMiddleware(_ => Task.CompletedTask).InvokeAsync(ctx);
        }
        await presence.Derniere;
        Assert.Empty(depot.Ecrits);
        Assert.Empty(registre.Vus(new Horloge().MaintenantUtc, TimeSpan.FromMinutes(5)));

        // Ces « postes » ne peuvent donc ni compter parmi les postes connus de la copie, ni former une majorité
        var connus = PostesConnus.Calculer(depot.Ecrits.Select(p => new PosteVuEnBase(p, new Horloge().MaintenantUtc)), new Horloge().MaintenantUtc, TimeSpan.FromMinutes(3))!;
        Assert.Empty(connus);
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
