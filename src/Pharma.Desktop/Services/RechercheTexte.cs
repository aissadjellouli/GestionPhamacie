using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Pharma.Desktop.Services;

/// <summary>
/// Filtre d'un tableau à l'écran (règle des listes, docs/charte-graphique.md) : saisie comparée sans tenir compte des majuscules
/// ni des accents, sur une partie du texte. Même comparaison que TexteRecherche côté serveur.
/// Les écrans relisent leur liste filtrée plusieurs fois par affichage (tableau, « aucune ligne », pagination) et à chaque frappe :
/// les textes déjà normalisés sont gardés en mémoire, et un texte sans accent ni caractère spécial évite la décomposition Unicode.
/// </summary>
public static class RechercheTexte
{
    private const int TailleCache = 20_000;
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    public static string Normaliser(string texte)
    {
        if (Ascii.IsValid(texte)) return texte.ToLowerInvariant();
        if (Cache.TryGetValue(texte, out var connu)) return connu;
        var normalise = string.Concat(texte.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark))
            .Normalize(NormalizationForm.FormC).ToLowerInvariant();
        if (Cache.Count >= TailleCache) Cache.Clear();   // mémoire bornée : on repart à vide plutôt que de grossir sans fin
        Cache[texte] = normalise;
        return normalise;
    }

    /// <summary>Vrai si la saisie est vide, ou si l'un des textes la contient.</summary>
    public static bool Contient(string? saisie, params string?[] textes)
    {
        if (string.IsNullOrWhiteSpace(saisie)) return true;
        var cherche = Normaliser(saisie.Trim());
        foreach (var t in textes)
            if (t is not null && Normaliser(t).Contains(cherche, StringComparison.Ordinal)) return true;
        return false;
    }
}
