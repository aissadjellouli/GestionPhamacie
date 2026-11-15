using Pharma.Domain.Commun;

namespace Pharma.Application.Caisse;

/// <summary>
/// Personne liée à une vente (2026-11-15, script 091) : fiche d'un patient (membre de la famille proposé en priorité) OU nom libre.
/// </summary>
public sealed record PersonneVente(long? PatientId, string? Nom);

/// <summary>
/// « Remis à » et « Pour » (règle du titulaire, 2026-11-15) : pas de dette familiale, la vente et sa dette restent au nom d'un seul patient.
/// Vente avec ordonnance : au nom du patient de l'ordonnance ; « Remis à » = la personne qui récupère les médicaments.
/// Vente sans ordonnance : au nom de celui qui passe la commande ; « Pour » = le membre de la famille concerné.
/// Psychotrope : le porteur saisi (registre) est repris en « Remis à » quand celui-ci n'est pas renseigné (pas de double saisie).
/// </summary>
public static class RemiseVente
{
    public const int LongueurMax = 200;

    /// <summary>Fiche choisie : seule la référence est gardée ; sinon nom libre nettoyé ; rien de saisi : null.</summary>
    public static PersonneVente? Normaliser(PersonneVente? p, string champ)
    {
        if (p is null) return null;
        if (p.PatientId is { } id) return new PersonneVente(id, null);
        var nom = p.Nom?.Trim();
        if (string.IsNullOrEmpty(nom)) return null;
        if (nom.Length > LongueurMax) throw new RegleMetierException($"« {champ} » : {LongueurMax} caractères au plus.");
        return new PersonneVente(null, nom);
    }

    public static (PersonneVente? RemisA, PersonneVente? Pour) Verifier(long? patientId, bool avecOrdonnance, PersonneVente? remisA, PersonneVente? pour,
        string? porteur = null)
    {
        remisA = Normaliser(remisA, "Remis à");
        pour = Normaliser(pour, "Pour");
        if (avecOrdonnance && pour is not null)
            throw new RegleMetierException("Vente avec ordonnance : elle est au nom du patient de l’ordonnance ; indiquez la personne qui récupère les médicaments dans « Remis à », pas « Pour ».", "pour");
        if (!avecOrdonnance && remisA is not null)
            throw new RegleMetierException("Vente sans ordonnance : elle est au nom de celui qui passe la commande ; indiquez le membre de la famille concerné dans « Pour », pas « Remis à ».", "remis_a");
        // Psychotrope : porteur déjà saisi, repris en « Remis à »
        if (avecOrdonnance && remisA is null && !string.IsNullOrWhiteSpace(porteur))
            remisA = Normaliser(new PersonneVente(null, porteur), "Remis à");
        if ((remisA ?? pour) is not null && patientId is null)
            throw new RegleMetierException("Choisissez d’abord le patient au nom de qui la vente est faite.");
        if (patientId is not null && (remisA?.PatientId == patientId || pour?.PatientId == patientId))
            throw new RegleMetierException("« Remis à » ou « Pour » : choisissez une autre personne que le patient de la vente.");
        return (remisA, pour);
    }

    /// <summary>Complément affiché après le nom du patient de la vente : « remis à Ali » ou « pour Karim » ; null sinon.</summary>
    public static string? Complement(string? remisA, string? pour) =>
        !string.IsNullOrWhiteSpace(remisA) ? $"remis à {remisA}" : !string.IsNullOrWhiteSpace(pour) ? $"pour {pour}" : null;

    /// <summary>« Karim · remis à Ali » ou « Ali · pour Karim » ; sans complément : le nom seul.</summary>
    public static string? Libelle(string? titulaire, string? remisA, string? pour) =>
        Complement(remisA, pour) is { } c ? (string.IsNullOrWhiteSpace(titulaire) ? c : $"{titulaire} · {c}") : titulaire;
}
