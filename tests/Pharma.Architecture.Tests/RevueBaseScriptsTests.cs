using System.Text.RegularExpressions;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Revue de la base (2026-11-23) : bascule sur le PC de secours, textes de restauration et numérotation de database/migration.
/// Contrôles de contenu des fichiers (pas de serveur nécessaire).
/// </summary>
public partial class RevueBaseScriptsTests
{
    private static string Lire(params string[] chemin) => File.ReadAllText(Path.Combine([ScriptsRejouablesTests.RacineDepot(), .. chemin]));

    [Fact]
    public void La_bascule_sur_le_PC_de_secours_rejoue_la_suite_sous_le_role_et_avec_le_marqueur_des_autres_outils()
    {
        var bascule = Lire("scripts", "basculer-pc-secours.ps1");
        // Marqueur de la mutualisation des réglages (règle 10 de docs/scripts-base-de-donnees.md)
        Assert.Contains("pharma.reglage_central=on", bascule);
        // Jamais le superutilisateur seul : SET ROLE pharma_app, puis pharma_proprietaire après 111b
        Assert.Contains("SET ROLE $roleScripts", bascule);
        Assert.Contains("111b_roles_integrite.sql", bascule);
        Assert.Contains("RoleProprietaire", bascule);
        var rejeu = bascule.Split('\n').Where(l => l.Contains("-f $s.FullName")).ToList();
        Assert.Single(rejeu);
        Assert.Contains("SET ROLE", rejeu[0]);
        // Documentation : l'outil figure dans la liste des outils qui posent le marqueur
        var regle10 = Lire("docs", "scripts-base-de-donnees.md").Split('\n').Single(l => l.StartsWith("10. **Écriture dans un réglage mutualisable"));
        Assert.Contains("basculer-pc-secours.ps1", regle10);
    }

    [Fact]
    public void Les_outils_de_restauration_ne_proposent_pas_de_rejouer_seulement_les_scripts_plus_recents()
    {
        var restaurer = Lire("scripts", "restaurer_pharmacie.ps1");
        Assert.DoesNotContain("0xx", restaurer);
        Assert.DoesNotContain("plus récents que la sauvegarde", restaurer);
        Assert.Contains("depuis le script 001", restaurer);
    }

    [Fact]
    public void La_suite_numerotee_de_l_officine_est_sans_trou_ni_doublon_hors_numeros_non_attribues()
    {
        var nonAttribues = NumerosNonAttribues();
        var fichiers = Directory.GetFiles(Path.Combine(ScriptsRejouablesTests.RacineDepot(), "database", "migration"), "*.sql").Select(Path.GetFileName).ToList();
        var numeros = fichiers.Select(f => NumeroSeul().Match(f!)).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.Empty(numeros.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key));
        Assert.Empty(numeros.Intersect(nonAttribues));   // un numéro listé « non attribué » ne doit pas avoir de script
        var manquants = Enumerable.Range(1, numeros.Max()).Except(numeros).Except(nonAttribues).ToList();
        Assert.True(manquants.Count == 0, "Numéros manquants dans database/migration (ni script, ni numéro non attribué) : " + string.Join(", ", manquants));
        // Scripts à suffixe : seulement ceux documentés (docs/scripts-base-de-donnees.md, 3.1, 3.2 et 3.9)
        var suffixes = fichiers.Where(f => SuffixeLettre().IsMatch(f!)).Select(f => f![..4]).Order().ToList();
        Assert.Equal(["101b", "111b", "163b"], suffixes);
    }

    [Fact]
    public void Les_outils_mep_de_l_officine_controlent_la_suite_sans_trou_avec_la_meme_liste_de_numeros_non_attribues()
    {
        Assert.Equal([113, 119], NumerosNonAttribues());
        Assert.Contains("function Test-SuiteSansTrou", Lire("scripts", "mep", "outils_mep.ps1"));
        Assert.Contains("Test-SuiteSansTrou", Lire("scripts", "mep", "creer_base_officine.ps1"));
        Assert.Contains("Test-SuiteSansTrou", Lire("scripts", "mep", "mettre_a_jour_base_officine.ps1"));
        Assert.Contains("113", Lire("docs", "base-de-donnees.md").Split("## Numérotation")[1]);
    }

    private static int[] NumerosNonAttribues()
    {
        var m = ListeNonAttribues().Match(Lire("scripts", "mep", "outils_mep.ps1"));
        Assert.True(m.Success, "Liste $script:NumerosNonAttribues introuvable dans outils_mep.ps1.");
        return m.Groups[1].Value.Split(',').Select(s => int.Parse(s.Trim())).ToArray();
    }

    [GeneratedRegex(@"^(\d{3})_.+\.sql$")]
    private static partial Regex NumeroSeul();

    [GeneratedRegex(@"^\d{3}[a-z]_.+\.sql$")]
    private static partial Regex SuffixeLettre();

    [GeneratedRegex(@"\$script:NumerosNonAttribues\s*=\s*@\(([\d,\s]+)\)")]
    private static partial Regex ListeNonAttribues();
}
