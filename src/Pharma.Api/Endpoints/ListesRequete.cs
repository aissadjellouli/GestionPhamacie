using Pharma.Domain.Commun;

namespace Pharma.Api.Endpoints;

/// <summary>
/// Liste (ou dictionnaire) attendue dans le corps d'une requête : absente (null), la requête est refusée avec un message clair
/// au lieu d'une erreur interne.
/// </summary>
internal static class ListesRequete
{
    public static T Requise<T>(this T? liste) where T : class =>
        liste ?? throw new RegleMetierException("Requête incomplète : liste manquante.");
}
