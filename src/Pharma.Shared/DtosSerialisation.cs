namespace Pharma.Shared;

// ---- Sérialisation DataMatrix : file hors ligne vers la base nationale (2026-11-16, script 120, lot 10) ----

/// <summary>Compteurs de la file et réglages. DerniereErreur : dernier motif de report (connecteur injoignable).</summary>
public sealed record EtatSerialisationDto(int EnAttente, int Reussies, int Refusees, DateTime? DernierEssaiLe, string? DerniereErreur,
    bool Actif, string Connecteur, bool FacticeHorsLigne, IReadOnlyList<IdentifiantGs1Dto> IdentifiantsGs1);

/// <summary>Identifiant d'application GS1 paramétré (parametre_gs1_ai). LongueurFixe null : longueur variable.</summary>
public sealed record IdentifiantGs1Dto(string Code, string Champ, int? LongueurFixe, int? LongueurMax);

/// <summary>
/// Opération de la file. Operation : verifier, decommissionner, reactiver ; Statut : en_attente, reussie, refusee ;
/// Resultat : authentique, inconnue, deja_decommissionnee, rappelee, expiree (null tant que la base nationale n'a pas répondu).
/// </summary>
public sealed record OperationSerialisationDto(long Id, long BoiteId, string Produit, string Gtin, string NumeroSerie, string? Lot,
    string Operation, string Statut, string? Resultat, string? Message, int Essais, DateTime CreeLe, DateTime? DernierEssaiLe, DateTime? TraiteLe);

/// <summary>Résultat d'une relance : opérations relancées, puis bilan du rejeu immédiat.</summary>
public sealed record RelanceSerialisationDto(int Relancees, int Reussies, int Refusees, bool ConnecteurIndisponible);

/// <summary>Réglages modifiables par le titulaire.</summary>
public sealed record ParametresSerialisationRequete(bool Actif, bool FacticeHorsLigne);
