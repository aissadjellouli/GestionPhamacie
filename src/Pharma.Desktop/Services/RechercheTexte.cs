using System.Globalization;
using System.Text;

namespace Pharma.Desktop.Services;

/// <summary>
/// Filtre d'un tableau à l'écran (règle des listes, docs/charte-graphique.md) : saisie comparée sans tenir compte des majuscules
/// ni des accents, sur une partie du texte. Même comparaison que TexteRecherche côté serveur.
/// </summary>
public static class RechercheTexte
{
    public static string Normaliser(string texte) =>
        string.Concat(texte.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark))
            .Normalize(NormalizationForm.FormC).ToLowerInvariant();

    /// <summary>Vrai si la saisie est vide, ou si l'un des textes la contient.</summary>
    public static bool Contient(string? saisie, params string?[] textes)
    {
        if (string.IsNullOrWhiteSpace(saisie)) return true;
        var cherche = Normaliser(saisie.Trim());
        return textes.Any(t => t is not null && Normaliser(t).Contains(cherche, StringComparison.Ordinal));
    }
}
