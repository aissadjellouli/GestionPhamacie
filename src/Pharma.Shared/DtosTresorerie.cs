namespace Pharma.Shared;

/// <summary>Type : coffre ou banque.</summary>
public sealed record CompteTresorerieDto(long Id, string Type, string Nom, string? Banque, string? NumeroCompte, bool RecoitCarte, bool Actif,
    decimal SoldeInitial, DateOnly DateInitiale, decimal Solde);

public sealed record CompteTresorerieRequete(string Nom, string? Banque, string? NumeroCompte, bool RecoitCarte, decimal SoldeInitial, DateOnly DateInitiale, bool Actif);

/// <summary>Sens : entree ou sortie. Origine : saisi ou calcule.</summary>
public sealed record MouvementTresorerieDto(DateOnly Date, string Sens, decimal Montant, string Nature, string? Categorie, string Libelle,
    string? Reference, string? Employe, string Origine);

public sealed record ChequeARemettreDto(long PaiementId, DateTime Date, decimal Montant, string Origine, string? Patient);
public sealed record CaisseAVerserDto(long SessionId, string Poste, DateTime FermeeLe, string OuvertePar, decimal Compte, decimal Propose);
public sealed record BordereauEnvoyeDto(long Id, long OrganismeId, string Organisme, string Numero, decimal Montant);
public sealed record AVerserDto(IReadOnlyList<ChequeARemettreDto> Cheques, IReadOnlyList<CaisseAVerserDto> Caisses, IReadOnlyList<BordereauEnvoyeDto> Bordereaux);

/// <summary>Nature : depense, encaissement ou versement_organisme. StatutBordereau : paye ou rejet_partiel.</summary>
public sealed record MouvementTresorerieRequete(long CompteId, DateOnly? Date, string Nature, decimal Montant, string? Categorie, string? Libelle,
    string? Reference, long? OrganismeId, long? BordereauId, string? StatutBordereau, IReadOnlyList<long>? Factures = null);

public sealed record TransfertRequete(long De, long Vers, DateOnly? Date, decimal Montant, string? Reference);
public sealed record RemiseChequesRequete(long CompteId, DateOnly? Date, string? Reference, IReadOnlyList<long> Paiements);
public sealed record MontantRequete(decimal Montant);
public sealed record CodeLibelleDto(string Code, string Libelle);
public sealed record CompteBancaireDto(long Id, string Nom, bool RecoitCarte);
public sealed record CompteDto(int Nombre);
