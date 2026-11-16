namespace Pharma.Shared;

// Tableau de bord financier du titulaire (lot 11) : titulaire ou droit « rapports.marges ». Lecture seule.

/// <summary>Valeur de la période, de la période précédente, écart en % (null si la précédente est à zéro).</summary>
public sealed record ComparaisonDto(decimal Actuel, decimal? Precedent, decimal? VariationPct);

public sealed record RepartitionEncaissementDto(decimal Comptant, decimal Credit, decimal Chifa, decimal Mdn, decimal AutresOrganismes, decimal Total);

public sealed record CreanceBordereauDto(long Id, string Numero, string Organisme, string Canal, string Statut, DateOnly? DateDepot, decimal Attendu,
    decimal Rejete, decimal Verse, decimal Reste, int? AgeJours, string Tranche);

public sealed record MontantParTrancheDto(string Tranche, int Nombre, decimal Montant);

public sealed record CreancesDto(List<CreanceBordereauDto> Bordereaux, decimal TotalBordereaux, List<MontantParTrancheDto> BordereauxParAge,
    decimal HorsBordereau, decimal CreditsPatients, int PatientsDebiteurs, List<MontantParTrancheDto> CreditsParAge);

public sealed record StockPilotageDto(decimal ValeurAchat, int Produits, decimal CoutVentesPeriode, decimal? RotationAnnuelle, decimal? CouvertureJours,
    int SeuilDormantJours, int Dormants, decimal ValeurDormante, int RupturesOfficine, int RupturesGrossistes);

public sealed record AchatsGrossisteDto(long FournisseurId, string Grossiste, int Receptions, decimal MontantHt, decimal MontantTtc);

public sealed record RejetsMotifDto(string Code, string Libelle, int Nombre, decimal Montant, decimal? TauxPct);

public sealed record RejetsChifaDto(int VentesDeposees, decimal MontantDepose, int Rejets, decimal MontantRejete, decimal? TauxRejetPct, List<RejetsMotifDto> ParMotif);

public sealed record TvaParTauxDto(decimal Taux, decimal Ttc, decimal Ht, decimal Tva, long Evenements);

public sealed record TableauFinancierDto(string Periode, DateOnly Du, DateOnly Au, string Libelle, DateOnly? PrecedentDu, DateOnly? PrecedentAu,
    ComparaisonDto ChiffreAffaires, ComparaisonDto NombreVentes, ComparaisonDto? PanierMoyen, ComparaisonDto MargeBrute,
    decimal ChiffreAffairesHt, decimal CoutAchat, decimal? TauxMarge, decimal? TauxMargePrecedent,
    List<VentesJourDto> ParJour, RepartitionEncaissementDto Repartition, CreancesDto Creances, StockPilotageDto Stock,
    List<AchatsGrossisteDto> Achats, RejetsChifaDto Rejets, List<TvaParTauxDto> Tva);

public sealed record MargeRegroupeeDto(string Cle, int Quantite, decimal ChiffreAffairesHt, decimal CoutAchat, decimal Marge, decimal? TauxPct);

public sealed record ProduitDormantDto(long ProduitId, string Produit, string? Laboratoire, int Stock, decimal Valeur, DateTime? DerniereVente, int? JoursSansVente);
