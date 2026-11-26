namespace Pharma.Shared;

// ---- Financements : prêts bancaires, échéances, compte du titulaire (titulaire seul) ----

/// <summary>
/// Prêt : CompteId = banque suivie en trésorerie, null pour un prêteur hors trésorerie (Banque : son nom) ; ResteDu = capital restant dû ;
/// Utilise = part employée (versée, dépense, facture, traite) ; Document : chemin GED. Taux (annuel, %) et DureeMois : tels qu'au contrat
/// (null : non saisis). ProchainMontant : mensualité (prochaine échéance non payée, assurance comprise : ProchaineAssurance).
/// </summary>
public sealed record PretDto(long Id, long? CompteId, string Banque, string Reference, string? Objet, decimal Montant, DateOnly DatePret, string? Document,
    decimal CapitalRembourse, decimal ResteDu, decimal Utilise, int Echeances, int EcheancesPayees, int EchuesNonPayees, DateOnly? ProchaineEcheance,
    decimal? ProchainMontant, string Employe, decimal? Taux = null, int? DureeMois = null, decimal? ProchaineAssurance = null,
    bool EcheancierARemplacer = false, decimal CapitalAnticipe = 0);

/// <summary>Ligne d'échéancier (saisie, importée ou enregistrée). Montant = Capital + Interets + Tva + Marge + Assurance.</summary>
public sealed record LigneEcheancierDto(DateOnly Date, decimal Capital, decimal Interets, decimal Tva, decimal Marge, decimal Montant, decimal CapitalRestant,
    decimal Assurance = 0);

/// <summary>
/// Échéance enregistrée ; PayeeLe et Moyen (especes, cheque, virement, prelevement) : échéance entièrement payée, null sinon.
/// MontantPaye, CapitalPaye, ChargesPayees : déjà payé, même en partie (script 170).
/// </summary>
public sealed record EcheancePretDto(long Id, long PretId, string Pret, string Banque, int Rang, LigneEcheancierDto Ligne, DateOnly? PayeeLe,
    string? Moyen, string? Compte, string? ReferencePaiement, string? Employe, decimal MontantPaye = 0, decimal CapitalPaye = 0, decimal ChargesPayees = 0)
{
    public decimal Reste => Ligne.Montant - MontantPaye;
    public bool Entamee => MontantPaye > 0 && PayeeLe is null;
}

/// <summary>Remboursement anticipé d'une partie du capital (script 170) ; frais comptés en charges financières.</summary>
public sealed record RemboursementAnticipeDto(long Id, DateOnly Date, decimal Capital, decimal Frais, string Moyen, string? Compte, string? Reference, string Employe);

/// <summary>Remboursement anticipé : capital et frais ; mêmes moyens que le paiement d'une échéance.</summary>
public sealed record RemboursementAnticipeRequete(DateOnly? Date, decimal Capital, decimal Frais, string? Moyen, long? CompteId, string? Reference, decimal PartTiroir);

/// <summary>Usage : tresorerie, depense, reglement (facture d'un grossiste) ou traite.</summary>
public sealed record UtilisationFinancementDto(long Id, string Source, long? PretId, string Usage, DateOnly Date, decimal Montant, string Libelle, string Employe);

public sealed record PretDetailDto(PretDto Pret, IReadOnlyList<EcheancePretDto> Echeances, IReadOnlyList<UtilisationFinancementDto> Utilisations,
    IReadOnlyList<RemboursementAnticipeDto>? Anticipes = null);

/// <summary>
/// CompteId : banque suivie en trésorerie ; ou Preteur : nom d'un prêteur hors trésorerie (CompteId null) ; exactement l'un des deux.
/// Taux (annuel, %) et DureeMois : facultatifs, tels qu'au contrat.
/// </summary>
public sealed record PretRequete(long? CompteId, string? Reference, string? Objet, decimal Montant, DateOnly? Date, string? Preteur = null,
    decimal? Taux = null, int? DureeMois = null);

/// <summary>Chemin « Type\nom » du document déposé dans la GED.</summary>
public sealed record DocumentPretRequete(string? Chemin);

/// <summary>Incohérence : Ligne (à partir de 1 ; 0 = échéancier entier), Champ (colonne en cause), Message.</summary>
public sealed record AnomalieEcheancierDto(int Ligne, string Champ, string Message);

public sealed record EcheancierRequete(IReadOnlyList<LigneEcheancierDto> Lignes);

/// <summary>Colonnes (à partir de 0) du tableau de la banque ; null : absente. LignesIgnorees : lignes d'en-tête.</summary>
public sealed record CorrespondanceDto(int Date, int? Capital, int? Interets, int? Tva, int? Marge, int? Montant, int? Encours, int LignesIgnorees,
    int? Assurance = null);

/// <summary>Fichier fourni par la banque (Excel .xlsx ou CSV), en base64.</summary>
public sealed record TableauFichierRequete(string NomFichier, string ContenuBase64);

/// <summary>Lignes de texte lues sur le poste dans un PDF ou un scan.</summary>
public sealed record TableauTexteRequete(IReadOnlyList<string> Lignes);

/// <summary>Tableau lu (cellules en texte) et correspondance retenue pour la banque du prêt (null : aucune).</summary>
public sealed record TableauLuDto(IReadOnlyList<IReadOnlyList<string?>> Lignes, CorrespondanceDto? Correspondance);

public sealed record ApercuRequete(IReadOnlyList<IReadOnlyList<string?>> Lignes, CorrespondanceDto Correspondance);

public sealed record ApercuEcheancierDto(IReadOnlyList<LigneEcheancierDto> Lignes, IReadOnlyList<int> Origines, IReadOnlyList<string> Ecartees,
    IReadOnlyList<AnomalieEcheancierDto> Anomalies);

/// <summary>Moyen : especes, cheque, virement ou prelevement ; CompteId et Reference obligatoires hors espèces ; PartTiroir : complément du tiroir.</summary>
/// <summary>Montant : payé cette fois (script 170) ; vide : ce qui reste à payer sur l'échéance.</summary>
public sealed record PaiementEcheanceRequete(DateOnly? Date, string? Moyen, long? CompteId, string? Reference, decimal PartTiroir, decimal? Montant = null);

/// <summary>Source : « titulaire » ou « pret:ID ». Usage : tresorerie (versé sur le compte) ou depense (Categorie et Libelle obligatoires).</summary>
public sealed record UtilisationRequete(string? Source, string? Usage, DateOnly? Date, decimal Montant, long CompteId, string? Categorie, string? Libelle,
    string? Reference);

public sealed record RemboursementTitulaireRequete(DateOnly? Date, decimal Montant, long CompteId, string? Libelle, string? Reference);

/// <summary>Sens : apport (la pharmacie doit au titulaire) ou remboursement.</summary>
public sealed record MouvementTitulaireDto(DateOnly Date, string Sens, decimal Montant, string Usage, string Libelle, string Employe);

public sealed record CompteTitulaireDto(decimal SoldeDu, decimal TotalApports, decimal TotalRemboursements, PageDto<MouvementTitulaireDto> Mouvements);

/// <summary>Code : « titulaire » ou « pret:ID » ; Disponible : reste du prêt à employer (null : apport du titulaire).</summary>
public sealed record SourceFinancementDto(string Code, string Libelle, decimal? Disponible);
