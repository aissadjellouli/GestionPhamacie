using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Copie jetable « pharmacie_jetable_m4_… » (lot M4 de la mutualisation des réglages) : base neuve créée avec database/init (schema.sql et
/// seed_referentiel.sql = base à jour, script 141 compris), supprimée à la fin. Sans serveur PostgreSQL joignable, sans psql ou sans droit
/// CREATEDB, <see cref="Disponible"/> est faux et les tests ne font rien (comme les autres tests de base de ce projet).
/// </summary>
public sealed class BaseJetableM4 : IDisposable
{
    private readonly string administration = "";
    public string? Nom { get; }
    public string? Psql { get; }
    public NpgsqlConnectionStringBuilder Connexion { get; }
    public bool Disponible => Nom is not null;
    public NpgsqlDataSource? Source { get; }

    public BaseJetableM4() : this("pharmacie_jetable_m4_", true) { }

    internal BaseJetableM4(string prefixe, bool initialiser)
    {
        Connexion = new NpgsqlConnectionStringBuilder(ChaineConnexion());
        Psql = Outil("psql.exe");
        if (Psql is null) return;
        var nom = prefixe + Guid.NewGuid().ToString("N")[..8];
        administration = new NpgsqlConnectionStringBuilder(Connexion.ConnectionString) { Database = "postgres", Pooling = false }.ConnectionString;
        try
        {
            using var cx = new NpgsqlConnection(administration);
            cx.Open();
            using var creer = new NpgsqlCommand($"CREATE DATABASE \"{nom}\" ENCODING 'UTF8' TEMPLATE template0", cx);
            creer.ExecuteNonQuery();
        }
        catch (NpgsqlException) { return; }   // serveur injoignable ou compte sans droit CREATEDB : rien à vérifier ici
        Nom = nom;
        if (initialiser)
        {
            var init = Path.Combine(RacineDepot(), "database", "init");
            Executer(["-f", Chemin(Path.Combine(init, "schema.sql"))]);
            Executer(["-f", Chemin(Path.Combine(init, "seed_referentiel.sql"))]);
        }
        Source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(Connexion.ConnectionString) { Database = nom }.ConnectionString);
    }

    public string ChaineBase => new NpgsqlConnectionStringBuilder(Connexion.ConnectionString) { Database = Nom }.ConnectionString;

    /// <summary>Lance psql sur la copie ; <paramref name="options"/> = PGOPTIONS. Renvoie le code de sortie et les erreurs.</summary>
    public (int Code, string Erreurs) Lancer(string[] arguments, string options = "-c client_min_messages=warning")
    {
        var info = new ProcessStartInfo(Psql!)
        {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in new[] { "--no-password", $"--host={Connexion.Host}", $"--port={Connexion.Port}", $"--username={Connexion.Username}", $"--dbname={Nom}",
                     "-v", "ON_ERROR_STOP=1", "-q" }.Concat(arguments))
            info.ArgumentList.Add(a);
        info.Environment["PGPASSWORD"] = Connexion.Password ?? "";
        info.Environment["PGCLIENTENCODING"] = "UTF8";
        info.Environment["PGOPTIONS"] = options;
        using var p = Process.Start(info) ?? throw new InvalidOperationException("psql n'a pas pu démarrer.");
        var sortie = p.StandardOutput.ReadToEndAsync();
        var erreurs = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        _ = sortie.Result;
        var texte = erreurs.Result.Trim();
        return (p.ExitCode, texte.Length > 800 ? texte[..800] + "…" : texte);
    }

    public void Executer(string[] arguments)
    {
        var (code, erreurs) = Lancer(arguments);
        Assert.True(code == 0, $"psql a échoué ({string.Join(' ', arguments)}) : {erreurs}");
    }

    public static string Chemin(string fichier) => fichier.Replace('\\', '/');

    public void Dispose()
    {
        Source?.Dispose();
        if (Nom is null) return;
        try
        {
            using var cx = new NpgsqlConnection(administration);
            cx.Open();
            using var supprimer = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Nom}\" WITH (FORCE)", cx);
            supprimer.ExecuteNonQuery();
        }
        catch (NpgsqlException) { /* copie restée : supprimable à la main (préfixe pharmacie_jetable_m4_) */ }
    }

    private static string? Outil(string nom)
    {
        if (Environment.GetEnvironmentVariable("PHARMA_PG_BIN") is { Length: > 0 } bin && File.Exists(Path.Combine(bin, nom))) return Path.Combine(bin, nom);
        var racine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL");
        return Directory.Exists(racine)
            ? Directory.GetDirectories(racine).Select(d => Path.Combine(d, "bin", nom)).Where(File.Exists)
                .OrderByDescending(f => int.TryParse(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f))), out var v) ? v : 0)
                .FirstOrDefault()
            : null;
    }

    private static string ChaineConnexion()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_TEST_CNX") is { Length: > 0 } cnx) return cnx;
        var dossierApi = Path.Combine(RacineDepot(), "src", "Pharma.Api");
        var appsettings = Path.Combine(dossierApi, "appsettings.Development.json");
        if (!File.Exists(appsettings)) appsettings = Path.Combine(dossierApi, "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(appsettings), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        return doc.RootElement.GetProperty("ConnectionStrings").GetProperty("Pharmacie").GetString()!;
    }

    public static string RacineDepot()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } racine) return racine;
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }
}
