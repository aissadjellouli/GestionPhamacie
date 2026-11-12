using System.Globalization;
using System.Text;

namespace Pharma.Domain.Commun;

/// <summary>
/// Accord automatique des « (s) » d'un texte selon le nombre qui précède : 0 ou 1 (et tout nombre inférieur à 2) → singulier,
/// 2 et plus → pluriel. « 3 lot(s) » → « 3 lots », « 1 boîte(s) ajoutée(s) » → « 1 boîte ajoutée ». Le nombre vaut jusqu'à la
/// prochaine ponctuation (, ; : . · ( ) ou le prochain nombre ; un « (s) » sans nombre devant est laissé tel quel.
/// Même règle sur le poste (wwwroot/js/poste.js, pharma.accord) pour tout le texte affiché.
/// </summary>
public static class Accord
{
    private const string Marque = "(s)";

    public static string Texte(string? texte) => Accorder(texte, null).Texte;

    /// <summary>Accorde le texte ; <paramref name="nombre"/> : nombre en cours venu du texte qui précède (null s'il n'y en a pas).</summary>
    public static (string Texte, decimal? Nombre) Accorder(string? texte, decimal? nombre)
    {
        if (string.IsNullOrEmpty(texte)) return (texte ?? "", nombre);
        var sortie = new StringBuilder(texte.Length);
        var i = 0;
        while (i < texte.Length)
        {
            var c = texte[i];
            if (char.IsDigit(c))
            {
                // Nombre : chiffres, séparateurs de milliers (espace, espace insécable, espace fine) et décimales (« , » ou « . » suivi d'un chiffre)
                var debut = i;
                var chiffres = new StringBuilder();
                while (i < texte.Length)
                {
                    var d = texte[i];
                    if (char.IsDigit(d)) { chiffres.Append(d); i++; }
                    else if (d is ' ' or ' ' or ' ' && i + 1 < texte.Length && char.IsDigit(texte[i + 1]) && chiffres.Length > 0) i++;
                    else if (d is ',' or '.' && i + 1 < texte.Length && char.IsDigit(texte[i + 1]) && !chiffres.ToString().Contains('.')) { chiffres.Append('.'); i++; }
                    else break;
                }
                sortie.Append(texte, debut, i - debut);
                nombre = decimal.TryParse(chiffres.ToString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) ? v : null;
                continue;
            }
            if (string.CompareOrdinal(texte, i, Marque, 0, Marque.Length) == 0 && i > 0 && char.IsLetter(texte[i - 1]))
            {
                if (nombre is { } n) { if (n >= 2) sortie.Append('s'); }
                else sortie.Append(Marque);
                i += Marque.Length;
                continue;
            }
            if (c is ',' or ';' or ':' or '.' or '·' or '(' or ')' or '\n' or '!' or '?') nombre = null;
            sortie.Append(c);
            i++;
        }
        return (sortie.ToString(), nombre);
    }
}
