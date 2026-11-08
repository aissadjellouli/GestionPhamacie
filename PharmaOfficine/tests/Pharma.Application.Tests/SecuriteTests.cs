using Pharma.Application.Securite;
using Pharma.Domain.Securite;

namespace Pharma.Application.Tests;

public class SecuriteTests
{
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
