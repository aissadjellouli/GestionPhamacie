namespace Pharma.Application.Securite;

/// <summary>
/// Employé qui a autorisé sur place, avec son code comptoir, l'action de la requête en cours (faute de droit de l'employé connecté).
/// Le journal des actions l'inscrit dans « autorisé par ».
/// </summary>
public sealed class AutorisationCourante
{
    public long? AutoriseParId { get; set; }
    public string? AutoriseParNom { get; set; }
}
