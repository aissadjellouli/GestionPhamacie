using System.Text.RegularExpressions;

namespace Pharma.Infrastructure.Systeme;

/// <summary>
/// Racine des données du serveur (réglage Donnees:Racine) : base des chemins par défaut (GED, journaux, clés, archives fiscales,
/// copie de secours). Sans réglage : C:\ProgramData\PharmaOfficine sous Windows (inchangé), /var/lib/pharmaofficine ailleurs.
/// </summary>
public static partial class DossiersDonnees
{
    public const string RacineHorsWindows = "/var/lib/pharmaofficine";

    public static string Racine(string? reglage) => Racine(reglage, OperatingSystem.IsWindows());

    public static string Racine(string? reglage, bool windows) =>
        !string.IsNullOrWhiteSpace(reglage) ? reglage
        : windows ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PharmaOfficine")
        : RacineHorsWindows;

    /// <summary>Valeur réglée gardée, sauf hors Windows un chemin Windows (« C:\… », « \\serveur\… ») : null, le défaut s'applique.</summary>
    public static string? CheminUtilisable(string? valeur) => CheminUtilisable(valeur, OperatingSystem.IsWindows());

    public static string? CheminUtilisable(string? valeur, bool windows) =>
        string.IsNullOrEmpty(valeur) ? null : windows || !EstCheminWindows(valeur) ? valeur : null;

    public static bool EstCheminWindows(string chemin) => CheminWindows().IsMatch(chemin);

    [GeneratedRegex(@"^([A-Za-z]:[\\/]|\\\\)")]
    private static partial Regex CheminWindows();
}
