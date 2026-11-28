using Npgsql;
using PharmaOfficine.Livraison;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Lot P1 de la protection du code (décision du titulaire du 2026-11-27 : « nettoyer à la livraison, pas dans git ») : les scripts SQL
/// livrés perdent leurs commentaires (scripts\mep\NettoyageSql.cs, appelé par nettoyer_livraison.ps1) sans que la base produite change.
/// </summary>
public class NettoyageLivraisonTests
{
    [Theory]
    [InlineData("SELECT 1; -- fin\n", "SELECT 1;\n")]
    [InlineData("-- tête\n-- encore\nSELECT 1;\n", "SELECT 1;\n")]
    [InlineData("SELECT /* a /* imbriqué */ b */ 1;", "SELECT 1;")]
    [InlineData("SELECT 1/*x*/+2;", "SELECT 1 +2;")]
    [InlineData("/* bloc\n sur deux lignes */\nSELECT 1;\n", "SELECT 1;\n")]
    [InlineData("SELECT '-- pas un commentaire', '/* non plus */';", "SELECT '-- pas un commentaire', '/* non plus */';")]
    [InlineData("SELECT 'l''apostrophe -- reste';", "SELECT 'l''apostrophe -- reste';")]
    [InlineData("SELECT E'a\\' -- dans le texte';", "SELECT E'a\\' -- dans le texte';")]
    [InlineData("SELECT \"col -- nom\" FROM t;", "SELECT \"col -- nom\" FROM t;")]
    [InlineData("\\set ON_ERROR_STOP on -- psql\nSELECT 1;", "\\set ON_ERROR_STOP on -- psql\nSELECT 1;")]
    [InlineData("-- Init : scripts 001 à 174\nSELECT 1;", "-- Init : scripts 001 à 174\nSELECT 1;")]
    [InlineData("SELECT $1 + a$b; -- x", "SELECT $1 + a$b;")]
    // Texte entre dollars : une donnée est gardée telle quelle, un corps de code (AS, DO, EXECUTE) est nettoyé
    [InlineData("INSERT INTO t VALUES ($$a -- reste$$); -- x", "INSERT INTO t VALUES ($$a -- reste$$);")]
    [InlineData("COMMENT ON TABLE t IS $c$note /* reste */ -- aussi$c$;", "COMMENT ON TABLE t IS $c$note /* reste */ -- aussi$c$;")]
    [InlineData("DO $$ BEGIN -- parti\nNULL; END $$;", "DO $$ BEGIN\nNULL; END $$;")]
    [InlineData("DO LANGUAGE plpgsql $$ BEGIN -- parti\nNULL; END $$;", "DO LANGUAGE plpgsql $$ BEGIN\nNULL; END $$;")]
    [InlineData("DO $$ BEGIN EXECUTE $q$SELECT 1 -- parti\n$q$; END $$;", "DO $$ BEGIN EXECUTE $q$SELECT 1\n$q$; END $$;")]
    // En-tête « 001 à NNN » : gardé, réduit à cette mention
    [InlineData("-- Valeurs des scripts 001 à 175 (175 : notes de conception)\nSELECT 1;", "-- Valeurs des scripts 001 à 175\nSELECT 1;")]
    [InlineData("SELECT 1;\r\n-- commentaire\r\nSELECT 2; -- fin\r\n", "SELECT 1;\r\nSELECT 2;\r\n")]
    public void Commentaires_retires_textes_et_commandes_intacts(string avant, string apres) =>
        Assert.Equal(apres, NettoyageSql.Nettoyer(avant));

    [Fact]
    public void Corps_de_fonction_commentaires_retires_textes_du_corps_gardes()
    {
        const string avant = """
            CREATE FUNCTION f() RETURNS text LANGUAGE plpgsql AS $corps$
            BEGIN
                -- règle métier expliquée
                RETURN 'a -- b' || $q$ x $q$; /* fin */
            END $corps$;
            """;
        const string attendu = """
            CREATE FUNCTION f() RETURNS text LANGUAGE plpgsql AS $corps$
            BEGIN
                RETURN 'a -- b' || $q$ x $q$;
            END $corps$;
            """;
        Assert.Equal(attendu, NettoyageSql.Nettoyer(avant));
    }

    [Theory]
    [InlineData("SELECT 'non fermé")]
    [InlineData("SELECT /* non fermé")]
    [InlineData("DO $$ BEGIN")]
    public void Script_mal_forme_refuse(string sql) => Assert.Throws<FormatException>(() => NettoyageSql.Nettoyer(sql));

    /// <summary>Scripts livrés : ceux que Pharma.Api copie à la publication (Pharma.Api.csproj) et les données de démonstration du paquet d'essai.</summary>
    internal static IEnumerable<string> ScriptsLivres()
    {
        var racine = BaseJetableM4.RacineDepot();
        var base_ = Path.Combine(racine, "database");
        return Directory.GetFiles(Path.Combine(base_, "migration"), "*.sql").Where(f => !Path.GetFileName(f).Contains("b_", StringComparison.Ordinal))
            .Concat(Directory.GetFiles(Path.Combine(base_, "init"), "*.sql").Where(f => Path.GetFileName(f) != "clean.sql"))
            .Concat(Directory.GetFiles(Path.Combine(base_, "data"), "9*.sql"))
            .Order(StringComparer.Ordinal);
    }

    [Fact]
    public void Tous_les_scripts_livres_se_nettoient_de_facon_stable_en_gardant_l_entete_de_l_init()
    {
        var ecarts = new List<string>();
        long avant = 0, apres = 0;
        foreach (var f in ScriptsLivres())
        {
            var texte = File.ReadAllText(f);
            Assert.DoesNotContain("FROM stdin", texte, StringComparison.OrdinalIgnoreCase);   // COPY … FROM stdin : non pris en charge par le nettoyeur
            var propre = NettoyageSql.Nettoyer(texte);
            avant += texte.Length; apres += propre.Length;
            if (NettoyageSql.Nettoyer(propre) != propre) ecarts.Add($"{Path.GetFileName(f)} : second nettoyage différent");
            if (Path.GetFileName(f) is "schema.sql" or "seed_referentiel.sql" && !System.Text.RegularExpressions.Regex.IsMatch(propre, @"001 à \d{3}"))
                ecarts.Add($"{Path.GetFileName(f)} : en-tête « 001 à NNN » perdu");
        }
        Assert.True(ecarts.Count == 0, string.Join("\n", ecarts));
        Assert.True(apres < avant, "Aucun commentaire retiré : le nettoyage ne fait rien.");
    }

    /// <summary>
    /// Preuve dans PostgreSQL : une base créée puis mise à jour avec les scripts NETTOYÉS (init, référentiel, fuseau, puis rejeu de toute la
    /// suite comme mettre_a_jour_base_officine.ps1) a exactement la même structure (rubriques de InitEquivalentSuiteMigrationsTests ; corps
    /// des fonctions comparés sans leurs commentaires) et les mêmes données que la base faite avec les scripts du dépôt.
    /// </summary>
    [FactPostgres]
    public void La_base_produite_par_les_scripts_nettoyes_est_identique()
    {
        var racine = BaseJetableM4.RacineDepot();
        var init = Path.Combine(racine, "database", "init");
        var propres = Path.Combine(Path.GetTempPath(), $"pharma_livraison_{Guid.NewGuid():N}");
        Directory.CreateDirectory(propres);
        using var origine = new BaseJetableM4("pharmacie_jetable_livr_orig_", initialiser: false);
        using var nettoyee = new BaseJetableM4("pharmacie_jetable_livr_net_", initialiser: false);
        try
        {
            Assert.True(origine.Disponible && nettoyee.Disponible, "Bases jetables non créées (PostgreSQL injoignable ou sans droit CREATEDB).");
            string Propre(string fichier)
            {
                var cible = Path.Combine(propres, Path.GetFileName(fichier));
                if (!File.Exists(cible)) File.WriteAllText(cible, NettoyageSql.Nettoyer(File.ReadAllText(fichier)));
                return cible;
            }
            foreach (var f in new[] { "schema.sql", "seed_referentiel.sql", "fuseau_horaire.sql" }.Select(n => Path.Combine(init, n)))
            {
                origine.Executer(["-f", BaseJetableM4.Chemin(f)]);
                nettoyee.Executer(["-f", BaseJetableM4.Chemin(Propre(f))]);
            }
            // Mise à jour d'une officine : rejeu de toute la suite, par lots comme le rejeu de référence
            var numero = 0;
            foreach (var (fichiers, transaction) in ScriptsRejouablesTests.Lots())
            {
                foreach (var (cible, chemin) in new (BaseJetableM4, Func<string, string>)[] { (origine, f => f), (nettoyee, Propre) })
                {
                    var lignes = fichiers.Select(f => $"\\i '{BaseJetableM4.Chemin(chemin(f)).Replace("'", "''")}'").ToList();
                    if (transaction) { lignes.Insert(0, "BEGIN;"); lignes.Add("COMMIT;"); }
                    var lot = Path.Combine(propres, $"lot_{numero++}.sql.txt");
                    File.WriteAllLines(lot, lignes);
                    cible.Executer(["-f", BaseJetableM4.Chemin(lot)]);
                }
            }

            var ecarts = new List<string>();
            foreach (var (rubrique, requete) in InitEquivalentSuiteMigrationsTests.Rubriques)
            {
                var a = InitEquivalentSuiteMigrationsTests.Lire(origine, requete);
                var b = InitEquivalentSuiteMigrationsTests.Lire(nettoyee, requete);
                if (rubrique == "fonction") a = a.Select(NettoyageSql.Nettoyer).ToHashSet(StringComparer.Ordinal);
                ecarts.AddRange(a.Except(b).Take(5).Select(l => $"[{rubrique}] seulement avec les scripts du dépôt : {InitEquivalentSuiteMigrationsTests.Court(l)}"));
                ecarts.AddRange(b.Except(a).Take(5).Select(l => $"[{rubrique}] seulement avec les scripts nettoyés : {InitEquivalentSuiteMigrationsTests.Court(l)}"));
            }
            foreach (var table in InitEquivalentSuiteMigrationsTests.Lire(origine, "SELECT tablename FROM pg_tables WHERE schemaname = 'public'").Order(StringComparer.Ordinal))
            {
                // Données comparées sans les colonnes tirées au sort ou datées à l'exécution (uuid, octets, dates et heures, empreintes salées des mots de passe)
                var requete = $"""
                    SELECT COALESCE(md5(string_agg(x, '|' ORDER BY x)), 'vide') FROM (
                        SELECT (to_jsonb(t) - ARRAY(SELECT column_name::text FROM information_schema.columns
                                                   WHERE table_schema = 'public' AND table_name = '{table}'
                                                     AND (data_type IN ('uuid', 'bytea', 'date') OR data_type LIKE 'time%' OR column_name LIKE '%hash%')))::text AS x
                        FROM public."{table}" t) s
                    """;
                if (!InitEquivalentSuiteMigrationsTests.Lire(origine, requete).SetEquals(InitEquivalentSuiteMigrationsTests.Lire(nettoyee, requete)))
                    ecarts.Add($"[données] table {table} différente");
            }
            Assert.True(ecarts.Count == 0, $"Les scripts nettoyés ne produisent pas la même base ({ecarts.Count} écart(s)) :\n" + string.Join("\n", ecarts.Take(10)));
        }
        finally
        {
            try { Directory.Delete(propres, recursive: true); } catch (IOException) { }
        }
    }
}
