using Pharma.Domain.Commun;

namespace Pharma.Domain.Reglages;

/// <summary>Seuils du contrôle interne (table parametre_surveillance) hors horaires d'ouverture, qui restent locaux (D7).</summary>
public sealed record SeuilsSurveillance(int SeuilLignesRetirees, int SeuilTiroir, int SeuilAttentesAbandonnees, int SeuilRemises,
    int SeuilRemisesPatient, int SeuilAjustements, int SeuilCasses, int SeuilEchantillons, int JoursComptageSensibles, bool ReceptionAveugle,
    int JoursAvoirRetour, decimal SeuilCreditNouveauPatient, int JoursPatientRecent, decimal SeuilCreditsJour, int JoursCreditSansRemboursement,
    int SeuilAutorisationsValideur, int SeuilAutorisationsEmploye, int MinutesPostesSimultanes, int SeuilChangementsPoste, int SeuilBaissesPrix,
    int SeuilSortiesTiroir);

/// <summary>Règles du contrôle interne, extraites de ReglerSurveillance (messages et ordre des contrôles inchangés).</summary>
public static class RegleSurveillance
{
    public static void VerifierSeuils(SeuilsSurveillance p)
    {
        if (p.SeuilLignesRetirees < 1 || p.SeuilTiroir < 1 || p.SeuilAttentesAbandonnees < 1 || p.SeuilRemises < 1 || p.SeuilRemisesPatient < 1
            || p.SeuilAjustements < 1 || p.SeuilCasses < 1 || p.SeuilEchantillons < 1 || p.SeuilAutorisationsValideur < 1
            || p.SeuilAutorisationsEmploye < 1 || p.SeuilChangementsPoste < 1 || p.SeuilBaissesPrix < 1 || p.SeuilSortiesTiroir < 1)
            throw new RegleMetierException("Chaque seuil doit être d’au moins 1.");
        if (p.SeuilCreditNouveauPatient <= 0 || p.SeuilCreditsJour <= 0)
            throw new RegleMetierException("Les montants de crédit surveillés doivent être supérieurs à 0.", "seuil_credit");
        if (p.JoursComptageSensibles is < 1 or > 90)
            throw new RegleMetierException("Comptage des produits sensibles : tous les 1 à 90 jours.", "jours_sensibles");
        if (p.JoursAvoirRetour is < 1 or > 365)
            throw new RegleMetierException("Retour sans avoir : délai de 1 à 365 jours.", "jours_avoir");
        if (p.JoursPatientRecent is < 1 or > 90)
            throw new RegleMetierException("Patient récent : de 1 à 90 jours.", "jours_patient_recent");
        if (p.JoursCreditSansRemboursement is < 1 or > 365)
            throw new RegleMetierException("Crédit jamais remboursé : délai de 1 à 365 jours.", "jours_credit");
        if (p.MinutesPostesSimultanes is < 1 or > 120)
            throw new RegleMetierException("Postes simultanés : de 1 à 120 minutes.", "minutes_postes");
    }

    /// <summary>Horaires d'ouverture : propres à chaque officine, jamais dans un modèle central.</summary>
    public static void VerifierHoraires(TimeOnly? ouverture, TimeOnly? fermeture)
    {
        if ((ouverture is null) != (fermeture is null))
            throw new RegleMetierException("Horaires : renseignez l’ouverture et la fermeture, ou aucune des deux (pas de contrôle).", "heure_ouverture");
        // Une plage peut passer minuit (ouverture 08:00, fermeture 00:30) ; ouverture et fermeture identiques n'ont pas de sens
        if (ouverture is { } o && fermeture is { } f && o == f)
            throw new RegleMetierException("Horaires : l’ouverture et la fermeture doivent être différentes.", "heure_ouverture");
    }
}
