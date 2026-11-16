namespace Pharma.Shared;

// ---- Ruptures et substitution (lot 7, M3b) -----------------------------------------

public sealed record RuptureProduitDto(long ProduitId, string Produit, int Grossistes, DateTime Depuis, int StockOfficine, int PatientsEnAttente);

public sealed record PeriodeRuptureDto(long Id, long ProduitId, string Produit, long FournisseurId, string Grossiste, DateTime Debut, DateTime? Fin);

/// <summary>Demande « Prévenir quand disponible ». Disponible : stock reçu ou grossiste qui le déclare disponible.</summary>
public sealed record DemandePatientDto(long Id, long PatientId, string Patient, string? Telephone, bool ConsentementSms, DateTime? ConsentementSmsLe,
    long ProduitId, string Produit, int Quantite, DateTime CreeLe, string Statut, DateTime? PrevenuLe, string? PrevenuCanal, string? SmsErreur,
    int StockOfficine, bool DisponibleGrossiste, bool Disponible);

public sealed record ProduitDuEnAttenteDto(long PanierId, string Libelle, long ProduitId, string Produit, int Quantite, DateTime CreeLe,
    int StockOfficine, bool DisponibleGrossiste);

public sealed record AlertesDisponibiliteDto(IReadOnlyList<DemandePatientDto> Patients, IReadOnlyList<ProduitDuEnAttenteDto> VentesEnAttente);

public sealed record NouvelleDemandePatientRequete(long PatientId, long ProduitId, int Quantite = 1);

public sealed record PrevenirPatientRequete(bool ParSms);

/// <summary>Servie : produit remis au patient ; sinon demande annulée.</summary>
public sealed record CloturerDemandeRequete(bool Servie);

public sealed record ConsentementSmsDto(bool Accord, DateTime? Le, bool FormulaireRecu = false, DateTime? FormulaireRecuLe = null);
/// <summary>« Formulaire d'accord SMS signé reçu » (2026-11-16, script 123).</summary>
public sealed record FormulaireSmsRecuRequete(bool Recu);
/// <summary>Texte du formulaire d'accord SMS réglé par le titulaire ; vide = texte par défaut.</summary>
public sealed record TexteFormulaireSmsDto(string Texte, bool ParDefaut);

public sealed record ParametreSubstitutionDto(bool EquivalentExactSeulement, DateTime? ModifieLe, string? ModifiePar);

public sealed record LienListeBlancheDto(long ProduitId, string Produit, long RemplacantId, string Remplacant, DateTime CreeLe, string? CreePar);

public sealed record LienListeBlancheRequete(long ProduitId, long RemplacantId);

public sealed record ProduitFabricationDto(long ProduitId, string Produit, string? Laboratoire, bool? Saisie, bool? Nomenclature);

/// <summary>Null : reprendre la nomenclature nationale importée.</summary>
public sealed record FabricationLocaleRequete(bool? FabricationLocale);
