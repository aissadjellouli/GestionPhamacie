using System.Text;
using System.Text.RegularExpressions;

namespace Pharma.Cloud.Api.Schema;

/// <summary>Classement d'une table locale (sync_table, scripts 126 et suivants).</summary>
public sealed record ClassementTable(string Nom, string Sens, IReadOnlyList<string> Exclues, IReadOnlyList<string> Patient, bool Porteur);

/// <summary>Colonne d'une table : nom et type PostgreSQL (tel que pg_dump l'écrit).</summary>
public sealed record ColonneMiroir(string Nom, string Type);

/// <summary>Table miroir de la base centrale : mêmes colonnes que la table locale envoyée (moins les colonnes exclues,
/// patients sous code en texte, porteur sous code), clé = (officine_id, clé locale).</summary>
public sealed record TableMiroir(string Nom, IReadOnlyList<string> Cle, IReadOnlyList<ColonneMiroir> Colonnes);

/// <summary>
/// Génère le script database/cloud/002_tables_miroir.sql à partir du schéma local (database/init/schema.sql) et du classement
/// des tables (sync_table : scripts database/migration 126, 127…). Schémas local et central restent ainsi alignés : le test
/// SchemaMiroirTests échoue si le fichier n'est pas à jour. Commande : Pharma.Cloud.Api.exe --generer-miroir &lt;racine du dépôt&gt;.
/// </summary>
public static partial class GenerateurMiroir
{
    public const string ColonneOfficine = "officine_id";
    /// <summary>Tables « jamais » envoyées en option (sync_sens_effectif, script 127).</summary>
    public static readonly IReadOnlySet<string> TablesOptionnelles = new HashSet<string>(StringComparer.Ordinal) { "medecin" };
    private static readonly string[] ColonnesPorteur = ["porteur_nom", "porteur_adresse", "porteur_piece"];
    private static readonly string[] ColonnesSync = ["sync_epoque", "sync_numero", "sync_recu_le", "sync_chargement", "sync_absente_le"];

    public static string CheminScript(string racine) => Path.Combine(racine, "database", "cloud", "002_tables_miroir.sql");

    public static string Generer(string racine)
    {
        var schema = File.ReadAllText(Path.Combine(racine, "database", "init", "schema.sql")).Replace("\r\n", "\n");
        var scripts = Directory.GetFiles(Path.Combine(racine, "database", "migration"), "*.sql").Order(StringComparer.Ordinal)
            .Select(f => File.ReadAllText(f).Replace("\r\n", "\n")).ToList();
        return Script(Tables(schema, Classement(scripts)));
    }

    /// <summary>
    /// Classement final des tables : inserts du script 126, puis mises à jour des colonnes exclues des scripts suivants. Un insert
    /// suivi de « ON CONFLICT … DO UPDATE SET sens » (reclassement, script 144 : compteur_ticket) change le sens d'une table déjà
    /// classée, sans toucher à ses colonnes ; les autres inserts (« DO NOTHING ») ne changent pas un classement existant.
    /// </summary>
    public static Dictionary<string, ClassementTable> Classement(IEnumerable<string> scripts)
    {
        var res = new Dictionary<string, ClassementTable>(StringComparer.Ordinal);
        foreach (var sql in scripts)
        {
            foreach (Match ins in InsertionSyncTable().Matches(sql))
            {
                var reclasse = ins.Groups[2].Value.Contains("DO UPDATE SET sens", StringComparison.Ordinal);
                foreach (Match m in InsertClassement().Matches(ins.Groups[1].Value))
                {
                    var c = new ClassementTable(m.Groups[1].Value, m.Groups[2].Value, Liste(m.Groups[3].Value), Liste(m.Groups[4].Value),
                        m.Groups[5].Value == "true");
                    if (!reclasse) res.TryAdd(c.Nom, c);
                    else res[c.Nom] = res.TryGetValue(c.Nom, out var avant) ? avant with { Sens = c.Sens } : c;
                }
            }
            foreach (Match m in MajExclues().Matches(sql))
                if (res.TryGetValue(m.Groups[2].Value, out var c)) res[c.Nom] = c with { Exclues = Liste(m.Groups[1].Value) };
        }
        return res;
    }

    public static bool Envoyee(ClassementTable c) => c.Sens is "montante" or "bidirectionnelle" || TablesOptionnelles.Contains(c.Nom);

    /// <summary>Tables miroir (ordre alphabétique) à partir du schéma local.</summary>
    public static List<TableMiroir> Tables(string schema, IReadOnlyDictionary<string, ClassementTable> classement)
    {
        var colonnes = ColonnesLocales(schema);
        var cles = ClesPrimaires().Matches(schema).ToDictionary(m => m.Groups[1].Value,
            m => m.Groups[2].Value.Split(',').Select(c => c.Trim().Trim('"')).ToList(), StringComparer.Ordinal);
        var res = new List<TableMiroir>();
        foreach (var c in classement.Values.Where(Envoyee).OrderBy(c => c.Nom, StringComparer.Ordinal))
        {
            Identifiant(c.Nom);
            if (!colonnes.TryGetValue(c.Nom, out var locales)) throw new InvalidOperationException($"Table {c.Nom} classée mais absente de schema.sql.");
            if (!cles.TryGetValue(c.Nom, out var cle)) throw new InvalidOperationException($"Table {c.Nom} sans clé primaire.");
            if (locales.All(l => l.Nom != ColonneOfficine)) throw new InvalidOperationException($"Table {c.Nom} sans colonne {ColonneOfficine} (script 128).");
            if (cle.Contains(ColonneOfficine)) throw new InvalidOperationException($"Table {c.Nom} : {ColonneOfficine} dans la clé locale.");
            var liste = new List<ColonneMiroir>();
            foreach (var l in locales)
            {
                if (ColonnesSync.Contains(l.Nom)) throw new InvalidOperationException($"Table {c.Nom} : colonne {l.Nom} réservée au serveur central.");
                if (c.Exclues.Contains(l.Nom) || (c.Porteur && ColonnesPorteur.Contains(l.Nom))) continue;
                Identifiant(l.Nom);
                if (!TypeSur().IsMatch(l.Type)) throw new InvalidOperationException($"Type inattendu pour {c.Nom}.{l.Nom} : {l.Type}");
                liste.Add(c.Patient.Contains(l.Nom) ? new ColonneMiroir(l.Nom, "text") : l);
            }
            if (c.Porteur) liste.Add(new ColonneMiroir("porteur_code", "text"));
            res.Add(new TableMiroir(c.Nom, cle, liste));
        }
        return res;
    }

    /// <summary>Colonnes de chaque table du schéma local (format pg_dump : « CREATE TABLE public.x ( … ); »).</summary>
    public static Dictionary<string, List<ColonneMiroir>> ColonnesLocales(string schema)
    {
        var res = new Dictionary<string, List<ColonneMiroir>>(StringComparer.Ordinal);
        foreach (Match t in CreationTable().Matches(schema))
        {
            var cols = new List<ColonneMiroir>();
            foreach (var ligne in t.Groups[2].Value.Split('\n'))
            {
                var l = ligne.Trim().TrimEnd(',');
                if (l.Length == 0 || l.StartsWith("CONSTRAINT ", StringComparison.Ordinal)) continue;
                var m = DefinitionColonne().Match(l);
                if (!m.Success) throw new InvalidOperationException($"Colonne illisible dans {t.Groups[1].Value} : {l}");
                cols.Add(new ColonneMiroir(m.Groups[1].Value.Trim('"'), m.Groups[2].Value));
            }
            res[t.Groups[1].Value] = cols;
        }
        return res;
    }

    /// <summary>Nom de table ou de colonne permis (minuscules, chiffres, soulignés) ; toujours écrit entre guillemets.</summary>
    public static string Identifiant(string nom) =>
        IdentifiantSur().IsMatch(nom) ? nom : throw new InvalidOperationException($"Identifiant refusé : {nom}");

    private static string Q(string nom) => "\"" + Identifiant(nom) + "\"";

    public static string Script(IReadOnlyList<TableMiroir> tables)
    {
        var s = new StringBuilder();
        s.Append("""
            -- =====================================================================
            -- Base centrale : tables miroir des tables envoyées par les officines.
            -- FICHIER GÉNÉRÉ, ne pas modifier à la main :
            --   Pharma.Cloud.Api.exe --generer-miroir <racine du dépôt>
            -- Source : database/init/schema.sql (schéma local) et classement
            -- sync_table (database/migration 126 et suivants). Test d'alignement :
            -- tests/Pharma.Cloud.Tests/SchemaMiroirTests.cs.
            -- Mêmes tables et colonnes que la base locale (dont officine_id), sauf
            -- colonnes jamais envoyées (exclues, porteur remplacé par porteur_code,
            -- patients sous code P-… donc en texte) ; clé = (officine_id, clé
            -- locale) ; colonnes sync_epoque, sync_numero, sync_recu_le (état final
            -- = ligne de plus grand n°), sync_chargement et sync_absente_le
            -- (rechargement complet, script 003). RLS forcée par officine. Idempotent ; une
            -- colonne ajoutée localement est ajoutée ici (ADD COLUMN IF NOT EXISTS) ;
            -- un changement de type se traite par un script central à part.
            -- =====================================================================

            """.Replace("\r\n", "\n"));
        foreach (var t in tables)
        {
            var cle = string.Join(", ", t.Cle.Select(Q));
            var nom = "public." + Q(t.Nom);
            s.Append('\n').Append($"CREATE TABLE IF NOT EXISTS {nom} (\n");
            s.Append("    officine_id uuid NOT NULL,\n");
            foreach (var k in t.Cle) s.Append($"    {Q(k)} {t.Colonnes.First(c => c.Nom == k).Type} NOT NULL,\n");
            s.Append("    sync_epoque integer NOT NULL,\n    sync_numero bigint NOT NULL,\n    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,\n");
            s.Append($"    PRIMARY KEY (officine_id, {cle})\n);\n");
            foreach (var c in t.Colonnes.Where(c => c.Nom != ColonneOfficine && !t.Cle.Contains(c.Nom)))
                s.Append($"ALTER TABLE {nom} ADD COLUMN IF NOT EXISTS {Q(c.Nom)} {c.Type};\n");
            // Lot S6 : dernier chargement complet ayant vu la ligne, date à laquelle elle a été trouvée absente de l'officine
            s.Append($"ALTER TABLE {nom} ADD COLUMN IF NOT EXISTS sync_chargement integer;\n");
            s.Append($"ALTER TABLE {nom} ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;\n");
            s.Append($"ALTER TABLE {nom} ENABLE ROW LEVEL SECURITY;\n");
            s.Append($"ALTER TABLE {nom} FORCE ROW LEVEL SECURITY;\n");
            s.Append($"DROP POLICY IF EXISTS cloisonnement ON {nom};\n");
            s.Append($"CREATE POLICY cloisonnement ON {nom} USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());\n");
            s.Append($"GRANT SELECT, INSERT, UPDATE, DELETE ON {nom} TO pharma_cloud_api;\n");
            s.Append($"INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('{t.Nom}', '{{{string.Join(",", t.Cle)}}}', " +
                     $"'{{{string.Join(",", t.Colonnes.Select(c => c.Nom))}}}')\n" +
                     "    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;\n");
        }
        return s.ToString();
    }

    private static List<string> Liste(string brut) =>
        brut.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    [GeneratedRegex("^[a-z_][a-z0-9_]*$")]
    private static partial Regex IdentifiantSur();

    [GeneratedRegex(@"^[a-z0-9_ (),\[\]]+$")]
    private static partial Regex TypeSur();

    [GeneratedRegex(@"\('(\w+)', '(montante|bidirectionnelle|descendante|jamais)', '\{([^}]*)\}', '\{([^}]*)\}', (true|false)")]
    private static partial Regex InsertClassement();

    [GeneratedRegex(@"INSERT INTO sync_table\b(.*?)\bON CONFLICT\b([^;]*)", RegexOptions.Singleline)]
    private static partial Regex InsertionSyncTable();

    [GeneratedRegex(@"UPDATE sync_table\s+SET colonnes_exclues = '\{([^}]*)\}'[^;]*?WHERE nom_table = '(\w+)'")]
    private static partial Regex MajExclues();

    [GeneratedRegex(@"^CREATE TABLE public\.(\w+) \(\n(.*?)\n\);", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex CreationTable();

    [GeneratedRegex(@"ALTER TABLE ONLY public\.(\w+)\s+ADD CONSTRAINT \w+ PRIMARY KEY \(([^)]+)\);")]
    private static partial Regex ClesPrimaires();

    [GeneratedRegex(@"^(""?\w+""?) (.+?)(?: (?:DEFAULT|NOT NULL|GENERATED|COLLATE|CONSTRAINT|CHECK)\b.*)?$")]
    private static partial Regex DefinitionColonne();
}
