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

    /// <summary>
    /// Réglage général complet (table parametre_alerte) : orange et rouge, puis risque d'écoulement, stock visé, délai « dormant »,
    /// seuils de lot délaissé, délai de livraison et stock de sécurité. Ordre et messages repris de EnregistrerSeuils.
    /// </summary>
    public static void VerifierReglageGeneral(int joursOrange, int joursRouge, int horizonRisqueJours, int delaiFournisseurJours, int joursSecurite,
        int joursCibleCommande, int joursDormant, int seuilLotDelaisse, int seuilLotDelaisseCollectif)
    {
        VerifierGeneral(joursOrange, joursRouge);
        if (horizonRisqueJours <= 0 || joursCibleCommande <= 0 || joursDormant <= 0)
            throw new RegleMetierException("Le risque d’écoulement, le stock visé et le délai « dormant » doivent être d’au moins 1 jour.");
        if (seuilLotDelaisse < 1 || seuilLotDelaisseCollectif < 1)
            throw new RegleMetierException("Ancien lot délaissé : les seuils doivent être d’au moins 1 boîte.", "seuil_lot_delaisse");
        if (delaiFournisseurJours < 0 || joursSecurite < 0)
            throw new RegleMetierException("Le délai de livraison et le stock de sécurité ne peuvent pas être négatifs.");
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
