namespace Pharma.Application.Commun;

/// <summary>Recherche dans les listes (règle des listes, docs/charte-graphique.md) : comparaison sans majuscules ni accents.</summary>
public static class TexteRecherche
{
    public static string Normaliser(string texte)
    {
        var decompose = texte.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(decompose.Length);
        foreach (var c in decompose)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();
    }

    /// <summary>Vrai si l'un des textes contient la recherche (vide : tout répond).</summary>
    public static bool Contient(string? recherche, params string?[] textes)
    {
        if (string.IsNullOrWhiteSpace(recherche)) return true;
        var r = Normaliser(recherche.Trim());
        return textes.Any(t => t is not null && Normaliser(t).Contains(r, StringComparison.Ordinal));
    }
}
