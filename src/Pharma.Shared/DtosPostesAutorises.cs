namespace Pharma.Shared;

// Liste des postes autorisés (docs/taches/2026-11-24-postes-autorises/contrat-api.md § 4 et § 5)

/// <summary>Poste de la liste (écran Administration › Postes). Statut : en_attente, actif, revoque.</summary>
public sealed record PosteAutoriseDto(long Id, string Nom, string Statut, int Generation, DateTime CreeLe, DateTime? EnroleLe,
    DateTime? DernierePreuveLe, string? Ordinateur, string? EmpreinteCle, DateTime? CodeExpireLe, DateTime? RevoqueLe, string? MotifRevocation);

/// <summary>Code d'enrôlement affiché une seule fois au titulaire.</summary>
public sealed record CodeEnrolementDto(long PosteId, string Nom, string Code, DateTime ExpireLe);

/// <summary>Réglage de la liste (mode inactif / transition / exige). NomsSansPreuve : en lecture seule, ignoré à l'enregistrement.</summary>
public sealed record ReglagesPostesDto(string Mode, int MaximumPostes, int DureeCodeMinutes, IReadOnlyList<NomSansPreuveDto>? NomsSansPreuve = null);

/// <summary>Nom de poste vu récemment à une connexion, qui n'est pas un poste enrôlé.</summary>
public sealed record NomSansPreuveDto(string Nom, DateTime VuLe);

public sealed record NomPosteRequete(string Nom);

public sealed record RevocationPosteRequete(string Motif);

/// <summary>État du poste appelant (GET /api/postes/etat). Refus : code de la preuve refusée, sinon null.</summary>
public sealed record EtatPosteAutoriseDto(string Mode, bool Prouve, long? PosteId, string? Nom, string? Refus);

public sealed record EnrolementPosteRequete(string Code, string ClePublique, string? Ordinateur, long Horodatage, string Preuve);

public sealed record EnrolementPosteReponse(long PosteId, string Nom);
