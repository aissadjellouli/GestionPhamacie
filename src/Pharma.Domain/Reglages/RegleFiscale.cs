using System.Text.RegularExpressions;
using Pharma.Domain.Commun;

namespace Pharma.Domain.Reglages;

/// <summary>
/// Réglage fiscal (table parametre_fiscal, ligne unique). Règles aujourd'hui portées par les contraintes SQL (ck_parametre_fiscal_*) :
/// conservation de 6 à 30 ans, format d'export fixe « csv-v1 », séparateur du CSV. Le fuseau est un identifiant de fuseau
/// (par exemple Africa/Algiers).
/// </summary>
public static partial class RegleFiscale
{
    public const int ConservationMin = 6;
    public const int ConservationMax = 30;
    public const string FormatCsvV1 = "csv-v1";
    public const int FuseauLongueurMax = 64;

    public static void VerifierConservation(int annees)
    {
        if (annees is < ConservationMin or > ConservationMax)
            throw new RegleMetierException($"Conservation des documents fiscaux : de {ConservationMin} à {ConservationMax} ans.", "conservation_annees");
    }

    public static void VerifierFormat(string? format)
    {
        if (format != FormatCsvV1)
            throw new RegleMetierException($"Format de l’export fiscal : seul « {FormatCsvV1} » est pris en charge.", "format_export_dgi");
    }

    public static void VerifierSeparateur(string? separateur)
    {
        if (separateur is not (";" or "," or "\t"))
            throw new RegleMetierException("Séparateur du fichier CSV : point-virgule, virgule ou tabulation.", "separateur_csv");
    }

    public static void VerifierFuseau(string? fuseau)
    {
        if (string.IsNullOrWhiteSpace(fuseau) || fuseau.Length > FuseauLongueurMax || !IdentifiantFuseau().IsMatch(fuseau))
            throw new RegleMetierException("Fuseau horaire : indiquez un identifiant de fuseau (par exemple Africa/Algiers), 64 caractères au plus.", "fuseau");
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_+\\-]*(/[A-Za-z0-9_+\\-]+){0,2}$")]
    private static partial Regex IdentifiantFuseau();
}
