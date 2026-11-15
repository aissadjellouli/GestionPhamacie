using System.Text.RegularExpressions;

namespace Pharma.Domain.Ventes;

/// <summary>
/// N° de facture Chifa (2026-11-15, script 101) : exactement 8 chiffres, comme le contrôle ck_vente_chifa_facture_format
/// de vente.chifa_facture (seule colonne du n° de facture Chifa).
/// </summary>
public static partial class FormatFactureChifa
{
    public const int Longueur = 8;

    [GeneratedRegex("^[0-9]{8}$")]
    private static partial Regex HuitChiffres();

    public static bool EstValide(string? numero) => numero is not null && HuitChiffres().IsMatch(numero);
}
