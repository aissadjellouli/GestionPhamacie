using System.Globalization;
using System.Text;
using Pharma.Domain.Commun;

namespace Pharma.Domain.Catalogue;

public enum SituationNomenclature { EnVigueur, NonRenouvele, Retire }

/// <summary>Ligne de la nomenclature nationale des produits pharmaceutiques (fichier Excel du MIPH).</summary>
public sealed record LigneNomenclature(
    SituationNomenclature Situation, string? NumEnregistrement, string? Code, string? Dci, string? Marque,
    string? Forme, string? Dosage, string? Conditionnement, string? Liste, string? P1, string? P2, string? Obs,
    string? Laboratoire, string? Pays, DateOnly? DateInitiale, DateOnly? DateFinale, string? Type, string? Statut,
    string? Stabilite);

/// <summary>
/// Lecture des feuilles du fichier officiel : la ligne d'en-tête est cherchée sous les titres
/// (« N°ENREGISTREMENT », « CODE », « DENOMINATION COMMUNE INTERNATIONALE »…), les colonnes sont
/// reconnues par leur intitulé, pas par leur position, pour suivre les nouvelles versions du fichier.
/// </summary>
public static class Nomenclature
{
    private enum Champ
    {
        Num, Code, Dci, Marque, Forme, Dosage, Cond, Liste, P1, P2, Obs, Labo, Pays, DateInit, DateFin, Type, Statut, Stabilite
    }

    // Intitulé normalisé (majuscules, sans accents ni espaces) : début d'en-tête reconnu
    private static readonly (string Debut, Champ Champ)[] Entetes =
    [
        ("NENREGISTREMENT", Champ.Num), ("NUMENREGISTREMENT", Champ.Num), ("NDENREGISTREMENT", Champ.Num),
        ("CODE", Champ.Code), ("DENOMINATIONCOMMUNE", Champ.Dci), ("DCI", Champ.Dci), ("NOMDEMARQUE", Champ.Marque),
        ("FORME", Champ.Forme), ("DOSAGE", Champ.Dosage), ("CONDITIONNEMENT", Champ.Cond), ("COND", Champ.Cond),
        ("LISTE", Champ.Liste), ("P1", Champ.P1), ("P2", Champ.P2), ("OBS", Champ.Obs),
        ("LABORATOIRE", Champ.Labo), ("PAYS", Champ.Pays),
        ("DATEDENREGISTREMENTINITIAL", Champ.DateInit), ("DATEDENREGISTREMENTFINAL", Champ.DateFin),
        ("TYPE", Champ.Type), ("STATUT", Champ.Statut), ("DUREEDESTABILITE", Champ.Stabilite)
    ];

    /// <summary>Feuille « Retraits » → retiré ; « Non renouvelés » → non renouvelé ; sinon en vigueur.</summary>
    public static SituationNomenclature SituationDeFeuille(string feuille)
    {
        var n = Normaliser(feuille);
        return n.Contains("RETRAIT") || n.Contains("RETIRE") ? SituationNomenclature.Retire
            : n.Contains("NONRENOUVEL") ? SituationNomenclature.NonRenouvele
            : SituationNomenclature.EnVigueur;
    }

    /// <summary>Lignes d'une feuille (cellules en texte) ; vide si la feuille n'a pas d'en-tête de nomenclature.</summary>
    public static IReadOnlyList<LigneNomenclature> Lire(string feuille, IReadOnlyList<IReadOnlyList<string?>> cellules)
    {
        var situation = SituationDeFeuille(feuille);
        for (var i = 0; i < Math.Min(cellules.Count, 30); i++)
        {
            var colonnes = Colonnes(cellules[i]);
            if (!colonnes.ContainsKey(Champ.Num) || !colonnes.ContainsKey(Champ.Marque)) continue;

            var lignes = new List<LigneNomenclature>();
            foreach (var c in cellules.Skip(i + 1))
            {
                string? V(Champ ch) => colonnes.TryGetValue(ch, out var k) && k < c.Count && !string.IsNullOrWhiteSpace(c[k])
                    ? c[k]!.Trim() : null;
                if (V(Champ.Num) is null && V(Champ.Marque) is null) continue;   // ligne vide ou titre
                lignes.Add(new LigneNomenclature(situation, V(Champ.Num), V(Champ.Code), V(Champ.Dci), V(Champ.Marque),
                    V(Champ.Forme), V(Champ.Dosage), V(Champ.Cond), V(Champ.Liste), V(Champ.P1), V(Champ.P2), V(Champ.Obs),
                    V(Champ.Labo), V(Champ.Pays), LireDate(V(Champ.DateInit)), LireDate(V(Champ.DateFin)),
                    V(Champ.Type), V(Champ.Statut), V(Champ.Stabilite)));
            }
            return lignes;
        }
        return [];
    }

    /// <summary>Contrôle global : un fichier sans médicament en vigueur n'est pas une nomenclature.</summary>
    public static void Verifier(IReadOnlyCollection<LigneNomenclature> lignes)
    {
        if (lignes.Count(l => l.Situation == SituationNomenclature.EnVigueur) < 100)
            throw new RegleMetierException(
                "Ce fichier ne ressemble pas à la nomenclature nationale : aucune feuille avec les colonnes « N°ENREGISTREMENT » et « NOM DE MARQUE » et au moins 100 médicaments.");
    }

    /// <summary>Date Excel (nombre de jours depuis 1899-12-30) ou texte (jj/mm/aaaa, aaaa-mm-jj).</summary>
    public static DateOnly? LireDate(string? texte)
    {
        if (string.IsNullOrWhiteSpace(texte)) return null;
        var t = texte.Trim();
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var serie) && serie is > 1000 and < 80000)
            return DateOnly.FromDateTime(new DateTime(1899, 12, 30).AddDays(Math.Floor(serie)));
        string[] formats = ["dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "dd-MM-yyyy", "dd.MM.yyyy"];
        return DateTime.TryParseExact(t, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? DateOnly.FromDateTime(d) : null;
    }

    private static Dictionary<Champ, int> Colonnes(IReadOnlyList<string?> entete)
    {
        var colonnes = new Dictionary<Champ, int>();
        for (var k = 0; k < entete.Count; k++)
        {
            var n = Normaliser(entete[k]);
            if (n.Length == 0) continue;
            // Le plus long début reconnu l'emporte (« CODE » ne doit pas capter « CONDITIONNEMENT »)
            var trouve = Entetes.Where(e => n.StartsWith(e.Debut, StringComparison.Ordinal))
                                .OrderByDescending(e => e.Debut.Length).Select(e => (Champ?)e.Champ).FirstOrDefault();
            if (trouve is { } ch) colonnes.TryAdd(ch, k);
        }
        return colonnes;
    }

    private static string Normaliser(string? texte)
    {
        var sb = new StringBuilder();
        foreach (var c in (texte ?? "").Normalize(NormalizationForm.FormD))
            if (char.IsLetterOrDigit(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToUpperInvariant(c));
        return sb.ToString();
    }
}
