namespace Pharma.Domain.Commun;

/// <summary>Contenu d'un Data Matrix GS1 : GTIN (01), péremption (17), lot (10), série (21).</summary>
public sealed record Gs1DataMatrix(string Gtin, DateOnly? Peremption, string? Lot, string? NumeroSerie)
{
    private const char Gs = (char)29; // séparateur des champs de longueur variable

    public static bool TryParse(string brut, out Gs1DataMatrix? resultat)
    {
        resultat = null;
        var s = brut.StartsWith("]d2", StringComparison.Ordinal) ? brut[3..] : brut; // préfixe du scanner
        s = s.TrimStart(Gs);
        string? gtin = null, lot = null, serie = null;
        DateOnly? peremption = null;
        var i = 0;

        while (i + 2 <= s.Length)
        {
            var ai = s.Substring(i, 2);
            i += 2;
            switch (ai)
            {
                case "01" when i + 14 <= s.Length:
                    gtin = s.Substring(i, 14); i += 14; break;
                case "17" when i + 6 <= s.Length:
                    peremption = LireDate(s.Substring(i, 6)); i += 6; break;
                case "10":
                    lot = LireVariable(s, ref i); break;
                case "21":
                    serie = LireVariable(s, ref i); break;
                default:
                    return false; // identifiant d'application non géré
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
        if (!int.TryParse(aammjj[..2], out var aa) ||
            !int.TryParse(aammjj[2..4], out var mm) ||
            !int.TryParse(aammjj[4..6], out var jj) || mm is < 1 or > 12)
            return null;
        var annee = 2000 + aa;
        var jour = jj == 0 ? DateTime.DaysInMonth(annee, mm) : jj;
        return jour <= DateTime.DaysInMonth(annee, mm) ? new DateOnly(annee, mm, jour) : null;
    }
}
