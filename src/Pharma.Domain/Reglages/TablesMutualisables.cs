namespace Pharma.Domain.Reglages;

/// <summary>
/// Liste fermée des 11 tables de réglages du groupe 2 que le serveur central peut mutualiser (docs/conception-synchronisation.md § 16 bis).
/// Le groupe 3 (caisse, sauvegardes, notifications, rapports, serveurs, compteurs, intégrité) n'y figure jamais.
/// Même liste dans le script officine 141, le script central 010 et sync_table (test d'architecture du lot M8).
/// </summary>
public static class TablesMutualisables
{
    public const string Fiscal = "parametre_fiscal";
    public const string AlertesPsychotropes = "regle_alerte_psychotrope";
    public const string Tracabilite = "parametre_tracabilite";
    public const string Serialisation = "parametre_serialisation";
    public const string Substitution = "parametre_substitution";
    public const string Motifs = "motif_reglable";
    public const string FormulaireSms = "parametre_formulaire_sms";
    public const string Surveillance = "parametre_surveillance";
    public const string Alertes = "parametre_alerte";
    public const string Inventaire = "parametre_inventaire";
    public const string Remises = "remise_automatique";

    /// <summary>Préfixe de la clé de l'élément descendant : mutu_&lt;table&gt;.</summary>
    public const string PrefixeCle = "mutu_";

    public static readonly IReadOnlyList<string> Toutes =
    [
        Fiscal, AlertesPsychotropes, Tracabilite, Serialisation, Substitution, Motifs, FormulaireSms, Surveillance, Alertes, Inventaire, Remises
    ];

    public static bool Connue(string? table) => table is not null && Toutes.Contains(table);

    public static string Cle(string table) => PrefixeCle + table;
}
