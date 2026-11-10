namespace Pharma.Domain.Commun;

/// <summary>Violation d'une règle métier, avec un message lisible au comptoir.</summary>
public sealed class RegleMetierException(string message) : Exception(message);
