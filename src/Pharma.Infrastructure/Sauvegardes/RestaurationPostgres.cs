using System.Diagnostics;
using Npgsql;
using Pharma.Application.SuperAdmin;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Sauvegardes;

/// <summary>
/// Restauration de la base depuis un fichier pg_dump (format personnalisé), avec le compte de l'application (droit CREATEDB) :
/// 1. base neuve « …_restauree_AAAAMMJJ_HHMMSS » remplie par pg_restore ;
/// 2. scripts database/0xx rejoués (sauvegarde ancienne mise à la version du logiciel), comme pour une mise à jour ;
/// 3. la base en service est renommée « …_avant_restauration_AAAAMMJJ_HHMM » (jamais supprimée), puis la base neuve prend son nom.
/// Un échec avant l'étape 3 ne touche pas la base en service (la base neuve, incomplète, est supprimée).
/// </summary>
public sealed class RestaurationPostgres(string chaineConnexion, string? cheminPgDump, string dossierScripts, NpgsqlDataSource source) : IRestaurationBase
{
    private readonly NpgsqlConnectionStringBuilder connexion = new(chaineConnexion);

    public string NomBase => connexion.Database ?? "";

    private NpgsqlConnection Administration() =>
        new(new NpgsqlConnectionStringBuilder(chaineConnexion) { Database = "postgres", Pooling = false }.ConnectionString);

    private static string Nom(string identifiant) => "\"" + identifiant.Replace("\"", "\"\"") + "\"";

    public async Task<bool> BaseExisteAsync(CancellationToken ct = default)
    {
        try
        {
            await using var cx = Administration();
            await cx.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = $1", cx);
            cmd.Parameters.AddWithValue(NomBase);
            return await cmd.ExecuteScalarAsync(ct) is not null;
        }
        catch (NpgsqlException e)
        {
            throw new RegleMetierException($"Serveur PostgreSQL injoignable ({e.Message}) : vérifiez que le service PostgreSQL est démarré.");
        }
    }

    public async Task VerifierAsync(string fichier, CancellationToken ct = default)
    {
        var (code, erreurs) = await LancerAsync("pg_restore.exe", ["--list", fichier], ct);
        if (code != 0) throw new RegleMetierException($"Sauvegarde illisible ou incomplète ({erreurs}) : choisissez-en une autre.");
    }

    public async Task<string?> RestaurerAsync(string fichier, Action<string> etape, CancellationToken ct = default)
    {
        var horodatage = DateTime.Now;
        var neuve = Limiter(NomBase, $"_restauree_{horodatage:yyyyMMdd_HHmmss}");
        var ancienne = Limiter(NomBase, $"_avant_restauration_{horodatage:yyyyMMdd_HHmm}");

        etape($"Création de la base {neuve}");
        await AdministrerAsync($"CREATE DATABASE {Nom(neuve)} ENCODING 'UTF8' TEMPLATE template0", ct);
        try
        {
            etape("Restauration du contenu (pg_restore), quelques minutes");
            var (code, erreurs) = await LancerAsync("pg_restore.exe",
                ["--no-password", ..Hote(), $"--dbname={neuve}", "--no-owner", "--no-privileges", "--exit-on-error", fichier], ct);
            if (code != 0) throw new RegleMetierException($"pg_restore a échoué : {erreurs}");

            etape("Mise à la version du logiciel (scripts de la base rejoués)");
            await RejouerScriptsAsync(neuve, ct);
        }
        catch
        {
            // Base neuve incomplète : supprimée ; la base en service n'a pas été touchée
            try { await AdministrerAsync($"DROP DATABASE IF EXISTS {Nom(neuve)}", CancellationToken.None); } catch (NpgsqlException) { }
            throw;
        }

        etape("Échange avec la base en service");
        var existait = await BaseExisteAsync(ct);
        if (existait)
        {
            await RenommerAsync(NomBase, ancienne, ct);
            etape($"Ancienne base gardée sous le nom {ancienne}");
        }
        try
        {
            await RenommerAsync(neuve, NomBase, ct);
        }
        catch when (existait)
        {
            // Remise en place de l'ancienne base
            await RenommerAsync(ancienne, NomBase, CancellationToken.None);
            throw;
        }
        source.Clear();
        NpgsqlConnection.ClearAllPools();
        etape($"Base {NomBase} restaurée");
        return existait ? ancienne : null;
    }

    /// <summary>Renommage : les connexions ouvertes sur la base sont fermées d'abord (plusieurs essais : le serveur peut en rouvrir).</summary>
    private async Task RenommerAsync(string de, string vers, CancellationToken ct)
    {
        for (var essai = 1; ; essai++)
        {
            source.Clear();
            NpgsqlConnection.ClearAllPools();
            try
            {
                await AdministrerAsync($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{de.Replace("'", "''")}' AND pid <> pg_backend_pid()", ct);
                await AdministrerAsync($"ALTER DATABASE {Nom(de)} RENAME TO {Nom(vers)}", ct);
                return;
            }
            catch (PostgresException e) when (e.SqlState == "55006" && essai < 5)   // base encore utilisée
            {
                await Task.Delay(1000, ct);
            }
            catch (PostgresException e) when (e.SqlState == "55006")
            {
                throw new RegleMetierException($"La base {de} est encore utilisée par un autre programme (pgAdmin, un autre service…) : fermez-le puis recommencez.");
            }
        }
    }

    private async Task AdministrerAsync(string sql, CancellationToken ct)
    {
        await using var cx = Administration();
        await cx.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, cx) { CommandTimeout = 120 };
        try { await cmd.ExecuteNonQueryAsync(ct); }
        catch (PostgresException e) when (e.SqlState == "42501")
        {
            throw new RegleMetierException($"Le compte {connexion.Username} n'a pas le droit de créer ou renommer une base : sur le PC serveur, " +
                                           $"avec le compte postgres : ALTER ROLE {connexion.Username} CREATEDB (docs/mise-en-production.md, étape 1).");
        }
    }

    /// <summary>Tous les scripts, dans l'ordre : 001 à 032 en une transaction, 033 seul (index CONCURRENTLY), 034 et suivants en une transaction.</summary>
    private async Task RejouerScriptsAsync(string baseCible, CancellationToken ct)
    {
        var scripts = Directory.Exists(dossierScripts)
            ? Directory.GetFiles(dossierScripts, "0*.sql").OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList()
            : [];
        if (scripts.Count == 0) throw new RegleMetierException($"Scripts de la base introuvables ({dossierScripts}) : réinstallez l'API.");
        static int Numero(string f) => int.TryParse(Path.GetFileName(f)[..3], out var n) ? n : 0;
        var lots = new List<(List<string> Fichiers, bool Transaction)>
        {
            (scripts.Where(f => Numero(f) <= 32).ToList(), true),
            (scripts.Where(f => Numero(f) == 33).ToList(), false),
            (scripts.Where(f => Numero(f) >= 34).ToList(), true)
        };
        var temporaire = Path.Combine(Path.GetTempPath(), $"pharma_rejeu_{Guid.NewGuid():N}.sql");
        try
        {
            foreach (var (fichiers, transaction) in lots.Where(l => l.Fichiers.Count > 0))
            {
                // Chemins avec « / » : psql interprète les « \ » entre apostrophes
                var lignes = fichiers.Select(f => $"\\i '{f.Replace('\\', '/').Replace("'", "''")}'").ToList();
                if (transaction) { lignes.Insert(0, "BEGIN;"); lignes.Add("COMMIT;"); }
                await File.WriteAllLinesAsync(temporaire, lignes, ct);
                var (code, erreurs) = await LancerAsync("psql.exe",
                    ["--no-password", ..Hote(), $"--dbname={baseCible}", "-v", "ON_ERROR_STOP=1", "-q", "-f", temporaire], ct, scripts: true);
                if (code != 0) throw new RegleMetierException($"Mise à la version du logiciel impossible : {erreurs}");
            }
        }
        finally { try { File.Delete(temporaire); } catch (IOException) { } }
    }

    private string[] Hote() => [$"--host={connexion.Host}", $"--port={connexion.Port}", $"--username={connexion.Username}"];

    /// <summary>Nom de base de 63 caractères au plus (limite de PostgreSQL), suffixe gardé.</summary>
    private static string Limiter(string nom, string suffixe) => (nom.Length + suffixe.Length > 63 ? nom[..(63 - suffixe.Length)] : nom) + suffixe;

    private async Task<(int Code, string Erreurs)> LancerAsync(string outil, string[] arguments, CancellationToken ct, bool scripts = false)
    {
        var info = new ProcessStartInfo(Outil(outil))
        {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        info.Environment["PGPASSWORD"] = connexion.Password ?? "";   // jamais sur la ligne de commande
        info.Environment["PGCLIENTENCODING"] = "UTF8";
        if (scripts) info.Environment["PGOPTIONS"] = "-c client_min_messages=warning";
        using var p = Process.Start(info) ?? throw new InvalidOperationException($"{outil} n'a pas pu démarrer.");
        var sortie = p.StandardOutput.ReadToEndAsync(ct);
        var erreurs = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        await sortie;
        var texte = (await erreurs).Trim();
        return (p.ExitCode, texte.Length > 600 ? texte[..600] + "…" : texte);
    }

    /// <summary>Outil PostgreSQL : à côté de pg_dump (réglage Sauvegardes:PgDump), sinon dans la dernière version installée.</summary>
    private string Outil(string nom)
    {
        if (!string.IsNullOrWhiteSpace(cheminPgDump) && Path.GetDirectoryName(cheminPgDump) is { } bin && File.Exists(Path.Combine(bin, nom)))
            return Path.Combine(bin, nom);
        var racine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL");
        var trouve = Directory.Exists(racine)
            ? Directory.GetDirectories(racine).Select(d => Path.Combine(d, "bin", nom)).Where(File.Exists)
                .OrderByDescending(f => int.TryParse(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f))), out var v) ? v : 0)
                .FirstOrDefault()
            : null;
        return trouve ?? throw new InvalidOperationException($"{nom} introuvable : renseignez Sauvegardes:PgDump dans appsettings.json.");
    }
}
