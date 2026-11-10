using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;
using Pharma.Domain.Stock;

namespace Pharma.Domain.Ventes;

public enum TypeVente
{
    Libre,
    TiersPayant
}

public enum StatutVente
{
    EnCours,
    Validee,
    Annulee
}

public class Vente
{
    private readonly List<LigneVente> _lignes = [];
    private bool _sousRegistre;

    public long Id { get; init; }
    public string Numero { get; init; } = "";
    public DateTime DateVente { get; init; }
    public long UtilisateurId { get; init; }
    public long? SessionCaisseId { get; init; }
    public long? PatientId { get; init; }
    public long? OrdonnanceId { get; init; }
    public TypeVente Type { get; init; } = TypeVente.Libre;
    public long? OrganismeId { get; init; }
    /// <summary>Taux choisi au comptoir en tiers payant (80 ou 100 %).</summary>
    public decimal? TauxTiersPayant { get; init; }

    /// <summary>Montant de la vente, remise déduite.</summary>
    public decimal MontantTotal { get; private set; }
    public decimal PartOrganisme { get; private set; }
    /// <summary>Ce que paie le patient, remise déduite.</summary>
    public decimal PartPatient { get; private set; }
    public decimal RemisePct { get; private set; }
    public decimal MontantRemise { get; private set; }
    public StatutVente Statut { get; private set; } = StatutVente.EnCours;
    public string? NumFactureChifa { get; init; }
    public IReadOnlyList<LigneVente> Lignes => _lignes;

    /// <summary>Vrai si la vente contient un psychotrope ou un stupéfiant (registre, ordonnance).</summary>
    public bool ContientSousRegistre => _sousRegistre;

    public void AjouterLigne(LigneVente ligne)
    {
        _lignes.Add(ligne);
        RecalculerTotaux();
    }

    /// <summary>
    /// Ajoute des boîtes d'un lot précis : lot vendable, quantité disponible (lignes déjà saisies comprises),
    /// part de l'organisme en tiers payant pour un produit remboursable.
    /// </summary>
    public LigneVente AjouterProduit(Produit produit, Lot lot, int quantite, DateOnly aujourdhui)
    {
        if (quantite <= 0)
            throw new RegleMetierException("La quantité doit être d’au moins une boîte.");
        if (lot.ProduitId != produit.Id)
            throw new ArgumentException("Ce lot n'appartient pas à ce produit.", nameof(lot));

        lot.VerifierVendable(aujourdhui);
        var demandee = _lignes.Where(l => l.LotId == lot.Id).Sum(l => l.Quantite) + quantite;
        if (demandee > lot.QuantiteDisponible)
            throw new RegleMetierException(
                $"{produit.NomCommercial}, lot {lot.NumeroLot} : {lot.QuantiteDisponible} boîte(s) disponible(s), {demandee} demandée(s).");

        var ligne = new LigneVente { ProduitId = produit.Id, LotId = lot.Id, Quantite = quantite, PrixUnitaire = produit.PrixPublic };
        if (Type == TypeVente.TiersPayant && produit.Remboursable && produit.TarifReference is { } tarif)
            ligne.CalculerTiersPayant(tarif, TauxTiersPayant ?? 0);
        else
            ligne.SansTiersPayant();

        _sousRegistre |= produit.EstSousRegistre;
        _lignes.Add(ligne);
        RecalculerTotaux();
        return ligne;
    }

    /// <summary>Remise en pourcentage sur la part du patient, plafonnée par la remise maximale du rôle.</summary>
    public void AppliquerRemise(decimal pourcentage, decimal remiseMaxPct)
    {
        if (pourcentage is < 0 or > 100)
            throw new RegleMetierException("La remise doit être comprise entre 0 et 100 %.");
        if (pourcentage > remiseMaxPct)
            throw new RegleMetierException($"Remise de {pourcentage:0.##} % refusée : votre maximum est de {remiseMaxPct:0.##} %.");
        RemisePct = pourcentage;
        RecalculerTotaux();
    }

    public void RecalculerTotaux()
    {
        var brut = _lignes.Sum(l => l.PrixUnitaire * l.Quantite);
        PartOrganisme = _lignes.Sum(l => l.MontantOrganisme);
        var partPatientBrute = brut - PartOrganisme;
        MontantRemise = Math.Round(partPatientBrute * RemisePct / 100m, 2, MidpointRounding.AwayFromZero);
        PartPatient = partPatientBrute - MontantRemise;
        MontantTotal = brut - MontantRemise;
    }

    /// <summary>Contrôles avant l'encaissement (mêmes règles que les contraintes et déclencheurs en base).</summary>
    public void VerifierAvantEncaissement(bool ordonnanceSaisie)
    {
        if (_lignes.Count == 0)
            throw new RegleMetierException("Le panier est vide.");
        if (Type == TypeVente.TiersPayant)
        {
            if (PatientId is null || OrganismeId is null)
                throw new RegleMetierException("Tiers payant : l’assuré et l’organisme (CNAS, CASNOS) sont obligatoires.");
            if (TauxTiersPayant is not (> 0 and <= 100))
                throw new RegleMetierException("Tiers payant : choisissez le taux de remboursement.");
        }
        if (_sousRegistre)
        {
            if (!ordonnanceSaisie)
                throw new RegleMetierException("Psychotrope ou stupéfiant : l’ordonnance est obligatoire.");
            if (PatientId is null)
                throw new RegleMetierException("Psychotrope ou stupéfiant : le patient doit être identifié.");
        }
    }

    public void Valider() => Statut = StatutVente.Validee;
}
