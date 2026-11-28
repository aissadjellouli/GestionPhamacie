using Pharma.Desktop.Services;

namespace Pharma.Application.Tests;

/// <summary>Décision du titulaire du 2026-11-28 : les rubriques repliées du menu de gauche sont gardées pour chaque employé, pas pour le poste.</summary>
public class EtatMenusTests
{
    [Fact]
    public void Les_choix_d_un_employe_ne_passent_pas_a_l_employe_suivant_et_lui_sont_rendus_a_son_retour()
    {
        string? connecte = EtatMenus.Employe(true, false, "Vendeur Démo");
        var etat = new EtatMenus(() => connecte);
        etat.Choisir("stock", false, "app://stock");
        Assert.False(etat.Ouvert("stock", true, false, "app://stock"));

        connecte = EtatMenus.Employe(true, false, "Assistant Démo");     // changement d'utilisateur sur le même poste
        Assert.True(etat.Ouvert("stock", true, false, "app://stock"));      // état par défaut, pas le choix du vendeur

        connecte = EtatMenus.Employe(true, true, "Vendeur Démo");         // super admin du même nom : à part
        Assert.True(etat.Ouvert("stock", true, false, "app://stock"));

        connecte = EtatMenus.Employe(true, false, "Vendeur Démo");        // retour du vendeur : son choix est gardé
        Assert.False(etat.Ouvert("stock", true, false, "app://stock"));
        Assert.Null(EtatMenus.Employe(false, false, "Vendeur Démo"));
    }
}
