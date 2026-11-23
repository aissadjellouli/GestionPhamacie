using System.Text.RegularExpressions;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>
/// Garde-fou de nom : toute création, suppression ou connexion de test passe par <see cref="Verifier"/>. Seules les bases
/// « pharmacie_jetable_sql_&lt;aaaammjjhhmmss&gt;_&lt;etiquette&gt;_&lt;8 car.&gt; » sont acceptées ; pharmacie_dev, pharmacie_test, pharmacie_cloud_dev
/// et toute autre base sont refusées.
/// </summary>
internal static partial class NomsJetables
{
    public const string Prefixe = "pharmacie_jetable_sql_";

    public static string Verifier(string nom)
    {
        if (!NomValide().IsMatch(nom ?? ""))
            throw new InvalidOperationException($"Nom de base refusé par le garde-fou des tests SQL : « {nom} » (attendu : {Prefixe}<horodatage>_<etiquette>_<8 car.>).");
        return nom!;
    }

    public static string Nouveau(DateTime suiteUtc, string etiquette) =>
        Verifier($"{Prefixe}{suiteUtc:yyyyMMddHHmmss}_{etiquette}_{Guid.NewGuid().ToString("N")[..8]}");

    /// <summary>Horodatage de la suite qui a créé la base, ou null si le nom n'est pas au format des bases jetables.</summary>
    public static DateTime? Horodatage(string nom)
    {
        var m = NomValide().Match(nom);
        return m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMddHHmmss", null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }

    [GeneratedRegex(@"^pharmacie_jetable_sql_(\d{14})_(modele|copie)_[0-9a-f]{8}$")]
    private static partial Regex NomValide();
}
