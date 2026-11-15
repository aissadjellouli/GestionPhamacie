using System.Text.RegularExpressions;

namespace Pharma.Shared;

/// <summary>
/// Rang de l'assuré (script 102, 2026-11-15) : les 2 chiffres du suffixe « /NN » du n° d'assuré, comme la fonction SQL
/// rang_assure(num_assure). Null pour un n° vide, sans suffixe ou de format atypique (lettres-lettres-chiffre, etc.).
/// Sens du suffixe = rang de l'ayant droit : à confirmer sur cartes réelles (docs/chifa.md).
/// </summary>
public static partial class RangAssure
{
    [GeneratedRegex(@"/\s*([0-9]{2})\s*$")]
    private static partial Regex Suffixe();

    public static string? Extraire(string? numAssure) =>
        numAssure is not null && Suffixe().Match(numAssure) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>« rang NN » à afficher à côté du n° d'assuré, ou chaîne vide.</summary>
    public static string Libelle(string? numAssure) => Extraire(numAssure) is { } r ? $"rang {r}" : "";
}
