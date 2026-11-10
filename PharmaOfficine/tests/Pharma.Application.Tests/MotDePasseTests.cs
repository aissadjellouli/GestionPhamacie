using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Tests;

public class MotDePasseTests
{
    private static Utilisateur Titulaire(bool provisoire) => new()
    {
        Id = 1, NomComplet = "Titulaire Fictif", Identifiant = "titulaire", MotDePasseHash = "hash:Provisoire-2026", RoleId = 1,
        DoitChangerMotDePasse = provisoire
    };

    [Fact]
    public async Task Mot_de_passe_provisoire_donne_un_jeton_sans_droit_signale_au_poste()
    {
        var jetons = new JetonsFactices();
        var s = await new SeConnecter(new ComptesEnMemoire(Titulaire(true)), new VerificateurFactice(), jetons).ExecuterAsync("titulaire", "Provisoire-2026");
        Assert.True(s!.DoitChangerMotDePasse);
        Assert.True(jetons.AChanger.Single());
    }

    [Fact]
    public async Task Changement_exige_l_actuel_et_delivre_un_jeton_normal()
    {
        var comptes = new ComptesEnMemoire(Titulaire(true));
        var jetons = new JetonsFactices();
        var cas = new ChangerMonMotDePasse(comptes, new VerificateurFactice(), new HacheurFactice(), jetons, new JournalEspion());
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(1, "faux", "Nouveau-2026", "Nouveau-2026"));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(1, "Provisoire-2026", "court", "court"));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(1, "Provisoire-2026", "Nouveau-2026", "Autre-2026"));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(1, "Provisoire-2026", "Provisoire-2026", "Provisoire-2026"));

        var s = await cas.ExecuterAsync(1, "Provisoire-2026", "Nouveau-2026", "Nouveau-2026");
        Assert.False(s.DoitChangerMotDePasse);
        Assert.Equal((1L, "hash:Nouveau-2026"), comptes.Change);
        Assert.False(jetons.AChanger.Single());
    }
}
