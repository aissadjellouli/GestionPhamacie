using Pharma.Application.Securite;
using Pharma.Domain.Securite;

namespace Pharma.Application.Tests;

public class SecuriteTests
{
    [Fact]
    public async Task La_connexion_est_datee()
    {
        var comptes = new ComptesEnMemoire(
            new Utilisateur { Id = 7, Identifiant = "vendeur", NomComplet = "Vendeur Test", MotDePasseHash = "hash:secret" });
        await new SeConnecter(comptes, new VerificateurFactice(), new JetonsFactices()).ExecuterAsync("vendeur", "secret");
        Assert.Equal([7L], comptes.Connexions);
    }

    [Fact]
    public async Task Cinq_mots_de_passe_faux_bloquent_la_connexion()
    {
        var comptes = new ComptesEnMemoire(
            new Utilisateur { Id = 7, Identifiant = "vendeur", NomComplet = "Vendeur Test", MotDePasseHash = "hash:secret" });
        var journal = new JournalEspion();
        var essais = new EssaisConnexion();
        var cas = new SeConnecter(comptes, new VerificateurFactice(), new JetonsFactices(), essais, new HorlogeFixe(new DateOnly(2026, 11, 10)), journal);

        for (var i = 1; i < EssaisConnexion.EssaisMax; i++)
            Assert.Null(await cas.ExecuterAsync("vendeur", "faux"));
        // Le cinquième essai bloque ; le bon mot de passe est ensuite refusé pendant le blocage
        await Assert.ThrowsAsync<Pharma.Domain.Commun.RegleMetierException>(() => cas.ExecuterAsync("vendeur", "faux"));
        await Assert.ThrowsAsync<Pharma.Domain.Commun.RegleMetierException>(() => cas.ExecuterAsync("vendeur", "secret"));
        Assert.Empty(comptes.Connexions);

        // Blocage terminé : la connexion repasse, le compteur repart de zéro
        var apres = new HorlogeFixe(new DateOnly(2026, 11, 11));
        Assert.NotNull(await new SeConnecter(comptes, new VerificateurFactice(), new JetonsFactices(), essais, apres, journal).ExecuterAsync("vendeur", "secret"));
        Assert.Null(essais.BloqueJusqu("vendeur", apres.MaintenantUtc));
    }

    [Fact]
    public async Task Une_connexion_reussie_remet_les_essais_a_zero()
    {
        var comptes = new ComptesEnMemoire(
            new Utilisateur { Id = 7, Identifiant = "vendeur", NomComplet = "Vendeur Test", MotDePasseHash = "hash:secret" });
        var cas = new SeConnecter(comptes, new VerificateurFactice(), new JetonsFactices(), new EssaisConnexion(), new HorlogeFixe(new DateOnly(2026, 11, 10)));
        for (var tour = 0; tour < 3; tour++)
        {
            for (var i = 1; i < EssaisConnexion.EssaisMax; i++)
                Assert.Null(await cas.ExecuterAsync("vendeur", "faux"));
            Assert.NotNull(await cas.ExecuterAsync("vendeur", "secret"));
        }
    }

    private static SeConnecter CasDUsage() => new(
        new ComptesEnMemoire(
            new Utilisateur { Id = 7, Identifiant = "vendeur", NomComplet = "Vendeur Test", MotDePasseHash = "hash:secret" },
            new Utilisateur { Id = 8, Identifiant = "ancien", NomComplet = "Ancien", MotDePasseHash = "hash:secret", Actif = false }),
        new VerificateurFactice(),
        new JetonsFactices());

    [Fact]
    public async Task Connexion_reussie_renvoie_une_session()
    {
        var session = await CasDUsage().ExecuterAsync(" vendeur ", "secret");

        Assert.NotNull(session);
        Assert.Equal("jeton-7", session.Jeton);
        Assert.Equal("Vendeur Test", session.NomComplet);
    }

    [Theory]
    [InlineData("vendeur", "faux")]
    [InlineData("inconnu", "secret")]
    [InlineData("ancien", "secret")]
    [InlineData("", "secret")]
    [InlineData("vendeur", "")]
    public async Task Connexion_refusee(string identifiant, string motDePasse) =>
        Assert.Null(await CasDUsage().ExecuterAsync(identifiant, motDePasse));
}
