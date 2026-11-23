using System.Globalization;
namespace Pharma.Domain.Commun;

/// <summary>Donnée d'une boîte portée par un identifiant d'application GS1.</summary>
public enum ChampGs1 { Gtin, Peremption, Lot, Serie }

/// <summary>
/// Identifiant d'application GS1 lu dans un Data Matrix : code (2 à 4 chiffres), donnée portée, longueur fixe (null : longueur
/// variable, terminée par le séparateur GS ou la fin du code) et longueur maximale d'une donnée variable (null : sans limite).
/// </summary>
public sealed record Gs1Identifiant(string Code, ChampGs1 Champ, int? LongueurFixe, int? LongueurMax = null);

/// <summary>
/// Norme de lecture des Data Matrix (script 120, table parametre_gs1_ai) [À VÉRIFIER IANOR / GS1].
/// Par défaut : 01 GTIN (14), 17 péremption (6, AAMMJJ), 10 lot (variable), 21 série (variable), comme avant le script 120.
/// </summary>
public sealed class Gs1Norme
{
    public IReadOnlyList<Gs1Identifiant> Identifiants { get; }

    public Gs1Norme(IEnumerable<Gs1Identifiant> identifiants)
    {
        // Codes numériques de 2 à 4 chiffres, un seul par code ; une péremption se lit toujours sur 6 chiffres (AAMMJJ)
        Identifiants = identifiants
            .Where(i => i.Code.Length is >= 2 and <= 4 && i.Code.All(char.IsAsciiDigit))
            .Where(i => i.LongueurFixe is null or > 0 && i.LongueurMax is null or > 0)
            .Where(i => i.Champ != ChampGs1.Peremption || i.LongueurFixe == 6)
            .GroupBy(i => i.Code).Select(g => g.First())
            .OrderByDescending(i => i.Code.Length)
            .ToList();
    }

    public static Gs1Norme ParDefaut { get; } = new(
    [
        new("01", ChampGs1.Gtin, 14),
        new("17", ChampGs1.Peremption, 6),
        new("10", ChampGs1.Lot, null),
        new("21", ChampGs1.Serie, null)
    ]);

    private static Gs1Norme courante = ParDefaut;

    /// <summary>
    /// Norme utilisée par <see cref="Gs1DataMatrix.TryParse(string, out Gs1DataMatrix?)"/> : celle de la base, chargée par le serveur
    /// (tâche de sérialisation) ; la norme par défaut tant qu'elle n'est pas chargée ou si le paramétrage est vide.
    /// </summary>
    public static Gs1Norme Courante
    {
        get => Volatile.Read(ref courante);
        set => Volatile.Write(ref courante, value is { Identifiants.Count: > 0 } && value.Identifiants.Any(i => i.Champ == ChampGs1.Gtin) ? value : ParDefaut);
    }

    /// <summary>Identifiant qui commence à la position i (le plus long code d'abord), null s'il n'est pas paramétré.</summary>
    internal Gs1Identifiant? Trouver(string s, int i) =>
        Identifiants.FirstOrDefault(a => string.CompareOrdinal(s, i, a.Code, 0, a.Code.Length) == 0 && i + a.Code.Length <= s.Length);
}

/// <summary>Contenu d'un Data Matrix GS1 : GTIN (01), péremption (17), lot (10), série (21) ; identifiants paramétrables (Gs1Norme).</summary>
public sealed record Gs1DataMatrix(string Gtin, DateOnly? Peremption, string? Lot, string? NumeroSerie)
{
    private const char Gs = (char)29; // séparateur des champs de longueur variable

    public static bool TryParse(string brut, out Gs1DataMatrix? resultat) => TryParse(brut, Gs1Norme.Courante, out resultat);

    public static bool TryParse(string brut, Gs1Norme norme, out Gs1DataMatrix? resultat)
    {
        resultat = null;
        var s = brut.StartsWith("]d2", StringComparison.Ordinal) ? brut[3..] : brut; // préfixe du scanner
        s = s.TrimStart(Gs);
        string? gtin = null, lot = null, serie = null;
        DateOnly? peremption = null;
        var i = 0;

        while (i < s.Length)   // un caractère isolé en fin de code n'est pas un identifiant : code illisible
        {
            if (norme.Trouver(s, i) is not { } ai) return false; // identifiant d'application non géré
            i += ai.Code.Length;
            string valeur;
            if (ai.LongueurFixe is { } n)
            {
                if (i + n > s.Length) return false;
                valeur = s.Substring(i, n);
                i += n;
            }
            else
            {
                valeur = LireVariable(s, ref i);
                if (ai.LongueurMax is { } max && valeur.Length > max) return false;
            }
            switch (ai.Champ)
            {
                case ChampGs1.Gtin: gtin = valeur; break;
                case ChampGs1.Peremption:
                    peremption = LireDate(valeur);
                    if (peremption is null) return false;   // date impossible (mois 13, jour 31 d'un mois de 30 jours, signe…) : code illisible
                    break;
                case ChampGs1.Lot: lot = valeur; break;
                case ChampGs1.Serie: serie = valeur; break;
            }
        }

        if (gtin is null) return false;
        resultat = new Gs1DataMatrix(gtin, peremption, lot, serie);
        return true;
    }

    private static string LireVariable(string s, ref int i)
    {
        var fin = s.IndexOf(Gs, i);
        if (fin < 0) fin = s.Length;
        var valeur = s[i..fin];
        i = Math.Min(fin + 1, s.Length);
        return valeur;
    }

    // AAMMJJ ; un jour 00 signifie le dernier jour du mois
    private static DateOnly? LireDate(string aammjj)
    {
        const NumberStyles chiffres = NumberStyles.None;   // ni signe ni espace : seuls les chiffres comptent
        if (aammjj.Length != 6 ||
            !int.TryParse(aammjj[..2], chiffres, CultureInfo.InvariantCulture, out var aa) ||
            !int.TryParse(aammjj[2..4], chiffres, CultureInfo.InvariantCulture, out var mm) ||
            !int.TryParse(aammjj[4..6], chiffres, CultureInfo.InvariantCulture, out var jj) || mm is < 1 or > 12)
            return null;
        var annee = 2000 + aa;
        var jour = jj == 0 ? DateTime.DaysInMonth(annee, mm) : jj;
        return jour <= DateTime.DaysInMonth(annee, mm) ? new DateOnly(annee, mm, jour) : null;
    }
}
