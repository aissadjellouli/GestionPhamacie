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

/// <summary>Prix de vente d'une boîte : PPA du lot (prix de la vignette) ou, à défaut, prix public du catalogue, plus le SHP.</summary>
public static class PrixVente
{
    public static decimal Public(Produit produit, Lot lot) => lot.Ppa ?? produit.PrixPublic;
    public static decimal Unitaire(Produit produit, Lot lot) => Public(produit, lot) + produit.ShpParBoite;
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
    /// <summary>Tiers payant : reste à charge du patient saisi par le caissier (le taux n'est pas appliqué).</summary>
    public decimal? ResteSaisi { get; private set; }
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
    /// <param name="autoriserBloque">Vrai quand la vente d'un produit bloqué est validée par une personne habilitée (contrôlé par l'encaissement).</param>
    public LigneVente AjouterProduit(Produit produit, Lot lot, int quantite, DateOnly aujourdhui, bool autoriserBloque = false)
    {
        if (quantite <= 0)
            throw new RegleMetierException("La quantité doit être d’au moins une boîte.");
        if (lot.ProduitId != produit.Id)
            throw new ArgumentException("Ce lot n'appartient pas à ce produit.", nameof(lot));

        if (produit.BloqueVente && !autoriserBloque)
            throw new RegleMetierException($"{produit.NomCommercial} : produit bloqué à la vente{(produit.MotifBlocage is { } m ? $" ({m})" : "")}. Retirez-le du panier.");
        lot.VerifierVendable(aujourdhui);
        var demandee = _lignes.Where(l => l.LotId == lot.Id).Sum(l => l.Quantite) + quantite;
        if (demandee > lot.QuantiteDisponible)
            throw new RegleMetierException(
                $"{produit.NomCommercial}, lot {lot.NumeroLot} : {lot.QuantiteDisponible} boîte(s) disponible(s), {demandee} demandée(s).");

        var ligne = new LigneVente
        {
            ProduitId = produit.Id, LotId = lot.Id, Quantite = quantite,
            PrixUnitaire = PrixVente.Unitaire(produit, lot), Shp = produit.ShpParBoite
        };
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

    /// <summary>
    /// Tiers payant : le caissier fixe le reste à charge du patient. La part de l'organisme est la différence, répartie sur les
    /// lignes au prorata de leur montant (le taux de prise en charge choisi n'est pas appliqué).
    /// </summary>
    public void SaisirResteACharge(decimal reste)
    {
        if (Type != TypeVente.TiersPayant)
            throw new RegleMetierException("Le reste à charge ne se saisit qu’en tiers payant.", "reste_a_charge");
        var brut = _lignes.Sum(l => l.PrixUnitaire * l.Quantite);
        if (reste < 0 || reste > brut)
            throw new RegleMetierException($"Le reste à charge doit être compris entre 0 et {brut:N2} DA (montant de la vente).", "reste_a_charge");
        ResteSaisi = Math.Round(reste, 2, MidpointRounding.AwayFromZero);
        RecalculerTotaux();
    }

    public void RecalculerTotaux()
    {
        var brut = _lignes.Sum(l => l.PrixUnitaire * l.Quantite);
        if (ResteSaisi is { } reste && brut > 0)
        {
            var organisme = brut - Math.Min(reste, brut);
            var reparti = 0m;
            for (var i = 0; i < _lignes.Count; i++)
            {
                var montant = _lignes[i].PrixUnitaire * _lignes[i].Quantite;
                var part = i == _lignes.Count - 1 ? organisme - reparti : Math.Round(organisme * montant / brut, 2, MidpointRounding.AwayFromZero);
                _lignes[i].ImposerPartOrganisme(part);
                reparti += part;
            }
        }
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
            if (ResteSaisi is null && TauxTiersPayant is not (> 0 and <= 100))
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
