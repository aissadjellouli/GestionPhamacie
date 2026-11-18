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
        Assert.Throws<ArgumentException>(() => OrdresAdministration.Creation(["--ordre-prix", "--produit", "2"]));
        Assert.Throws<ArgumentException>(() => OrdresAdministration.Creation(["--ordre-prix", "--produit", "2", "--prix", "abc"]));
        var p = OrdresAdministration.Creation(["--ordre-prix", "--produit", "2", "--prix", "380,50"])!.Value;
        Assert.Equal(("prix_produit", "2", (object?)380.50m, (object?)null), (p.Type, p.Cible, p.Donnees["prix_public"], p.Donnees["tarif_reference"]));
    }

    [Fact]
    public void Ordre_commande_lit_chaque_ligne_et_refuse_une_ligne_mal_ecrite()
    {
        var c = OrdresAdministration.Creation(["--ordre-commande", "--officine", "x", "--grossiste", "4", "--ligne", "42:10", "--ligne", "43:2:350.50", "--auteur", "a"])!.Value;
        Assert.Equal(("commande", "4"), (c.Type, c.Cible));
        var lignes = Assert.IsType<List<Dictionary<string, object?>>>(c.Donnees["lignes"]);
        Assert.Equal([42L, 43L], lignes.Select(l => (long)l["produit"]!));
        Assert.Equal([null, 350.50m], lignes.Select(l => (decimal?)l["prix_achat_ht"]));
        Assert.Throws<ArgumentException>(() => OrdresAdministration.Creation(["--ordre-commande", "--grossiste", "4"]));
        Assert.Throws<ArgumentException>(() => OrdresAdministration.Creation(["--ordre-commande", "--grossiste", "4", "--ligne", "42"]));
        Assert.Throws<ArgumentException>(() => OrdresAdministration.Creation(["--ordre-commande", "--ligne", "42:1"]));
    }

    [Fact]
    public void Ordres_de_referentiels_lisent_leur_fichier()
    {
        var dossier = Directory.CreateTempSubdirectory("ordres-ref-");
        try
        {
            var dci = Path.Combine(dossier.FullName, "dci.txt");
            File.WriteAllLines(dci, ["# antibiotiques", "AMOXICILLINE", "", "  ACIDE CLAVULANIQUE, AMOXICILLINE  "]);
            var c = OrdresAdministration.Creation(["--ordre-classe-suivie", "--classe", "antibiotiques", "--fichier", dci])!.Value;
            Assert.Equal(("classe_suivie", "antibiotiques"), (c.Type, c.Cible));
            Assert.Equal(["AMOXICILLINE", "ACIDE CLAVULANIQUE, AMOXICILLINE"], (List<string>)c.Donnees["dci"]!);

            var org = Path.Combine(dossier.FullName, "organismes.json");
            File.WriteAllText(org, """{"organismes":[{"code":"CNAS","nom":"Caisse"}]}""");
            var o = OrdresAdministration.Creation(["--ordre-organismes", "--fichier", org])!.Value;
            Assert.Equal(("organismes", (string?)null), (o.Type, o.Cible));
            Assert.Equal(1, ((System.Text.Json.JsonElement)o.Donnees["organismes"]!).GetArrayLength());

            File.WriteAllText(org, "{ pas du json");
            Assert.Throws<ArgumentException>(() => OrdresAdministration.Creation(["--ordre-organismes", "--fichier", org]));
            Assert.Throws<ArgumentException>(() => OrdresAdministration.Creation(["--ordre-classe-suivie", "--classe", "antibiotiques", "--fichier", Path.Combine(dossier.FullName, "absent.txt")]));
        }
        finally
        {
            dossier.Delete(true);
        }
    }
}
