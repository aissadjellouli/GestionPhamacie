namespace Pharma.Shared;

// ---- Multi-grossistes (lot 6) : profil de fichier, import du catalogue, comparaison, répartition, envoi par e-mail ----

/// <summary>Nom de l'en-tête de chaque colonne du fichier du grossiste (null : absente) ; valeurs texte séparées par « ; ».</summary>
public sealed record ProfilCatalogueDto(
    long FournisseurId, string? ColonneCode, string? ColonneCodeBarre, string? ColonneDesignation, string? ColonnePrixHt,
    string? ColonneDisponibilite, string? ColonneDelai, string? ValeursIndisponible, string? ValeursDisponible, DateTime? ModifieLe);

/// <summary>Fichier du grossiste (Excel .xlsx ou CSV) en base64.</summary>
public sealed record FichierCatalogueRequete(string NomFichier, string ContenuBase64);

public sealed record AnomalieCatalogueDto(int NumeroLigne, string Message);

public sealed record BilanImportCatalogueDto(long ImportId, int LignesLues, int LignesRapprochees, int LignesIgnorees,
    IReadOnlyList<string> NonRapprochees, IReadOnlyList<AnomalieCatalogueDto> Anomalies);

public sealed record ImportCatalogueDto(long Id, long FournisseurId, string Grossiste, string NomFichier, DateTime ImporteLe, string? Utilisateur,
    int LignesLues, int LignesRapprochees, int LignesIgnorees);

/// <summary>Offre d'un grossiste pour un produit. Prix (catalogue et dernier achat) : null sans le droit « rapports.marges ».</summary>
public sealed record OffreGrossisteDto(long FournisseurId, string Grossiste, decimal? PrixHt, bool? Disponible, int? QuantiteDisponible,
    int? DelaiJours, DateTime? CatalogueLe, decimal? DernierPrixAchatHt, DateTime? DernierAchatLe);

public sealed record ComparaisonProduitDto(long ProduitId, string Produit, string Statut, int Stock, int QteSuggeree,
    IReadOnlyList<OffreGrossisteDto> Offres, long? GrossistePropose, string MotifProposition);

public sealed record LigneRepartitionDto(long ProduitId, long FournisseurId, int Quantite);
public sealed record RepartitionRequete(IReadOnlyList<LigneRepartitionDto> Lignes);
public sealed record BonRepartiDto(long CommandeId, long FournisseurId, string Grossiste, int Lignes);

public sealed record BonEnvoyeDto(string Numero, string Adresse, string NomFichier, string PdfBase64);
