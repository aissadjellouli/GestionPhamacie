namespace Pharma.Domain.Commun;

/// <summary>
/// Violation d'une règle métier, avec un message lisible au comptoir. Champ : nom du champ de saisie en cause, quand l'erreur
/// en vise un (« montant », « email », « motif »…) ; le poste met alors en rouge le champ qui porte data-champ="…" de ce nom
/// (docs/charte-graphique.md, « Erreurs de saisie »).
/// </summary>
public sealed class RegleMetierException(string message, string? champ = null) : Exception(message)
{
    public string? Champ { get; } = champ;
}
