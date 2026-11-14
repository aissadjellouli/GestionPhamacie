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
    /// <summary>Produit de la catégorie « médicament » : sa réduction est un don partiel du reste à charge, pas une remise.</summary>
    public bool Medicament { get; init; }
    /// <summary>Produit remboursable : jamais de remise automatique.</summary>
    public bool Remboursable { get; init; }
    /// <summary>Remise automatique (%) du produit ou de sa catégorie, en vente libre seulement ; 0 sinon.</summary>
    public decimal RemiseAutoPct { get; private set; }
    /// <summary>Réduction due à la remise automatique, en DA (remise, ou don partiel pour un médicament).</summary>
    public decimal ReductionAuto { get; private set; }
    /// <summary>Réduction due à la remise saisie par le vendeur, en DA (remise, ou don partiel pour un médicament).</summary>
    public decimal ReductionManuelle { get; private set; }
    public decimal Reduction => ReductionAuto + ReductionManuelle;
    /// <summary>Pourcentage appliqué à la ligne (automatique, plus la remise du vendeur si elle s'ajoute), au plus 100.</summary>
    public decimal PctApplique { get; private set; }
    public decimal BaseRemboursement { get; private set; }
    public decimal TauxRemboursement { get; private set; }
    public decimal MontantOrganisme { get; private set; }
    public decimal MontantPatient { get; private set; }

    /// <summary>Part de l'organisme = base de remboursement x taux, arrondie au centime (comme les factures Chifa : 1 160,82 x 80 % = 928,66).</summary>
    public void CalculerTiersPayant(decimal baseRemboursement, decimal tauxPourcent)
    {
        BaseRemboursement = baseRemboursement;
        TauxRemboursement = tauxPourcent;
        var part = Math.Round(Math.Min(baseRemboursement, PrixUnitaire) * Quantite * tauxPourcent / 100m, 2, MidpointRounding.AwayFromZero);
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

    internal void FixerRemiseAuto(decimal pct) => RemiseAutoPct = Remboursable ? 0 : Math.Clamp(pct, 0, 100);

    /// <summary>
    /// Réductions de la ligne, sur la part du patient hors SHP (le SHP n'est jamais réduit) : remise automatique, puis remise du vendeur
    /// si la ligne n'a pas de remise automatique ou si le vendeur choisit de l'additionner (somme des pourcentages, au plus 100 %).
    /// </summary>
    internal void CalculerReductions(decimal manuellePct, bool additionner)
    {
        var baseReduction = Math.Max(MontantPatient - Math.Min(Shp * Quantite, MontantPatient), 0);
        var manuelle = RemiseAutoPct == 0 || additionner ? manuellePct : 0;
        PctApplique = Math.Min(RemiseAutoPct + manuelle, 100);
        ReductionAuto = Math.Round(baseReduction * RemiseAutoPct / 100m, 2, MidpointRounding.AwayFromZero);
        ReductionManuelle = Math.Round(baseReduction * PctApplique / 100m, 2, MidpointRounding.AwayFromZero) - ReductionAuto;
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
