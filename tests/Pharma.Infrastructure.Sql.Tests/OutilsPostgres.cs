using System.Text.Json;
using Npgsql;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>Recherche de psql, de la racine du dépôt et de la chaîne de connexion des tests (PHARMA_TEST_CNX, sinon appsettings de l'API).</summary>
internal static class OutilsPostgres
{
    public static string? Outil(string nom)
    {
        if (Environment.GetEnvironmentVariable("PHARMA_PG_BIN") is { Length: > 0 } bin && File.Exists(Path.Combine(bin, nom))) return Path.Combine(bin, nom);
        var racine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL");
        return Directory.Exists(racine)
            ? Directory.GetDirectories(racine).Select(d => Path.Combine(d, "bin", nom)).Where(File.Exists)
                .OrderByDescending(f => int.TryParse(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f))), out var v) ? v : 0)
                .FirstOrDefault()
            : null;
    }

    public static string ChaineConnexion()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_TEST_CNX") is { Length: > 0 } cnx) return cnx;
        var dossierApi = Path.Combine(RacineDepot(), "src", "Pharma.Api");
        var appsettings = Path.Combine(dossierApi, "appsettings.Development.json");
        if (!File.Exists(appsettings)) appsettings = Path.Combine(dossierApi, "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(appsettings), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        return doc.RootElement.GetProperty("ConnectionStrings").GetProperty("Pharmacie").GetString()!;
    }

    /// <summary>Connexion d'administration : même serveur et même compte, base « postgres » (uniquement pour créer, lister et supprimer les bases jetables).</summary>
    public static string ChaineAdministration() =>
        new NpgsqlConnectionStringBuilder(ChaineConnexion()) { Database = "postgres", Pooling = false, Timeout = 5, ApplicationName = "sql-test-admin" }.ConnectionString;

    public static string RacineDepot()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } racine) return racine;
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    public static bool ExigerBase => Environment.GetEnvironmentVariable("PHARMA_EXIGER_BASE") == "1";
}
