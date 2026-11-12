using Pharma.Application.Securite;

namespace Pharma.Application.Tests;

public class ValidationConnexionTests
{
    private static readonly DateTime Emise = new(2026, 11, 12, 10, 0, 5, DateTimeKind.Utc);

    [Fact]
    public void Compte_actif_sans_revocation_connexion_valable() =>
        Assert.Null(ValidationConnexion.Refus(new EtatConnexion(true, false, null), Emise, false));

    [Fact]
    public void Compte_desactive_ou_inconnu_refuse()
    {
        Assert.NotNull(ValidationConnexion.Refus(new EtatConnexion(false, false, null), Emise, false));
        Assert.NotNull(ValidationConnexion.Refus(null, Emise, false));
    }

    [Fact]
    public void Connexion_ouverte_avant_la_revocation_refusee_apres_valable()
    {
        var revoque = new DateTime(2026, 11, 12, 10, 0, 6, 300, DateTimeKind.Utc);
        Assert.NotNull(ValidationConnexion.Refus(new EtatConnexion(true, false, revoque), Emise, false));
        // Nouveau jeton délivré dans la seconde du changement de mot de passe (le jeton ne garde que la seconde)
        Assert.Null(ValidationConnexion.Refus(new EtatConnexion(true, false, revoque), new DateTime(2026, 11, 12, 10, 0, 6, DateTimeKind.Utc), false));
    }

    [Fact]
    public void Mot_de_passe_reinitialise_seul_le_jeton_provisoire_passe()
    {
        Assert.NotNull(ValidationConnexion.Refus(new EtatConnexion(true, true, null), Emise, marqueMotDePasseAChanger: false));
        Assert.Null(ValidationConnexion.Refus(new EtatConnexion(true, true, null), Emise, marqueMotDePasseAChanger: true));
    }
}
