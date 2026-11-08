namespace Pharma.Domain.Ventes;

public class LigneVente
{
    public long Id { get; init; }
    public long VenteId { get; init; }
    public long ProduitId { get; init; }
    public long LotId { get; init; }
    public int Quantite { get; init; }
    public decimal PrixUnitaire { get; init; }
    public decimal BaseRemboursement { get; private set; }
    public decimal TauxRemboursement { get; private set; }
    public decimal MontantOrganisme { get; private set; }
    public decimal MontantPatient { get; private set; }

    /// <summary>Part de l'organisme = base de remboursement x taux, arrondie au dinar inférieur.</summary>
    public void CalculerTiersPayant(decimal baseRemboursement, decimal tauxPourcent)
    {
        BaseRemboursement = baseRemboursement;
        TauxRemboursement = tauxPourcent;
        var part = Math.Floor(Math.Min(baseRemboursement, PrixUnitaire) * Quantite * tauxPourcent / 100m);
        MontantOrganisme = part;
        MontantPatient = PrixUnitaire * Quantite - part;
    }

    /// <summary>Produit non remboursé (ou vente libre) : le patient paie tout.</summary>
    public void SansTiersPayant()
    {
        BaseRemboursement = 0;
        TauxRemboursement = 0;
        MontantOrganisme = 0;
        MontantPatient = PrixUnitaire * Quantite;
    }
}
