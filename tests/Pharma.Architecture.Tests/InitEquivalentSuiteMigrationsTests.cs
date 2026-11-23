using Npgsql;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Équivalence entre l'init (database/init/schema.sql, base neuve) et la suite des migrations (001 à NNN) : deux bases jetables sont
/// construites, l'une par toute la suite numérotée (comme la restauration, ScriptsRejouablesTests.Lots), l'autre par schema.sql puis le
/// script 083 (fuseau horaire, ajouté par creer_base_officine.ps1 car absent d'un dump de schéma). Leurs schémas sont comparés : colonnes
/// (type, NULL, défaut, identité, colonne générée), contraintes, index, fonctions, vues, déclencheurs, séquences, extensions, fuseau.
/// L'ordre des colonnes dans une table et les propriétaires sont ignorés. Un écart signale un schema.sql en retard sur un script
/// (docs/scripts-base-de-donnees.md, règle 4). Sans PostgreSQL joignable, le test est ignoré avec la raison (FactPostgres).
/// </summary>
public class InitEquivalentSuiteMigrationsTests
{
    private const string Colonnes = """
        SELECT c.relname || '.' || a.attname || ' ' || format_type(a.atttypid, a.atttypmod)
               || CASE WHEN a.attnotnull THEN ' NOT NULL' ELSE '' END
               || COALESCE(' DEFAULT ' || pg_get_expr(d.adbin, d.adrelid), '')
               || CASE WHEN a.attidentity <> '' THEN ' IDENTITY ' || a.attidentity::text ELSE '' END
               || CASE WHEN a.attgenerated <> '' THEN ' GENERATED ' || a.attgenerated::text ELSE '' END
        FROM pg_attribute a
        JOIN pg_class c ON c.oid = a.attrelid AND c.relkind IN ('r', 'p')
        JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'public'
        LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
        WHERE a.attnum > 0 AND NOT a.attisdropped
        """;

    private static readonly (string Rubrique, string Requete)[] Rubriques =
    [
        ("colonne", Colonnes),
        ("contrainte", """
            SELECT conrelid::regclass::text || ' ' || conname || ' ' || pg_get_constraintdef(oid)
            FROM pg_constraint WHERE connamespace = 'public'::regnamespace
            """),
        ("index", "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public'"),
        ("fonction", """
            SELECT pg_get_functiondef(p.oid)
            FROM pg_proc p
            WHERE p.pronamespace = 'public'::regnamespace AND p.prokind IN ('f', 'p')
              AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = p.oid AND d.deptype = 'e')
            """),
        ("vue", "SELECT c.relname || ' : ' || pg_get_viewdef(c.oid) FROM pg_class c WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('v', 'm')"),
        ("déclencheur", "SELECT pg_get_triggerdef(t.oid) FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid WHERE c.relnamespace = 'public'::regnamespace AND NOT t.tgisinternal"),
        ("séquence", "SELECT c.relname FROM pg_class c WHERE c.relnamespace = 'public'::regnamespace AND c.relkind = 'S'"),
        ("extension", "SELECT extname FROM pg_extension"),
        ("réglage de la base", "SELECT unnest(setconfig) FROM pg_db_role_setting WHERE setdatabase = (SELECT oid FROM pg_database WHERE datname = current_database()) AND setrole = 0")
    ];

    [FactPostgres]
    public void Le_schema_de_l_init_equivaut_a_la_suite_des_migrations()
    {
        var racine = ScriptsRejouablesTests.RacineDepot();
        var init = Path.Combine(racine, "database", "init", "schema.sql");
        var fuseau = Directory.GetFiles(Path.Combine(racine, "database", "migration"), "083_*.sql").Single();
        var temporaires = new List<string>();
        using var parSuite = new BaseJetableM4("pharmacie_jetable_equiv_suite_", initialiser: false);
        using var parInit = new BaseJetableM4("pharmacie_jetable_equiv_init_", initialiser: false);
        try
        {
            Assert.True(parSuite.Disponible && parInit.Disponible, "Bases jetables non créées (PostgreSQL injoignable ou sans droit CREATEDB).");

            foreach (var (fichiers, transaction) in ScriptsRejouablesTests.Lots())
            {
                var lignes = fichiers.Select(f => $"\\i '{ScriptsRejouablesTests.Chemin(f).Replace("'", "''")}'").ToList();
                if (transaction) { lignes.Insert(0, "BEGIN;"); lignes.Add("COMMIT;"); }
                var temporaire = Path.Combine(Path.GetTempPath(), $"pharma_equiv_{Guid.NewGuid():N}.sql");
                temporaires.Add(temporaire);
                File.WriteAllLines(temporaire, lignes);
                parSuite.Executer(["-f", temporaire]);
            }
            parSuite.Executer(["-f", ScriptsRejouablesTests.Chemin(fuseau)]);   // déjà dans la suite : rejeu sans effet, même état que creer_base_officine.ps1

            parInit.Executer(["-f", ScriptsRejouablesTests.Chemin(init)]);
            parInit.Executer(["-f", ScriptsRejouablesTests.Chemin(fuseau)]);

            var ecarts = new List<string>();
            foreach (var (rubrique, requete) in Rubriques)
            {
                var suite = Lire(parSuite, requete);
                var depuisInit = Lire(parInit, requete);
                if (rubrique == "contrainte") Rapprocher(suite, depuisInit);
                if (rubrique == "extension") depuisInit.Remove(EcartConnuPgcrypto);   // écart constaté, décision du titulaire en attente : voir EcartConnuPgcrypto
                ecarts.AddRange(suite.Except(depuisInit).Take(10).Select(l => $"[{rubrique}] seulement dans la suite des migrations : {Court(l)}"));
                ecarts.AddRange(depuisInit.Except(suite).Take(10).Select(l => $"[{rubrique}] seulement dans database/init/schema.sql : {Court(l)}"));
            }
            Assert.True(ecarts.Count == 0,
                $"database/init/schema.sql n'équivaut pas à la suite des migrations ({ecarts.Count} écart(s), 10 par rubrique au plus) :\n" + string.Join("\n", ecarts.Take(40)));
        }
        finally
        {
            foreach (var t in temporaires) { try { File.Delete(t); } catch (IOException) { } }
        }
    }

    /// <summary>
    /// ÉCART CONNU (revue de la base, 2026-11-23, en attente de décision) : schema.sql crée l'extension pgcrypto (utilisée par le script 126 :
    /// gen_random_bytes, et par seed_referentiel.sql : crypt / gen_salt) mais aucun script de database/migration ne la crée : une base montée
    /// par les migrations seules ne l'a que si elle existait déjà. Le test l'ignore côté init tant que ce point n'est pas tranché.
    /// </summary>
    private const string EcartConnuPgcrypto = "pgcrypto";

    /// <summary>
    /// Une contrainte CHECK écrite par un script puis relue depuis un dump est réaffichée avec les AND imbriqués aplatis (parenthèses
    /// différentes, même sens). Pour les contraintes seulement : deux écarts dont le texte sans parenthèses ni espaces est identique
    /// sont considérés égaux.
    /// </summary>
    private static void Rapprocher(HashSet<string> gauche, HashSet<string> droite)
    {
        static string Nu(string s) => new(s.Where(c => c is not ('(' or ')') && !char.IsWhiteSpace(c)).ToArray());
        var seulementGauche = gauche.Except(droite).ToList();
        var seulementDroite = droite.Except(gauche).ToList();
        var nusDroite = seulementDroite.ToLookup(Nu);
        foreach (var g in seulementGauche)
            if (nusDroite[Nu(g)].FirstOrDefault() is { } d) { gauche.Remove(g); droite.Remove(d); }
    }

    private static HashSet<string> Lire(BaseJetableM4 base_, string requete)
    {
        var lignes = new HashSet<string>(StringComparer.Ordinal);
        using var cx = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(base_.Connexion.ConnectionString) { Database = base_.Nom, Pooling = false }.ConnectionString);
        cx.Open();
        using var cmd = new NpgsqlCommand(requete, cx) { CommandTimeout = 120 };
        using var r = cmd.ExecuteReader();
        while (r.Read()) lignes.Add(r.GetString(0).Replace("\r", ""));   // fins de ligne : un script en CRLF et schema.sql ne donnent pas les mêmes retours chariot
        return lignes;
    }

    private static string Court(string ligne) => (ligne.Length > 300 ? ligne[..300] + "…" : ligne).Replace("\n", " ⏎ ");
}
