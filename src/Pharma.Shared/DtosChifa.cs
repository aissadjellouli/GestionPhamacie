namespace Pharma.Shared;

// ---- Volet Chifa : compléments (2026-11-15, script 100) ----

/// <summary>Centre payeur d'un organisme Chifa (CNAS, CASNOS).</summary>
public sealed record CentrePayeurDto(long Id, long OrganismeId, string OrganismeCode, string Code, string Nom, bool Actif);
public sealed record CentrePayeurSaisie(long OrganismeId, string Code, string Nom, bool Actif = true);

/// <summary>Informations Chifa d'une vente ; RetenirCentre : le centre devient le centre habituel du patient.</summary>
public sealed record ChifaInfosSaisie(DateOnly? DateSoins, long? CentrePayeurId, long? SaisiParId, string? MajorationType, decimal MajorationMontant,
    bool RetenirCentre = false);

/// <summary>Exclusion du traitement Chifa : motif de la liste « chifa_exclusion », texte pour « Autre ».</summary>
public sealed record ExclusionChifaRequete(string MotifCode, string? Texte);

/// <summary>Vente Chifa (Finances › Chifa) : informations Chifa et exclusion éventuelle.</summary>
public sealed record VenteChifaDto(long Id, string Numero, DateTime Date, string Statut, bool DansBordereau, bool Rattachee,
    long? PatientId, string? Patient, string? NumAssure, long? OrganismeId, string? Organisme, string Vendeur, long VendeurId,
    decimal Total, decimal PartOrganisme, string? FactureChifa,
    DateOnly? DateSoins, long? CentrePayeurId, string? CentrePayeur, long? SaisiParId, string? SaisiPar,
    string? MajorationType, string? Majoration, decimal MajorationMontant,
    DateTime? ExclueLe, string? ExcluePar, string? Exclusion, long? CentreHabituelId)
{
    /// <summary>Part organisme facturée : part de la caisse plus la majoration (D-3).</summary>
    public decimal PartOrganismeTotale => PartOrganisme + MajorationMontant;
}

/// <summary>Employé proposé pour « saisi dans Chifa par ».</summary>
public sealed record EmployeChoixDto(long Id, string Nom);

/// <summary>
/// Conflit de n° de facture Chifa (script 101, 2026-11-15) : ancien n° (colonne obsolète) et chifa_facture différentes
/// (NumeroActuel null : recopie impossible, n° déjà porté par une autre vente). ValeurRetenue null : à trancher par le titulaire.
/// </summary>
public sealed record ConflitFactureDto(long VenteId, string NumeroVente, DateTime DateVente, string AncienNumero, string? NumeroActuel,
    string? ValeurRetenue, DateTime? TrancheLe, string? TranchePar);

/// <summary>N° de facture Chifa retenu par le titulaire (8 chiffres).</summary>
public sealed record TrancherConflitFactureRequete(string ValeurRetenue);

// ---- Bordereaux Chifa / MDN (script 110) ----

/// <summary>
/// Bordereau : Statut preparation, depose, paye_partiel, paye (rejets à traiter), solde. Reste = attendu − versé − rejeté ;
/// Ecart = déclaré − attendu (rouge s'il n'est pas nul) ; Impaye : déposé non soldé au-delà du délai réglé.
/// </summary>
public sealed record BordereauDto(long Id, long OrganismeId, string Organisme, string Canal, long? CentrePayeurId, string? CentrePayeur,
    string Numero, DateOnly PeriodeDebut, DateOnly PeriodeFin, DateOnly? DateDepot, string Statut, string StatutLibelle, decimal? MontantDeclare,
    int Ventes, decimal Attendu, decimal Rejete, int RejetsATraiter, decimal Verse, decimal Reste, decimal? Ecart, DateTime CreeLe, string? CreePar,
    bool Impaye);

/// <summary>Facture (vente) d'un bordereau : Suite redeposer, perte, patient, ou null (rejet à traiter).</summary>
public sealed record LigneBordereauDto(long VenteId, string NumeroVente, DateTime DateVente, long? PatientId, string? Patient, string? FactureChifa,
    decimal PartOrganisme, DateTime AjouteLe, DateTime? RejeteLe, string? Motif, decimal MontantRejete, string? Suite, string? SuiteLibelle,
    DateTime? SuiteLe, string? SuitePar);

public sealed record VersementBordereauDto(long MouvementId, DateOnly Date, string Compte, string? Reference, decimal Montant);

/// <summary>Historique d'un bordereau : entrées du journal (action, date, employé, détails lisibles).</summary>
public sealed record HistoriqueBordereauDto(DateTime Date, string Employe, string Action, string? Details);

public sealed record FicheBordereauDto(BordereauDto Bordereau, IReadOnlyList<LigneBordereauDto> Lignes, IReadOnlyList<VersementBordereauDto> Versements);

public sealed record VenteADeposerDto(long VenteId, string Numero, DateTime Date, long OrganismeId, string Organisme, string Canal,
    long? CentrePayeurId, string? CentrePayeur, long? PatientId, string? Patient, string? FactureChifa, decimal PartOrganisme, bool Redeposee, bool EnRetard);

public sealed record CompteursBordereauxDto(int VentesADeposer, int VentesEnRetard, decimal MontantADeposer, int BordereauxImpayes, decimal MontantImpaye,
    int RejetsATraiter);

/// <summary>Nouveau bordereau (n° de CHIFA-Officine) ou modification de son en-tête en préparation.</summary>
public sealed record BordereauSaisie(long OrganismeId, long? CentrePayeurId, string Numero, DateOnly PeriodeDebut, DateOnly PeriodeFin, decimal? MontantDeclare);

/// <summary>Ventes cochées, ou « toutes » celles du filtre (Toutes non null : centre, recherche, retard).</summary>
public sealed record AjoutVentesBordereauRequete(IReadOnlyList<long>? Ventes, FiltreVentesADeposerDto? Toutes = null);
public sealed record FiltreVentesADeposerDto(long? CentrePayeurId, string? Recherche, bool RetardSeulement);
public sealed record AjoutFactureBordereauRequete(string FactureChifa);
public sealed record DepotBordereauRequete(DateOnly? Date);

/// <summary>Rejet : motif (liste rejet_chifa, texte pour « Autre »), montant ≤ part organisme ; Suite facultative (redeposer, perte, patient).</summary>
public sealed record RejetChifaRequete(string MotifCode, string? Texte, decimal Montant, string? Suite);
public sealed record SuiteRejetRequete(string Suite);
