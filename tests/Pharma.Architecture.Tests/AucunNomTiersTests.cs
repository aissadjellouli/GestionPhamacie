using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Règle du titulaire (2026-11-16) : aucun nom de logiciel tiers dans les livrables suivis par git.
/// Les motifs interdits ne sont jamais écrits en clair : seules leurs empreintes SHA-256 (mot ou paire de mots
/// en minuscules, séparés par une espace) sont stockées ; chaque mot et chaque paire de mots consécutifs des
/// fichiers suivis est comparé à ces empreintes.
/// Règle simple pour le vocabulaire technique : seuls les mots exacts du qualificatif interdit (singulier et
/// pluriel masculins) sont refusés ; « concurrence », « concurrentes », le mot-clé SQL CONCURRENTLY et les
/// identifiants .NET composés (ConcurrentDictionary…) restent permis car ce sont des mots différents.
/// Seul l'espace de noms .NET System.Collections.Concurrent est retiré avant le découpage.
/// </summary>
public class AucunNomTiersTests
{
    private static readonly HashSet<string> EmpreintesInterdites = new(StringComparer.Ordinal)
    {
        "a35178ecc6460905972723565d23d62ff38f8d206c2b399c25d06853e17d30fe",
        "c09a403e6fd052556b08da356d9da49a9af313c6aa4872cd998be3beada63cbd",
        "f10b7ca159ae18ccce2fc26f2c1bc2b7faa8913a319e6583759d9e85635546bf",
        "3f472205094d4323869cf49a0ac73b6b67efb1d303ed4810501e8e5d0ccb4e3c",
        "56997adffa84ab716b3e8564ec4b492c81996ff247ce272c37fcac8e54eab1dd",
        "deb665b13f802e845025f1d5da0f382f9770eb8ddd3ba3d34a7f0ed8ffbb711c",
        "7b5775ab391312de5cd23c1ed8b6719b37d979338c8a977b5acff7ff9ddd03a8",
        "9715b61f7dfba928eeb6e118136407df4f2a914e672ebf8f446bfb48e634d38e",
        "8e474e4950b060aaae1533bb981c47a91f9d9d2f797fccb680d23292b3834f8b",
        "2a6171c049b184154f9b5e73b2809aae6eba1fdf3ebed70a0cc00ceaeae5e608",
        "70fa99fbe157a5afcfebdfc420c6cb14cae14748a39fec33444f9d141d9bd90f",
        "4ba949c97edc566bd289d5cd7aef132200240827cd349af52e1a60f5175d2d87",
        "99f01b88a95d5a2d4c6984191d7a2aa2cc0965f1bf002a3b1a1c6c401a0257b9",
        "a4021c009e2ac0dcafb90a44c0a2526e23716efef4571c457251fa7ff9fbef1a",
        "f0157fce03b33ef3bfb3c6ea9d66e1a8f816470cb1dca2080a35c35c131a7e26",
        "071c9c7ea48c9fe0a6dc75ad9743c066ae74275424033e51fb34634620d0c169",
    };

    private static readonly string[] Extensions =
        [".cs", ".razor", ".sql", ".md", ".ps1", ".json", ".sln", ".slnx", ".gitignore", ".csproj"];

    private static string Racine()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } racine) return racine;
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    private static string Empreinte(string texte) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(texte)));

    /// <summary>Mots (lettres et chiffres) en minuscules ; tout autre caractère sépare.</summary>
    private static List<string> Mots(string texte)
    {
        texte = texte.Replace("System.Collections.Concurrent", " ", StringComparison.Ordinal);
        var mots = new List<string>();
        var courant = new StringBuilder();
        foreach (var c in texte)
        {
            if (char.IsLetterOrDigit(c)) courant.Append(char.ToLowerInvariant(c));
            else if (courant.Length > 0) { mots.Add(courant.ToString()); courant.Clear(); }
        }
        if (courant.Length > 0) mots.Add(courant.ToString());
        return mots;
    }

    /// <summary>Mots ou paires de mots du texte dont l'empreinte est interdite.</summary>
    private static IEnumerable<string> Interdits(string texte)
    {
        var mots = Mots(texte);
        for (var i = 0; i < mots.Count; i++)
        {
            if (EmpreintesInterdites.Contains(Empreinte(mots[i]))) yield return mots[i];
            if (i + 1 < mots.Count)
            {
                var paire = mots[i] + " " + mots[i + 1];
                if (EmpreintesInterdites.Contains(Empreinte(paire))) yield return paire;
            }
        }
    }

    private static List<string> FichiersSuivis(string racine)
    {
        var info = new ProcessStartInfo("git", "ls-files -z")
        {
            WorkingDirectory = racine,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var git = Process.Start(info) ?? throw new InvalidOperationException("git introuvable.");
        var sortie = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, "git ls-files a échoué : " + git.StandardError.ReadToEnd());
        return sortie.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static bool AControler(string chemin)
    {
        var c = chemin.Replace('\\', '/');
        if (c.StartsWith("docs/taches/", StringComparison.OrdinalIgnoreCase)
            || c.StartsWith("docs/EXEMPLE/", StringComparison.OrdinalIgnoreCase)) return false;
        if (c.Split('/').Any(d => d is "bin" or "obj")) return false;
        var nom = Path.GetFileName(c);
        return Extensions.Any(e => nom.Equals(e, StringComparison.OrdinalIgnoreCase)
                                   || nom.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Aucun_nom_de_logiciel_tiers_dans_les_fichiers_suivis()
    {
        var racine = Racine();
        var fautes = new List<string>();
        foreach (var chemin in FichiersSuivis(racine))
        {
            var complet = Path.Combine(racine, chemin);
            // Le nom du fichier lui-même est contrôlé, quel que soit son type
            foreach (var m in Interdits(chemin)) fautes.Add($"{chemin} (nom de fichier) : {Empreinte(m)[..12]}");
            if (!AControler(chemin) || !File.Exists(complet)) continue;
            var lignes = File.ReadAllLines(complet);
            for (var i = 0; i < lignes.Length; i++)
                foreach (var m in Interdits(lignes[i]))
                    fautes.Add($"{chemin}:{i + 1} : empreinte {Empreinte(m)[..12]}");
        }
        Assert.True(fautes.Count == 0, "Nom de logiciel tiers dans les livrables :" + Environment.NewLine
                                       + string.Join(Environment.NewLine, fautes));
    }

    [Fact]
    public void Vocabulaire_technique_permis_et_qualificatif_interdit_detecte()
    {
        // Mots techniques permis
        Assert.Empty(Interdits("CREATE INDEX CONCURRENTLY ; contrôle de concurrence ; insertions concurrentes"));
        Assert.Empty(Interdits("using System.Collections.Concurrent; new ConcurrentDictionary<string, int>()"));
        // Qualificatif interdit, reconstitué ici pour ne pas figurer en clair dans ce fichier
        var interdit = "concur" + "rent";
        Assert.Single(Interdits("un logiciel " + interdit));
        Assert.Single(Interdits("les " + interdit + "s"));
    }
}
