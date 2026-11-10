using Pharma.Domain.Commun;

namespace Pharma.Domain.Alertes;

/// <summary>
/// Seuils de péremption sur trois niveaux : produit &gt; grossiste &gt; réglage général.
/// Un seuil vide reprend celui du niveau au-dessus ; le seuil rouge effectif doit rester plus court que l'orange.
/// </summary>
public static class RegleSeuils
{
    public static void VerifierGeneral(int orange, int rouge)
    {
        if (orange <= 0 || rouge <= 0)
            throw new RegleMetierException("Réglage général : les seuils orange et rouge doivent être d’au moins 1 jour.");
        if (rouge >= orange)
            throw new RegleMetierException($"Réglage général : l’alerte rouge ({rouge} j) doit être plus courte que l’orange ({orange} j).");
    }

    /// <summary>Seuils particuliers (grossiste ou produit) comparés une fois complétés par le niveau au-dessus.</summary>
    public static void VerifierParticulier(string nom, int? orange, int? rouge, int orangeHerite, int rougeHerite)
    {
        if (orange is <= 0 || rouge is <= 0)
            throw new RegleMetierException($"{nom} : un seuil doit être d’au moins 1 jour (ou laissé vide).");
        var o = orange ?? orangeHerite;
        var r = rouge ?? rougeHerite;
        if (r >= o)
            throw new RegleMetierException($"{nom} : l’alerte rouge effective ({r} j) doit être plus courte que l’orange ({o} j).");
    }
}
