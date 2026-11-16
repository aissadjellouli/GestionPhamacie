using System.Text.RegularExpressions;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Installation neuve depuis database/init : le référentiel de départ doit contenir le classement sync_table de toutes les tables
/// (même sens que les scripts numérotés), la ligne de réglage sync_parametre et les familles nominatives, sinon la synchronisation
/// ne pourrait pas être activée (sync_tables_non_classees) ou rien ne serait retiré avant envoi.
/// </summary>
public partial class SynchronisationInventaireTests
{
    [Fact]
    public void Le_seed_contient_le_classement_de_synchronisation_des_scripts()
    {
        var seed = CommentaireLigne().Replace(File.ReadAllText(Path.Combine(Racine, "database", "init", "seed_referentiel.sql")), "");
        var dansSeed = new Dictionary<string, string>();
        foreach (Match insertion in InsertionSyncTable().Matches(seed))
            foreach (Match m in Classement().Matches(insertion.Value)) dansSeed[m.Groups[1].Value] = m.Groups[2].Value;
        var scripts = ClassementScripts();
        var ecarts = scripts.Where(c => !dansSeed.TryGetValue(c.Key, out var s) || s != c.Value).Select(c => $"{c.Key} ({c.Value})")
            .Concat(dansSeed.Keys.Where(k => !scripts.ContainsKey(k)).Select(k => k + " (absente des scripts)")).ToList();
        Assert.True(ecarts.Count == 0, "database/init/seed_referentiel.sql ne suit pas sync_table : " + string.Join(", ", ecarts));
        Assert.Contains("INSERT INTO sync_parametre DEFAULT VALUES ON CONFLICT (id) DO NOTHING;", seed);
        Assert.Contains("INSERT INTO sync_famille_colonne", seed);
    }
}
