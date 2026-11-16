using Pharma.Domain.Commun;

namespace Pharma.Domain.Reglages;

/// <summary>
/// Règles d'alerte du registre des psychotropes (table regle_alerte_psychotrope, 5 lignes à code fixe) : seuil de 1 à 1000, période de 1 à 3650
/// jours, unités par jour de 1 à 100 ou vide. Extrait de ParametrerAlertesPsychotropes (messages inchangés).
/// </summary>
public static class RegleAlertesPsychotropes
{
    public const string PatientRepete = "patient_repete";
    public const string OrdonnanceReutilisee = "ordonnance_reutilisee";
    public const string QuantiteAnormale = "quantite_anormale";
    public const string PrescripteurInhabituel = "prescripteur_inhabituel";
    /// <summary>Wilaya du médecin connue et hors de la zone de l'officine (script 118).</summary>
    public const string PrescripteurHorsZone = "prescripteur_hors_zone";

    public static readonly IReadOnlySet<string> Codes = new HashSet<string>
        { PatientRepete, OrdonnanceReutilisee, QuantiteAnormale, PrescripteurInhabituel, PrescripteurHorsZone };

    /// <summary>Nom lisible d'une règle dans les messages d'erreur d'un modèle (le libellé local n'est pas transmis).</summary>
    public static string Nom(string code) => code switch
    {
        PatientRepete => "Patient revenu trop souvent",
        OrdonnanceReutilisee => "Ordonnance réutilisée",
        QuantiteAnormale => "Quantité anormale",
        PrescripteurInhabituel => "Prescripteur inhabituel",
        PrescripteurHorsZone => "Prescripteur hors zone",
        _ => code
    };

    public static void VerifierRegle(string code, string libelle, int seuil, int periodeJours, int? unitesParJour)
    {
        if (!Codes.Contains(code)) throw new RegleMetierException($"Règle inconnue : {code}.");
        if (seuil is < 1 or > 1000) throw new RegleMetierException($"{libelle} : seuil entre 1 et 1000.", code);
        if (periodeJours is < 1 or > 3650) throw new RegleMetierException($"{libelle} : période entre 1 et 3650 jours.", code);
        if (unitesParJour is < 1 or > 100) throw new RegleMetierException($"{libelle} : unités par jour entre 1 et 100 (ou vide).", code);
    }
}
