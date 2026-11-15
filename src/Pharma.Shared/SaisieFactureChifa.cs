using System.Text.RegularExpressions;

namespace Pharma.Shared;

/// <summary>Saisie du n° de facture Chifa au poste (2026-11-15, script 101) : 8 chiffres exactement, contrôlés aussi par le serveur.</summary>
public static partial class SaisieFactureChifa
{
    [GeneratedRegex("^[0-9]{8}$")]
    private static partial Regex HuitChiffres();

    /// <summary>Vrai si le n° saisi (espaces retirés) compte exactement 8 chiffres.</summary>
    public static bool EstValide(string? saisie) => saisie is not null && HuitChiffres().IsMatch(saisie.Trim());

    public const string Message = "N° de facture Chifa : 8 chiffres exactement.";
}
