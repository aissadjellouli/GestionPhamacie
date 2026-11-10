using System.Data;
using System.Data.Odbc;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Win32;
using Pharma.Application.Reprise;
using Pharma.Domain.Commun;
using Pharma.Domain.Reprise;

namespace Pharma.Infrastructure.Reprise;

/// <summary>
/// Ancienne base lue par ODBC (pilotes installés sur le PC serveur), en lecture seule : uniquement des SELECT,
/// construits avec des noms de tables et de colonnes relus dans la structure de la base (aucun texte saisi n'est exécuté).
/// </summary>
public sealed class SourceOdbc : ISourceReprise
{
    private const int DelaiSecondes = 60;
    private const int TablesMax = 400;

    // ---- Sources disponibles ------------------------------------------------------

    public Task<IReadOnlyList<SourceDisponible>> SourcesAsync(string? dossier, CancellationToken ct = default)
    {
        var sources = new List<SourceDisponible>();
        if (!OperatingSystem.IsWindows()) return Task.FromResult<IReadOnlyList<SourceDisponible>>(sources);

        // Sources ODBC déclarées (souvent créées par l'ancien logiciel à son installation)
        foreach (var (racine, portee) in new[] { (Registry.LocalMachine, "machine"), (Registry.CurrentUser, "utilisateur") })
        {
            using var cle = racine.OpenSubKey(@"SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources");
            foreach (var nom in cle?.GetValueNames() ?? [])
                sources.Add(new SourceDisponible($"{nom} ({cle!.GetValue(nom)})", $"DSN={nom};", "dsn", $"Source ODBC déclarée ({portee})",
                    cle.GetValue(nom)?.ToString(), null, nom));
        }
        using (var cle32 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\ODBC\ODBC.INI\ODBC Data Sources"))
            foreach (var nom in cle32?.GetValueNames() ?? [])
                if (!sources.Any(s => s.ChaineConnexion == $"DSN={nom};"))
                    sources.Add(new SourceDisponible($"{nom} ({cle32!.GetValue(nom)})", null, "dsn",
                        "Source ODBC 32 bits : le serveur (64 bits) ne peut pas l’utiliser ; déclarez-la aussi en 64 bits"));

        if (dossier is not null) sources.AddRange(FichiersDeBase(dossier));
        return Task.FromResult<IReadOnlyList<SourceDisponible>>(sources);
    }

    private static IEnumerable<SourceDisponible> FichiersDeBase(string dossier)
    {
        if (!Directory.Exists(dossier)) throw new RegleMetierException($"Dossier introuvable sur le PC serveur : {dossier}");
        var pilotes = PilotesInstalles();
        string? Pilote(string motCle) => pilotes.FirstOrDefault(p => p.Contains(motCle, StringComparison.OrdinalIgnoreCase));

        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
        var fichiers = Directory.EnumerateFiles(dossier, "*", options).Take(5000).ToList();
        var vus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in fichiers)
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            var rep = Path.GetDirectoryName(f)!;
            switch (ext)
            {
                case ".mdb" or ".accdb":
                    yield return Pilote("Access Driver") is { } a
                        ? new SourceDisponible($"Base Access : {f}", $"Driver={{{a}}};Dbq={f};", "fichier", "Saisissez le mot de passe si la base en a un", a, f)
                        : new SourceDisponible($"Base Access : {f}", null, "fichier", "Pilote ODBC Access absent du PC serveur");
                    break;
                case ".dbf" when vus.Add("dbf|" + rep):
                    yield return Pilote("dBASE") is { } d
                        ? new SourceDisponible($"Tables dBase (DBF) : {rep}", $"Driver={{{d}}};DefaultDir={rep};", "fichier", null, d, rep)
                        : new SourceDisponible($"Tables dBase (DBF) : {rep}", null, "fichier", "Pilote ODBC dBase absent du PC serveur");
                    break;
                case ".csv" or ".txt" when vus.Add("txt|" + rep):
                    if (Pilote("Text Driver") is { } t)
                        yield return new SourceDisponible($"Fichiers texte (CSV) : {rep}", $"Driver={{{t}}};Dbq={rep};Extensions=asc,csv,tab,txt;", "fichier",
                            "Exports CSV : séparateur virgule par défaut", t, rep);
                    break;
                case ".xls" or ".xlsx":
                    if (Pilote("Excel Driver") is { } x)
                        yield return new SourceDisponible($"Classeur Excel : {f}", $"Driver={{{x}}};Dbq={f};", "fichier", null, x, f);
                    break;
                case ".fic" when vus.Add("hfsql|" + rep):
                    yield return new SourceDisponible($"Base HFSQL (WinDev) : {rep}", null, "fichier",
                        Pilote("HFSQL") is null ? "Pilote ODBC HFSQL à installer (fourni par PC SOFT), puis déclarer une source ODBC"
                                                : "Déclarez une source ODBC HFSQL vers ce dossier, puis choisissez-la dans la liste");
                    break;
                case ".fdb" or ".gdb":
                    yield return Pilote("Firebird") is { } fb
                        ? new SourceDisponible($"Base Firebird : {f}", $"Driver={{{fb}}};Dbname={f};", "fichier", "Saisissez l’utilisateur et le mot de passe (souvent SYSDBA)", fb, f)
                        : new SourceDisponible($"Base Firebird : {f}", null, "fichier", "Pilote ODBC Firebird absent du PC serveur");
                    break;
                case ".db" or ".sqlite" or ".sqlite3" or ".db3":
                    yield return Pilote("SQLite") is { } s
                        ? new SourceDisponible($"Base SQLite : {f}", $"Driver={{{s}}};Database={f};", "fichier", null, s, f)
                        : new SourceDisponible($"Base SQLite ? {f}", null, "fichier", "Pilote ODBC SQLite absent du PC serveur");
                    break;
            }
        }
    }

    public IReadOnlyList<string> Pilotes() => PilotesInstalles().OrderBy(p => p).ToList();

    private static List<string> PilotesInstalles()
    {
        if (!OperatingSystem.IsWindows()) return [];
        using var cle = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers");
        return cle?.GetValueNames().ToList() ?? [];
    }

    // ---- Connexion et structure ---------------------------------------------------

    public async Task<InfoSource> TesterAsync(string chaineConnexion, CancellationToken ct = default)
    {
        await using var cnx = await Ouvrir(chaineConnexion, ct);
        return new InfoSource(cnx.Driver, string.IsNullOrEmpty(cnx.ServerVersion) ? null : cnx.ServerVersion, 0);
    }

    public async Task<IReadOnlyList<TableSource>> ScannerAsync(string chaineConnexion, int exemples, CancellationToken ct = default)
    {
        await using var cnx = await Ouvrir(chaineConnexion, ct);
        var resultat = new List<TableSource>();
        foreach (var table in NomsDeTables(cnx).Take(TablesMax))
        {
            ct.ThrowIfCancellationRequested();
            var colonnes = Colonnes(cnx, table);
            if (colonnes.Count == 0) continue;
            var echantillon = colonnes.ToDictionary(c => c.Nom, _ => new List<string?>());
            try
            {
                await using var cmd = Commande(cnx, $"SELECT * FROM {Citer(cnx, table)}");
                await using var r = await cmd.ExecuteReaderAsync(ct);
                for (var n = 0; n < exemples && await r.ReadAsync(ct); n++)
                    for (var i = 0; i < r.FieldCount; i++)
                        if (echantillon.TryGetValue(r.GetName(i), out var liste)) liste.Add(Texte(r, i));
            }
            catch (OdbcException) { /* table illisible (droits, verrou) : listée sans exemples */ }
            resultat.Add(new TableSource(table, await Compter(cnx, table, ct),
                colonnes.Select(c => new ColonneSource(c.Nom, c.Type, echantillon[c.Nom])).ToList()));
        }
        return resultat;
    }

    public async IAsyncEnumerable<string?[]> LireAsync(string chaineConnexion, string table, IReadOnlyList<string> colonnes,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var cnx = await Ouvrir(chaineConnexion, ct);
        var vraieTable = NomsDeTables(cnx).FirstOrDefault(t => string.Equals(t, table, StringComparison.OrdinalIgnoreCase))
            ?? throw new RegleMetierException($"Table « {table} » introuvable dans l’ancienne base.");
        var connues = Colonnes(cnx, vraieTable);
        var vraies = colonnes.Select(c => connues.FirstOrDefault(k => string.Equals(k.Nom, c, StringComparison.OrdinalIgnoreCase)).Nom
            ?? throw new RegleMetierException($"Colonne « {c} » introuvable dans la table « {table} ».")).ToList();

        await using var cmd = Commande(cnx, $"SELECT {string.Join(", ", vraies.Select(c => Citer(cnx, c)))} FROM {Citer(cnx, vraieTable)}");
        cmd.CommandTimeout = 0;   // grosses tables : pas de délai
        await using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await r.ReadAsync(ct))
        {
            var ligne = new string?[vraies.Count];
            for (var i = 0; i < vraies.Count; i++) ligne[i] = Texte(r, i);
            yield return ligne;
        }
    }

    private static async Task<OdbcConnection> Ouvrir(string chaine, CancellationToken ct)
    {
        var cnx = new OdbcConnection(chaine) { ConnectionTimeout = 20 };
        try
        {
            await cnx.OpenAsync(ct);
            return cnx;
        }
        catch (Exception e) when (e is OdbcException or ArgumentException or InvalidOperationException)
        {
            await cnx.DisposeAsync();
            // Le message du pilote ne contient pas la chaîne de connexion (donc pas de mot de passe)
            throw new RegleMetierException($"Connexion impossible à l’ancienne base : {e.Message.Split('\n')[0].Trim()}");
        }
    }

    private static List<string> NomsDeTables(OdbcConnection cnx)
    {
        using var schema = cnx.GetSchema("Tables");
        return schema.Rows.Cast<DataRow>()
            .Where(r => r["TABLE_TYPE"]?.ToString() is "TABLE" or "VIEW")
            .Select(r => r["TABLE_NAME"]!.ToString()!)
            .Where(n => !n.StartsWith("MSys", StringComparison.OrdinalIgnoreCase))
            .Distinct().OrderBy(n => n).ToList();
    }

    private static List<(string Nom, string Type)> Colonnes(OdbcConnection cnx, string table)
    {
        try
        {
            using var schema = cnx.GetSchema("Columns", [null, null, table, null]);
            return schema.Rows.Cast<DataRow>()
                .OrderBy(r => Convert.ToInt32(r["ORDINAL_POSITION"], CultureInfo.InvariantCulture))
                .Select(r => (r["COLUMN_NAME"]!.ToString()!, r["TYPE_NAME"]?.ToString() ?? ""))
                .ToList();
        }
        catch (OdbcException) { return []; }
    }

    private static async Task<long?> Compter(OdbcConnection cnx, string table, CancellationToken ct)
    {
        try
        {
            await using var cmd = Commande(cnx, $"SELECT COUNT(*) FROM {Citer(cnx, table)}");
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        catch (OdbcException) { return null; }
    }

    private static OdbcCommand Commande(OdbcConnection cnx, string sql) => new(sql, cnx) { CommandTimeout = DelaiSecondes };

    /// <summary>Nom entre délimiteurs du pilote (guillemets, crochets ou accents graves).</summary>
    private static string Citer(OdbcConnection cnx, string nom)
    {
        using var b = new OdbcCommandBuilder();
        return b.QuoteIdentifier(nom, cnx);
    }

    private static string? Texte(IDataRecord r, int i)
    {
        if (r.IsDBNull(i)) return null;
        return r.GetValue(i) switch
        {
            DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            byte[] => null,
            var v => v.ToString()
        };
    }
}
