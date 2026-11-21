using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Pharma.Cloud.Web.Tests;

internal static class Depot
{
    public static string Racine()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } r) return r;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "PharmaOfficine.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }
}

/// <summary>
/// Base centrale JETABLE « pharmacie_cloud_jetable_s71_… » : tous les scripts database/cloud passés par psql (comme
/// Pharma.Cloud.Tests), supprimée à la fin. Sans serveur PostgreSQL, sans psql ou sans droit CREATEDB : null (le test ne fait rien).
/// </summary>
internal sealed class BaseCentraleJetable : IDisposable
{
    private readonly string administration;
    private readonly string nom;

    /// <summary>Connexion du propriétaire (pharma_app sur le poste de développement).</summary>
    public string Connexion { get; }

    /// <summary>Même connexion, rôle pharma_cloud_ecrans pris à l'ouverture (« SET ROLE » : le propriétaire en a le droit, script 030).</summary>
    public string ConnexionEcrans => new NpgsqlConnectionStringBuilder(Connexion) { Options = "-c role=pharma_cloud_ecrans" }.ConnectionString;

    private BaseCentraleJetable(string administration, string nom, string connexion) => (this.administration, this.nom, Connexion) = (administration, nom, connexion);

    public static BaseCentraleJetable? Creer()
    {
        var psql = Outil("psql.exe");
        if (psql is null) return null;
        var racine = Depot.Racine();
        NpgsqlConnectionStringBuilder modele;
        try { modele = new NpgsqlConnectionStringBuilder(ChaineConnexion(racine)); }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException) { return null; }
        var nom = "pharmacie_cloud_jetable_s71_" + Guid.NewGuid().ToString("N")[..8];
        var administration = new NpgsqlConnectionStringBuilder(modele.ConnectionString) { Database = "postgres", Pooling = false }.ConnectionString;
        try
        {
            using var cx = new NpgsqlConnection(administration);
            cx.Open();
            using var creer = new NpgsqlCommand($"CREATE DATABASE \"{nom}\" ENCODING 'UTF8' TEMPLATE template0", cx);
            creer.ExecuteNonQuery();
        }
        catch (NpgsqlException) { return null; }
        var base_ = new BaseCentraleJetable(administration, nom, new NpgsqlConnectionStringBuilder(modele.ConnectionString) { Database = nom }.ConnectionString);
        try
        {
            foreach (var script in Directory.GetFiles(Path.Combine(racine, "database", "cloud"), "0*.sql").Order(StringComparer.Ordinal))
                Psql(psql, modele, nom, script);
        }
        catch
        {
            base_.Dispose();
            throw;
        }
        return base_;
    }

    public async Task ExecuterAsync(string sql, params object[] parametres)
    {
        await using var db = NpgsqlDataSource.Create(Connexion);
        await using var cmd = db.CreateCommand(sql);
        foreach (var p in parametres) cmd.Parameters.AddWithValue(p);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<object?> ScalaireAsync(string sql, params object[] parametres)
    {
        await using var db = NpgsqlDataSource.Create(Connexion);
        await using var cmd = db.CreateCommand(sql);
        foreach (var p in parametres) cmd.Parameters.AddWithValue(p);
        return await cmd.ExecuteScalarAsync();
    }

    private static void Psql(string psql, NpgsqlConnectionStringBuilder cnx, string nom, string script)
    {
        var info = new ProcessStartInfo(psql)
        {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in new[] { "--no-password", $"--host={cnx.Host}", $"--port={cnx.Port}", $"--username={cnx.Username}", $"--dbname={nom}",
                     "-v", "ON_ERROR_STOP=1", "-q", "-f", script.Replace('\\', '/') })
            info.ArgumentList.Add(a);
        info.Environment["PGPASSWORD"] = cnx.Password ?? "";
        info.Environment["PGCLIENTENCODING"] = "UTF8";
        info.Environment["PGOPTIONS"] = "-c client_min_messages=warning";
        using var p = Process.Start(info) ?? throw new InvalidOperationException("psql n'a pas pu démarrer.");
        var sortie = p.StandardOutput.ReadToEndAsync();
        var erreurs = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        _ = sortie.Result;
        Assert.True(p.ExitCode == 0, $"psql a échoué sur {Path.GetFileName(script)} : {erreurs.Result.Trim()}");
    }

    public void Dispose()
    {
        NpgsqlConnection.ClearAllPools();
        try
        {
            using var cx = new NpgsqlConnection(administration);
            cx.Open();
            using var supprimer = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{nom}\" WITH (FORCE)", cx);
            supprimer.ExecuteNonQuery();
        }
        catch (NpgsqlException) { /* base restée : supprimable à la main (préfixe pharmacie_cloud_jetable_s71_) */ }
    }

    private static string? Outil(string fichier)
    {
        if (Environment.GetEnvironmentVariable("PHARMA_PG_BIN") is { Length: > 0 } bin && File.Exists(Path.Combine(bin, fichier))) return Path.Combine(bin, fichier);
        var racine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL");
        return Directory.Exists(racine)
            ? Directory.GetDirectories(racine).Select(d => Path.Combine(d, "bin", fichier)).Where(File.Exists)
                .OrderByDescending(f => int.TryParse(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f))), out var v) ? v : 0)
                .FirstOrDefault()
            : null;
    }

    private static string ChaineConnexion(string racine)
    {
        if (Environment.GetEnvironmentVariable("PHARMA_TEST_CNX") is { Length: > 0 } cnx) return cnx;
        var dossierApi = Path.Combine(racine, "src", "Pharma.Api");
        var appsettings = Path.Combine(dossierApi, "appsettings.Development.json");
        if (!File.Exists(appsettings)) appsettings = Path.Combine(dossierApi, "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(appsettings), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        return doc.RootElement.GetProperty("ConnectionStrings").GetProperty("Pharmacie").GetString()!;
    }
}
