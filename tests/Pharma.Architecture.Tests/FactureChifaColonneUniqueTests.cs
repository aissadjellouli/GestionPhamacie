namespace Pharma.Architecture.Tests;

/// <summary>
/// Fusion du n° de facture Chifa (2026-11-15, script 101) : chifa_facture est la seule colonne lue et écrite ; l'ancienne colonne
/// num_facture_chifa (obsolète, suppression par le script 101b après validation) n'apparaît plus dans le code.
/// </summary>
public class FactureChifaColonneUniqueTests
{
    /// <summary>Seule exception : la table des conflits du script 101, qui garde l'ancienne valeur (colonne num_facture_chifa de cette table).</summary>
    private static readonly HashSet<string> Autorises = ["Pharma.Infrastructure/Chifa/ConflitsFactureChifaSql.cs"];

    [Fact]
    public void Ancienne_colonne_num_facture_chifa_jamais_lue_ni_ecrite_dans_le_code()
    {
        var src = Path.Combine(RacineDepot(), "src");
        var fautifs = Directory.GetFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".razor") || f.EndsWith(".xaml"))
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f) is var t && (t.Contains("num_facture_chifa") || t.Contains("NumFactureChifa")))
            .Select(f => Path.GetRelativePath(src, f))
            .Where(f => !Autorises.Contains(f.Replace('\\', '/')))
            .ToList();
        Assert.True(fautifs.Count == 0, "Ancienne colonne num_facture_chifa encore utilisée : " + string.Join(", ", fautifs));
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
