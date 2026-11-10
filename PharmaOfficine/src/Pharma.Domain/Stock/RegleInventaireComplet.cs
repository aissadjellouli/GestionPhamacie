using Pharma.Domain.Commun;

namespace Pharma.Domain.Stock;

/// <summary>Avancement d'un rayon dans l'inventaire complet. Statut : a_compter, en_cours ou termine.</summary>
public sealed record RayonCampagne(string Rayon, int NombreLots, long? InventaireId, string Statut, int EcartsAValider);

public static class RegleInventaireComplet
{
    /// <summary>Rayon des lots dont le produit n'a ni emplacement de vente ni réserve.</summary>
    public const string SansEmplacement = "(Sans emplacement)";

    /// <summary>ouverte : les ventes continuent ; fermee : la caisse refuse les ventes jusqu'à la clôture.</summary>
    public static string Mode(string? mode) => mode is "ouverte" or "fermee"
        ? mode
        : throw new RegleMetierException("Choisissez si la pharmacie reste ouverte ou fermée pendant l’inventaire.");

    /// <summary>Clôture : chaque rayon qui a du stock est compté et terminé, aucun gros écart n'attend le titulaire.</summary>
    public static void VerifierCloture(IReadOnlyList<RayonCampagne> rayons)
    {
        var restants = rayons.Where(r => r.NombreLots > 0 && r.Statut != "termine").Select(r => r.Rayon).ToList();
        if (restants.Count > 0)
            throw new RegleMetierException($"Rayon(s) encore à compter ou à terminer : {string.Join(", ", restants)}.");
        var ecarts = rayons.Sum(r => r.EcartsAValider);
        if (ecarts > 0)
            throw new RegleMetierException($"{ecarts} gros écart(s) attendent la décision du titulaire avant la clôture.");
    }
}
