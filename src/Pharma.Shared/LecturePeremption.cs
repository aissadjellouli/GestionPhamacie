using System.Globalization;

namespace Pharma.Shared;

/// <summary>
/// Péremption saisie à l'écran (réception, comptage, correction d'une entrée de stock) : MM/AA ou MM/AAAA, comme sur la boîte,
/// devient le dernier jour du mois (02/28 → 29/02/2028) ; JJ/MM/AAAA (ou AAAA-MM-JJ) reste accepté tel quel.
/// Séparateurs acceptés : « / », « - », « . » ou espace. Saisie illisible : null.
/// </summary>
public static class LecturePeremption
{
    public static DateOnly? Lire(string? saisie)
    {
        var s = saisie?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso)) return iso;
        var parts = s.Split(['/', '-', '.', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => !p.All(char.IsAsciiDigit))) return null;
        switch (parts.Length)
        {
            case 2:
            {
                var (mois, annee) = (int.Parse(parts[0], CultureInfo.InvariantCulture), Annee(parts[1]));
                if (annee is null || mois is < 1 or > 12) return null;
                return new DateOnly(annee.Value, mois, DateTime.DaysInMonth(annee.Value, mois));
            }
            case 3:
            {
                var (jour, mois, annee) = (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), Annee(parts[2]));
                if (annee is null || mois is < 1 or > 12 || jour < 1 || jour > DateTime.DaysInMonth(annee.Value, mois)) return null;
                return new DateOnly(annee.Value, mois, jour);
            }
            default:
                return null;
        }
    }

    /// <summary>AA : 20AA ; AAAA tel quel.</summary>
    private static int? Annee(string a) => a.Length switch
    {
        2 => 2000 + int.Parse(a, CultureInfo.InvariantCulture),
        4 when int.Parse(a, CultureInfo.InvariantCulture) is var n and >= 2000 and <= 2999 => n,
        _ => null
    };
}
