using System.Globalization;

namespace Pharma.Cloud.Web.Donnees;

/// <summary>Total d'un indicateur sur le groupe : somme des valeurs disponibles, nombre d'officines comptées et d'officines sans valeur.</summary>
public sealed record TotalGroupe(decimal? Somme, int Comptees, int NonDisponibles)
{
    /// <summary>Au moins une officine sans valeur : le total ne porte que sur les autres (jamais un « non disponible » compté comme 0).</summary>
    public bool Partiel => NonDisponibles > 0 && Somme is not null;
}

/// <summary>
/// Lot S7.3 : vue d'ensemble du titulaire (« Mes officines » côte à côte, cloud_ecran_titulaire_ensemble). Les lignes viennent de la
/// base, une par officine du compte (droit « consulter ») ; le total du groupe est calculé ici. Règle : une valeur NULL de la base
/// (non disponible) n'est JAMAIS additionnée comme 0 ; si une officine manque, le total devient « partiel » avec le nombre
/// d'officines comptées ; si aucune n'a la valeur, le total est « non disponible ».
/// </summary>
public static class VueEnsemble
{
    /// <summary>Indicateurs additionnés (colonnes de cloud_ecran_titulaire_tableau) : montants puis nombres.</summary>
    public static readonly IReadOnlyList<string> Montants = ["ca_jour", "ca_mois", "ca_mois_precedent", "part_chifa_mois", "credits_en_cours"];
    public static readonly IReadOnlyList<string> Nombres = ["ventes_jour", "ventes_mois", "alertes_stock", "alertes_peremption", "anomalies_ouvertes", "ordres_en_attente"];

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Total du groupe pour une colonne, sur TOUTES les lignes (pas seulement la page affichée).</summary>
    public static TotalGroupe Total(TableEcran table, string colonne)
    {
        decimal? somme = null;
        int comptees = 0, absentes = 0;
        foreach (var ligne in table.Lignes)
        {
            if (Valeur(table.Valeur(ligne, colonne)) is { } v) { somme = (somme ?? 0) + v; comptees++; }
            else absentes++;
        }
        return new TotalGroupe(somme, comptees, absentes);
    }

    /// <summary>Texte du total : montant (ou nombre), « partiel (n sur m officines) », ou « non disponible ».</summary>
    public static string Texte(TotalGroupe t, bool montant)
    {
        if (t.Somme is not { } s) return Tableau.NonDisponible;
        var valeur = montant ? s.ToString("N2", Fr) + " DA" : s.ToString("N0", Fr);
        return t.Partiel ? $"{valeur} (partiel : {t.Comptees} sur {t.Comptees + t.NonDisponibles} officines)" : valeur;
    }

    private static decimal? Valeur(object? v) => v switch
    {
        decimal d => d,
        int i => i,
        long l => l,
        short s => s,
        _ => null,
    };
}
