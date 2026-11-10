using System.Text.RegularExpressions;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Charte graphique, « Erreurs de saisie » : tout champ obligatoire (libellé « requis », astérisque rouge) peut passer en rouge
/// quand il manque — soit contrôlé sur le poste (attribut aria-invalid lié à Obligatoire), soit désigné par le serveur dans
/// son refus (attribut data-champ). Un champ obligatoire qui ne peut ni l'un ni l'autre laisserait l'employé chercher son erreur.
/// </summary>
public partial class ChampsObligatoiresTests
{
    [Fact]
    public void Chaque_champ_obligatoire_peut_passer_en_rouge()
    {
        var composants = Path.Combine(RacineDepot(), "src", "Pharma.Desktop", "Composants");
        var oublis = new List<string>();
        var vus = 0;
        foreach (var fichier in Directory.GetFiles(composants, "*.razor", SearchOption.AllDirectories))
        {
            var razor = File.ReadAllText(fichier);
            foreach (Match libelle in LibelleRequis().Matches(razor))
            {
                var id = libelle.Groups["id"].Value;
                if (id.Contains('@')) continue;   // identifiant calculé : contrôlé à la relecture
                vus++;
                var champ = Regex.Match(razor, $@"<(?:input|select|textarea)\b[^>]*\bid=""{Regex.Escape(id)}""[^>]*>", RegexOptions.Singleline);
                if (!champ.Success) { oublis.Add($"{Path.GetFileName(fichier)} : libellé obligatoire « {id} » sans champ"); continue; }
                if (!champ.Value.Contains("aria-invalid") && !champ.Value.Contains("data-champ"))
                    oublis.Add($"{Path.GetFileName(fichier)} : champ obligatoire « {id} » sans aria-invalid ni data-champ");
            }
        }
        Assert.True(vus > 20, $"Seulement {vus} champs obligatoires trouvés : l'analyse des écrans ne fonctionne plus.");
        Assert.True(oublis.Count == 0, "Champs obligatoires qui ne peuvent pas passer en rouge :\n" + string.Join("\n", oublis));
    }

    [Fact]
    public void Chaque_champ_designe_par_le_serveur_existe_sur_un_ecran()
    {
        // Les noms passés à RegleMetierException(message, champ) doivent correspondre à un data-champ du poste
        var racine = RacineDepot();
        var serveur = Directory.GetFiles(Path.Combine(racine, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => ChampServeur().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .ToHashSet();
        var poste = Directory.GetFiles(Path.Combine(racine, "src", "Pharma.Desktop", "Composants"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => DataChamp().Matches(File.ReadAllText(f)).SelectMany(m => m.Groups[1].Value.Split(' ')))
            .ToHashSet();
        Assert.True(serveur.Count > 20, "Analyse des refus du serveur : trop peu de champs trouvés.");
        var orphelins = serveur.Where(c => !poste.Contains(c)).Order().ToList();
        Assert.True(orphelins.Count == 0, "Champs désignés par le serveur sans data-champ sur le poste : " + string.Join(", ", orphelins));
    }

    private static string RacineDepot()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } racine) return racine;
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    // <label for="x" class="requis"> ou class="… requis …", dans un ordre d'attributs quelconque
    [GeneratedRegex(@"<label\b(?=[^>]*\bclass=""[^""]*\brequis\b)[^>]*\bfor=""(?<id>[^""]+)""[^>]*>", RegexOptions.Singleline)] private static partial Regex LibelleRequis();
    [GeneratedRegex(@"new RegleMetierException\((?:\$?""(?:[^""\\]|\\.)*""), ""([a-z_]+)""\)")] private static partial Regex ChampServeur();
    [GeneratedRegex(@"data-champ=""([a-z_ ]+)""")] private static partial Regex DataChamp();
}
