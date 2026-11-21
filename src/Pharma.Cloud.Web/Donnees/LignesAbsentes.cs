namespace Pharma.Cloud.Web.Donnees;

/// <summary>
/// Lot S7.5 : lignes absentes (reçues par le central, absentes de l'officine à la fin d'un chargement complet ; jamais effacées,
/// décision n° 8). Nombres seulement (script central 036) ; la cause probable vient de la base, dérivée de ce que le central sait
/// (restauration constatée, nouvelle époque), sinon « non expliquée ». Aucune anomalie n'est ouverte pour une cause non expliquée.
/// </summary>
public static class LignesAbsentes
{
    /// <summary>Clé de la rubrique de la fiche d'une officine (éditeur).</summary>
    public const string Rubrique = "absentes";

    public const string PiedDePage = "Les lignes absentes restent au central (jamais effacées) ; aucune ligne n'est montrée.";

    /// <summary>Causes rendues par cloud_lignes_absentes_groupes (liste fermée de la base).</summary>
    public static readonly IReadOnlyDictionary<string, string> Causes = new Dictionary<string, string>
    {
        ["restauration"] = "Restauration d'une sauvegarde",
        ["nouvelle_epoque"] = "Nouvelle époque (retour en arrière ou bascule)",
        ["non_expliquee"] = "Non expliquée",
    };

    /// <summary>Libellé court d'une cause (tuile du résumé).</summary>
    public static readonly IReadOnlyDictionary<string, string> CausesCourtes = new Dictionary<string, string>
    {
        ["restauration"] = "Restauration", ["nouvelle_epoque"] = "Nouvelle époque", ["non_expliquee"] = "Non expliquée",
    };

    /// <summary>Libellé d'une cause ; une valeur inconnue est montrée telle quelle (jamais devinée).</summary>
    public static string Cause(string? cause) => cause is null ? "—" : Causes.TryGetValue(cause, out var l) ? l : cause;

    public static string CauseCourte(string? cause) => cause is null ? "—" : CausesCourtes.TryGetValue(cause, out var l) ? l : cause;
}
