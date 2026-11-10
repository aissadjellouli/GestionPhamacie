namespace Pharma.Shared;

/// <summary>Ligne de vente proposée à la facturation (part : patient ou organisme selon le destinataire).</summary>
public sealed record LigneVenteSourceDto(long VenteId, string NumeroVente, DateTime DateVente, long LigneVenteId, string Designation, decimal Quantite,
    decimal MontantPatient, decimal MontantOrganisme, decimal RemiseVente, long? PatientId, string? Patient, long? OrganismeId,
    long? OrdonnanceId, string? OrdonnanceNumero, bool DejaFacturee);

public sealed record LigneLibreDto(string Designation, decimal Quantite, decimal PrixTtc, decimal TauxTva);

/// <summary>Destinataire : patient, entreprise ou organisme ; ventes entières et lignes libres.</summary>
public sealed record DemandeFactureDto(string Destinataire, long? PatientId, long? ClientId, long? OrganismeId, DateOnly? Date, DateOnly? Du, DateOnly? Au,
    IReadOnlyList<long> Ventes, IReadOnlyList<LigneLibreDto> Libres, string? Commentaire, string? Motif = null);

public sealed record FactureEmiseDto(long Id, string Numero);

/// <summary>Statut : emise, rejetee ou remplacee. Reste : à encaisser (0 si remplacée ou avoir).</summary>
public sealed record FactureResumeDto(long Id, string Type, string Numero, DateOnly Date, string Destinataire, string DestinataireNom, decimal Ttc,
    decimal DejaPaye, decimal Avoirs, decimal Regle, decimal Reste, string Statut, int Version, bool HorsSuivi, string? MotifHorsSuivi,
    string? RemplaceNumero, string? RemplaceeParNumero, string? OrigineNumero, string? MotifRejet, long? OrganismeId);

public sealed record LigneFactureDto(long? VenteId, long? LigneVenteId, string Part, string Designation, decimal Quantite, decimal PrixUnitaireTtc,
    decimal TauxTva, decimal MontantTtc);

public sealed record FactureDetailDto(FactureResumeDto Resume, long? PatientId, long? ClientId, string? DestinataireInfos, DateOnly? Du, DateOnly? Au,
    decimal Ht, decimal Tva, string? Commentaire, string? Motif, string Employe, IReadOnlyList<LigneFactureDto> Lignes, IReadOnlyList<int> Versions);

public sealed record MotifRequete(string? Motif);
public sealed record AvoirClientRequete(decimal Montant, string? Motif);
public sealed record SuiviRequete(bool HorsSuivi, string? Motif);

public sealed record ClientFactureDto(long? Id, string RaisonSociale, string? Adresse, string? Telephone, string? Nif, string? Rc, string? Nis,
    string? ArticleImposition, bool Actif);

public sealed record OrganismeFicheDto(long? Id, string Code, string Nom, string? Adresse, string? Nif, bool Actif);

// ---- Inventaire complet -----------------------------------------------------------------

/// <summary>Mode : ouverte ou fermee. Statut : en_cours, cloturee ou annulee.</summary>
public sealed record CampagneInventaireDto(long Id, string Mode, string Statut, DateTime CommenceLe, string CommencePar, DateTime? ClotureLe,
    string? CloturePar, int? NombreLots, int? NombreBoites, decimal? ValeurStock, string? Commentaire);

/// <summary>Statut : a_compter, en_cours ou termine.</summary>
public sealed record RayonCampagneDto(string Rayon, int NombreLots, long? InventaireId, string Statut, int EcartsAValider);

public sealed record InventaireCompletDto(CampagneInventaireDto? EnCours, IReadOnlyList<RayonCampagneDto> Rayons, IReadOnlyList<CampagneInventaireDto> Historique);
public sealed record CommencerCampagneRequete(string Mode);
public sealed record CloturerCampagneRequete(string? Commentaire);
