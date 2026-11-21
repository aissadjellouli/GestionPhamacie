using Pharma.Application.Administration;
using Pharma.Application.Demo;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Tests;

/// <summary>Mode démonstration (docs/demo-cloud.md, étape 2) : règles de démarrage et mots de passe des comptes de démonstration.</summary>
public class ModeDemoTests
{
    private static Utilisateur Compte(long id, string identifiant) => new()
    {
        Id = id, NomComplet = "Compte Fictif", Identifiant = identifiant, MotDePasseHash = "hash:demo-2026", RoleId = 1
    };

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Demarrage_permis(bool actif, bool? marqueur) => Assert.Null(ModeDemo.RefusDemarrage(actif, marqueur));

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void Demarrage_refuse_en_demonstration_sans_marqueur(bool? marqueur)
    {
        var refus = ModeDemo.RefusDemarrage(true, marqueur);
        Assert.NotNull(refus);
        Assert.StartsWith("Démarrage refusé", refus);
        Assert.Contains(ModeDemo.TableMarqueur, refus);
    }

    [Fact]
    public void Base_de_demonstration_sans_mode_demo_avertie_hors_developpement()
    {
        Assert.Contains("ATTENTION", ModeDemo.AvertissementDemarrage(false, true, developpement: false));
        Assert.Null(ModeDemo.AvertissementDemarrage(false, true, developpement: true));
        Assert.Null(ModeDemo.AvertissementDemarrage(true, true, developpement: false));
        Assert.Null(ModeDemo.AvertissementDemarrage(false, false, developpement: false));
        Assert.Null(ModeDemo.AvertissementDemarrage(false, null, developpement: false));
    }

    [Fact]
    public void Fonction_coupee_seulement_en_demonstration()
    {
        new ModeDemo(false).Exiger("envoi de SMS");
        var e = Assert.Throws<RegleMetierException>(() => new ModeDemo(true).Exiger("envoi de SMS"));
        Assert.StartsWith("Indisponible en démonstration : envoi de SMS", e.Message);
    }

    [Theory]
    [InlineData("titulaire", true)]
    [InlineData("Assistant", true)]
    [InlineData(" vendeur ", true)]
    [InlineData("karim", false)]
    [InlineData(null, false)]
    public void Comptes_de_demonstration(string? identifiant, bool attendu) => Assert.Equal(attendu, ModeDemo.EstCompteDemo(identifiant));

    [Fact]
    public async Task Compte_de_demonstration_ne_change_pas_son_mot_de_passe_en_demonstration()
    {
        var comptes = new ComptesEnMemoire(Compte(1, "vendeur"));
        var cas = new ChangerMonMotDePasse(comptes, new VerificateurFactice(), new HacheurFactice(), new JetonsFactices(), new JournalEspion(),
            demo: new ModeDemo(true));
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(1, "demo-2026", "Nouveau-2026", "Nouveau-2026"));
        Assert.StartsWith("Indisponible en démonstration", e.Message);
        Assert.Null(comptes.Change);
    }

    [Fact]
    public async Task Autre_compte_ou_hors_demonstration_change_son_mot_de_passe()
    {
        var autre = new ComptesEnMemoire(Compte(2, "karim"));
        await new ChangerMonMotDePasse(autre, new VerificateurFactice(), new HacheurFactice(), new JetonsFactices(), new JournalEspion(), demo: new ModeDemo(true))
            .ExecuterAsync(2, "demo-2026", "Nouveau-2026", "Nouveau-2026");
        Assert.Equal((2L, "hash:Nouveau-2026"), autre.Change);

        var officine = new ComptesEnMemoire(Compte(1, "vendeur"));
        await new ChangerMonMotDePasse(officine, new VerificateurFactice(), new HacheurFactice(), new JetonsFactices(), new JournalEspion(), demo: new ModeDemo(false))
            .ExecuterAsync(1, "demo-2026", "Nouveau-2026", "Nouveau-2026");
        Assert.Equal((1L, "hash:Nouveau-2026"), officine.Change);
    }

    [Fact]
    public async Task Administration_ne_change_pas_le_mot_de_passe_d_un_compte_de_demonstration()
    {
        var admin = new UtilisateursEnMemoire();
        var cas = new ModifierUtilisateur(admin, new HacheurFactice(), new JournalEspion(), new DroitsFactices { Titulaire = true }, new ModeDemo(true));
        var e = await Assert.ThrowsAsync<RegleMetierException>(() =>
            cas.ExecuterAsync(3, "Vendeur", roleId: 3, actif: true, nouveauMotDePasse: "nouveau-mdp", auteurId: 1));
        Assert.StartsWith("Indisponible en démonstration", e.Message);
        Assert.Null(admin.Modifie);

        // Sans mot de passe : la fiche reste modifiable ; hors démonstration : inchangé
        await cas.ExecuterAsync(3, "Vendeur", roleId: 3, actif: true, nouveauMotDePasse: null, auteurId: 1);
        Assert.Equal((3L, 3L, true, (string?)null), admin.Modifie);
        await new ModifierUtilisateur(admin, new HacheurFactice(), new JournalEspion(), new DroitsFactices { Titulaire = true }, new ModeDemo(false))
            .ExecuterAsync(3, "Vendeur", roleId: 3, actif: true, nouveauMotDePasse: "nouveau-mdp", auteurId: 1);
        Assert.Equal((3L, 3L, true, "hash:nouveau-mdp"), admin.Modifie);
    }
}
