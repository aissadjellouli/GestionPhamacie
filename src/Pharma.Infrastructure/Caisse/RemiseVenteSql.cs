namespace Pharma.Infrastructure.Caisse;

/// <summary>
/// « Remis à » / « Pour » d'une vente (script 091) en SQL, sur l'alias v de la table vente : nom de la fiche choisie, sinon nom libre.
/// </summary>
internal static class RemiseVenteSql
{
    public const string RemisA =
        "COALESCE((SELECT NULLIF(trim(concat_ws(' ', x.nom, x.prenom)), '') FROM patient x WHERE x.id = v.remis_a_patient_id), v.remis_a_nom)";

    public const string Pour =
        "COALESCE((SELECT NULLIF(trim(concat_ws(' ', x.nom, x.prenom)), '') FROM patient x WHERE x.id = v.pour_patient_id), v.pour_nom)";

    /// <summary>Nom du patient de la vente (alias v).</summary>
    public const string Titulaire =
        "(SELECT NULLIF(trim(concat_ws(' ', x.nom, x.prenom)), '') FROM patient x WHERE x.id = v.patient_id)";
}
