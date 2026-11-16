using System.Text.RegularExpressions;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Synchronisation, lot S1 (docs/synchronisation-inventaire.md) : chaque table du schéma public créée par un script numéroté est
/// classée (montante, descendante, bidirectionnelle ou jamais) dans sync_table par un script, avec le même sens dans
/// l'inventaire documenté. Une nouvelle table oubliée fait échouer ce test (et sync_definir_active(true) en base).
/// </summary>
public partial class SynchronisationInventaireTests
{
    private static readonly string Racine = RacineDepot();
    private static readonly string[] Scripts = Directory.GetFiles(Path.Combine(Racine, "database", "migration"), "*.sql")
        .Where(f => Regex.IsMatch(Path.GetFileName(f), @"^\d{3}_.+\.sql$"))
        .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
        .Select(f => CommentaireBloc().Replace(CommentaireLigne().Replace(File.ReadAllText(f), ""), " "))
        .ToArray();

    private static Dictionary<string, string> ClassementScripts()
    {
        var sens = new Dictionary<string, string>();
        foreach (var sql in Scripts)
            foreach (Match insertion in InsertionSyncTable().Matches(sql))
                foreach (Match m in Classement().Matches(insertion.Value)) sens[m.Groups[1].Value] = m.Groups[2].Value;
        return sens;
    }

    private static List<string> TablesPubliques()
    {
        var etat = new Dictionary<string, bool>();
        foreach (var sql in Scripts)
        {
            var ordres = CreationTable().Matches(sql).Select(m => (m.Index, Nom: m.Groups[1].Value.ToLowerInvariant(), Vivant: true))
                .Concat(SuppressionTable().Matches(sql).Select(m => (m.Index, Nom: m.Groups[1].Value.ToLowerInvariant(), Vivant: false)))
                .OrderBy(o => o.Index);
            foreach (var o in ordres) etat[o.Nom.StartsWith("public.", StringComparison.Ordinal) ? o.Nom[7..] : o.Nom] = o.Vivant;
        }
        return etat.Where(e => e.Value && !e.Key.Contains('.')).Select(e => e.Key).Order().ToList();
    }

    [Fact]
    public void Chaque_table_du_schema_public_est_classee()
    {
        var tables = TablesPubliques();
        Assert.True(tables.Count > 100, "L'analyse des scripts ne trouve plus les tables.");
        var classees = ClassementScripts();
        var oubliees = tables.Where(t => !classees.ContainsKey(t)).ToList();
        Assert.True(oubliees.Count == 0,
            "Tables non classées pour la synchronisation (ajoutez-les à sync_table dans un script et à docs/synchronisation-inventaire.md) : "
            + string.Join(", ", oubliees));
        var inconnues = classees.Keys.Where(t => !tables.Contains(t)).ToList();
        Assert.True(inconnues.Count == 0, "Tables classées mais créées par aucun script : " + string.Join(", ", inconnues));
    }

    [Fact]
    public void L_inventaire_documente_suit_le_classement_des_scripts()
    {
        var doc = File.ReadAllText(Path.Combine(Racine, "docs", "synchronisation-inventaire.md"));
        var documente = LigneInventaire().Matches(doc).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        var ecarts = ClassementScripts().Where(c => !documente.TryGetValue(c.Key, out var s) || s != c.Value)
            .Select(c => $"{c.Key} ({c.Value})").ToList();
        Assert.True(ecarts.Count == 0, "docs/synchronisation-inventaire.md ne suit pas sync_table : " + string.Join(", ", ecarts));
        Assert.Equal(ClassementScripts().Count, documente.Count);
    }

    [Fact]
    public void Patients_et_porteurs_ne_partent_que_sous_code()
    {
        var sql = string.Join("\n", Scripts);
        var patient = Regex.Match(sql, @"\('patient',\s*'montante',\s*'\{([^}]*)\}',\s*'\{([^}]*)\}'");
        Assert.True(patient.Success, "Ligne patient introuvable dans sync_table.");
        foreach (var c in new[] { "nom", "prenom", "num_assure", "telephone", "email", "date_naissance" })
            Assert.Contains(c, patient.Groups[1].Value.Split(','));
        Assert.Contains("id", patient.Groups[2].Value.Split(','));
        foreach (var registre in new[] { "registre_psychotrope", "journal_registre_psychotrope" })
            Assert.Matches(new Regex($@"\('{registre}',\s*'montante',\s*'\{{porteur_nom,porteur_adresse,porteur_piece\}}',\s*'\{{patient_id\}}',\s*true"), sql);
        foreach (var jamais in new[] { "ordonnance", "sync_pseudonyme", "message_sortant", "destinataire_notification", "historique_chifa" })
            Assert.Equal("jamais", ClassementScripts()[jamais]);
    }

    private static string RacineDepot()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } racine) return racine;
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    [GeneratedRegex(@"--[^\n]*")] private static partial Regex CommentaireLigne();
    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)] private static partial Regex CommentaireBloc();
    [GeneratedRegex(@"(?<!TEMP )(?<!TEMPORARY )\bCREATE TABLE (?:IF NOT EXISTS )?([a-z_][a-z0-9_.]*)", RegexOptions.IgnoreCase)] private static partial Regex CreationTable();
    [GeneratedRegex(@"\bDROP TABLE (?:IF EXISTS )?([a-z_][a-z0-9_.]*)", RegexOptions.IgnoreCase)] private static partial Regex SuppressionTable();
    // Insertion idempotente : se termine par ON CONFLICT (un « ; » peut figurer dans un motif)
    [GeneratedRegex(@"INSERT INTO sync_table\b.*?\bON CONFLICT\b",RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex InsertionSyncTable();
    [GeneratedRegex(@"\(\s*'([a-z_][a-z0-9_]*)',\s*'(montante|descendante|bidirectionnelle|jamais)'")] private static partial Regex Classement();
    [GeneratedRegex(@"^\|\s*`([a-z_][a-z0-9_]*)`\s*\|\s*(montante|descendante|bidirectionnelle|jamais)\s*\|", RegexOptions.Multiline)] private static partial Regex LigneInventaire();
}
