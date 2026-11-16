using Pharma.Domain.Commun;

namespace Pharma.Domain.Reglages;

/// <summary>Seuils de l'inventaire (table parametre_inventaire), extraits de ModifierParametresInventaire.</summary>
public static class RegleInventaire
{
    public static void VerifierSeuils(int seuilBoites, decimal seuilMontant)
    {
        if (seuilBoites < 0 || seuilMontant < 0) throw new RegleMetierException("Les seuils ne peuvent pas être négatifs.");
    }
}
