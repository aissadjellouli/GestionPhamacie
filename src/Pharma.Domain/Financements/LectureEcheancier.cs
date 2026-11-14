using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pharma.Domain.Commun;

namespace Pharma.Domain.Financements;

/// <summary>
/// Correspondance des colonnes d'un tableau d'échéances (Excel, CSV ou texte lu dans un PDF ou un scan) : n° de colonne (à partir
/// de 0) de chaque information, null quand le tableau ne la donne pas. LignesIgnorees : lignes d'en-tête en haut du tableau.
/// La date est obligatoire, et deux au moins parmi capital, intérêts et échéance totale (la troisième se calcule).
/// TVA, marge absentes : 0. Capital restant dû absent : calculé (montant du prêt moins le capital cumulé).
/// </summary>
public sealed record CorrespondanceColonnes(int Date, int? Capital, int? Interets, int? Tva, int? Marge, int? Montant, int? Encours, int LignesIgnorees = 0)
{
    public void Verifier()
    {
        int?[] colonnes = [Date, Capital, Interets, Tva, Marge, Montant, Encours];
        if (colonnes.Any(c => c < 0) || LignesIgnorees < 0) throw new RegleMetierException("Correspondance des colonnes invalide.", "correspondance");
        if (new[] { Capital, Interets, Montant }.Count(c => c is not null) < 2)
            throw new RegleMetierException("Indiquez au moins deux colonnes parmi le capital, les intérêts et l’échéance totale.", "correspondance");
        var choisies = colonnes.OfType<int>().ToList();
        if (choisies.Count != choisies.Distinct().Count())
            throw new RegleMetierException("Une même colonne est choisie pour deux informations.", "correspondance");
    }
}

/// <summary>
/// Échéancier lu : lignes reconnues (date lisible), n° de la ligne du tableau d'origine pour chacune, lignes écartées (sans date
/// lisible : en-têtes, totaux) et cellules illisibles (montant mis à 0, à corriger). Toujours vérifié avant l'enregistrement.
/// </summary>
public sealed record EcheancierLu(IReadOnlyList<LigneEcheancier> Lignes, IReadOnlyList<int> Origines, IReadOnlyList<string> Ecartees,
    IReadOnlyList<AnomalieEcheancier> Illisibles);

/// <summary>Lecture d'un tableau d'échéances fourni par une banque : texte des cellules → échéances, sans rien supposer du format.</summary>
public static partial class LectureEcheancier
{
    /// <summary>Applique la correspondance aux lignes du tableau (cellules en texte).</summary>
    public static EcheancierLu Appliquer(IReadOnlyList<IReadOnlyList<string?>> tableau, CorrespondanceColonnes c, decimal montantPret)
    {
        c.Verifier();
        var lues = new List<(int Origine, DateOnly Date, decimal? Capital, decimal? Interets, decimal Tva, decimal Marge, decimal? Montant, decimal? Encours)>();
        var ecartees = new List<string>();
        var illisibles = new List<AnomalieEcheancier>();
        for (var i = c.LignesIgnorees; i < tableau.Count; i++)
        {
            var ligne = tableau[i];
            if (ligne.All(string.IsNullOrWhiteSpace)) continue;
            string? Cellule(int? col) => col is { } k && k < ligne.Count ? ligne[k] : null;
            if (Date(Cellule(c.Date)) is not { } date)
            {
                ecartees.Add($"Ligne {i + 1} : {string.Join(" | ", ligne.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()))}");
                continue;
            }
            var n = lues.Count + 1;
            decimal? Valeur(int? col, string champ)
            {
                if (col is null) return null;
                var texte = Cellule(col);
                if (string.IsNullOrWhiteSpace(texte)) return 0;
                if (Montant(texte) is { } m) return m;
                illisibles.Add(new(n, champ, $"Ligne {n} : montant illisible « {texte.Trim()} » ({champ}), mis à 0 : corrigez-le."));
                return 0;
            }
            lues.Add((i + 1, date, Valeur(c.Capital, "capital"), Valeur(c.Interets, "interets"), Valeur(c.Tva, "tva") ?? 0, Valeur(c.Marge, "marge") ?? 0,
                Valeur(c.Montant, "montant"), Valeur(c.Encours, "capital_restant")));
        }

        // Information absente du tableau : calculée à partir des autres
        var lignes = lues.Select(l =>
        {
            var capital = l.Capital ?? (l.Montant ?? 0) - (l.Interets ?? 0) - l.Tva - l.Marge;
            var interets = l.Interets ?? (l.Montant ?? 0) - capital - l.Tva - l.Marge;
            var montant = l.Montant ?? capital + interets + l.Tva + l.Marge;
            return (l.Date, capital, interets, l.Tva, l.Marge, montant, l.Encours);
        }).ToList();
        var encours = RegleEcheancier.Encours(montantPret, lignes.Select(l => l.capital));
        return new EcheancierLu(
            lignes.Select((l, k) => new LigneEcheancier(l.Date, l.capital, l.interets, l.Tva, l.Marge, l.montant, l.Encours ?? encours[k])).ToList(),
            lues.Select(l => l.Origine).ToList(), ecartees, illisibles);
    }

    /// <summary>
    /// Montant écrit par une banque ou un tableur : séparateurs de milliers (espace, espace insécable, point ou virgule), virgule ou
    /// point décimal (le dernier des deux quand les deux sont présents), « DA » ou « DZD », signe moins. Arrondi au centime.
    /// Null si le texte n'est pas un montant.
    /// </summary>
    public static decimal? Montant(string? texte)
    {
        if (string.IsNullOrWhiteSpace(texte)) return null;
        var t = new StringBuilder();
        foreach (var ch in texte.Trim())
            if (!char.IsWhiteSpace(ch) && ch != ' ' && ch != ' ' && ch != '\'') t.Append(ch);
        var s = MonnaieRegex().Replace(t.ToString(), "");
        var negatif = s.StartsWith('-') || (s.StartsWith('(') && s.EndsWith(')'));
        s = s.Trim('-', '(', ')', '+');
        if (s.Length == 0 || !ChiffresRegex().IsMatch(s)) return null;
        var virgule = s.LastIndexOf(',');
        var point = s.LastIndexOf('.');
        string normalise;
        if (virgule >= 0 && point >= 0)
        {
            var dec = Math.Max(virgule, point);
            normalise = s[..dec].Replace(",", "").Replace(".", "") + "." + s[(dec + 1)..];
        }
        else if (virgule >= 0)
            // Une seule virgule suivie d'1 ou 2 chiffres : décimale ; sinon séparateur de milliers
            normalise = s.Count(x => x == ',') == 1 && s.Length - virgule - 1 is 1 or 2 ? s.Replace(',', '.') : s.Replace(",", "");
        else if (point >= 0)
            // Plusieurs points : milliers (1.234.567) ; un seul : point décimal (valeur d'un tableur)
            normalise = s.Count(x => x == '.') > 1 ? s.Replace(".", "") : s;
        else normalise = s;
        if (!decimal.TryParse(normalise, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)) return null;
        return Arrondi.Centime(negatif ? -v : v);
    }

    /// <summary>
    /// Date écrite jj/mm/aaaa (ou avec - ou .), jj/mm/aa, aaaa-mm-jj, ou numéro de jour d'Excel (cellule de date sans format).
    /// Null si le texte n'est pas une date.
    /// </summary>
    public static DateOnly? Date(string? texte)
    {
        if (string.IsNullOrWhiteSpace(texte)) return null;
        var t = texte.Trim();
        var iso = DateIsoRegex().Match(t);
        if (iso.Success) return Construire(int.Parse(iso.Groups[1].Value), int.Parse(iso.Groups[2].Value), int.Parse(iso.Groups[3].Value));
        var fr = DateFrRegex().Match(t);
        if (fr.Success)
        {
            var annee = int.Parse(fr.Groups[3].Value);
            if (fr.Groups[3].Value.Length == 2) annee += 2000;
            return Construire(annee, int.Parse(fr.Groups[2].Value), int.Parse(fr.Groups[1].Value));
        }
        // Excel : jours depuis le 30/12/1899 (cellule date lue sans son format) ; bornes : 1954 à 2119
        if (double.TryParse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var serie) && serie is >= 20000 and <= 80000
            && serie == Math.Floor(serie))
            return DateOnly.FromDateTime(new DateTime(1899, 12, 30).AddDays(serie));
        return null;
    }

    private static DateOnly? Construire(int annee, int mois, int jour) =>
        annee is >= 1900 and <= 2200 && mois is >= 1 and <= 12 && jour >= 1 && jour <= DateTime.DaysInMonth(annee, mois) ? new DateOnly(annee, mois, jour) : null;

    /// <summary>
    /// Ligne de texte lue dans un PDF ou un scan, découpée en cellules : dates, montants (avec leurs espaces de milliers),
    /// et le reste du texte. Découpage approximatif (un « 1 000 » peut être lu « 1 » et « 000 ») : l'aperçu est toujours vérifié.
    /// </summary>
    public static IReadOnlyList<string> Cellules(string ligne)
    {
        var cellules = new List<string>();
        var texte = new StringBuilder();
        foreach (Match m in JetonRegex().Matches(ligne))
        {
            if (m.Groups["date"].Success || m.Groups["nombre"].Success)
            {
                if (texte.ToString().Trim() is { Length: > 0 } avant) cellules.Add(avant);
                texte.Clear();
                cellules.Add(m.Value.Trim());
            }
            else texte.Append(m.Value);
        }
        if (texte.ToString().Trim() is { Length: > 0 } fin) cellules.Add(fin);
        return cellules;
    }

    /// <summary>Fichier CSV (texte déjà décodé) : séparateur point-virgule, tabulation ou virgule (le plus fréquent de la première ligne), guillemets.</summary>
    public static IReadOnlyList<IReadOnlyList<string?>> Csv(string texte)
    {
        var lignes = texte.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var premiere = lignes.FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        var separateur = new[] { ';', '\t', ',' }.OrderByDescending(s => premiere.Count(x => x == s)).First();
        var tableau = new List<IReadOnlyList<string?>>();
        foreach (var ligne in lignes)
        {
            if (ligne.Trim().Length == 0) continue;
            var cellules = new List<string?>();
            var courante = new StringBuilder();
            var guillemets = false;
            for (var i = 0; i < ligne.Length; i++)
            {
                var ch = ligne[i];
                if (ch == '"')
                {
                    if (guillemets && i + 1 < ligne.Length && ligne[i + 1] == '"') { courante.Append('"'); i++; }
                    else guillemets = !guillemets;
                }
                else if (ch == separateur && !guillemets) { cellules.Add(courante.ToString()); courante.Clear(); }
                else courante.Append(ch);
            }
            cellules.Add(courante.ToString());
            tableau.Add(cellules);
        }
        return tableau;
    }

    [GeneratedRegex(@"(?i)(DZD|DA|د\.ج)$")] private static partial Regex MonnaieRegex();
    [GeneratedRegex(@"^[0-9.,]*[0-9][0-9.,]*$")] private static partial Regex ChiffresRegex();
    [GeneratedRegex(@"^(\d{4})-(\d{1,2})-(\d{1,2})")] private static partial Regex DateIsoRegex();
    [GeneratedRegex(@"^(\d{1,2})[/.\-](\d{1,2})[/.\-](\d{4}|\d{2})$")] private static partial Regex DateFrRegex();
    [GeneratedRegex(@"(?<date>\b\d{1,2}[/.\-]\d{1,2}[/.\-](?:\d{4}|\d{2})\b)|(?<nombre>-?\b\d{1,3}(?:[   .]\d{3})+(?:,\d{1,2})?\b|-?\b\d+(?:[.,]\d{1,2})?\b)|(?<texte>[^\d]+|\d)")]
    private static partial Regex JetonRegex();
}
