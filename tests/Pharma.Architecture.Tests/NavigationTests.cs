namespace Pharma.Architecture.Tests;

/// <summary>
/// Navigation en deux niveaux (docs/navigation.md) : en haut les domaines, à gauche les écrans du domaine (NavDomaine).
/// Un écran à menu vertical passe par NavDomaine (ou NavAdmin pour l'Administration) : un menu fait à la main ne listerait pas
/// les autres écrans du domaine et réintroduirait les groupes « Autres écrans … » retirés le 2026-11-13.
/// </summary>
public class NavigationTests
{
    /// <summary>Composants qui gardent leur propre menu vertical, avec la raison.</summary>
    private static readonly Dictionary<string, string> MenusPropres = new()
    {
        ["Mise/NavDomaine.razor"] = "le menu de domaine lui-même",
        ["Pages/GuideEssai.razor"] = "guide d'essai : hors des domaines (menu du compte)",
    };

    [Fact]
    public void Les_menus_verticaux_passent_par_NavDomaine()
    {
        var composants = Path.Combine(RacineDepot(), "src", "Pharma.Desktop", "Composants");
        var ecarts = Directory.GetFiles(composants, "*.razor", SearchOption.AllDirectories)
            .Select(f => (Nom: Path.GetRelativePath(composants, f).Replace('\\', '/'), Texte: File.ReadAllText(f)))
            .Where(f => f.Texte.Contains("class=\"nav-laterale", StringComparison.Ordinal) && !MenusPropres.ContainsKey(f.Nom))
            .Select(f => f.Nom).ToList();
        Assert.True(ecarts.Count == 0, "Menu vertical fait à la main (utiliser NavDomaine) : " + string.Join(", ", ecarts));
    }

    [Fact]
    public void Plus_de_groupe_autres_ecrans()
    {
        var composants = Path.Combine(RacineDepot(), "src", "Pharma.Desktop", "Composants");
        var ecarts = Directory.GetFiles(composants, "*.razor", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("Autres écrans", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(composants, f)).ToList();
        Assert.True(ecarts.Count == 0, "Groupe « Autres écrans … » : les écrans du domaine sont déjà listés par NavDomaine : " + string.Join(", ", ecarts));
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
