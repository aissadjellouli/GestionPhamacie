using Pharma.Domain.Commun;

namespace Pharma.Domain.Reglages;

/// <summary>
/// Traçabilité (table parametre_tracabilite, ligne unique). Règle aujourd'hui portée par la contrainte SQL
/// parametre_tracabilite_patient_obligatoire_check : le patient est obligatoire jamais, pour les psychotropes, pour les ordonnances ou toujours.
/// </summary>
public static class RegleTracabilite
{
    public const string Jamais = "jamais";
    public const string Psychotrope = "psychotrope";
    public const string Ordonnance = "ordonnance";
    public const string Toujours = "toujours";

    public static readonly IReadOnlyList<string> PatientObligatoire = [Jamais, Psychotrope, Ordonnance, Toujours];

    public static void VerifierPatientObligatoire(string? valeur)
    {
        if (valeur is null || !PatientObligatoire.Contains(valeur))
            throw new RegleMetierException("Patient obligatoire : « jamais », « psychotrope », « ordonnance » ou « toujours ».", "patient_obligatoire");
    }
}
