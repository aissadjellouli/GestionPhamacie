namespace Pharma.Domain.Ventes;

public class LigneVente
{
    public long Id { get; init; }
    public long VenteId { get; init; }
    public long ProduitId { get; init; }
    public long LotId { get; init; }
    public int Quantite { get; init; }
    /// <summary>Prix de vente d'une boîte : PPA du lot (ou prix du catalogue) plus le SHP.</summary>
    public decimal PrixUnitaire { get; init; }
    /// <summary>SHP par boîte, compris dans PrixUnitaire.</summary>
    public decimal Shp { get; init; }
    /// <summary>Lot plus récent servi alors qu'un lot plus ancien du produit est en stock : pourquoi (RaisonLot), sinon null.</summary>
    public string? RaisonLot { get; set; }
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

    /// <summary>Reste à charge saisi par le caissier : part de l'organisme imposée, taux effectif recalculé (taux choisi ignoré).</summary>
    public void ImposerPartOrganisme(decimal part)
    {
        var montant = PrixUnitaire * Quantite;
        BaseRemboursement = PrixUnitaire;
        MontantOrganisme = part;
        MontantPatient = montant - part;
        TauxRemboursement = montant > 0 ? Math.Clamp(Math.Round(part / montant * 100m, 2, MidpointRounding.AwayFromZero), 0, 100) : 0;
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
