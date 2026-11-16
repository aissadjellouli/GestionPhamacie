using System.Text.RegularExpressions;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Écran fixe (docs/charte-graphique.md, « Vérification de l'écran fixe ») : à 1 536 × 890, la page ne défile jamais ;
/// seul un tableau, une liste ou un cadre défile à l'intérieur. La mesure se fait sur le poste (sans navigateur ici) :
/// ces tests imposent qu'un nouvel écran soit ajouté à la liste des écrans vérifiés, et gardent les corrections en place.
/// </summary>
public partial class EcranFixeTests
{
    [Fact]
    public void Chaque_ecran_du_poste_figure_dans_la_liste_des_ecrans_verifies()
    {
        var racine = RacineDepot();
        var charte = File.ReadAllText(Path.Combine(racine, "docs", "charte-graphique.md"));
        var debut = charte.IndexOf("### Vérification de l’écran fixe", StringComparison.Ordinal);
        Assert.True(debut >= 0, "docs/charte-graphique.md : section « Vérification de l’écran fixe » absente");
        var section = charte[debut..];
        var fin = section.IndexOf("\n### ", 5, StringComparison.Ordinal);
        if (fin > 0) section = section[..fin];

        var pages = Path.Combine(racine, "src", "Pharma.Desktop", "Composants", "Pages");
        var absents = new List<string>();
        foreach (var f in Directory.GetFiles(pages, "*.razor"))
        {
            var m = Route().Match(File.ReadAllText(f));
            if (!m.Success) continue;
            var route = m.Groups[1].Value;
            if (route is "/connexion") continue;   // écran de connexion : hors menu, sans barre de menu
            if (!section.Contains("`" + route + "`", StringComparison.Ordinal)) absents.Add(route);
        }
        Assert.True(absents.Count == 0, "Écrans sans vérification de l'écran fixe consignée dans docs/charte-graphique.md : " + string.Join(", ", absents));
    }

    [Fact]
    public void Les_tuiles_de_l_accueil_defilent_dans_leur_cadre()
    {
        var accueil = File.ReadAllText(Path.Combine(RacineDepot(), "src", "Pharma.Desktop", "Composants", "Pages", "Accueil.razor"));
        Assert.Contains("class=\"cadre-defilant accueil-tuiles\"", accueil);
    }

    [GeneratedRegex("@page \"([^\"]+)\"")]
    private static partial Regex Route();

    private static string RacineDepot()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } racine) return racine;
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }
}
