using System.Diagnostics;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Règle du titulaire (2026-11-20) : tout document Markdown (.md) et tout script PowerShell (.ps1) du dépôt figure dans les deux
/// solutions Visual Studio (PharmaOfficine.slnx et PharmaOfficine.sln), pour être visible et ouvert depuis l'Explorateur de
/// solutions. Seuls les fichiers suivis par git sont contrôlés (docs/taches et les notes locales ignorées restent hors du dépôt).
/// </summary>
public class SolutionFichiersTests
{
    private static string Racine()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } racine) return racine;
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    /// <summary>Fichiers .md et .ps1 suivis par git ; null si git est absent (poste sans git : rien à contrôler).</summary>
    private static List<string>? FichiersSuivis(string racine)
    {
        try
        {
            var demarrage = new ProcessStartInfo("git", "ls-files -- *.md *.ps1")
            {
                WorkingDirectory = racine,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var processus = Process.Start(demarrage);
            if (processus is null) return null;
            var sortie = processus.StandardOutput.ReadToEnd();
            processus.WaitForExit();
            if (processus.ExitCode != 0) return null;
            return sortie.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(f => !f.StartsWith("docs/taches/", StringComparison.Ordinal))
                .ToList();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [Fact]
    public void Chaque_document_et_script_PowerShell_figure_dans_les_deux_solutions()
    {
        var racine = Racine();
        var fichiers = FichiersSuivis(racine);
        if (fichiers is null) return;
        Assert.NotEmpty(fichiers);

        var slnx = File.ReadAllText(Path.Combine(racine, "PharmaOfficine.slnx"));
        var sln = File.ReadAllText(Path.Combine(racine, "PharmaOfficine.sln"));
        var manquants = new List<string>();
        foreach (var f in fichiers)
        {
            if (!slnx.Contains($"Path=\"{f}\"", StringComparison.Ordinal)) manquants.Add($"{f} (PharmaOfficine.slnx)");
            var w = f.Replace('/', '\\');
            if (!sln.Contains($"{w} = {w}", StringComparison.Ordinal)) manquants.Add($"{f} (PharmaOfficine.sln)");
        }
        Assert.True(manquants.Count == 0,
            "Fichiers .md / .ps1 absents de la solution Visual Studio (les ajouter aux deux solutions) :\n" + string.Join('\n', manquants));
    }
}
