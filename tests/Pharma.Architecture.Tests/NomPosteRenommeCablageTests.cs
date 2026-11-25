namespace Pharma.Architecture.Tests;

/// <summary>
/// Poste renommé par le titulaire (constat du 2026-11-26) : le poste reprend le nom que le serveur donne à ce poste prouvé, l'enregistre dans
/// son identité et l'affiche sans redémarrage (la décision est testée dans Pharma.Application.Tests/NomPosteServeurTests).
/// </summary>
public class NomPosteRenommeCablageTests
{
    private static string Lire(params string[] chemin) =>
        File.ReadAllText(Path.Combine([BaseJetableM4.RacineDepot(), "src", "Pharma.Desktop", .. chemin])).Replace("\r\n", "\n");

    [Fact]
    public void Nom_recu_du_serveur_enregistre_puis_affiche_dans_l_en_tete_et_la_barre()
    {
        var api = Lire("Services", "ApiClient.cs");
        Assert.Contains("NomPosteServeur.NouveauNom(identite.PosteId, identite.Nom, etat) is { } nouveauNom) identite.Renommer(nouveauNom);", api);

        var identite = Lire("Services", "IdentitePoste.cs");
        Assert.Contains("var lu = identite with { Nom = nom };", identite);   // seul le nom change : identifiant et clé gardés

        Assert.Contains("identite.Changee += () => poste.Renommer(identite.Nom);", Lire("App.xaml.cs"));
        Assert.Contains("Identite.Changee += SurIdentite;", Lire("Composants", "Mise", "MiseEnPage.razor"));
        Assert.Contains("IdentiteDuPoste.Changee += SurIdentite;", Lire("Composants", "Mise", "BarreEtat.razor"));
        // Renommé depuis ce poste : relu tout de suite (les autres postes, à leur prochain écran de connexion)
        Assert.Contains("await Api.EtatAutorisationPosteAsync();", Lire("Composants", "Pages", "AdminPostes.razor"));
    }
}
