using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Pharma.Api.Securite;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Décision du titulaire du 2026-11-28 : un jeton d'employé sans poste (page mobile) n'atteint que les routes de la page mobile
/// (<see cref="JetonsSansPoste"/>) ; depuis un simple outil HTTP, le mot de passe d'un employé n'ouvre plus la vente, les patients ni les finances.
/// </summary>
public class JetonsSansPosteTests
{
    private static ClaimsPrincipal Jeton(params (string Type, string Valeur)[] revendications) =>
        new(new ClaimsIdentity(revendications.Select(r => new Claim(r.Type, r.Valeur)).Append(new Claim(ClaimTypes.NameIdentifier, "7")), "Test"));

    [Fact]
    public void Chaque_appel_de_la_page_mobile_est_permis_a_un_jeton_sans_poste()
    {
        var js = File.ReadAllText(Path.Combine(BaseJetableM4.RacineDepot(), "src", "Pharma.Api", "wwwroot", "mobile", "mobile.js"));
        var appels = Regex.Matches(js, @"api\(""(?<verbe>GET|POST|PUT|DELETE)"",\s*[""`](?<route>/api/[^""`?]+)")
            .Select(m => (Verbe: m.Groups["verbe"].Value, Route: Regex.Replace(m.Groups["route"].Value, @"\$\{[^}]+\}", "1"))).Distinct().ToList();
        Assert.True(appels.Count >= 15, "Appels de mobile.js non reconnus : la forme api(\"VERBE\", \"/api/…\") a changé.");
        var refuses = appels.Where(a => !JetonsSansPoste.RouteAutorisee(a.Verbe, a.Route)).Select(a => $"{a.Verbe} {a.Route}").ToList();
        Assert.True(refuses.Count == 0, "Appels de la page mobile refusés à un jeton sans poste (JetonsSansPoste) : " + string.Join(", ", refuses));
    }

    [Theory]
    [InlineData("GET", "/api/ventes")]
    [InlineData("POST", "/api/ventes/encaisser")]
    [InlineData("GET", "/api/caisse")]
    [InlineData("GET", "/api/patients")]
    [InlineData("GET", "/api/produits")]
    [InlineData("PUT", "/api/produits/5")]
    [InlineData("PUT", "/api/produits/5/prix")]
    [InlineData("PUT", "/api/produits/abc/emplacement")]
    [InlineData("POST", "/api/emplacements")]
    [InlineData("POST", "/api/pilotage")]
    [InlineData("GET", "/api/admin/utilisateurs")]
    [InlineData("GET", "/api/rapports/ventes")]
    [InlineData("GET", "/api/inventairesx")]
    public void Hors_de_la_page_mobile_un_jeton_sans_poste_est_refuse(string verbe, string route) =>
        Assert.False(JetonsSansPoste.RouteAutorisee(verbe, route));

    [Fact]
    public void Seul_le_jeton_d_employe_sans_poste_est_concerne()
    {
        Assert.True(JetonsSansPoste.Concerne(Jeton()));
        Assert.False(JetonsSansPoste.Concerne(Jeton((PorteeConnexion.RevendicationPoste, "CAISSE-1"), (PorteeConnexion.RevendicationPosteId, "3"))));
        Assert.False(JetonsSansPoste.Concerne(Jeton((PorteeConnexion.RevendicationPoste, "CAISSE-1"))));
        Assert.False(JetonsSansPoste.Concerne(Jeton((SuperAdminSecurite.Revendication, "admin"))));
        Assert.False(JetonsSansPoste.Concerne(Jeton((AccesAdministrationSeule.Revendication, AccesAdministrationSeule.Valeur))));
        Assert.False(JetonsSansPoste.Concerne(new ClaimsPrincipal(new ClaimsIdentity())));   // sans jeton : les gardes ordinaires s'appliquent
    }

    [Fact]
    public async Task Le_controle_du_jeton_refuse_en_403_sans_fermer_la_session_et_laisse_passer_la_page_mobile()
    {
        async Task<(int Statut, bool Passe)> Essai(string verbe, string route, ClaimsPrincipal user)
        {
            var ctx = new DefaultHttpContext { User = user };
            ctx.Request.Method = verbe;
            ctx.Request.Path = route;
            ctx.Response.Body = new MemoryStream();
            ctx.RequestServices = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(Microsoft.Extensions.DependencyInjection.LoggingServiceCollectionExtensions.AddLogging(new Microsoft.Extensions.DependencyInjection.ServiceCollection()));
            ctx.Items[PreuvePosteMiddleware.ItemMode] = Pharma.Shared.PreuvePoste.ModeExige;
            var passe = false;
            await ControlePosteJeton.FiltrerAsync(ctx, () => { passe = true; return Task.CompletedTask; });
            return (ctx.Response.StatusCode, passe);
        }
        Assert.Equal((403, false), await Essai("GET", "/api/ventes", Jeton()));
        Assert.Equal((200, true), await Essai("GET", "/api/inventaires", Jeton()));
        Assert.Equal((200, true), await Essai("PUT", "/api/produits/12/emplacement", Jeton()));
        Assert.Equal((200, true), await Essai("GET", "/api/healthcheck", Jeton()));                                   // route ouverte
        Assert.Equal((200, true), await Essai("GET", "/api/super-admin/restauration/etat", Jeton((SuperAdminSecurite.Revendication, "admin"))));
    }

    // ---- Autres constats de la revue du 2026-11-28, figés ici ----

    /// <summary>L'API écoute par défaut sur 5080 (adresse par défaut du poste, lanceurs, manuels) ; 5081 est l'instance de test, par --urls.</summary>
    [Fact]
    public void L_API_ecoute_par_defaut_sur_le_port_5080()
    {
        var reglages = File.ReadAllText(Path.Combine(BaseJetableM4.RacineDepot(), "src", "Pharma.Api", "appsettings.json"));
        Assert.Contains("\"Urls\": \"http://0.0.0.0:5080\"", reglages);
    }

    /// <summary>Le poste et le serveur refusent le même montant en espèces invraisemblable (code-barres scanné dans un champ de montant).</summary>
    [Fact]
    public void La_borne_des_especes_est_la_meme_au_poste_et_au_serveur()
    {
        Assert.Equal(Pharma.Domain.Ventes.Reglement.MontantEspecesMax, Pharma.Shared.LimitesCaisse.MontantEspecesMax);
        Assert.True(Pharma.Shared.LimitesCaisse.Invraisemblable(3400930000000m));   // un code-barres à 13 chiffres
        Assert.False(Pharma.Shared.LimitesCaisse.Invraisemblable(200000m));
        Assert.False(Pharma.Shared.LimitesCaisse.Invraisemblable(null));
        Assert.Throws<Pharma.Domain.Commun.RegleMetierException>(() => Pharma.Domain.Caisse.SessionCaisse.VerifierFond(3400930000000m));
    }

    /// <summary>Relais du 2e serveur : une cible en forme absolue ne part jamais vers un autre hôte que le serveur principal.</summary>
    [Theory]
    [InlineData("/api/ventes?q=1", true)]
    [InlineData("http://autre-hote/api/ventes", false)]
    [InlineData("//autre-hote/api/ventes", false)]
    [InlineData(@"/\autre-hote/api", false)]
    [InlineData("api/ventes", false)]
    public void Le_relais_ne_suit_qu_une_cible_locale(string cible, bool attendu) => Assert.Equal(attendu, RelaisVeille.EstCibleLocale(cible));
}
