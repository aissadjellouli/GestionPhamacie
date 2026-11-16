namespace Pharma.Shared;

// ---- Registre des psychotropes renforcé (lot 8, script 117) ----

public sealed record LignePsychotropeDto(long ProduitId, int Quantite);

/// <summary>Délivrance à contrôler ou à forcer : patient, prescripteur, n° d'ordonnance, produits (les autres sont ignorés).</summary>
public sealed record DelivrancePsychotropeDto(long? PatientId, long? MedecinId, string? NumeroOrdonnance, DateOnly? DatePrescription,
    IReadOnlyList<LignePsychotropeDto> Lignes, int? DureeTraitementJours = null);

public sealed record AlertePsychotropeDto(string Code, string Libelle, string Message, bool ExigeValideur);

/// <summary>Avis : messages non bloquants (ex. « wilaya du prescripteur à renseigner », 2026-11-16).</summary>
public sealed record ControleDelivranceDto(IReadOnlyList<AlertePsychotropeDto> Alertes, string? Repertoire, IReadOnlyList<string>? Avis = null);

public sealed record ForcageRequete(DelivrancePsychotropeDto Delivrance, string? Motif);

/// <summary>UnitesParJour : quantité anormale, unités par jour de traitement (vide : contrôle boîtes × contenance inactif).</summary>
public sealed record RegleAlertePsychotropeDto(string Code, string Libelle, bool Active, int Seuil, int PeriodeJours, bool ExigerValideur,
    int? UnitesParJour = null);

// ---- Wilayas et zone de l'officine (script 118) ----

public sealed record WilayaDto(string Code, string Nom);

/// <summary>Zone de l'officine : sa wilaya et des wilayas voisines (codes 01 à 69).</summary>
public sealed record ZoneOfficineDto(string? Officine, IReadOnlyList<string> Voisines);

public sealed record ForcageAlerteDto(long Id, DateTime Date, string Employe, string? Valideur, IReadOnlyList<string> Codes, string Messages,
    string Motif, string? Patient, string? Medecin, string? NumeroOrdonnance, string Produits);

public sealed record AnnulationEcritureRequete(string? Motif);

public sealed record ClasseSuivieDto(string Code, string Libelle, bool Active, int NombreDci);
public sealed record DciClasseDto(long DciId, string Nom);
public sealed record LigneSuiviClasseDto(string Dci, int Annee, int Mois, int Boites, int Delivrances);
