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
    private bool _surOrdonnance;

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
    /// <summary>Remise saisie par le vendeur (%), sur toute la vente.</summary>
    public decimal RemisePct { get; private set; }
    /// <summary>Le vendeur additionne sa remise aux remises automatiques des lignes qui en ont (sinon elle ne vaut que pour les autres lignes).</summary>
    public bool RemiseAdditionnee { get; private set; }
    /// <summary>Remises accordées sur les produits hors médicaments (automatiques et du vendeur), en DA.</summary>
    public decimal MontantRemise { get; private set; }
    /// <summary>Don partiel du reste à charge : réductions sur les médicaments (automatiques et du vendeur), en DA.</summary>
    public decimal MontantDon { get; private set; }
    /// <summary>Part des réductions due aux remises automatiques (remises et dons), en DA.</summary>
    public decimal MontantReductionsAuto => _lignes.Sum(l => l.ReductionAuto);
    /// <summary>Tiers payant : reste à charge du patient saisi par le caissier (le taux n'est pas appliqué).</summary>
    public decimal? ResteSaisi { get; private set; }
    public StatutVente Statut { get; private set; } = StatutVente.EnCours;
    public string? NumFactureChifa { get; init; }
    public IReadOnlyList<LigneVente> Lignes => _lignes;

    /// <summary>Vrai si la vente contient un psychotrope ou un stupéfiant (registre, ordonnance).</summary>
    public bool ContientSousRegistre => _sousRegistre;
    /// <summary>Le panier contient un médicament de liste I ou II.</summary>
    public bool ContientSurOrdonnance => _surOrdonnance;

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
    /// <param name="controlerLot">
    /// Faux seulement pour recalculer les montants d'une vente déjà enregistrée (même panier renvoyé) : son stock a déjà été sorti,
    /// le lot n'est pas contrôlé.
    /// </param>
    public LigneVente AjouterProduit(Produit produit, Lot lot, int quantite, DateOnly aujourdhui, bool autoriserBloque = false, bool controlerLot = true)
    {
        if (quantite <= 0)
            throw new RegleMetierException("La quantité doit être d’au moins une boîte.");
        if (lot.ProduitId != produit.Id)
            throw new RegleMetierException($"{produit.NomCommercial} : ce lot n’appartient pas à ce produit, rescannez la boîte.");

        if (produit.BloqueVente && !autoriserBloque)
            throw new RegleMetierException($"{produit.NomCommercial} : produit bloqué à la vente{(produit.MotifBlocage is { } m ? $" ({m})" : "")}. Retirez-le du panier.");
        if (controlerLot) lot.VerifierVendable(aujourdhui);
        var demandee = _lignes.Where(l => l.LotId == lot.Id).Sum(l => l.Quantite) + quantite;
        if (controlerLot && demandee > lot.QuantiteDisponible)
            throw new RegleMetierException(
                $"{produit.NomCommercial}, lot {lot.NumeroLot} : {lot.QuantiteDisponible} boîte(s) disponible(s), {demandee} demandée(s).");

        var ligne = new LigneVente
        {
            ProduitId = produit.Id, LotId = lot.Id, Quantite = quantite,
            PrixUnitaire = PrixVente.Unitaire(produit, lot), Shp = produit.ShpParBoite,
            Medicament = produit.EstMedicament, Remboursable = produit.Remboursable
        };
        if (Type == TypeVente.TiersPayant && produit.Remboursable && produit.TarifReference is { } tarif)
        {
            ligne.PriseEnCharge = true;
            ligne.CalculerTiersPayant(tarif, TauxTiersPayant ?? 0);
        }
        else
            ligne.SansTiersPayant();

        _sousRegistre |= produit.EstSousRegistre;
        _surOrdonnance |= produit.EstSurOrdonnance;
        _lignes.Add(ligne);
        RecalculerTotaux();
        return ligne;
    }

    /// <summary>
    /// Remise du vendeur en pourcentage entier sur la part du patient, hors SHP. Le droit et le maximum de l'employé (ou de celui qui
    /// valide sur place) sont contrôlés par l'encaissement.
    /// </summary>
    /// <param name="additionner">Vrai : s'ajoute aux remises automatiques ; faux : ne vaut que pour les lignes sans remise automatique.</param>
    public void AppliquerRemise(decimal pourcentage, bool additionner = false)
    {
        if (pourcentage is < 0 or > 100 || pourcentage != Math.Truncate(pourcentage))
            throw new RegleMetierException("La remise est un nombre entier de 0 à 100 %.", "remise");
        RemisePct = pourcentage;
        RemiseAdditionnee = additionner;
        RecalculerTotaux();
    }

    /// <summary>
    /// Remises automatiques décidées par le titulaire (pourcentage par produit, déjà choisi entre produit et catégorie) : en vente libre
    /// seulement, jamais sur un produit remboursable.
    /// </summary>
    public void AppliquerRemisesAutomatiques(IReadOnlyDictionary<long, decimal> pctParProduit)
    {
        foreach (var l in _lignes)
            l.FixerRemiseAuto(Type == TypeVente.Libre ? pctParProduit.GetValueOrDefault(l.ProduitId) : 0);
        RecalculerTotaux();
    }

    /// <summary>La remise du vendeur réduit au moins une ligne hors médicament (remise : droit et maximum de remise).</summary>
    public bool RemiseManuelleSurProduits => _lignes.Any(l => !l.Medicament && l.ReductionManuelle > 0);
    /// <summary>La remise du vendeur réduit au moins un médicament (don partiel : droit de don).</summary>
    public bool DonManuelSurMedicaments => _lignes.Any(l => l.Medicament && l.ReductionManuelle > 0);

    /// <summary>
    /// Tiers payant : le caissier fixe le reste à charge du patient. La part de l'organisme est la différence, répartie au prorata
    /// de leur montant sur les seules lignes prises en charge (produit remboursable avec un tarif de référence), sans plafond par ligne
    /// au tarif de référence ; les autres lignes restent entièrement à la charge du patient : le reste saisi est donc au moins leur
    /// montant. Le taux de prise en charge choisi n'est pas appliqué.
    /// </summary>
    public void SaisirResteACharge(decimal reste)
    {
        if (Type != TypeVente.TiersPayant)
            throw new RegleMetierException("Le reste à charge ne se saisit qu’en tiers payant.", "reste_a_charge");
        var brut = _lignes.Sum(l => l.PrixUnitaire * l.Quantite);
        var nonPrisEnCharge = _lignes.Where(l => !l.PriseEnCharge).Sum(l => l.PrixUnitaire * l.Quantite);
        if (reste < 0 || reste > brut)
            throw new RegleMetierException($"Le reste à charge doit être compris entre 0 et {brut:N2} DA (montant de la vente).", "reste_a_charge");
        if (reste < nonPrisEnCharge)
            throw new RegleMetierException(
                $"Le reste à charge doit être d’au moins {nonPrisEnCharge:N2} DA : montant des produits non remboursables (ou sans tarif de référence), que l’organisme ne prend pas en charge.",
                "reste_a_charge");
        ResteSaisi = Arrondi.Centime(reste);
        RecalculerTotaux();
    }

    public void RecalculerTotaux()
    {
        var brut = _lignes.Sum(l => l.PrixUnitaire * l.Quantite);
        if (ResteSaisi is { } reste && brut > 0)
        {
            // Part de l'organisme : seulement sur les lignes prises en charge ; les autres restent à 0 (SansTiersPayant)
            var prises = _lignes.Where(l => l.PriseEnCharge).ToList();
            var basePrise = prises.Sum(l => l.PrixUnitaire * l.Quantite);
            var organisme = Math.Min(brut - Math.Min(reste, brut), basePrise);
            var reparti = 0m;
            for (var i = 0; i < prises.Count; i++)
            {
                var montant = prises[i].PrixUnitaire * prises[i].Quantite;
                var part = i == prises.Count - 1 ? organisme - reparti : basePrise == 0 ? 0 : Arrondi.Centime(organisme * montant / basePrise);
                prises[i].ImposerPartOrganisme(part);
                reparti += part;
            }
        }
        PartOrganisme = _lignes.Sum(l => l.MontantOrganisme);
        var partPatientBrute = brut - PartOrganisme;
        // Réductions ligne par ligne, sur la part du patient hors SHP (honoraire du pharmacien, jamais réduit) :
        // remise sur les produits hors médicaments, don partiel sur les médicaments
        foreach (var l in _lignes) l.CalculerReductions(RemisePct, RemiseAdditionnee);
        MontantRemise = _lignes.Where(l => !l.Medicament).Sum(l => l.Reduction);
        MontantDon = _lignes.Where(l => l.Medicament).Sum(l => l.Reduction);
        PartPatient = partPatientBrute - MontantRemise - MontantDon;
        MontantTotal = brut - MontantRemise - MontantDon;
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
