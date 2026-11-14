namespace Pharma.Domain.Ventes;

/// <summary>
/// Recherche d'un patient par téléphone (caisse et crédits clients, même règle) : par début de numéro, à partir de 4 chiffres ;
/// espaces et séparateurs ignorés ; « +213 » et « 00213 » valent « 0 ». Les numéros enregistrés sont normalisés de la même façon
/// (côté base : Pharma.Infrastructure, TelephoneSql).
/// </summary>
public static class RechercheTelephone
{
    public const int ChiffresMinimum = 4;

    /// <summary>
    /// Début de numéro à chercher (chiffres seuls, indicatif algérien ramené à 0), ou null si le texte n'est pas un numéro :
    /// lettres présentes, ou moins de 4 chiffres.
    /// </summary>
    public static string? Prefixe(string? texte)
    {
        if (string.IsNullOrWhiteSpace(texte)) return null;
        var t = texte.Trim();
        // Séparateurs admis : espace, point, tiret, barre oblique, parenthèses ; le « + » seulement en tête
        if (t.Any(c => !char.IsAsciiDigit(c) && c is not (' ' or '.' or '-' or '/' or '(' or ')' or '+')) || t.LastIndexOf('+') > 0)
            return null;
        var international = t.StartsWith('+');
        var chiffres = new string(t.Where(char.IsAsciiDigit).ToArray());
        if (international && chiffres.StartsWith("213")) chiffres = "0" + chiffres[3..];
        else if (!international && chiffres.StartsWith("00213")) chiffres = "0" + chiffres[5..];
        return chiffres.Length >= ChiffresMinimum ? chiffres : null;
    }
}
