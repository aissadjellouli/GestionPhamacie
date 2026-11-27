namespace Pharma.Shared.Journalisation;

/// <summary>
/// Dossier des journaux en fichiers de tout le logiciel (décision du titulaire du 2026-11-28) : sous Windows,
/// <c>&lt;lecteur du système&gt;\Logs\PharmaOfficine\&lt;application&gt;</c> (C:\Logs\PharmaOfficine\Api, …\Central, …\Poste\&lt;nom du PC&gt;),
/// au lieu d'un dossier par programme. Seuls les journaux y vont : les fichiers que le logiciel relit (décisions de bascule, repères)
/// restent sous la racine des données. Hors Windows, pas de dossier commun : chaque programme garde son dossier sous sa racine.
/// </summary>
public static class DossiersJournaux
{
    public const string Api = "Api";
    public const string Central = "Central";
    public const string Poste = "Poste";

    /// <summary>Dossier commun sous Windows pour une application (sous-dossiers facultatifs), ou null hors Windows.</summary>
    public static string? Windows(string application, params string[] sousDossiers)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var lecteur = Path.GetPathRoot(Environment.SystemDirectory) is { Length: > 0 } r ? r : @"C:\";
        var chemin = Path.Combine(lecteur, "Logs", "PharmaOfficine", application);
        foreach (var s in sousDossiers) chemin = Path.Combine(chemin, NomSur(s));
        return chemin;
    }

    /// <summary>
    /// Le dossier préféré s'il existe ou peut être créé, et si on peut y écrire ; sinon le dossier de repli (ancien emplacement) : un journal
    /// n'empêche jamais le programme de démarrer ni de travailler (PC où le dossier commun n'a pas été préparé, compte sans droit).
    /// </summary>
    public static string Choisir(string? prefere, string repli)
    {
        if (string.IsNullOrWhiteSpace(prefere)) return repli;
        try
        {
            Directory.CreateDirectory(prefere);
            var essai = Path.Combine(prefere, $".ecriture-{Guid.NewGuid():N}.tmp");
            using (new FileStream(essai, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return prefere;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return repli;
        }
    }

    /// <summary>Nom de dossier sans caractère interdit (nom de PC ou de poste).</summary>
    public static string NomSur(string nom)
    {
        var interdits = Path.GetInvalidFileNameChars();
        var propre = new string((nom ?? "").Trim().Select(c => interdits.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        return propre.Length == 0 ? "inconnu" : propre;
    }
}
