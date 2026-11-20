namespace Pharma.Infrastructure.Sauvegardes;

/// <summary>
/// Recherche des outils PostgreSQL (pg_dump, pg_restore, psql). Sous Windows, inchangé : « nom.exe » à côté du réglage
/// Sauvegardes:PgDump, sinon dans C:\Program Files\PostgreSQL\&lt;version&gt;\bin (version la plus récente). Hors Windows : nom sans
/// « .exe », à côté du réglage, sinon /usr/lib/postgresql/&lt;version&gt;/bin (version la plus récente), sinon le PATH, /usr/bin,
/// /usr/local/bin.
/// </summary>
public static class OutilsPostgres
{
    public static string NomExecutable(string outil, bool windows) => windows ? outil + ".exe" : outil;

    /// <summary>Dossiers « racine/&lt;version&gt;/bin » parcourus après le réglage.</summary>
    public static IReadOnlyList<string> RacinesVersions(bool windows) => windows
        ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL")]
        : ["/usr/lib/postgresql"];

    /// <summary>Dossiers parcourus en dernier hors Windows : PATH, puis /usr/bin et /usr/local/bin.</summary>
    public static IReadOnlyList<string> DossiersPath(bool windows, string? variablePath) => windows
        ? []
        : [.. (variablePath ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), "/usr/bin", "/usr/local/bin"];

    /// <summary>Outil de la plateforme courante ; null s'il est introuvable.</summary>
    public static string? Trouver(string outil, string? cheminPgDump)
    {
        var windows = OperatingSystem.IsWindows();
        return Trouver(outil, cheminPgDump, windows, RacinesVersions(windows), DossiersPath(windows, Environment.GetEnvironmentVariable("PATH")));
    }

    public static string? Trouver(string outil, string? cheminPgDump, bool windows, IReadOnlyList<string> racinesVersions, IReadOnlyList<string> dossiersPath)
    {
        var nom = NomExecutable(outil, windows);
        // 1. À côté de pg_dump réglé (Sauvegardes:PgDump)
        if (!string.IsNullOrWhiteSpace(cheminPgDump) && Path.GetDirectoryName(cheminPgDump) is { } bin && File.Exists(Path.Combine(bin, nom)))
            return Path.Combine(bin, nom);
        // 2. Dernière version installée
        foreach (var racine in racinesVersions)
        {
            var trouve = Directory.Exists(racine)
                ? Directory.GetDirectories(racine).Select(d => Path.Combine(d, "bin", nom)).Where(File.Exists)
                    .OrderByDescending(f => int.TryParse(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f))), out var v) ? v : 0)
                    .FirstOrDefault()
                : null;
            if (trouve is not null) return trouve;
        }
        // 3. PATH (hors Windows seulement : sous Windows, la recherche reste celle d'origine)
        return dossiersPath.Select(d => Path.Combine(d, nom)).FirstOrDefault(File.Exists);
    }
}
