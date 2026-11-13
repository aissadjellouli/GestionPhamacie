namespace Pharma.Architecture.Tests;

/// <summary>
/// « ← Retour » (docs/navigation.md) : un tableau paginé retrouve ses filtres et sa page quand on revient sur son écran.
/// Tout composant qui affiche une pagination doit donc dire à l'historique quoi retenir (Historique.Suivre) et le relire
/// (Historique.Restaurer). Un nouveau tableau qui l'oublierait reviendrait toujours en page 1, filtres effacés.
/// </summary>
public class RetourEcranTests
{
    /// <summary>
    /// Écrans jamais rouverts par « ← Retour » (HistoriqueNavigation.JamaisRouverts) : l'historique n'y retient rien,
    /// un Suivre/Restaurer y serait du code sans effet. Composant → raison.
    /// </summary>
    private static readonly Dictionary<string, string> JamaisRouverts = new()
    {
        ["Pages/AdminRestauration.razor"] = "restauration de la base : écran jamais rouvert",
        ["Pages/AdminReprise.razor"] = "reprise de données : écran jamais rouvert",
    };

    [Fact]
    public void Chaque_tableau_pagine_est_retenu_par_l_historique()
    {
        var composants = Path.Combine(RacineDepot(), "src", "Pharma.Desktop", "Composants");
        var oublis = Directory.GetFiles(composants, "*.razor", SearchOption.AllDirectories)
            .Where(f => !JamaisRouverts.ContainsKey(Path.GetRelativePath(composants, f).Replace('\\', '/')))
            .Where(f => File.ReadAllText(f) is var t && t.Contains("<Pagination ", StringComparison.Ordinal)
                && !(t.Contains("Historique.Suivre(", StringComparison.Ordinal) && t.Contains("Historique.Restaurer(", StringComparison.Ordinal)))
            .Select(f => Path.GetRelativePath(composants, f))
            .ToList();
        Assert.True(oublis.Count == 0, "Tableaux paginés sans état retenu pour « ← Retour » : " + string.Join(", ", oublis));
    }

    [Fact]
    public void Les_ecrans_jamais_rouverts_sont_ceux_de_l_historique()
    {
        var racine = RacineDepot();
        var navigation = File.ReadAllText(Directory.GetFiles(Path.Combine(racine, "src", "Pharma.Desktop"), "HistoriqueNavigation.cs", SearchOption.AllDirectories).Single());
        foreach (var composant in JamaisRouverts.Keys)
        {
            var texte = File.ReadAllText(Path.Combine(racine, "src", "Pharma.Desktop", "Composants", composant));
            var route = texte.Split('\n')[0].Trim().Replace("@page \"/", "").TrimEnd('"');
            Assert.True(navigation.Contains($"\"{route}\"", StringComparison.Ordinal), $"{composant} ({route}) n'est pas dans HistoriqueNavigation.JamaisRouverts");
        }
    }

    private static string RacineDepot()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } racine) return racine;
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }
}
