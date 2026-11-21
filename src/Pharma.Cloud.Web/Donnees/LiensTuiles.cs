namespace Pharma.Cloud.Web.Donnees;

/// <summary>Destination d'une tuile : adresse relative (balise base « / ») et infobulle qui dit où la tuile mène.</summary>
public sealed record LienTuile(string Adresse, string Infobulle);

/// <summary>
/// Tuiles navigables du central : chaque tuile mène à l'écran qui détaille son chiffre ; à défaut (le titulaire n'a droit qu'aux
/// totaux), à l'écran permis le plus proche. Jamais un écran de l'autre rôle : le titulaire reste sous mes-officines, l'éditeur
/// sous officines/{id} (règle pure, testée contre ControleAcces).
/// </summary>
public static class LiensTuiles
{
    /// <summary>Ancre du tableau de la rubrique « Lignes absentes » (fiche d'une officine, éditeur).</summary>
    public const string AncreAbsentes = "tableau-absentes";

    /// <summary>Tuiles du tableau de bord du titulaire (liste fermée, dans l'ordre de l'écran).</summary>
    public static readonly IReadOnlyList<string> TuilesTitulaire =
    [
        "ca_jour", "ventes_jour", "ca_mois", "ca_mois_precedent", "part_chifa_mois", "credits_en_cours", "alertes_stock", "alertes_peremption",
        "dernier_contact", "dernier_lot", "chargement_complet", "anomalies_ouvertes", "ordres_en_attente", "lignes_absentes",
    ];

    /// <summary>Tuiles de la rubrique « Lignes absentes » de l'éditeur.</summary>
    public static readonly IReadOnlyList<string> TuilesEditeur = ["total", "tables", "dernier_chargement", "cause"];

    private const string Ensemble = "mes-officines/ensemble";
    private const string MesOfficines = "mes-officines";

    /// <summary>
    /// Tuile du titulaire. Les totaux de ventes, de stock, d'intégrité et de lignes absentes mènent à la vue d'ensemble (même total,
    /// officine par officine, et total du groupe) ; la synchronisation, à « Mes officines » (état et dernier contact de chaque
    /// officine) ; les ordres, à l'écran des ordres si le droit « ordres » est accordé, sinon à la vue d'ensemble (colonne Ordres).
    /// </summary>
    public static LienTuile Titulaire(string tuile, Guid officine, IReadOnlyCollection<string> droits) => tuile switch
    {
        "ca_jour" => VueEnsemble("chiffre d'affaires du jour"),
        "ventes_jour" => VueEnsemble("ventes du jour"),
        "ca_mois" => VueEnsemble("chiffre d'affaires du mois"),
        "ca_mois_precedent" => VueEnsemble("chiffre d'affaires du mois précédent"),
        "part_chifa_mois" => VueEnsemble("part Chifa du mois"),
        "credits_en_cours" => VueEnsemble("crédits en cours"),
        "alertes_stock" => VueEnsemble("alertes de stock"),
        "alertes_peremption" => VueEnsemble("alertes de péremption"),
        "anomalies_ouvertes" => VueEnsemble("anomalies d'intégrité"),
        "lignes_absentes" => VueEnsemble("lignes absentes"),
        "dernier_contact" or "dernier_lot" or "chargement_complet" =>
            new(MesOfficines, "Ouvre « Mes officines » : état, dernier contact et retard de chaque officine"),
        "ordres_en_attente" when droits.Contains("ordres") =>
            new($"mes-officines/{officine}/ordres", "Ouvre les ordres à distance de cette officine : type et statut de chaque ordre"),
        "ordres_en_attente" => VueEnsemble("ordres en attente"),
        _ => throw new ArgumentOutOfRangeException(nameof(tuile), tuile, "Tuile du titulaire inconnue."),
    };

    /// <summary>
    /// Tuile de la rubrique « Lignes absentes » (éditeur). Total et tables : le tableau de la même rubrique (même page) ; dernier
    /// chargement : rubrique Chargements ; cause : rubrique Restaurations (restauration) ou Changements d'époque (nouvelle époque),
    /// sinon le tableau (cause non expliquée : aucune autre rubrique ne la détaille).
    /// </summary>
    public static LienTuile Editeur(string tuile, Guid officine, string? cause, int page)
    {
        var tableau = $"officines/{officine}?rubrique={LignesAbsentes.Rubrique}{(page > 1 ? $"&page={page}" : "")}#{AncreAbsentes}";
        return tuile switch
        {
            "total" => new(tableau, "Va au tableau détaillé : lignes absentes par table, avec leur cause probable"),
            "tables" => new(tableau, "Va au tableau : une ligne par table concernée"),
            "dernier_chargement" => new($"officines/{officine}?rubrique=chargements", "Ouvre la rubrique Chargements : 20 derniers chargements complets"),
            "cause" when cause == "restauration" => new($"officines/{officine}?rubrique=restaurations", "Ouvre la rubrique Restaurations : restaurations constatées"),
            "cause" when cause == "nouvelle_epoque" => new($"officines/{officine}?rubrique=changements", "Ouvre la rubrique Changements d'époque : changements déclarés par l'officine"),
            "cause" => new(tableau, "Va au tableau : cause probable de chaque groupe de lignes"),
            _ => throw new ArgumentOutOfRangeException(nameof(tuile), tuile, "Tuile de l'éditeur inconnue."),
        };
    }

    private static LienTuile VueEnsemble(string chiffre) =>
        new(Ensemble, $"Ouvre la vue d'ensemble : {chiffre} de chaque officine et total du groupe");
}
