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

/// <summary>Exact : même DCI, dosage et forme. Sinon autre dosage ou autre forme, à valider par le pharmacien.</summary>
public sealed record ProduitAlternatifDto(long ProduitId, string NomCommercial, decimal PrixPublic, int Stock,
    bool Exact = true, string? Dosage = null, string? Forme = null);

public sealed record AlerteDto(
    long Id, string Type, string Niveau, long ProduitId, long? LotId,
    string Message, decimal? ValeurDzd, string Statut, DateTime MiseAJourLe,
    string Produit, string? NumeroLot, string? Emplacement = null);

public sealed record IndicateursStockDto(int ReferencesEnStock, decimal ValeurAchat, int LotsDateAVerifier);

/// <summary>Statut : ok, rupture, critique, dormant ou surstock.</summary>
public sealed record LigneStockDto(
    long ProduitId, string NomCommercial, string? Dci, string? CodeBarre, string Classe,
    int Stock, int? CouvertureJours, DateOnly? PremierePeremption, int NombreLots,
    IReadOnlyList<string> Grossistes, IReadOnlyList<string> NumerosLots,
    string Statut, bool PeremptionProche, long? EmplacementId = null, string? Emplacement = null, long? ReserveId = null, string? Reserve = null);

public sealed record EtatStockDto(IndicateursStockDto Indicateurs, IReadOnlyList<LigneStockDto> Lignes);

public sealed record EtatEtiquetageDto(int BoitesIdentifiees, int BoitesEnStock, int Pourcentage, bool ModeStrict);

public sealed record ReporterAlerteRequete(DateOnly JusquAu);
public sealed record TraiterAlerteRequete(string? ActionPrise);

public sealed record RappelLotRequete(long ProduitId, string NumeroLot, string Motif);
public sealed record VenteARappelerDto(string Patient, string? Telephone, DateTime DateVente, string Ticket, int Boites, bool Anonyme);
public sealed record RappelLotDto(
    string Produit, string NumeroLot, int LotsBloques, int BoitesEnStock, int BoitesVendues,
    int PatientsAContacter, int VentesAnonymes, IReadOnlyList<VenteARappelerDto> Ventes, string? Emplacements = null);

public sealed record EtapeParcoursDto(string Type, DateTime Date, string? Commentaire);

public sealed record ParcoursBoiteDto(
    long BoiteId, string Identifiant, string Produit, string NumeroLot, DateOnly DatePeremption,
    string Fournisseur, string? NumeroBl, DateTime DateReception, string Statut, bool LotBloque,
    string? Ticket, DateTime? DateVente, string? NumFactureChifa, string? Patient,
    IReadOnlyList<EtapeParcoursDto> Etapes, long ProduitId);

public sealed record ErreurDto(string Message);

// ---- Caisse -----------------------------------------------------------------

public sealed record EtatCaisseDto(
    long? SessionId, DateTime? OuverteLe, decimal? FondCaisse, string? Poste,
    bool ComptageAveugle, decimal FondCaisseDefaut, decimal SeuilEcart, int DeconnexionMinutes,
    bool PeutVoirEcart, decimal? VentesEspeces);

public sealed record OuvrirCaisseRequete(string? Poste, decimal? FondCaisse);

public sealed record LigneCommandeDto(long ProduitId, long LotId, int Quantite);
/// <summary>Médecin obligatoire pour un psychotrope ; JetonScan : image envoyée par POST api/ordonnances/scans.</summary>
public sealed record OrdonnanceDto(long? MedecinId, DateOnly DatePrescription, string? Numero,
    string? JetonScan = null, string? TexteReconnu = null);

/// <summary>Mode : especes, carte, cheque ou credit.</summary>
public sealed record CommandeVenteDto(
    IReadOnlyList<LigneCommandeDto> Lignes, bool TiersPayant, long? OrganismeId, decimal? TauxTiersPayant,
    long? PatientId, OrdonnanceDto? Ordonnance, decimal RemisePct, string Mode, decimal? MontantRecu);

public sealed record ApercuVenteDto(
    decimal MontantBrut, decimal PartOrganisme, decimal MontantRemise, decimal PartPatient,
    decimal MontantTotal, bool OrdonnanceRequise);

public sealed record VenteEncaisseeDto(
    long VenteId, string Numero, decimal MontantTotal, decimal PartOrganisme, decimal PartPatient,
    string Mode, decimal Rendu);

/// <summary>Saisies par coupure : b2000, b1000, b500, b200, p100 (nombres), autres (montant total).</summary>
public sealed record ClotureRequete(IReadOnlyDictionary<string, decimal> Saisies);

public sealed record ClotureDto(
    long SessionId, decimal Compte, decimal? FondCaisse, decimal? VentesEspeces, decimal? Attendu,
    decimal? Ecart, bool? AuDelaDuSeuil, decimal? SeuilEcart);

public sealed record ParametresCaisseDto(bool ComptageAveugle, decimal FondCaisseDefaut, decimal SeuilEcart, int DeconnexionMinutes);

public sealed record EntreeJournalDto(DateTime Date, string Employe, string Action, string? Details, string? AutorisePar);

// ---- Patients, médecins, organismes -------------------------------------------

public sealed record PatientDto(
    long Id, string Nom, string? Prenom, string? Telephone, string? NumAssure,
    long? OrganismeId, string? Organisme, decimal PlafondCredit, decimal SoldeCredit);

public sealed record NouveauPatientDto(string Nom, string? Prenom, string? Telephone, string? NumAssure, long? OrganismeId, decimal PlafondCredit);
public sealed record MedecinDto(long Id, string Nom, string? Specialite);
public sealed record NouveauMedecinDto(string Nom, string? Specialite);
public sealed record OrganismeDto(long Id, string Code, string Nom);
public sealed record IdentifiantDto(long Id);

// ---- Administration : seuils ----------------------------------------------------

public sealed record ParametresAlerteDto(
    int JoursOrange, int JoursRouge, int HorizonRisqueJours,
    int DelaiFournisseurJours, int JoursSecurite, int JoursCibleCommande, int JoursDormant);
public sealed record SeuilGrossisteDto(long FournisseurId, string Nom, int? DelaiRepriseJours, int? JoursOrange, int? JoursRouge);
public sealed record ExceptionProduitDto(long ProduitId, string Nom, int? JoursOrange, int? JoursRouge);
public sealed record ReglagesSeuilsDto(ParametresAlerteDto General, IReadOnlyList<SeuilGrossisteDto> Grossistes, IReadOnlyList<ExceptionProduitDto> Exceptions);
public sealed record EnregistrementSeuilsDto(int AlertesMisesAJour);

// ---- Administration : utilisateurs et droits -------------------------------------

public sealed record UtilisateurAdminDto(
    long Id, string NomComplet, string Identifiant, long RoleId, string Role, bool RoleSysteme, bool Actif, DateTime? DerniereConnexion);
public sealed record RoleAdminDto(long Id, string Code, string Nom, bool Systeme, decimal RemiseMaxPct, IReadOnlyList<string> Permissions);
public sealed record PermissionAdminDto(string Code, string Module, string Libelle, bool Sensible);
public sealed record EtatDroitsDto(IReadOnlyList<UtilisateurAdminDto> Utilisateurs, IReadOnlyList<RoleAdminDto> Roles, IReadOnlyList<PermissionAdminDto> Permissions);
public sealed record NouvelUtilisateurDto(string NomComplet, string Identifiant, string MotDePasse, long RoleId);
/// <summary>MotDePasse vide : inchangé.</summary>
public sealed record ModificationUtilisateurDto(string NomComplet, long RoleId, bool Actif, string? MotDePasse);
public sealed record DroitsRoleDto(long RoleId, decimal RemiseMaxPct, IReadOnlyList<string> Permissions);

// ---- Administration : pharmacie ----------------------------------------------------

/// <summary>LangueDefaut : ar, fr ou en.</summary>
public sealed record PharmacieDto(string? Nom, string? Titulaire, string? Adresse, string? Telephone, string? Nif, string? ChifaOfficine, string LangueDefaut);

// ---- Ordonnance scannée ----------------------------------------------------------

public sealed record AnalyseOrdonnanceRequete(IReadOnlyList<string> Lignes);

/// <summary>Score de ressemblance de 0 à 1 entre la ligne lue et le produit.</summary>
public sealed record ProduitCandidatDto(
    long ProduitId, string NomCommercial, string? Dci, string? Dosage, string? Forme,
    decimal PrixPublic, int Stock, string Classe, double Score);

public sealed record LigneOrdonnanceDto(string Texte, IReadOnlyList<ProduitCandidatDto> Candidats, int? NombreBoites = null);
public sealed record AnalyseOrdonnanceDto(IReadOnlyList<LigneOrdonnanceDto> Lignes, int LignesIgnorees);
public sealed record ScanOrdonnanceDto(string Jeton);

// ---- Achats -----------------------------------------------------------------

public sealed record FournisseurDto(long Id, string RaisonSociale, string? Telephone, decimal Solde, int? DelaiRepriseJours);

/// <summary>Statut : brouillon, envoyee, recue_partielle, recue ou annulee.</summary>
public sealed record CommandeAchatDto(
    long Id, string Numero, long FournisseurId, string Fournisseur, DateTime Date, string Statut, int NombreLignes, decimal MontantHt);

public sealed record LigneCommandeAchatDto(long Id, long ProduitId, string Produit, int QteCommandee, int QteRecue, decimal PrixAchatHt);
public sealed record CommandeAchatDetailDto(CommandeAchatDto Entete, IReadOnlyList<LigneCommandeAchatDto> Lignes);

public sealed record SuggestionCommandeDto(
    long ProduitId, string Produit, string Statut, int Stock, int QteSuggeree,
    decimal? DernierPrixHt, long? DernierFournisseurId, string? DernierFournisseur);

/// <summary>Sans prix, le dernier prix d'achat connu est repris.</summary>
public sealed record AjoutCommandeRequete(long FournisseurId, long ProduitId, int Quantite, decimal? PrixAchatHt);
public sealed record ModificationLigneCommandeRequete(int Quantite, decimal PrixAchatHt);

public sealed record LigneReceptionDto(
    long ProduitId, string Nom, string NumeroLot, DateOnly DatePeremption, int Quantite, decimal PrixAchatHt,
    string? Gtin = null, IReadOnlyList<string>? NumerosSerie = null);

public sealed record ReceptionRequete(
    long FournisseurId, long? CommandeId, string? NumeroBl, string? NumeroFacture, IReadOnlyList<LigneReceptionDto> Lignes);

public sealed record LotRecuDto(long LotId, string Produit, string NumeroLot, DateOnly DatePeremption, int Quantite, int BoitesSansIdentite);
public sealed record ReceptionEnregistreeDto(long ReceptionId, decimal MontantTtc, IReadOnlyList<LotRecuDto> Lots);

public sealed record ReceptionResumeDto(
    long Id, string Fournisseur, string? NumeroBl, string? NumeroFacture, string? NumeroCommande,
    DateTime Date, int NombreLots, int NombreBoites, decimal MontantTtc);

public sealed record DataMatrixRequete(string Code);
public sealed record BoiteLueDto(string Gtin, string? NumeroLot, DateOnly? Peremption, string? NumeroSerie, long? ProduitId, string? Produit);

public sealed record LotRetournableDto(
    long LotId, long ProduitId, string Produit, string NumeroLot, DateOnly DatePeremption,
    long FournisseurId, string Fournisseur, int Disponible, decimal PrixAchatHt, bool Bloque, int BoitesIdentifiees);

/// <summary>Motif : peremption_proche, perime, defaut, erreur_livraison ou rappel_lot.</summary>
public sealed record LotARetournerDto(long LotId, int Quantite, IReadOnlyList<string> CodesBoites);
public sealed record PreparationRetourRequete(string Motif, IReadOnlyList<LotARetournerDto> Lots);

public sealed record LigneRetourDto(string Produit, string NumeroLot, int Quantite);

/// <summary>Statut : brouillon (préparé, stock sorti), envoye, avoir_recu ou refuse.</summary>
public sealed record RetourDto(
    long Id, string Numero, long FournisseurId, string Fournisseur, string Motif, string Statut,
    DateTime CreeLe, DateTime? EnvoyeLe, DateTime? AvoirRecuLe, decimal MontantAttendu, decimal? MontantAvoir,
    IReadOnlyList<LigneRetourDto> Lignes);

public sealed record AvoirRequete(decimal Montant);
public sealed record RefusRetourRequete(string? Motif);

public sealed record LotAEtiqueterDto(long LotId, string Produit, string NumeroLot, DateOnly DatePeremption, int Disponible, int BoitesSansIdentite);
public sealed record EtiquettesRequete(long LotId, int Nombre);

/// <summary>Codes créés, planche PDF (base 64) et texte ZPL pour une imprimante thermique.</summary>
public sealed record EtiquettesDto(IReadOnlyList<string> Codes, string PdfBase64, string Zpl);

// ---- Notifications (titulaire seulement) -------------------------------------

/// <summary>Le mot de passe SMTP n'est jamais renvoyé : seul son enregistrement est indiqué.</summary>
public sealed record ReglagesNotificationDto(
    string? SmtpServeur, int SmtpPort, bool SmtpTls, string? SmtpExpediteur, string? SmtpIdentifiant, bool MotDePasseEnregistre,
    string? ModemPort, TimeOnly HeureRapport, DateOnly? DernierRapport, decimal? SmsSeuilEcart, bool SmsLotBloque, bool SmsSauvegarde);

/// <summary>NouveauMotDePasse : null = inchangé, chaîne vide = effacé.</summary>
public sealed record ModificationReglagesNotificationDto(
    string? SmtpServeur, int SmtpPort, bool SmtpTls, string? SmtpExpediteur, string? SmtpIdentifiant, string? NouveauMotDePasse,
    string? ModemPort, TimeOnly HeureRapport, decimal? SmsSeuilEcart, bool SmsLotBloque, bool SmsSauvegarde);

/// <summary>Types : rapport_quotidien, ecart_caisse, lot_bloque, sauvegarde.</summary>
public sealed record DestinataireDto(
    long Id, string Nom, string? Email, string? Telephone, bool CanalEmail, bool CanalSms, IReadOnlyList<string> Types, bool Valide, bool Actif);

public sealed record SaisieDestinataireDto(
    string? Nom, string? Email, string? Telephone, bool CanalEmail, bool CanalSms, IReadOnlyList<string> Types, bool Actif);

public sealed record ValidationDestinataireDto(bool Valide);

/// <summary>Statut : en_attente, envoye ou echec.</summary>
public sealed record MessageSortantDto(
    long Id, string Canal, string Destinataire, string Adresse, string Type, string? Objet, string Corps, string Statut,
    int Tentatives, DateTime? ProchaineTentative, string? DerniereErreur, DateTime CreeLe, DateTime? EnvoyeLe);

public sealed record NombreDto(int Nombre);

// ---- Sauvegardes ------------------------------------------------------------

public sealed record ReglagesSauvegardeDto(bool Actif, string? Dossier, TimeOnly Heure, int Conservation, DateOnly? DerniereExecution);

/// <summary>Statut : reussie, echouee ou disque_absent.</summary>
public sealed record EntreeSauvegardeDto(
    long Id, DateTime Debut, DateTime? Fin, string Statut, string Declenchement, string? Fichier, long? TailleOctets, string? Message, string? Utilisateur);

public sealed record EtatSauvegardesDto(ReglagesSauvegardeDto Reglages, IReadOnlyList<EntreeSauvegardeDto> Journal);
public sealed record ResultatSauvegardeDto(string Statut, string? Fichier, long? TailleOctets, string Message);

// ---- Création d'un produit ----------------------------------------------------

/// <summary>Classe : libre, liste_i, liste_ii, psychotrope ou stupefiant.</summary>
public sealed record NouveauProduitDto(
    string? NomCommercial, string? Dci, string? Dosage, string? Forme, decimal PrixPublic, string Classe,
    string? CodeBarre, decimal? TarifReference, bool Remboursable, decimal TauxTva, int StockMinimum);

// ---- Emplacements ---------------------------------------------------------------

/// <summary>Zone : vente ou reserve.</summary>
public sealed record EmplacementDto(
    long Id, string Code, string Rayon, string? Etagere, string? Tiroir, string? Libelle, string Zone, bool Actif, int NombreProduits);

public sealed record SaisieEmplacementDto(string? Rayon, string? Etagere, string? Tiroir, string? Libelle, string Zone, bool Actif);
public sealed record RangementDto(long? EmplacementId, long? ReserveId);

// ---- Inventaire tournant ----------------------------------------------------------

public sealed record RayonInventaireDto(string Rayon, DateTime? DernierInventaire, int NombreLots, int LotsDateAVerifier, long? EnCoursId);
public sealed record ParametresInventaireDto(int SeuilBoites, decimal SeuilMontant);

/// <summary>Theorique, Ecart et ValeurEcart restent vides tant que l'inventaire est en cours (comptage à l'aveugle).</summary>
public sealed record LigneInventaireDto(
    long Id, long LotId, string Produit, string NumeroLot, DateOnly DatePeremption, bool DateAVerifier, string? Emplacement,
    int? Compte, DateOnly? DateVerifiee, string Statut, int? Theorique, int? Ecart, decimal? ValeurEcart);

public sealed record InventaireDto(long Id, string Rayon, string Statut, DateTime CommenceLe, string CommencePar, DateTime? TermineLe, IReadOnlyList<LigneInventaireDto> Lignes);
public sealed record InventaireResumeDto(long Id, string Rayon, string Statut, DateTime CommenceLe, string CommencePar, DateTime? TermineLe, int Lignes, int Ecarts, int AValider, decimal ValeurEcarts);
public sealed record EcartAValiderDto(long LigneId, long InventaireId, string Rayon, string Produit, string NumeroLot, int Theorique, int Compte, int Ecart, decimal ValeurEcart, string ComptePar, DateTime CompteLe);
public sealed record TableauInventaireDto(IReadOnlyList<RayonInventaireDto> Rayons, string? RayonPropose, ParametresInventaireDto Parametres, IReadOnlyList<EcartAValiderDto> EcartsAValider, IReadOnlyList<InventaireResumeDto> Historique);

public sealed record CommencerInventaireRequete(string Rayon);
public sealed record ComptageRequete(int Compte, DateOnly? DateVerifiee);
public sealed record ScanInventaireRequete(string Code);
public sealed record ScanInventaireDto(long LigneId, string Produit, string NumeroLot, int Compte);
public sealed record BilanInventaireDto(int Lignes, int Conformes, int Ajustes, int AValider, int BoitesEcart, decimal ValeurEcart);
public sealed record DecisionEcartRequete(bool Valider);
