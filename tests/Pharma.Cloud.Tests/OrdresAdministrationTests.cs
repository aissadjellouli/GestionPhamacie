using Pharma.Cloud.Api.Ordres;

namespace Pharma.Cloud.Tests;

/// <summary>Lots S5.1 et S5.5 : arguments des commandes d'ordres du serveur central (script 013).</summary>
public class OrdresAdministrationTests
{
    [Fact]
    public void Creation_d_un_compte_droits_et_blocage()
    {
        var c = OrdresAdministration.Creation(["--ordre-employe-creer", "--officine", "x", "--nom", "A B", "--identifiant", "ab", "--role", "vendeur", "--auteur", "admin"])!.Value;
        Assert.Equal(("employe_creer", (string?)null), (c.Type, c.Cible));
        Assert.Equal("A B", c.Donnees["nom"]);
        var d = OrdresAdministration.Creation(["--ordre-droits-role", "--role", "vendeur", "--permissions", "caisse.vendre, stock.voir,", "--auteur", "a"])!.Value;
        Assert.Equal(("droits_role", "vendeur"), (d.Type, d.Cible));
        Assert.Equal(["caisse.vendre", "stock.voir"], (string[])d.Donnees["permissions"]!);
        var b = OrdresAdministration.Creation(["--ordre-employe-bloquer", "--employe", "11"])!.Value;
        Assert.Equal(("employe_bloquer", "11"), (b.Type, b.Cible));
        Assert.Null(OrdresAdministration.Creation(["--ordres"]));
    }

    [Fact]
    public void Options_obligatoires_refusees_quand_elles_manquent()
    {
        Assert.Throws<ArgumentException>(() => OrdresAdministration.Creation(["--ordre-employe-creer", "--nom", "A"]));
        Assert.Throws<ArgumentException>(() => OrdresAdministration.Creation(["--ordre-employe-modifier", "--employe", "11"]));
        Assert.Throws<ArgumentException>(() => OrdresAdministration.Creation(["--ordre-employe-bloquer"]));
        Assert.True(OrdresAdministration.Concerne("--expirer-ordres"));
    }
}
