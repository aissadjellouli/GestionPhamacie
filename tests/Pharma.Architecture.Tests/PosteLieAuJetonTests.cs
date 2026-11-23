using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Pharma.Api.Securite;

namespace Pharma.Architecture.Tests;

/// <summary>Revue de sécurité du 2026-11-23 : poste lié au jeton, clé d'exemple en développement.</summary>
public class PosteLieAuJetonTests
{
    private static IConfiguration Config(params (string Cle, string Valeur)[] valeurs) =>
        new ConfigurationBuilder().AddInMemoryCollection(valeurs.Select(v => new KeyValuePair<string, string?>(v.Cle, v.Valeur))).Build();

    private static readonly string CleLongue = new('k', 80);

    private static HttpContext Requete(string? posteJeton, string? enTete, bool connecte = true)
    {
        var http = new DefaultHttpContext();
        if (connecte)
        {
            var revendications = new List<Claim> { new(ClaimTypes.NameIdentifier, "5") };
            if (posteJeton is not null) revendications.Add(new Claim(PorteeConnexion.RevendicationPoste, posteJeton));
            http.User = new ClaimsPrincipal(new ClaimsIdentity(revendications, "Bearer"));
        }
        if (enTete is not null) http.Request.Headers[PorteeConnexion.EnTetePoste] = Uri.EscapeDataString(enTete);
        return http;
    }

    [Fact]
    public void Le_jeton_porte_le_poste_de_la_connexion()
    {
        var jetons = new JetonService(Config(("Jwt:Cle", CleLongue), ("Jwt:Emetteur", "pharma")));
        var (avec, _) = jetons.Creer(5, "Vendeur", poste: "  Caisse 1 ");
        var (sans, _) = jetons.Creer(5, "Vendeur");
        var lu = new JsonWebTokenHandler().ReadJsonWebToken(avec);
        Assert.Equal("Caisse 1", lu.Claims.Single(c => c.Type == PorteeConnexion.RevendicationPoste).Value);
        Assert.DoesNotContain(new JsonWebTokenHandler().ReadJsonWebToken(sans).Claims, c => c.Type == PorteeConnexion.RevendicationPoste);
    }

    [Fact]
    public void Connecte_le_poste_vient_du_jeton_jamais_de_l_en_tete()
    {
        var http = Requete("Caisse 1", "Caisse 2");
        Assert.True(PorteeConnexion.PosteDiffere(http));
        Assert.Equal("Caisse 1", http.Request.Poste());

        var memePoste = Requete("Caisse 1", "Caisse 1");
        Assert.False(PorteeConnexion.PosteDiffere(memePoste));
        Assert.Equal("Caisse 1", memePoste.Request.Poste());

        // En-tête absent : le poste du jeton reste celui de la requête
        Assert.Equal("Caisse 1", Requete("Caisse 1", null).Request.Poste());
    }

    [Fact]
    public void Jeton_sans_poste_n_a_aucun_poste_meme_avec_un_en_tete()
    {
        var http = Requete(null, "Caisse 2");
        Assert.False(PorteeConnexion.PosteDiffere(http));
        Assert.Null(http.Request.Poste());
    }

    [Fact]
    public void Sans_connexion_le_poste_est_l_en_tete_pour_l_ecran_de_connexion()
    {
        var http = Requete(null, " Caisse 3 ", connecte: false);
        Assert.False(PorteeConnexion.PosteDiffere(http));
        Assert.Equal("Caisse 3", http.Request.Poste());
        Assert.Null(Requete(null, null, connecte: false).Request.Poste());
    }

    [Fact]
    public void Cle_d_exemple_en_developpement_avertit_si_l_api_n_ecoute_pas_seulement_sur_localhost()
    {
        var exemple = (Cle: "Jwt:Cle", Valeur: JetonService.PrefixeCleExemple + "-changez-moi");
        Assert.NotNull(JetonService.AvertissementCleExemple(Config(exemple, ("Urls", "http://0.0.0.0:5080")), developpement: true));
        Assert.NotNull(JetonService.AvertissementCleExemple(Config(exemple, ("Urls", "https://*:5443")), developpement: true));
        Assert.Null(JetonService.AvertissementCleExemple(Config(exemple, ("Urls", "http://localhost:5080")), developpement: true));
        Assert.Null(JetonService.AvertissementCleExemple(Config(exemple, ("Urls", "http://127.0.0.1:5080;https://[::1]:5443")), developpement: true));
        // Clé propre à la pharmacie, ou hors développement (l'API refuse alors de démarrer) : rien à avertir
        Assert.Null(JetonService.AvertissementCleExemple(Config(("Jwt:Cle", CleLongue), ("Urls", "http://0.0.0.0:5080")), developpement: true));
        Assert.Null(JetonService.AvertissementCleExemple(Config(exemple, ("Urls", "http://0.0.0.0:5080")), developpement: false));
    }
}
