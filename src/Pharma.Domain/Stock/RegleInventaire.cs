using Pharma.Domain.Commun;

namespace Pharma.Domain.Stock;

/// <summary>Sort d'une ligne comptée : conforme, ajustée tout de suite (sous les seuils) ou à valider par le titulaire.</summary>
public enum DecisionEcart { Conforme, Ajuste, AValider }

public sealed record RayonInventaire(string Rayon, DateTime? DernierInventaire, int NombreLots, int LotsDateAVerifier, long? EnCoursId);

public static class RegleInventaire
{
    public const int QuantiteMax = 100_000;

    public static void VerifierComptage(int compte)
    {
        if (compte is < 0 or > QuantiteMax)
            throw new RegleMetierException($"Quantité comptée entre 0 et {QuantiteMax}.");
    }

    /// <summary>Un écart reste sous les seuils si le nombre de boîtes ET la valeur au prix d'achat y restent.</summary>
    public static DecisionEcart Decider(int ecart, decimal valeurEcart, int seuilBoites, decimal seuilMontant) =>
        ecart == 0 ? DecisionEcart.Conforme
        : Math.Abs(ecart) <= seuilBoites && Math.Abs(valeurEcart) <= seuilMontant ? DecisionEcart.Ajuste
        : DecisionEcart.AValider;

    /// <summary>
    /// Rayon proposé aujourd'hui : d'abord ceux qui ont des lots « date à vérifier » (reprise),
    /// puis ceux jamais comptés, puis celui compté il y a le plus longtemps. Un rayon vide n'est pas proposé.
    /// </summary>
    public static RayonInventaire? Proposer(IEnumerable<RayonInventaire> rayons) =>
        rayons.Where(r => r.NombreLots > 0)
              .OrderByDescending(r => r.EnCoursId is not null)        // terminer d'abord un comptage commencé
              .ThenByDescending(r => r.LotsDateAVerifier > 0)
              .ThenBy(r => r.DernierInventaire ?? DateTime.MinValue)
              .ThenBy(r => r.Rayon, StringComparer.Ordinal)
              .FirstOrDefault();

    /// <summary>Une date de péremption confirmée ne peut pas être déjà passée (le lot serait à retirer, pas à recompter).</summary>
    public static void VerifierDate(DateOnly? dateVerifiee, DateOnly aujourdhui)
    {
        if (dateVerifiee is { } d && d < aujourdhui.AddYears(-1))
            throw new RegleMetierException("Date de péremption invraisemblable : vérifiez la boîte.");
    }
}
