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
