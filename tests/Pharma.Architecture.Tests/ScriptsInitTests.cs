using System.Reflection;
using System.Text.RegularExpressions;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Règle permanente (docs/base-de-donnees.md) : tout script numéroté database/migration/0NN_*.sql est répliqué dans les scripts
/// d'installation — la structure dans init/schema.sql, le référentiel dans init/seed_referentiel.sql — dans la même modification.
/// </summary>
public partial class ScriptsInitTests
{
    private static readonly string Base = Path.Combine(RacineDepot(), "database", "migration");

    /// <summary>Scripts numérotés, dans leur ordre d'exécution, commentaires SQL retirés.</summary>
    private static readonly (string Nom, int Numero, string Sql)[] Scripts = Directory.GetFiles(Base, "*.sql")
        .Select(f => (Nom: Path.GetFileName(f), Fichier: f))
        .Where(f => NumeroScript().IsMatch(f.Nom))
        .Select(f => (f.Nom, int.Parse(NumeroScript().Match(f.Nom).Groups[1].Value), SansCommentaires(File.ReadAllText(f.Fichier))))
        .OrderBy(s => s.Item2)
        .ToArray();

    private static readonly string Init = Path.Combine(RacineDepot(), "database", "init");
    private static readonly string Schema = File.ReadAllText(Path.Combine(Init, "schema.sql"));
    private static readonly string Seed = File.ReadAllText(Path.Combine(Init, "seed_referentiel.sql"));

    [Fact]
    public void Les_entetes_de_init_citent_le_dernier_script()
    {
        var dernier = Scripts[^1].Numero.ToString("000");
        Assert.True(Schema.Contains($"001 à {dernier}"), $"init/schema.sql doit couvrir les scripts 001 à {dernier} ({Scripts[^1].Nom}) : régénérez-le.");
        Assert.True(Seed.Contains($"001 à {dernier}"), $"init/seed_referentiel.sql doit couvrir les scripts 001 à {dernier} ({Scripts[^1].Nom}) : reportez-y le référentiel.");
    }

    [Fact]
    public void Chaque_table_creee_par_un_script_est_dans_schema_sql()
    {
        var manquantes = ObjetsVivants(CreationTable(), SuppressionTable())
            .Where(t => !Schema.Contains($"CREATE TABLE {Qualifie(t)} (")).ToList();
        Assert.True(manquantes.Count == 0, "Tables absentes de init/schema.sql : " + string.Join(", ", manquantes));
    }

    [Fact]
    public void Chaque_vue_et_fonction_creee_par_un_script_est_dans_schema_sql()
    {
        var vues = ObjetsVivants(CreationVue(), SuppressionVue()).Where(v => !Schema.Contains($"CREATE VIEW {Qualifie(v)} AS"));
        var fonctions = ObjetsVivants(CreationFonction(), SuppressionFonction()).Where(f => !Schema.Contains($"CREATE FUNCTION {Qualifie(f)}("));
        var manquants = vues.Select(v => "vue " + v).Concat(fonctions.Select(f => "fonction " + f)).ToList();
        Assert.True(manquants.Count == 0, "Absents de init/schema.sql : " + string.Join(", ", manquants));
    }

    [Fact]
    public void Chaque_colonne_ajoutee_par_un_script_est_dans_schema_sql()
    {
        // Colonnes ajoutées (ALTER TABLE … ADD COLUMN), moins celles supprimées par un script suivant
        var colonnes = new Dictionary<(string Table, string Colonne), string>();
        foreach (var (nom, _, sql) in Scripts)
            foreach (Match alter in AlterTable().Matches(sql))
            {
                var table = alter.Groups[1].Value.ToLowerInvariant();
                foreach (Match c in AjoutColonne().Matches(alter.Groups[2].Value)) colonnes[(table, c.Groups[1].Value.ToLowerInvariant())] = nom;
                foreach (Match c in SuppressionColonne().Matches(alter.Groups[2].Value)) colonnes.Remove((table, c.Groups[1].Value.ToLowerInvariant()));
            }

        var manquantes = colonnes.Where(c => !ColonneDansSchema(c.Key.Table, c.Key.Colonne))
            .Select(c => $"{c.Key.Table}.{c.Key.Colonne} ({c.Value})").ToList();
        Assert.True(colonnes.Count > 0, "Aucune colonne ajoutée trouvée : l'analyse des scripts ne fonctionne plus.");
        Assert.True(manquantes.Count == 0, "Colonnes absentes de init/schema.sql : " + string.Join(", ", manquantes));
    }

    [Fact]
    public void Chaque_permission_du_logiciel_est_dans_un_script_et_dans_le_seed()
    {
        var codes = typeof(Pharma.Domain.Securite.Permissions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        Assert.NotEmpty(codes);
        var horsScripts = codes.Where(c => !Scripts.Any(s => s.Sql.Contains($"'{c}'"))).ToList();
        var horsSeed = codes.Where(c => !Seed.Contains($"('{c}', '")).ToList();
        Assert.True(horsScripts.Count == 0, "Permissions créées par aucun script numéroté : " + string.Join(", ", horsScripts));
        Assert.True(horsSeed.Count == 0, "Permissions absentes de init/seed_referentiel.sql : " + string.Join(", ", horsSeed));
    }

    /// <summary>Objets créés par les scripts et pas supprimés ensuite (le dernier ordre, dans l'ordre des scripts, l'emporte).</summary>
    private static List<string> ObjetsVivants(Regex creation, Regex suppression)
    {
        var etat = new Dictionary<string, bool>();
        foreach (var (_, _, sql) in Scripts)
        {
            var ordres = creation.Matches(sql).Select(m => (m.Index, Nom: m.Groups[1].Value.ToLowerInvariant(), Vivant: true))
                .Concat(suppression.Matches(sql).Select(m => (m.Index, Nom: m.Groups[1].Value.ToLowerInvariant(), Vivant: false)))
                .OrderBy(o => o.Index);
            foreach (var o in ordres) etat[o.Nom] = o.Vivant;
        }
        return etat.Where(e => e.Value).Select(e => e.Key).Order().ToList();
    }

    private static string Qualifie(string nom) => nom.Contains('.') ? nom : "public." + nom;

    private static bool ColonneDansSchema(string table, string colonne)
    {
        var debut = Schema.IndexOf($"CREATE TABLE {Qualifie(table)} (", StringComparison.Ordinal);
        if (debut < 0) return false;
        var fin = Schema.IndexOf("\n);", debut, StringComparison.Ordinal);
        var bloc = Schema[debut..(fin < 0 ? Schema.Length : fin)];
        return Regex.IsMatch(bloc, $@"\n\s+""?{Regex.Escape(colonne)}""?\s");
    }

    private static string SansCommentaires(string sql) => CommentaireLigne().Replace(CommentaireBloc().Replace(sql, " "), "");

    private static string RacineDepot()
    {
        // Compilation hors du dépôt (dossier de sortie séparé) : le chemin du dépôt peut être donné par PHARMA_RACINE_DEPOT
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } racine) return racine;
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    [GeneratedRegex(@"^(\d{3})_.+\.sql$")] private static partial Regex NumeroScript();
    [GeneratedRegex(@"--[^\n]*")] private static partial Regex CommentaireLigne();
    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)] private static partial Regex CommentaireBloc();
    [GeneratedRegex(@"(?<!TEMP )(?<!TEMPORARY )\bCREATE TABLE (?:IF NOT EXISTS )?([a-z_][a-z0-9_.]*)", RegexOptions.IgnoreCase)] private static partial Regex CreationTable();
    [GeneratedRegex(@"\bDROP TABLE (?:IF EXISTS )?([a-z_][a-z0-9_.]*)", RegexOptions.IgnoreCase)] private static partial Regex SuppressionTable();
    [GeneratedRegex(@"\bCREATE (?:OR REPLACE )?VIEW ([a-z_][a-z0-9_.]*)", RegexOptions.IgnoreCase)] private static partial Regex CreationVue();
    [GeneratedRegex(@"\bDROP VIEW (?:IF EXISTS )?([a-z_][a-z0-9_.]*)", RegexOptions.IgnoreCase)] private static partial Regex SuppressionVue();
    [GeneratedRegex(@"\bCREATE (?:OR REPLACE )?FUNCTION ([a-z_][a-z0-9_.]*)\s*\(", RegexOptions.IgnoreCase)] private static partial Regex CreationFonction();
    [GeneratedRegex(@"\bDROP FUNCTION (?:IF EXISTS )?([a-z_][a-z0-9_.]*)\s*\(", RegexOptions.IgnoreCase)] private static partial Regex SuppressionFonction();
    [GeneratedRegex(@"\bALTER TABLE (?:ONLY )?([a-z_][a-z0-9_.]*)\s+(.*?);", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex AlterTable();
    [GeneratedRegex(@"\bADD COLUMN (?:IF NOT EXISTS )?([a-z_][a-z0-9_]*)", RegexOptions.IgnoreCase)] private static partial Regex AjoutColonne();
    [GeneratedRegex(@"\bDROP COLUMN (?:IF EXISTS )?([a-z_][a-z0-9_]*)", RegexOptions.IgnoreCase)] private static partial Regex SuppressionColonne();
}
