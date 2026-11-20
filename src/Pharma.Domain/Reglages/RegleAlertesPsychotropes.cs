using Pharma.Domain.Commun;

namespace Pharma.Domain.Reglages;

/// <summary>
/// Règles d'alerte du registre des psychotropes (table regle_alerte_psychotrope, 6 lignes à code fixe) : seuil de 1 à 1000, période de 1 à 3650
/// jours, unités par jour de 1 à 100 ou vide. Période vide (« sans limite ») acceptée seulement pour « ordonnance réutilisée » et
/// « traitement en cours » (script 153). Extrait de ParametrerAlertesPsychotropes (messages inchangés).
/// </summary>
public static class RegleAlertesPsychotropes
{
    public const string PatientRepete = "patient_repete";
    public const string OrdonnanceReutilisee = "ordonnance_reutilisee";
    public const string QuantiteAnormale = "quantite_anormale";
    public const string PrescripteurInhabituel = "prescripteur_inhabituel";
    /// <summary>Wilaya du médecin connue et hors de la zone de l'officine (script 118).</summary>
    public const string PrescripteurHorsZone = "prescripteur_hors_zone";
    /// <summary>Patient encore sous traitement par un psychotrope délivré avant (date + durée prescrite), script 153.</summary>
    public const string TraitementEnCours = "traitement_en_cours";

    public static readonly IReadOnlySet<string> Codes = new HashSet<string>
        { PatientRepete, OrdonnanceReutilisee, QuantiteAnormale, PrescripteurInhabituel, PrescripteurHorsZone, TraitementEnCours };

    /// <summary>Règles dont la période peut être vide : « sans limite » (ordonnance réutilisée) ou sans objet (traitement en cours).</summary>
    public static readonly IReadOnlySet<string> PeriodeFacultative = new HashSet<string> { OrdonnanceReutilisee, TraitementEnCours };

    /// <summary>
    /// Règles ajoutées après la première version du format des réglages partagés : un modèle du serveur central qui ne les contient pas
    /// reste valable (la règle locale est gardée telle quelle).
    /// </summary>
    public static readonly IReadOnlySet<string> AjouteesApresFormat = new HashSet<string> { TraitementEnCours };

    /// <summary>Nom lisible d'une règle dans les messages d'erreur d'un modèle (le libellé local n'est pas transmis).</summary>
    public static string Nom(string code) => code switch
    {
        PatientRepete => "Patient revenu trop souvent",
        OrdonnanceReutilisee => "Ordonnance réutilisée",
        QuantiteAnormale => "Quantité anormale",
        PrescripteurInhabituel => "Prescripteur inhabituel",
        PrescripteurHorsZone => "Prescripteur hors zone",
        TraitementEnCours => "Patient encore sous traitement",
        _ => code
    };

    public static void VerifierRegle(string code, string libelle, int seuil, int? periodeJours, int? unitesParJour)
    {
        if (!Codes.Contains(code)) throw new RegleMetierException($"Règle inconnue : {code}.");
        if (seuil is < 1 or > 1000) throw new RegleMetierException($"{libelle} : seuil entre 1 et 1000.", code);
        if (periodeJours is null && !PeriodeFacultative.Contains(code))
            throw new RegleMetierException($"{libelle} : période obligatoire (1 à 3650 jours).", code);
        if (periodeJours is < 1 or > 3650) throw new RegleMetierException($"{libelle} : période entre 1 et 3650 jours.", code);
        if (unitesParJour is < 1 or > 100) throw new RegleMetierException($"{libelle} : unités par jour entre 1 et 100 (ou vide).", code);
    }
}
