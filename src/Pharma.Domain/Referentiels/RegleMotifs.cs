using System.Text.RegularExpressions;
using Pharma.Domain.Commun;

namespace Pharma.Domain.Referentiels;

/// <summary>
/// Motif d'une liste réglable (table motif_reglable, script 094). Tiroir (script 096) : Effet (aucun, sortie, entree) et Detail exigé
/// (aucun, depense, fournisseur, patient, coffre, fond), voir <see cref="Caisse.RegleTiroir"/>.
/// </summary>
public sealed record MotifReglable(long Id, string Liste, string Code, string Libelle, bool Actif, bool Systeme, int Ordre,
    string Effet = Caisse.RegleTiroir.Aucun, string Detail = Caisse.RegleTiroir.Aucun);

/// <summary>
/// Motifs et catégories réglables par le titulaire (Administration › Motifs et catégories) : ajout, libellé, désactivation ;
/// jamais de suppression. « Autre » demande un texte. Sorties de stock et retours : codes fixes (contraintes de la base),
/// seuls le libellé et l'activation se règlent.
/// </summary>
public static partial class RegleMotifs
{
    public const string Tiroir = "tiroir";
    public const string Attente = "attente";
    public const string Charge = "charge";
    public const string SortieStock = "sortie_stock";
    public const string RetourFournisseur = "retour_fournisseur";
    /// <summary>Vente « Facturée par Chifa » encaissée sans n° de facture Chifa (script 097) : motif obligatoire.</summary>
    public const string VoletAbsent = "volet_absent";
    /// <summary>Motif d'exclusion d'une vente Chifa du traitement (script 100) ; « Autre » avec un texte.</summary>
    public const string ChifaExclusion = "chifa_exclusion";
    /// <summary>Types de majoration Chifa (script 100, D-3).</summary>
    public const string MajorationChifa = "majoration_chifa";
    public const string Autre = "autre";
    /// <summary>Mise en attente pour un produit manquant : les produits dus au patient sont gardés avec la vente en attente.</summary>
    public const string ProduitManquant = "produit_manquant";
    public const int TexteMax = 200;

    public static readonly IReadOnlyList<(string Code, string Libelle)> Listes =
    [
        (Tiroir, "Ouverture du tiroir sans vente"), (Attente, "Mise en attente d’une vente"), (Charge, "Catégories de dépenses"),
        (SortieStock, "Sorties de stock"), (RetourFournisseur, "Retours au fournisseur"),
        (VoletAbsent, "Vente Chifa sans n° de facture"), (ChifaExclusion, "Exclusion d’une vente Chifa"),
        (MajorationChifa, "Types de majoration Chifa")
    ];

    public static bool ListeConnue(string? liste) => Listes.Any(l => l.Code == liste);

    /// <summary>Ajout de motifs : tiroir, attente et charges ; pas les sorties ni les retours (codes fixés par la base).</summary>
    public static bool AjoutPermis(string liste) => liste is Tiroir or Attente or Charge or VoletAbsent or ChifaExclusion or MajorationChifa;

    public static string ExigerListe(string? liste) =>
        ListeConnue(liste) ? liste! : throw new RegleMetierException("Liste de motifs inconnue.", "liste");

    public static string VerifierLibelle(string? libelle)
    {
        var l = libelle?.Trim() ?? "";
        if (l.Length == 0) throw new RegleMetierException("Indiquez le libellé.", "libelle");
        if (l.Length > 80) throw new RegleMetierException("Libellé : 80 caractères au plus.", "libelle");
        return l;
    }

    /// <summary>Code d'un nouveau motif tiré du libellé (minuscules sans accent, « _ ») ; suffixe numérique s'il est déjà pris.</summary>
    public static string CodeDepuisLibelle(string libelle, IEnumerable<string> existants)
    {
        var decompose = libelle.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var sansAccent = new string(decompose.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
            != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
        var baseCode = NonAlphanumerique().Replace(sansAccent, "_").Trim('_');
        if (baseCode.Length == 0) baseCode = "motif";
        if (baseCode.Length > 34) baseCode = baseCode[..34].TrimEnd('_');
        var pris = existants.ToHashSet();
        var code = baseCode;
        for (var i = 2; pris.Contains(code); i++) code = $"{baseCode}_{i}";
        return code;
    }

    /// <summary>Désactivation : jamais d'un motif système (« Autre », périmé…).</summary>
    public static void VerifierModification(MotifReglable existant, bool actif)
    {
        if (existant.Systeme && !actif)
            throw new RegleMetierException($"« {existant.Libelle} » est indispensable : il ne peut pas être désactivé.", "actif");
    }

    /// <summary>
    /// Motif choisi : obligatoire, parmi les motifs actifs de la liste ; « Autre » avec un texte. Rend le texte à inscrire
    /// (libellé, ou « Autre : … ») et le texte précisé (null hors « Autre »).
    /// </summary>
    public static (string Code, string Libelle, string? Texte) Choisir(IEnumerable<MotifReglable> actifs, string? code, string? texte, string quoi)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new RegleMetierException($"Choisissez le motif {quoi}.", "motif");
        var m = actifs.FirstOrDefault(x => x.Code == code.Trim() && x.Actif)
            ?? throw new RegleMetierException("Ce motif n’existe pas ou a été désactivé : choisissez-en un autre.", "motif");
        var t = string.IsNullOrWhiteSpace(texte) ? null : texte.Trim();
        if (m.Code == Autre && t is null) throw new RegleMetierException("Motif « Autre » : précisez le motif.", "motif_texte");
        if (t is { Length: > TexteMax }) throw new RegleMetierException($"Motif : {TexteMax} caractères au plus.", "motif_texte");
        if (m.Code != Autre) t = null;
        return (m.Code, t is null ? m.Libelle : $"Autre : {t}", t);
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumerique();
}
