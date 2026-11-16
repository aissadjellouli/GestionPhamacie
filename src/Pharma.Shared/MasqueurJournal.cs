using System.Text.RegularExpressions;

namespace Pharma.Shared;

/// <summary>
/// Filet de sécurité des fichiers journaux (serveur et poste) : masque les données personnelles reconnaissables
/// qui auraient échappé aux appels de journalisation (message ou pile d'exception).
/// E-mail → a***@d***.dz ; téléphone algérien (05/06/07…, +213, 00213) → 06******12 ; n° d'assuré (9 à 12 chiffres,
/// suffixe /NN facultatif) → 6907******42/80. Les noms et textes libres ne sont pas reconnaissables : ils ne doivent
/// tout simplement pas être journalisés (identifiants techniques à la place).
/// </summary>
public static partial class MasqueurJournal
{
    [GeneratedRegex(@"\b([A-Za-z0-9])[A-Za-z0-9._%+-]*@([A-Za-z0-9])[A-Za-z0-9-]*(?:\.[A-Za-z0-9-]+)*(\.[A-Za-z]{2,})\b")]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<![\d+])(\+213|00213)[\s.-]?([5-7])((?:[\s.-]?\d){8})(?!\d)")]
    private static partial Regex TelephoneInternational();

    [GeneratedRegex(@"(?<!\d)0([5-7])((?:[\s.-]?\d){8})(?!\d)")]
    private static partial Regex TelephoneNational();

    [GeneratedRegex(@"(?<!\d)(\d{4})(\d{3,6})(\d{2})(/\d{1,3})?(?!\d)")]
    private static partial Regex NumeroAssure();

    /// <summary>Texte à écrire au journal, données personnelles reconnaissables masquées.</summary>
    public static string Masquer(string? texte)
    {
        if (string.IsNullOrEmpty(texte)) return texte ?? "";
        var r = Email().Replace(texte, m => $"{m.Groups[1].Value}***@{m.Groups[2].Value}***{m.Groups[3].Value}");
        r = TelephoneInternational().Replace(r, m => $"{m.Groups[1].Value}{m.Groups[2].Value}******{DeuxDerniers(m.Groups[3].Value)}");
        r = TelephoneNational().Replace(r, m => $"0{m.Groups[1].Value}******{DeuxDerniers(m.Groups[2].Value)}");
        r = NumeroAssure().Replace(r, m => $"{m.Groups[1].Value}******{m.Groups[3].Value}{m.Groups[4].Value}");
        return r;
    }

    /// <summary>Exception complète (type, message, pile, exceptions internes) masquée.</summary>
    public static string Masquer(Exception? exception) => exception is null ? "" : Masquer(exception.ToString());

    private static string DeuxDerniers(string chiffres)
    {
        var seuls = new string(chiffres.Where(char.IsDigit).ToArray());
        return seuls[^2..];
    }
}
