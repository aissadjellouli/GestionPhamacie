namespace Pharma.Shared;

public sealed record ConnexionRequete(string Identifiant, string MotDePasse);
public sealed record ConnexionReponse(string Jeton, string NomComplet, DateTime ExpireLe);

public sealed record ProduitDto(
    long Id, string NomCommercial, string? Dci, string? Dosage, string? Forme,
    decimal PrixPublic, int Stock, string Classe);

/// <summary>Raison : "perime" ou "bloque".</summary>
public sealed record LotEcarteDto(string NumeroLot, DateOnly DatePeremption, string Raison, string? MotifBlocage);

public sealed record LotFefoDto(
    long LotId, string NumeroLot, DateOnly DatePeremption, int Quantite, string Fournisseur,
    IReadOnlyList<LotEcarteDto> Ecartes);

/// <summary>Réponse 404 de lot-fefo : aucun lot vendable, avec les lots écartés s'il y en a.</summary>
public sealed record AucunLotDto(string Message, IReadOnlyList<LotEcarteDto> Ecartes);

public sealed record ProduitAlternatifDto(long ProduitId, string NomCommercial, decimal PrixPublic, int Stock);

public sealed record AlerteDto(
    long Id, string Type, string Niveau, long ProduitId, long? LotId,
    string Message, decimal? ValeurDzd, string Statut, DateTime MiseAJourLe,
    string Produit, string? NumeroLot);

public sealed record IndicateursStockDto(int ReferencesEnStock, decimal ValeurAchat, int LotsDateAVerifier);

/// <summary>Statut : ok, rupture, critique, dormant ou surstock.</summary>
public sealed record LigneStockDto(
    long ProduitId, string NomCommercial, string? Dci, string? CodeBarre, string Classe,
    int Stock, int? CouvertureJours, DateOnly? PremierePeremption, int NombreLots,
    IReadOnlyList<string> Grossistes, IReadOnlyList<string> NumerosLots,
    string Statut, bool PeremptionProche);

public sealed record EtatStockDto(IndicateursStockDto Indicateurs, IReadOnlyList<LigneStockDto> Lignes);

public sealed record EtatEtiquetageDto(int BoitesIdentifiees, int BoitesEnStock, int Pourcentage, bool ModeStrict);

public sealed record ReporterAlerteRequete(DateOnly JusquAu);
public sealed record TraiterAlerteRequete(string? ActionPrise);

public sealed record RappelLotRequete(long ProduitId, string NumeroLot, string Motif);
public sealed record VenteARappelerDto(string Patient, string? Telephone, DateTime DateVente, string Ticket, int Boites, bool Anonyme);
public sealed record RappelLotDto(
    string Produit, string NumeroLot, int LotsBloques, int BoitesEnStock, int BoitesVendues,
    int PatientsAContacter, int VentesAnonymes, IReadOnlyList<VenteARappelerDto> Ventes);

public sealed record EtapeParcoursDto(string Type, DateTime Date, string? Commentaire);

public sealed record ParcoursBoiteDto(
    long BoiteId, string Identifiant, string Produit, string NumeroLot, DateOnly DatePeremption,
    string Fournisseur, string? NumeroBl, DateTime DateReception, string Statut, bool LotBloque,
    string? Ticket, DateTime? DateVente, string? NumFactureChifa, string? Patient,
    IReadOnlyList<EtapeParcoursDto> Etapes, long ProduitId);

public sealed record ErreurDto(string Message);
