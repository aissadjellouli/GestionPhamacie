using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Securite;

namespace Pharma.Application.Tests;

/// <summary>
/// Titulaire sur un PC non autorisé : Administration seulement (décision du titulaire du 2026-11-27). Sur un PC non prouvé, la connexion n'est
/// ouverte qu'au pharmacien titulaire ; tout autre employé est refusé après la vérification du mot de passe, sans jeton ni trace de connexion.
/// </summary>
public class ConnexionPcNonAutoriseTests
{
    private static ComptesEnMemoire Comptes() => new(
        new Utilisateur { Id = 1, Identifiant = "titulaire", NomComplet = "Titulaire Test", MotDePasseHash = "hash:secret" },
        new Utilisateur { Id = 7, Identifiant = "vendeur", NomComplet = "Vendeur Test", MotDePasseHash = "hash:secret" });

    private sealed class TitulaireSeul : IPermissionService
    {
        public Task<bool> APermissionAsync(long u, string p, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<string>> ListerAsync(long u, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<decimal> RemiseMaxAsync(long u, CancellationToken ct = default) => Task.FromResult(0m);
        public Task<bool> EstTitulaireAsync(long u, CancellationToken ct = default) => Task.FromResult(u == 1);
    }

    [Fact]
    public async Task Le_titulaire_se_connecte_depuis_un_PC_non_autorise_sans_poste_lie()
    {
        var comptes = Comptes();
        var jetons = new JetonsFactices();
        var session = await new SeConnecter(comptes, new VerificateurFactice(), jetons, droits: new TitulaireSeul())
            .ExecuterAsync("titulaire", "secret", titulaireSeulement: true);
        Assert.NotNull(session);
        Assert.Equal([1L], comptes.Connexions);
        Assert.Equal([null], jetons.Postes);
    }

    [Fact]
    public async Task Un_autre_employe_est_refuse_avec_un_message_clair_sans_jeton()
    {
        var comptes = Comptes();
        var jetons = new JetonsFactices();
        var cas = new SeConnecter(comptes, new VerificateurFactice(), jetons, droits: new TitulaireSeul());
        var e = await Assert.ThrowsAsync<DroitRefuseException>(() => cas.ExecuterAsync("vendeur", "secret", titulaireSeulement: true));
        Assert.Equal(SeConnecter.MessageTitulaireSeulement, e.Message);
        Assert.Contains("seul le pharmacien titulaire", e.Message);
        Assert.Empty(comptes.Connexions);
        Assert.Empty(jetons.Postes);
        // Mot de passe faux : réponse ordinaire (aucune information sur le rôle du compte)
        Assert.Null(await cas.ExecuterAsync("vendeur", "faux", titulaireSeulement: true));
    }

    [Fact]
    public async Task Sans_service_des_droits_la_connexion_sur_un_PC_non_autorise_est_refusee()
    {
        var cas = new SeConnecter(Comptes(), new VerificateurFactice(), new JetonsFactices());
        await Assert.ThrowsAsync<DroitRefuseException>(() => cas.ExecuterAsync("titulaire", "secret", titulaireSeulement: true));
    }

    [Fact]
    public async Task Sur_un_poste_autorise_la_connexion_est_inchangee()
    {
        var comptes = Comptes();
        var jetons = new JetonsFactices();
        Assert.NotNull(await new SeConnecter(comptes, new VerificateurFactice(), jetons, droits: new TitulaireSeul())
            .ExecuterAsync("vendeur", "secret", poste: "CAISSE-1"));
        Assert.Equal(["CAISSE-1"], jetons.Postes);
    }
}
