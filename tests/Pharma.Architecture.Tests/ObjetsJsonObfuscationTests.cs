using System.Text.RegularExpressions;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Lot P2 de la protection du code (essai du 2026-11-27) : après obfuscation, un enregistrement positionnel NON public ne se relit plus
/// depuis du JSON (les paramètres de son constructeur sont renommés ; constaté : date plancher de l'offre illisible, autorisation d'un
/// poste impossible). L'attribut standard [Obfuscation(Exclude = true, ApplyToMembers = true)] garde ses noms.
/// Règle figée ici : dans un fichier source livré qui lit ou écrit du JSON, tout enregistrement positionnel non public porte cet attribut.
/// Les types publics ne sont pas renommés (réglage de l'obfuscation) : ils n'en ont pas besoin. Voir docs/protection-du-code.md § 9.7.
/// </summary>
public class ObjetsJsonObfuscationTests
{
    private static readonly string[] ProjetsLivres =
        ["Pharma.Api", "Pharma.Application", "Pharma.Domain", "Pharma.Infrastructure", "Pharma.Shared", "Pharma.Desktop"];

    private static readonly Regex UsageJson = new(@"JsonSerializer|FromJsonAsync|AsJsonAsync|Results\.(Ok|Json)", RegexOptions.Compiled);

    private static readonly Regex EnregistrementPositionnel = new(
        @"^[ \t]*(?<mods>(?:(?:public|internal|private|protected|file|sealed|abstract|readonly|partial|static)\s+)*)record\s+(?:struct\s+|class\s+)?(?<nom>\w+)\s*(?:<[^>]*>)?\s*\(",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex Attribut = new(@"Obfuscation\(\s*Exclude\s*=\s*true\s*,\s*ApplyToMembers\s*=\s*true\s*\)", RegexOptions.Compiled);

    internal static IEnumerable<string> SourcesLivrees()
    {
        var src = Path.Combine(BaseJetableM4.RacineDepot(), "src");
        return ProjetsLivres.SelectMany(p => Directory.EnumerateFiles(Path.Combine(src, p), "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
    }

    /// <summary>Enregistrements positionnels non publics d'un texte source, avec la présence de l'attribut dans les trois lignes qui précèdent.</summary>
    internal static IEnumerable<(string Nom, int Ligne, bool Protege)> NonPublics(string texte)
    {
        foreach (Match m in EnregistrementPositionnel.Matches(texte))
        {
            if (Regex.IsMatch(m.Groups["mods"].Value, @"\bpublic\b")) continue;
            var debut = m.Index;
            for (var n = 0; n < 3 && debut > 0; n++) debut = texte.LastIndexOf('\n', Math.Max(0, debut - 2)) + 1;
            var ligne = texte.AsSpan(0, m.Index).Count('\n') + 1;
            yield return (m.Groups["nom"].Value, ligne, Attribut.IsMatch(texte[debut..m.Index]));
        }
    }

    [Fact]
    public void Tout_enregistrement_non_public_d_un_fichier_qui_manie_du_JSON_garde_ses_noms_a_l_obfuscation()
    {
        var ecarts = new List<string>();
        var proteges = 0;
        foreach (var f in SourcesLivrees())
        {
            var texte = File.ReadAllText(f);
            if (!UsageJson.IsMatch(texte)) continue;
            foreach (var (nom, ligne, protege) in NonPublics(texte))
            {
                if (protege) proteges++;
                else ecarts.Add($"{Path.GetRelativePath(BaseJetableM4.RacineDepot(), f)}({ligne}) : {nom}");
            }
        }
        Assert.True(ecarts.Count == 0,
            "Enregistrements non publics à marquer [System.Reflection.Obfuscation(Exclude = true, ApplyToMembers = true)] (ou à rendre publics) :\n"
            + string.Join("\n", ecarts));
        Assert.True(proteges > 0, "Aucun enregistrement protégé trouvé : la détection ne fonctionne plus.");
    }

    [Theory]
    [InlineData("    private sealed record A(int X);", false)]
    [InlineData("    [System.Reflection.Obfuscation(Exclude = true, ApplyToMembers = true)]\n    private sealed record A(int X);", true)]
    [InlineData("    // commentaire\n    [System.Reflection.Obfuscation(Exclude = true, ApplyToMembers = true)]\n    /// <summary>x</summary>\n    record A(\n        int X);", true)]
    [InlineData("    [System.Reflection.Obfuscation(Exclude = true, ApplyToMembers = true)]\n    private sealed record A(int X);\n\n\n\n    private sealed record B(int Y);", false)]
    public void Detection_de_l_attribut(string source, bool tousProteges) =>
        Assert.Equal(tousProteges, NonPublics(source).All(e => e.Protege));

    [Fact]
    public void Un_enregistrement_public_n_est_pas_concerne() =>
        Assert.Empty(NonPublics("public sealed record A(int X);\n    public record B(\n int Y);"));
}
