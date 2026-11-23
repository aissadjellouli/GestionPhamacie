using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Règle permanente (docs/scripts-base-de-donnees.md) : toute la suite database/migration/001 à NNN se rejoue, dans l'ordre, sur une
/// base DÉJÀ à jour (restauration d'une sauvegarde récente, mise à jour d'une officine) sans erreur et sans changer la structure.
/// Cause d'échec historique : un ancien script recréait une vue (CREATE OR REPLACE VIEW) dont un script plus récent avait changé les
/// colonnes (« ne peut pas modifier le nom de la colonne d'une vue »), ou redéfinissait une fonction déjà remplacée.
/// - Les tests statiques vérifient le contenu des scripts ;
/// - le test de rejeu crée une base jetable (schema.sql + seed_referentiel.sql = base à jour), rejoue la suite deux fois comme
///   Sauvegardes/RestaurationPostgres (001 à 032 en transaction, 033 seul, 034 et suivants en transaction) puis compare la structure
///   (pg_dump --schema-only) avant/après. Sans serveur PostgreSQL joignable, sans psql/pg_dump ou sans droit CREATEDB, il est IGNORÉ avec la raison affichée (FactPostgres), il ne réussit plus à vide.
/// </summary>
public partial class ScriptsRejouablesTests
{
    private static readonly string Migration = Path.Combine(RacineDepot(), "database", "migration");

    /// <summary>Scripts numérotés « NNN_*.sql » (001 à 999), dans l'ordre ; les scripts d'administrateur (101b, 111b) sont exclus.</summary>
    private static List<(string Nom, int Numero, string Sql)> Scripts() => Directory.GetFiles(Migration, "*.sql")
        .Select(f => (Nom: Path.GetFileName(f), Fichier: f))
        .Where(f => NumeroScript().IsMatch(f.Nom))
        .Select(f => (f.Nom, int.Parse(f.Nom[..3]), SansCommentaires(File.ReadAllText(f.Fichier))))
        .OrderBy(s => s.Nom, StringComparer.Ordinal)
        .ToList();

    [Fact]
    public void Aucun_script_ne_supprime_une_vue_en_cascade()
    {
        // DROP VIEW … CASCADE détruirait aussi les vues et objets qui en dépendent : jamais dans un script rejouable
        var fautifs = Scripts().Where(s => SuppressionVueCascade().IsMatch(s.Sql)).Select(s => s.Nom).ToList();
        Assert.True(fautifs.Count == 0, "DROP VIEW … CASCADE interdit : " + string.Join(", ", fautifs));
    }

    [Fact]
    public void Aucune_vue_n_utilise_table_etoile()
    {
        // « t.* » dans une vue est figé à la création avec les colonnes d'alors : rejoué plus tard, il changerait la définition
        var scripts = Scripts();
        var fautifs = scripts
            .SelectMany(s => DefinitionVue().Matches(s.Sql).Select(m => (s.Nom, Vue: m.Groups[1].Value, Texte: m.Value)))
            .Where(v => ColonnesEtoile().Matches(v.Texte).Any(m => !ExpressionsNommees(v.Texte).Contains(m.Groups[1].Value.ToLowerInvariant())))
            .Select(v => $"{v.Nom} ({v.Vue})").ToList();
        Assert.True(scripts.Sum(s => DefinitionVue().Matches(s.Sql).Count) > 0, "Aucune vue trouvée : l'analyse des scripts ne fonctionne plus.");
        Assert.True(fautifs.Count == 0, "Vue définie avec « alias.* » (nommer les colonnes une à une) : " + string.Join(", ", fautifs));
    }

    [FactPostgres]
    public void Toute_la_suite_se_rejoue_deux_fois_sur_une_base_a_jour_sans_changer_la_structure()
    {
        var psql = Outil("psql.exe") ?? throw new InvalidOperationException("psql introuvable (le test devait être ignoré).");
        var pgDump = Outil("pg_dump.exe") ?? throw new InvalidOperationException("pg_dump introuvable (le test devait être ignoré).");
        var cnx = new NpgsqlConnectionStringBuilder(ChaineConnexion());
        var nom = "pharmacie_jetable_rejeu_" + Guid.NewGuid().ToString("N")[..8];
        var administration = new NpgsqlConnectionStringBuilder(cnx.ConnectionString) { Database = "postgres", Pooling = false }.ConnectionString;
        using (var cx = new NpgsqlConnection(administration))
        {
            cx.Open();   // serveur injoignable ou sans droit CREATEDB : FactPostgres ignore le test avant d'arriver ici ; un échec ici est donc réel
            using var creer = new NpgsqlCommand($"CREATE DATABASE \"{nom}\" ENCODING 'UTF8' TEMPLATE template0", cx);
            creer.ExecuteNonQuery();
        }

        var temporaires = new List<string>();
        try
        {
            var init = Path.Combine(RacineDepot(), "database", "init");
            // Base à jour : schéma complet + référentiel de départ (comme une installation neuve)
            ExecuterPsql(psql, cnx, nom, ["-f", Chemin(Path.Combine(init, "schema.sql"))]);
            ExecuterPsql(psql, cnx, nom, ["-f", Chemin(Path.Combine(init, "seed_referentiel.sql"))]);
            var avant = Structure(pgDump, cnx, nom);

            for (var passage = 1; passage <= 2; passage++)
                foreach (var (fichiers, transaction) in Lots())
                {
                    var lignes = fichiers.Select(f => $"\\i '{Chemin(f).Replace("'", "''")}'").ToList();
                    if (transaction) { lignes.Insert(0, "BEGIN;"); lignes.Add("COMMIT;"); }
                    var temporaire = Path.Combine(Path.GetTempPath(), $"pharma_rejeu_{Guid.NewGuid():N}.sql");
                    temporaires.Add(temporaire);
                    File.WriteAllLines(temporaire, lignes);
                    ExecuterPsql(psql, cnx, nom, ["-f", temporaire], $"passage {passage}, ");
                }

            var apres = Structure(pgDump, cnx, nom);
            Assert.True(avant == apres, "La structure a changé après le rejeu de toute la suite : " + Ecart(avant, apres));
        }
        finally
        {
            foreach (var t in temporaires) { try { File.Delete(t); } catch (IOException) { } }
            try
            {
                using var cx = new NpgsqlConnection(administration);
                cx.Open();
                using var supprimer = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{nom}\" WITH (FORCE)", cx);
                supprimer.ExecuteNonQuery();
            }
            catch (NpgsqlException) { /* base jetable restée : supprimable à la main (préfixe pharmacie_jetable_rejeu_) */ }
        }
    }

    /// <summary>Mêmes lots que Sauvegardes/RestaurationPostgres : 001 à 032 en transaction, 033 seul (index CONCURRENTLY), 034 et suivants en transaction.</summary>
    internal static List<(List<string> Fichiers, bool Transaction)> Lots()
    {
        var scripts = Scripts().Select(s => (s.Numero, Fichier: Path.Combine(Migration, s.Nom))).ToList();
        return new List<(List<string>, bool)>
        {
            (scripts.Where(s => s.Numero <= 32).Select(s => s.Fichier).ToList(), true),
            (scripts.Where(s => s.Numero == 33).Select(s => s.Fichier).ToList(), false),
            (scripts.Where(s => s.Numero >= 34).Select(s => s.Fichier).ToList(), true)
        }.Where(l => l.Item1.Count > 0).ToList();
    }

    /// <summary>Expressions de table (WITH nom AS (…)) de la vue : leurs colonnes sont nommées dans la vue elle-même, « nom.* » reste stable.</summary>
    private static HashSet<string> ExpressionsNommees(string vue) =>
        ExpressionTable().Matches(vue).Select(m => m.Groups[1].Value.ToLowerInvariant()).ToHashSet();

    internal static string Chemin(string fichier) => fichier.Replace('\\', '/');

    private static void ExecuterPsql(string psql, NpgsqlConnectionStringBuilder cnx, string nomBase, string[] arguments, string contexte = "")
    {
        var (code, _, erreurs) = Lancer(psql, cnx, [.. HoteArguments(cnx), $"--dbname={nomBase}", "-v", "ON_ERROR_STOP=1", "-q", .. arguments], scripts: true);
        Assert.True(code == 0, $"Rejeu impossible ({contexte}{string.Join(' ', arguments)}) : {Tronquer(erreurs)}");
    }

    /// <summary>Structure de la base : pg_dump --schema-only, sans les lignes \restrict (jeton aléatoire différent à chaque appel).</summary>
    private static string Structure(string pgDump, NpgsqlConnectionStringBuilder cnx, string nomBase)
    {
        var (code, sortie, erreurs) = Lancer(pgDump, cnx, [.. HoteArguments(cnx), "--schema-only", "--no-owner", "--no-privileges", nomBase], scripts: false);
        Assert.True(code == 0, "pg_dump a échoué : " + Tronquer(erreurs));
        // Fins de ligne ignorées : un script en CRLF (extraction Git sous Windows) et schema.sql ne donnent pas les mêmes retours chariot dans un corps de fonction
        return string.Join('\n', sortie.Replace("\r", "").Split('\n').Where(l => !JetonRestrict().IsMatch(l)));
    }

    private static string Ecart(string avant, string apres)
    {
        var a = avant.Split('\n');
        var b = apres.Split('\n');
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        string Ligne(string[] t) => i < t.Length ? t[i].Replace("\r", "<CR>").Replace("\t", "<TAB>") : "(fin)";
        return $"première différence ligne {i + 1} : « {Ligne(a)} » devenu « {Ligne(b)} »";
    }

    private static string Tronquer(string texte) => texte.Length > 800 ? texte[..800] + "…" : texte;

    private static string[] HoteArguments(NpgsqlConnectionStringBuilder cnx) =>
        ["--no-password", $"--host={cnx.Host}", $"--port={cnx.Port}", $"--username={cnx.Username}"];

    private static (int Code, string Sortie, string Erreurs) Lancer(string outil, NpgsqlConnectionStringBuilder cnx, string[] arguments, bool scripts)
    {
        var info = new ProcessStartInfo(outil)
        {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        info.Environment["PGPASSWORD"] = cnx.Password ?? "";
        info.Environment["PGCLIENTENCODING"] = "UTF8";
        if (scripts) info.Environment["PGOPTIONS"] = "-c client_min_messages=warning";
        using var p = Process.Start(info) ?? throw new InvalidOperationException($"{outil} n'a pas pu démarrer.");
        var sortie = p.StandardOutput.ReadToEndAsync();
        var erreurs = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, sortie.Result, erreurs.Result.Trim());
    }

    /// <summary>Outil PostgreSQL de la dernière version installée (PHARMA_PG_BIN pour un autre dossier) ; null s'il n'y en a pas.</summary>
    internal static string? Outil(string nom)
    {
        if (Environment.GetEnvironmentVariable("PHARMA_PG_BIN") is { Length: > 0 } bin && File.Exists(Path.Combine(bin, nom))) return Path.Combine(bin, nom);
        var racine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL");
        return Directory.Exists(racine)
            ? Directory.GetDirectories(racine).Select(d => Path.Combine(d, "bin", nom)).Where(File.Exists)
                .OrderByDescending(f => int.TryParse(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f))), out var v) ? v : 0)
                .FirstOrDefault()
            : null;
    }

    internal static string ChaineConnexion()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_TEST_CNX") is { Length: > 0 } cnx) return cnx;
        var dossierApi = Path.Combine(RacineDepot(), "src", "Pharma.Api");
        var appsettings = Path.Combine(dossierApi, "appsettings.Development.json");
        if (!File.Exists(appsettings)) appsettings = Path.Combine(dossierApi, "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(appsettings), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        return doc.RootElement.GetProperty("ConnectionStrings").GetProperty("Pharmacie").GetString()!;
    }

    internal static string RacineDepot()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } racine) return racine;
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    private static string SansCommentaires(string sql) => CommentaireLigne().Replace(CommentaireBloc().Replace(sql, " "), "");

    [GeneratedRegex(@"^(\d{3})_.+\.sql$")]
    private static partial Regex NumeroScript();

    [GeneratedRegex(@"DROP\s+VIEW\b[^;]*\bCASCADE\b", RegexOptions.IgnoreCase)]
    private static partial Regex SuppressionVueCascade();

    [GeneratedRegex(@"CREATE\s+(?:OR\s+REPLACE\s+)?VIEW\s+([\w.]+)[^;]*;", RegexOptions.IgnoreCase)]
    private static partial Regex DefinitionVue();

    [GeneratedRegex(@"\b([a-z_]\w*)\.\*", RegexOptions.IgnoreCase)]
    private static partial Regex ColonnesEtoile();

    [GeneratedRegex(@"(?:WITH|,)\s*(\w+)\s+AS\s*(?:NOT\s+MATERIALIZED\s*)?\(", RegexOptions.IgnoreCase)]
    private static partial Regex ExpressionTable();

    [GeneratedRegex(@"^\\(un)?restrict ")]
    private static partial Regex JetonRestrict();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentaireBloc();

    [GeneratedRegex(@"--[^\n]*")]
    private static partial Regex CommentaireLigne();
}
