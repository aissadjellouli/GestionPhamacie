using System.Text.RegularExpressions;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Décision du titulaire du 2026-11-24 : database/init contient TOUS les scripts de mise en production (base neuve), y compris les scripts
/// d'administrateur, et il est livré avec l'API. Une installation se fait avec le seul contenu livré, sans dépôt ni copie manuelle.
/// </summary>
public class InitCompletMepTests
{
    private static string Racine => ScriptsRejouablesTests.RacineDepot();
    private static string Lire(params string[] chemin) => File.ReadAllText(Path.Combine([Racine, .. chemin]));

    /// <summary>Scripts de mise en production attendus dans database/init.</summary>
    public static readonly string[] ScriptsMep =
    [
        "schema.sql", "fuseau_horaire.sql", "seed_referentiel.sql",
        "111b_roles_integrite.sql", "163b_droit_identifiant_base.sql", "166b_droit_promotion_base.sql"
    ];

    [Fact]
    public void Les_scripts_de_mise_en_production_sont_tous_dans_database_init()
    {
        var init = Directory.GetFiles(Path.Combine(Racine, "database", "init"), "*.sql").Select(Path.GetFileName).ToList();
        foreach (var s in ScriptsMep) Assert.Contains(s, init);
        // Aucun script d'administrateur de mise en production ne reste dans la suite numérotée : seul 101b (irréversible, jamais automatique)
        var horsInit = Directory.GetFiles(Path.Combine(Racine, "database", "migration"), "*b_*.sql").Select(Path.GetFileName).ToList();
        Assert.Equal(["101b_suppression_num_facture_chifa.sql"], horsInit);
        // Tout script « b » de l'init est attendu (un nouveau script d'administrateur doit être ajouté à la liste et aux outils de mise en production)
        Assert.All(init.Where(n => Regex.IsMatch(n!, @"^\d{3}b_")), n => Assert.Contains(n, ScriptsMep));
    }

    [Fact]
    public void Le_fuseau_de_l_init_est_identique_au_script_083()
    {
        var migration = Directory.GetFiles(Path.Combine(Racine, "database", "migration"), "083_*.sql").Single();
        Assert.Equal(CorpsSql(File.ReadAllText(migration)), CorpsSql(Lire("database", "init", "fuseau_horaire.sql")));
    }

    [Fact]
    public void L_api_publie_database_init_sans_clean_sql()
    {
        var projet = Lire("src", "Pharma.Api", "Pharma.Api.csproj");
        var ligne = projet.Split('\n').Single(l => l.Contains(@"database\init\*.sql"));
        Assert.Contains(@"Exclude=""..\..\database\init\clean.sql""", ligne);
        Assert.Contains(@"Link=""database\init\%(Filename)%(Extension)""", ligne);
        Assert.Contains(@"CopyToPublishDirectory=""PreserveNewest""", ligne);
        // 101b reste exclu de la publication (irréversible)
        Assert.Contains(@"Exclude=""..\..\database\migration\*b_*.sql""", projet);
    }

    [Fact]
    public void Les_outils_de_mise_en_production_prennent_les_scripts_dans_database_init()
    {
        var creer = Lire("scripts", "mep", "creer_base_officine.ps1");
        foreach (var s in new[] { "fuseau_horaire.sql", "111b_roles_integrite.sql", "163b_droit_identifiant_base.sql", "166b_droit_promotion_base.sql" })
            Assert.Contains($"Join-Path $dossierInit \"{s}\"", creer);
        Assert.DoesNotContain("083_*", creer);

        var majour = Lire("scripts", "mep", "mettre_a_jour_base_officine.ps1");
        foreach (var s in new[] { "111b_roles_integrite.sql", "163b_droit_identifiant_base.sql", "166b_droit_promotion_base.sql" })
        {
            Assert.Contains($@"Join-Path $DossierMigration ""init\{s}""", majour);
            Assert.Contains($@"""database\init\{s}""", majour);
        }

        var bascule = Lire("scripts", "basculer-pc-secours.ps1");
        foreach (var s in new[] { "111b_roles_integrite.sql", "163b_droit_identifiant_base.sql", "166b_droit_promotion_base.sql" })
        {
            Assert.Contains($@"""database\init\{s}""", bascule);
            Assert.DoesNotContain($@"database\migration\{s}", bascule);
        }
    }

    /// <summary>Corps SQL sans commentaires ni lignes vides (les en-têtes diffèrent : copie de mise en production).</summary>
    private static string CorpsSql(string texte) =>
        string.Join("\n", texte.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0 && !l.TrimStart().StartsWith("--")));
}
