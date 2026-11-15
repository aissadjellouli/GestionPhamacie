using System.Globalization;

namespace Pharma.Domain.Ventes;

/// <summary>Ligne d'un volet Chifa : code produit Chifa, nombre de boîtes, prix unitaire (au centime).</summary>
public sealed record LigneQrChifa(string Code, int Quantite, decimal PrixUnitaire);

/// <summary>
/// QR code imprimé sur le volet d'une facture Chifa (CNAS). Format relevé sur un seul volet réel, à confirmer avec l'éditeur de Chifa :
/// « 06930126;11/11/2026;690755003842;80;24;2;09/11/2026;07607,3,156.48; » = n° de facture ; date de la facture ; n° d'assuré ;
/// suffixe du n° d'assuré (« /80 » sur le volet) ; deux champs de sens inconnu (gardés tels quels) ; une seconde date (sens à
/// confirmer) ; puis une ligne par produit « code,quantité,prix unitaire ».
/// </summary>
public sealed record QrChifa(
    string NumeroFacture, DateOnly DateFacture, string NumeroAssure, string SuffixeAssure, string Champ5, string Champ6,
    DateOnly? DateSecondaire, IReadOnlyList<LigneQrChifa> Lignes, string Brut)
{
    /// <summary>N° d'assuré tel qu'imprimé sur le volet : « 690755003842/80 ».</summary>
    public string AssureComplet => $"{NumeroAssure}/{SuffixeAssure}";
    /// <summary>Date de l'ordonnance : la seconde date du QR (7e zone), hypothèse à confirmer avec l'éditeur de Chifa (docs/chifa.md).</summary>
    public DateOnly? DateOrdonnance => DateSecondaire;
    public decimal MontantOrdonnance =>Lignes.Sum(l => l.Quantite * l.PrixUnitaire);

    public static bool TryParse(string? texte, out QrChifa? qr)
    {
        qr = null;
        if (string.IsNullOrWhiteSpace(texte)) return false;
        var brut = texte.Trim();
        var champs = brut.Split(';').Select(c => c.Trim()).ToList();
        while (champs.Count > 0 && champs[^1].Length == 0) champs.RemoveAt(champs.Count - 1);
        if (champs.Count < 8) return false;
        if (!Chiffres(champs[0], 4, 14) || !Date(champs[1], out var dateFacture) || !Chiffres(champs[2], 8, 16) || !Chiffres(champs[3], 1, 3))
            return false;
        DateOnly? seconde = Date(champs[6], out var d) ? d : null;
        if (seconde is null && champs[6].Length > 0) return false;

        var lignes = new List<LigneQrChifa>();
        foreach (var l in champs.Skip(7))
        {
            var p = l.Split(',');
            if (p.Length != 3 || !Chiffres(p[0], 1, 20) || !int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var qte) || qte < 1
                || !decimal.TryParse(p[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var prix) || prix < 0)
                return false;
            lignes.Add(new LigneQrChifa(p[0], qte, prix));
        }
        if (lignes.Count == 0) return false;
        qr = new QrChifa(champs[0], dateFacture, champs[2], champs[3], champs[4], champs[5], seconde, lignes, brut);
        return true;
    }

    private static bool Chiffres(string s, int min, int max) => s.Length >= min && s.Length <= max && s.All(char.IsAsciiDigit);

    private static bool Date(string s, out DateOnly d) =>
        DateOnly.TryParseExact(s, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
}
