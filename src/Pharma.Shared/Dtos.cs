namespace Pharma.Shared;

public sealed record ConnexionRequete(string Identifiant, string MotDePasse);
/// <summary>DoitChangerMotDePasse : mot de passe provisoire, le poste ouvre l'écran de changement avant tout.</summary>
public sealed record ConnexionReponse(string Jeton, string NomComplet, DateTime ExpireLe, bool DoitChangerMotDePasse = false);

public sealed record ChangementMotDePasseRequete(string? Actuel, string? Nouveau, string? Confirmation);

/// <summary>Changement d'utilisateur au comptoir : code personnel de 4 à 6 chiffres.</summary>
public sealed record ConnexionCodeRequete(string Identifiant, string Code);

public sealed record VendeurComptoirDto(string Identifiant, string NomComplet);

/// <summary>Écran de connexion : la caisse du poste est ouverte, la reprise par code comptoir est possible.</summary>
public sealed record ComptoirDto(bool CaisseOuverte);

public sealed record CodeComptoirRequete(string Code);

public sealed record ProduitDto(
    long Id, string NomCommercial, string? Dci, string? Dosage, string? Forme,
    decimal PrixPublic, int Stock, string Classe);

/// <summary>Raison : "perime" ou "bloque".</summary>
public sealed record LotEcarteDto(string NumeroLot, DateOnly DatePeremption, string Raison, string? MotifBlocage);

/// <summary>StockProduit : boîtes vendables du produit, tous lots confondus.</summary>
public sealed record LotFefoDto(
    long LotId, string NumeroLot, DateOnly DatePeremption, int Quantite, string Fournisseur,
    IReadOnlyList<LotEcarteDto> Ecartes, int StockProduit = 0);

/// <summary>Réponse 404 de lot-fefo : aucun lot vendable, avec les lots écartés s'il y en a.</summary>
public sealed record AucunLotDto(string Message, IReadOnlyList<LotEcarteDto> Ecartes);

/// <summary>Exact : même DCI, dosage et forme. Sinon autre dosage ou autre forme, à valider par le pharmacien.</summary>
public sealed record ProduitAlternatifDto(long ProduitId, string NomCommercial, decimal PrixPublic, int Stock,
    bool Exact = true, string? Dosage = null, string? Forme = null);

/// <summary>Lots qui périment dans les N jours (cumulé), au prix d'achat.</summary>
public sealed record HorizonPeremptionDto(int Jours, int Lots, int Boites, decimal Valeur);
/// <summary>ReglementsAPreparer et MontantAPreparer : null sans le droit de régler les grossistes.</summary>
public sealed record IndicateursAlertesDto(IReadOnlyList<HorizonPeremptionDto> Peremption, int? ReglementsAPreparer, decimal? MontantAPreparer);

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
    int PatientsAContacter, int VentesAnonymes, IReadOnlyList<VenteARappelerDto> Ventes, string? Emplacements = null,
    int LotsNonBloques = 0, DateTime? RappeleLe = null, string? Motif = null)
{
    /// <summary>Lot bloqué chez tous les grossistes : rien à rebloquer, consultation seulement.</summary>
    public bool ToutBloque => LotsBloques > 0 && LotsNonBloques == 0;
}

public sealed record EtapeParcoursDto(string Type, DateTime Date, string? Commentaire);

public sealed record ParcoursBoiteDto(
    long BoiteId, string Identifiant, string Produit, string NumeroLot, DateOnly DatePeremption,
    string Fournisseur, string? NumeroBl, DateTime DateReception, string Statut, bool LotBloque,
    string? Ticket, DateTime? DateVente, string? NumFactureChifa, string? Patient,
    IReadOnlyList<EtapeParcoursDto> Etapes, long ProduitId);

/// <summary>Refus renvoyé au poste. Champ : champ de saisie en cause (data-champ côté poste), ou null.</summary>
/// <summary>Autorisable : refus de droit que le code comptoir d'un employé habilité peut lever (Permission : le droit, ou « role.titulaire »).</summary>
public sealed record ErreurDto(string Message, string? Champ = null, bool Autorisable = false, string? Permission = null);

// ---- Caisse -----------------------------------------------------------------

public sealed record EtatCaisseDto(
    long? SessionId, DateTime? OuverteLe, decimal? FondCaisse, string? Poste,
    bool ComptageAveugle, decimal FondCaisseDefaut, decimal SeuilEcart, int DeconnexionMinutes,
    bool PeutVoirEcart, decimal? VentesEspeces, string? OuvertePar = null, bool ConnexionParCode = false, decimal PlafondCreditDefaut = 0);

public sealed record OuvrirCaisseRequete(string? Poste, decimal? FondCaisse);

/// <summary>Boites : boîtes identifiées scannées (étiquette interne ou n° de série), rattachées à la vente.</summary>
public sealed record LigneCommandeDto(long ProduitId, long LotId, int Quantite, IReadOnlyList<long>? Boites = null, string? RaisonLot = null);
/// <summary>Médecin obligatoire pour un psychotrope ; JetonScan : image envoyée par POST api/ordonnances/scans.</summary>
/// <summary>Porteur* : à remplir quand le porteur de l'ordonnance n'est pas le patient (psychotrope).</summary>
public sealed record OrdonnanceDto(long? MedecinId, DateOnly DatePrescription, string? Numero,
    string? JetonScan = null, string? TexteReconnu = null,
    string? PorteurNom = null, string? PorteurAdresse = null, string? PorteurPiece = null);

/// <summary>Mode : especes, carte, cheque ou credit.</summary>
public sealed record CommandeVenteDto(
    IReadOnlyList<LigneCommandeDto> Lignes, bool TiersPayant, long? OrganismeId, decimal? TauxTiersPayant,
    long? PatientId, OrdonnanceDto? Ordonnance, decimal RemisePct, string Mode, decimal? MontantRecu, decimal? ResteACharge = null,
    ValidationBlocageDto? Blocage = null);

/// <summary>Accord pour un produit bloqué : confirmé par l'employé habilité, sinon validé par le code comptoir d'un habilité.</summary>
public sealed record ValidationBlocageDto(bool Accepte, string? ValidateurIdentifiant = null, string? ValidateurCode = null);

/// <summary>SurOrdonnance : le panier contient un médicament de liste I ou II (ordonnance, ou droit de le vendre sans).</summary>
public sealed record ApercuVenteDto(
    decimal MontantBrut, decimal PartOrganisme, decimal MontantRemise, decimal PartPatient,
    decimal MontantTotal, bool OrdonnanceRequise, IReadOnlyList<decimal>? PrixLignes = null, decimal Shp = 0, decimal TotalMedicaments = 0,
    IReadOnlyList<string>? ProduitsBloques = null, bool SurOrdonnance = false, IReadOnlyList<SituationLotDto>? Situations = null);

/// <summary>
/// Raison à demander pour le lot d'une ligne (même ordre que les lignes envoyées) : aucune, peremption (vérifiée, à valider par le
/// vendeur) ou obligatoire (à choisir : introuvable, abimee, demande_client). LotDelaisse : lot plus ancien en stock.
/// </summary>
public sealed record SituationLotDto(long LotId, string Situation, string? LotDelaisse, DateOnly? PeremptionDelaisse);

public sealed record VenteEncaisseeDto(
    long VenteId, string Numero, decimal MontantTotal, decimal PartOrganisme, decimal PartPatient,
    string Mode, decimal Rendu);

/// <summary>Saisies par coupure : b2000, b1000, b500, b200, p100 (nombres), autres (montant total).</summary>
public sealed record ClotureRequete(IReadOnlyDictionary<string, decimal> Saisies);

public sealed record ClotureDto(
    long SessionId, decimal Compte, decimal? FondCaisse, decimal? VentesEspeces, decimal? Attendu,
    decimal? Ecart, bool? AuDelaDuSeuil, decimal? SeuilEcart, decimal VerseAuCoffre = 0);

/// <summary>Relève : clôture comptée de la session précédente et caisse rouverte avec ce montant.</summary>
public sealed record ReleveDto(ClotureDto Cloture, long NouvelleSessionId);

/// <summary>VersementCloture : manuel ou automatique (argent des caisses clôturées vers le coffre).</summary>
public sealed record ParametresCaisseDto(bool ComptageAveugle, decimal FondCaisseDefaut, decimal SeuilEcart, int DeconnexionMinutes, string? VersementCloture = "manuel",
    decimal PlafondCreditDefaut = 0, int SeuilAnnulations = 0, bool AutorisationParCode = true);

public sealed record EntreeJournalDto(DateTime Date, string Employe, string Action, string? Details, string? AutorisePar, long? EmployeId = null, long? AutoriseParId = null);

// ---- Patients, médecins, organismes -------------------------------------------

public sealed record PatientDto(
    long Id, string Nom, string? Prenom, string? Telephone, string? NumAssure,
    long? OrganismeId, string? Organisme, decimal PlafondCredit, decimal SoldeCredit);

/// <summary>Fiche complète d'un patient (SoldeCredit en lecture seule).</summary>
public sealed record FichePatientDto(long Id, string Nom, string? Prenom, DateOnly? DateNaissance, string? Telephone, string? Email, string? NumAssure,
    long? OrganismeId, bool Chronique, decimal PlafondCredit, decimal SoldeCredit = 0);

public sealed record NouveauPatientDto(string Nom, string? Prenom, string? Telephone, string? NumAssure, long? OrganismeId, decimal PlafondCredit, string? Email = null);
public sealed record MedecinDto(long Id, string Nom, string? Specialite);
public sealed record NouveauMedecinDto(string Nom, string? Specialite);
public sealed record OrganismeDto(long Id, string Code, string Nom, int DelaiPaiementJours = 30);
public sealed record IdentifiantDto(long Id);

// ---- Administration : seuils ----------------------------------------------------

public sealed record ParametresAlerteDto(
    int JoursOrange, int JoursRouge, int HorizonRisqueJours,
    int DelaiFournisseurJours, int JoursSecurite, int JoursCibleCommande, int JoursDormant,
    int SeuilLotDelaisse = 5, int SeuilLotDelaisseCollectif = 10);
public sealed record SeuilGrossisteDto(long FournisseurId, string Nom, int? DelaiRepriseJours, int? JoursOrange, int? JoursRouge);
public sealed record ExceptionProduitDto(long ProduitId, string Nom, int? JoursOrange, int? JoursRouge);
public sealed record ReglagesSeuilsDto(ParametresAlerteDto General, IReadOnlyList<SeuilGrossisteDto> Grossistes, IReadOnlyList<ExceptionProduitDto> Exceptions);
public sealed record EnregistrementSeuilsDto(int AlertesMisesAJour);

// ---- Administration : utilisateurs et droits -------------------------------------

public sealed record UtilisateurAdminDto(
    long Id, string NomComplet, string Identifiant, long RoleId, string Role, bool RoleSysteme, bool Actif, DateTime? DerniereConnexion, bool CodeComptoir = false,
    string? Telephone = null, string? Email = null);
public sealed record RoleAdminDto(long Id, string Code, string Nom, bool Systeme, decimal RemiseMaxPct, IReadOnlyList<string> Permissions);
public sealed record PermissionAdminDto(string Code, string Module, string Libelle, bool Sensible);
public sealed record EtatDroitsDto(IReadOnlyList<UtilisateurAdminDto> Utilisateurs, IReadOnlyList<RoleAdminDto> Roles, IReadOnlyList<PermissionAdminDto> Permissions);
public sealed record NouvelUtilisateurDto(string NomComplet, string Identifiant, string MotDePasse, long RoleId, string? Telephone = null, string? Email = null);
/// <summary>MotDePasse vide : inchangé.</summary>
public sealed record ModificationUtilisateurDto(string NomComplet, long RoleId, bool Actif, string? MotDePasse, string? Telephone = null, string? Email = null);
public sealed record DroitsRoleDto(long RoleId, decimal RemiseMaxPct, IReadOnlyList<string> Permissions);

// ---- Administration : pharmacie ----------------------------------------------------

/// <summary>LangueDefaut : ar, fr ou en.</summary>
public sealed record PharmacieDto(string? Nom, string? Titulaire, string? Adresse, string? Telephone, string? Nif, string? ChifaOfficine, string LangueDefaut,
    string? Rc = null, string? Nis = null, string? ArticleImposition = null);

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

public sealed record FournisseurDto(long Id, string RaisonSociale, string? Telephone, decimal Solde, int? DelaiRepriseJours, string? Email = null,
    string? Adresse = null, string? Nif = null, string? Rc = null, bool Actif = true);
/// <summary>Grossiste créé (Id null) ou modifié.</summary>
public sealed record FicheGrossisteDto(long? Id, string? RaisonSociale, string? Telephone, string? Email, string? Adresse, string? Nif, string? Rc,
    int? DelaiRepriseJours, bool Actif = true);

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

/// <summary>PrixAchatHt : avant remise, comme sur la facture. Shp null : selon la liste du produit.</summary>
public sealed record LigneReceptionDto(
    long ProduitId, string Nom, string NumeroLot, DateOnly DatePeremption, int Quantite, decimal PrixAchatHt,
    string? Gtin = null, IReadOnlyList<string>? NumerosSerie = null, bool LotSepare = false, bool EntrerBloque = false,
    decimal RemisePct = 0, decimal? Ppa = null, decimal? Shp = null,
    int Ug = 0, bool UgCoutReparti = false, bool RemplacerPrixCatalogue = false);

public sealed record ReceptionRequete(
    long FournisseurId, long? CommandeId, string? NumeroBl, string? NumeroFacture, IReadOnlyList<LigneReceptionDto> Lignes, decimal Timbre = 0,
    ValidationBlocageDto? Blocage = null);

/// <summary>Liste du médicament, prix public du catalogue, TVA et SHP proposé par boîte.</summary>
public sealed record InfoAchatProduitDto(long ProduitId, string Classe, decimal PrixPublic, decimal TauxTva, decimal ShpParBoite,
    bool BloqueAchat = false, string? MotifBlocage = null);

public sealed record LotRecuDto(long LotId, string Produit, string NumeroLot, DateOnly DatePeremption, int Quantite, int Etiquettes, bool LotExistant);
public sealed record ReceptionEnregistreeDto(long ReceptionId, decimal MontantTtc, IReadOnlyList<LotRecuDto> Lots);

/// <summary>Numero : numéro relu (un scan fait en clavier QWERTY est remis en chiffres) ; DejaRecues : réceptions qui le portent déjà.</summary>
public sealed record DocumentReceptionDto(string? Numero, IReadOnlyList<ReceptionResumeDto> DejaRecues);

public sealed record ReceptionResumeDto(
    long Id, string Fournisseur, string? NumeroBl, string? NumeroFacture, string? NumeroCommande,
    DateTime Date, int NombreLots, int NombreBoites, decimal MontantTtc, long? FournisseurId = null);

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

/// <summary>RetentionGedJours : jours de conservation de la copie dans la GED (null : réglage du serveur, 7 jours par défaut).</summary>
public sealed record ReglagesSauvegardeDto(bool Actif, string? Dossier, TimeOnly Heure, int Conservation, DateOnly? DerniereExecution,
    int? RetentionGedJours = null, int RetentionGedParDefaut = 7, int? NettoyageGedAnnees = null, string? DossierGed = null);

/// <summary>Document rangé dans la GED du serveur : chemin « Type\nom » sous le dossier de la GED.</summary>
public sealed record DocumentGedDto(string Chemin);

/// <summary>Statut : reussie, echouee ou disque_absent.</summary>
public sealed record EntreeSauvegardeDto(
    long Id, DateTime Debut, DateTime? Fin, string Statut, string Declenchement, string? Fichier, long? TailleOctets, string? Message, string? Utilisateur);

/// <summary>Base et Serveur : base de données que le serveur utilise et sauvegarde (à titre d'information, sans mot de passe).</summary>
public sealed record EtatSauvegardesDto(ReglagesSauvegardeDto Reglages, IReadOnlyList<EntreeSauvegardeDto> Journal, string? Base = null, string? Serveur = null);
public sealed record ResultatSauvegardeDto(string Statut, string? Fichier, long? TailleOctets, string Message);

// ---- Création d'un produit ----------------------------------------------------

/// <summary>Classe : libre, liste_i, liste_ii, psychotrope ou stupefiant.</summary>
public sealed record NouveauProduitDto(
    string? NomCommercial, string? Dci, string? Dosage, string? Forme, decimal PrixPublic, string Classe,
    string? CodeBarre, decimal? TarifReference, bool Remboursable, decimal TauxTva, int? StockMinimum,
    string? NumEnregistrement = null, string Categorie = "medicament", string? ListePsychotrope = null);

/// <summary>Catégorie de produits (045) ; Medicament : comptée dans les totaux « médicaments ».</summary>
/// <summary>Remarque laissée pendant l'essai (page « Guide et retours ») ; Genre : probleme, suggestion ou question.</summary>
public sealed record RetourEssaiDto(DateTime Date, string Employe, string Ecran, string Genre, string Texte);
public sealed record NouveauRetourEssaiDto(string? Ecran, string? Genre, string? Texte);

/// <summary>Fiche modifiable d'un produit (Classe en lecture seule).</summary>
public sealed record FicheProduitModifiableDto(long Id, string NomCommercial, string? Dci, string? Dosage, string? Forme, string? CodeBarre,
    decimal PrixPublic, decimal? TarifReference, bool Remboursable, decimal TauxTva, bool Actif, string Classe = "libre");

/// <summary>Lot en stock proposé à la sortie (périmé ou cassé).</summary>
public sealed record LotEnStockDto(long LotId, long ProduitId, string Produit, string NumeroLot, DateOnly DatePeremption, int Quantite, bool Bloque, bool SousRegistre);
/// <summary>Motif : perime ou casse.</summary>
public sealed record SortieStockRequete(long LotId, int Quantite, string? Motif, string? Commentaire);

/// <summary>Blocage d'un produit à l'achat et/ou à la vente (046).</summary>
public sealed record BlocageProduitDto(long ProduitId, string Nom, bool BloqueAchat, bool BloqueVente, string? Motif);
public sealed record BlocageProduitRequete(bool BloqueAchat, bool BloqueVente, string? Motif);

public sealed record CategorieProduitDto(string Code, string Libelle, bool Medicament);
public sealed record ClassementProduitDto(long ProduitId, string Nom, string Classe, string Categorie, string? ListePsychotrope);
public sealed record ClassementProduitRequete(string? Categorie, string? ListePsychotrope);

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
public sealed record TableauInventaireDto(IReadOnlyList<RayonInventaireDto> Rayons, string? RayonPropose, ParametresInventaireDto Parametres, IReadOnlyList<EcartAValiderDto> EcartsAValider, IReadOnlyList<InventaireResumeDto> Historique,
    EtatSensiblesDto? Sensibles = null);
/// <summary>Produits sensibles : nombre, fin du dernier comptage, comptage en cours.</summary>
public sealed record EtatSensiblesDto(int Produits, DateTime? Dernier, long? EnCoursId);

public sealed record CommencerInventaireRequete(string Rayon);
public sealed record ComptageRequete(int Compte, DateOnly? DateVerifiee);
public sealed record ScanInventaireRequete(string Code);
public sealed record ScanInventaireDto(long LigneId, string Produit, string NumeroLot, int Compte);
public sealed record BilanInventaireDto(int Lignes, int Conformes, int Ajustes, int AValider, int BoitesEcart, decimal ValeurEcart);
public sealed record DecisionEcartRequete(bool Valider);

// ---- Scanner mobile ---------------------------------------------------------------

public sealed record LotFicheDto(long Id, string NumeroLot, DateOnly DatePeremption, bool DateAVerifier, int Quantite, bool Bloque, string? MotifBlocage, string Fournisseur, string Niveau);
public sealed record FicheProduitDto(long ProduitId, string Nom, string? Dci, string? Dosage, string? Forme, decimal PrixPublic, int Stock,
    long? EmplacementId, string? Emplacement, long? ReserveId, string? Reserve, IReadOnlyList<LotFicheDto> Lots);
/// <summary>Type : boite, lot ou produit (code-barres du fabricant, sans lot).</summary>
public sealed record FicheCodeDto(string Type, FicheProduitDto Produit, long? LotId, string? StatutBoite, IReadOnlyList<string> Alertes);
public sealed record CodeRequete(string Code);
public sealed record PeremptionRequete(DateOnly Date);
public sealed record ScanReceptionDto(long Id, long ProduitId, string Produit, string? Gtin, string? NumeroLot, DateOnly? Peremption, string? NumeroSerie, string ScannePar, DateTime ScanneLe);
public sealed record LigneScanneeDto(long ProduitId, string Produit, string? Gtin, string? NumeroLot, DateOnly? Peremption, int Quantite, IReadOnlyList<string> Series,
    IReadOnlyList<long>? ScanIds = null);
public sealed record IdsRequete(IReadOnlyList<long> Ids);

/// <summary>Boîte scannée en caisse : son lot est servi ; LotConseille périme avant elle (péremption proche).</summary>
public sealed record BoiteCaisseDto(long ProduitId, string Produit, decimal PrixPublic, int StockProduit,
    LotFefoDto Lot, LotFefoDto? LotConseille, string? Avertissement, string? Emplacement = null, string? CouleurAvertissement = null,
    long? BoiteId = null);

/// <summary>Seuil d'alerte de stock d'un produit : SeuilManuel (saisi) remplace SeuilAuto ; null = automatique.</summary>
public sealed record SeuilStockDto(long ProduitId, string Nom, int Stock, decimal VentesParJour, int SeuilAuto, int? SeuilManuel, int SeuilApplique, string Statut);
public sealed record SeuilStockRequete(int? Seuil);

/// <summary>Conflit de lot à la réception. Type : date_differente ou lot_bloque.</summary>
public sealed record ConflitReceptionDto(int Index, string Type, string Message, IReadOnlyList<DateOnly> Peremptions);

// ---- Rapports ---------------------------------------------------------------------

/// <summary>ChiffreAffaires : net des retours clients de la période (Retours, MontantRetours).</summary>
public sealed record SyntheseVentesDto(int NombreVentes, decimal ChiffreAffaires, decimal PartOrganismes, decimal CreditsAccordes, decimal Remises, int Annulations,
    int Retours = 0, decimal MontantRetours = 0, decimal VentesMedicaments = 0, decimal VentesAutres = 0, decimal Shp = 0);
/// <summary>Taux en % ; null si le chiffre d'affaires HT est nul.</summary>
public sealed record MargeBruteDto(decimal ChiffreAffairesHt, decimal CoutAchat, decimal Montant, decimal? Taux);
public sealed record VentesJourDto(DateOnly Date, int NombreVentes, decimal ChiffreAffaires);
public sealed record ProduitVenduDto(string Produit, int Quantite, decimal ChiffreAffaires);
/// <summary>
/// Retour client lié à la période : enregistré pendant la période (DeduitDansLaPeriode : son montant diminue le chiffre de la période),
/// ou portant sur une vente de la période et enregistré un autre jour (pour information, sans effet sur le chiffre de la période).
/// </summary>
public sealed record RetourRapportDto(string Ticket, DateTime DateVente, string Vendeur, DateTime DateRetour, string Poste, string Employe,
    string Produits, decimal Montant, decimal DetteDiminuee, bool DeduitDansLaPeriode, long? VendeurId = null, long? EmployeId = null);
public sealed record ClotureRapportDto(string Employe, string Poste, DateTime OuverteLe, DateTime? FermeeLe,
    decimal? Attendu, decimal? Compte, decimal? Ecart, int Annulations, int Remises, int Retours = 0, long? EmployeId = null);
/// <summary>Periode : jour, semaine ou mois. Marge : null sans le droit « rapports.marges ».</summary>
public sealed record RapportActiviteDto(string Periode, DateOnly Du, DateOnly Au, string Libelle, SyntheseVentesDto Synthese, MargeBruteDto? Marge,
    IReadOnlyList<VentesJourDto> ParJour, IReadOnlyList<ProduitVenduDto> TopProduits, IReadOnlyList<ClotureRapportDto> Clotures, bool EcartsVisibles,
    bool ParSemaine = false, IReadOnlyList<RetourRapportDto>? Retours = null);

// ---- Reprise de données (titulaire) ------------------------------------------------

/// <summary>ChaineConnexion : null si la source ne peut pas être ouverte telle quelle (voir Remarque).</summary>
public sealed record SourceRepriseDto(string Libelle, string? ChaineConnexion, string Origine, string? Remarque,
    string? Pilote = null, string? Emplacement = null, string? Dsn = null);
public sealed record DossierRepriseRequete(string? Dossier);
public sealed record ConnexionRepriseRequete(string ChaineConnexion);
public sealed record ColonneSourceDto(string Nom, string Type, IReadOnlyList<string?> Exemples);
public sealed record TableSourceDto(string Nom, long? Lignes, IReadOnlyList<ColonneSourceDto> Colonnes);
/// <summary>Cible : produits, stock, fournisseurs ou clients. Colonnes : champ de reprise → colonne de l'ancienne base (null : non trouvé).</summary>
public sealed record PropositionRepriseDto(string Cible, string? Table, double Score, IReadOnlyDictionary<string, string?> Colonnes);
public sealed record ChampRepriseDto(string Cible, string Code, string Libelle, bool Obligatoire);
public sealed record ScanRepriseDto(string Pilote, string? Version, IReadOnlyList<TableSourceDto> Tables, IReadOnlyList<PropositionRepriseDto> Propositions,
    IReadOnlyList<ChampRepriseDto> Champs);
public sealed record CorrespondanceTableDto(string Cible, string Table, IReadOnlyDictionary<string, string> Colonnes);
public sealed record ExtractionRepriseRequete(string ChaineConnexion, string Logiciel, IReadOnlyList<CorrespondanceTableDto> Correspondances);
public sealed record ExtractionRepriseDto(long SessionId, IReadOnlyDictionary<string, int> Lignes, int Doublons, int Bloquants);
/// <summary>LancePar : qui a lancé l'extraction ; ChargeLe, ChargePar : chargement définitif (date et auteur).</summary>
public sealed record SessionRepriseDto(long Id, string Logiciel, DateTime Debut, string Statut, string? LancePar = null, DateTime? ChargeLe = null, string? ChargePar = null);

// ---- Super admin ----
/// <summary>Avant une action sensible : action prévue et mot de passe ressaisi ; un code part sur le téléphone du super admin.</summary>
public sealed record ValidationSuperAdminRequete(string Action, string MotDePasse);
/// <summary>Code envoyé : par quel canal, à quel destinataire (masqué), jusqu'à quand.</summary>
public sealed record DemandeValidationDto(Guid Id, string Canal, string Destinataire, DateTime ExpireLe);
public sealed record CompteSuperAdminDto(string Identifiant, string Telephone, string Email);
public sealed record ProduitAConfirmerDto(string RefSource, string Nom, string? Dosage, string? CodeBarre, long? ProduitPropose, string? NomPropose, decimal? Score);
public sealed record AnomalieRepriseDto(string Entite, string? RefSource, string Gravite, string Message);
public sealed record VerificationRepriseDto(string Indicateur, decimal Ancien, decimal Nouveau, decimal Ecart);
public sealed record EtatRepriseDto(SessionRepriseDto Session, IReadOnlyDictionary<string, int> Lignes, IReadOnlyDictionary<string, int> Rapprochement,
    IReadOnlyList<ProduitAConfirmerDto> AConfirmer, IReadOnlyList<AnomalieRepriseDto> Anomalies, IReadOnlyList<VerificationRepriseDto> Verification);
/// <summary>Decision : confirmer, autre (ProduitId obligatoire) ou creer.</summary>
public sealed record DecisionProduitRepriseRequete(string RefSource, string Decision, long? ProduitId);

// ---- Nomenclature nationale ------------------------------------------------------

public sealed record VersionNomenclatureDto(string Libelle, string Fichier, int EnVigueur, int NonRenouveles, int Retraits,
    DateTime ImporteeLe, string ImporteePar);

/// <summary>Situation : en_vigueur, non_renouvele ou retire ; ClasseProposee : libre, liste_i, liste_ii, psychotrope ou stupefiant.</summary>
public sealed record MedicamentNomenclatureDto(
    string? NumEnregistrement, string? Code, string? Dci, string? Marque, string? Forme, string? Dosage, string? Conditionnement,
    string? Liste, string? Laboratoire, string? Pays, string Situation, string ClasseProposee, bool DejaAuCatalogue);

/// <summary>Motif : « classe » (substance surveillée mal classée) ou « situation » (retiré / non renouvelé).</summary>
public sealed record ProduitAVerifierDto(long ProduitId, string Nom, string? Dosage, string? Forme, string? Dci,
    string? NumEnregistrement, string Classe, string? ClasseProposee, string? Situation, string Motif);

public sealed record EtatNomenclatureDto(VersionNomenclatureDto? Version, IReadOnlyList<ProduitAVerifierDto> AVerifier);

public sealed record ClasseProduitRequete(string Classe);

// ---- Registre des psychotropes ---------------------------------------------------

/// <summary>Type : ouverture, entree, retour_client, retour_refuse, ecart_plus, sortie, retour_fournisseur, destruction, casse, ecart_moins.</summary>
public sealed record LigneRegistreDto(
    long Numero, DateTime Date, long ProduitId, string Produit, string NumeroLot, string Type, string Operation,
    int? Entree, int? Sortie, int? SoldeProduit, int SoldeLot,
    string? Patient, string? Medecin, string? Ordonnance, string? Fournisseur, string? Reference,
    string? PorteurNom, string? PorteurAdresse, string? PorteurPiece, string Employe, long? PatientId = null);

public sealed record ProduitRegistreDto(long Id, string Nom);

public sealed record BalanceMensuelleDto(long ProduitId, string Produit, int Annee, int Mois, int Entrees, int Sorties, int? SoldeFin);

public sealed record RegistreDto(DateOnly Du, DateOnly Au, string? Produit, IReadOnlyList<LigneRegistreDto> Lignes,
    IReadOnlyList<BalanceMensuelleDto> Balance, bool NomsMasques);

// ---- Ticket de caisse ------------------------------------------------------------

public sealed record LigneTicketDto(string Produit, int Quantite, decimal PrixUnitaire, decimal Montant, decimal PartOrganisme, decimal Shp = 0, bool Medicament = true);

/// <summary>Mode : especes, carte, cheque ou credit ; Statut : validee ou annulee.</summary>
public sealed record TicketDto(
    string Numero, DateTime Date, string Vendeur, string? Poste, string Statut,
    string? Organisme, decimal? TauxTiersPayant, IReadOnlyList<LigneTicketDto> Lignes,
    decimal MontantBrut, decimal Remise, decimal PartOrganisme, decimal PartPatient, string? Mode,
    string? Pharmacie, string? Adresse, string? Telephone, string? Nif, IReadOnlyList<string>? Retours = null);

// ---- Affichage (clair, sombre, automatique) --------------------------------------

/// <summary>Affichage : auto (suit Windows), clair ou sombre.</summary>
public sealed record AffichageDto(string Affichage);

// ---- Ventes en attente -------------------------------------------------------------

public sealed record VenteEnAttenteDto(long Id, string Libelle, int Lignes, decimal Total, string Employe, DateTime CreeLe);

/// <summary>Contenu : panier du poste en JSON (PanierAttenteDto), rendu tel quel à la reprise.</summary>
public sealed record MiseEnAttenteRequete(string? Libelle, int Lignes, decimal Total, string Contenu);

public sealed record LignePanierAttenteDto(long ProduitId, string Nom, decimal Prix, LotFefoDto? Lot, int Quantite, bool BoiteScannee, int StockProduit);

/// <summary>Panier mis en attente par le poste : lignes et règlement en cours de saisie.</summary>
public sealed record PanierAttenteDto(
    IReadOnlyList<LignePanierAttenteDto> Lignes, bool TiersPayant, long OrganismeId, int Taux, PatientDto? Patient,
    MedecinDto? Medecin, DateOnly DatePrescription, string? NumeroOrdonnance, decimal RemisePct, string? JetonScan, string? TexteReconnu);

// ---- Annulation d'une vente ------------------------------------------------------

public sealed record LigneVenteAnnulableDto(string Produit, int Quantite, decimal Montant, string? Lot = null);

/// <summary>Ventes trouvées en scannant la boîte rapportée (Exacte : la boîte désigne sa vente).</summary>
public sealed record VentesParBoiteDto(bool Exacte, string Produit, string? Lot, string? Note, IReadOnlyList<VenteAnnulableDto> Ventes);

/// <summary>CaisseOuverte : vente récente (le caissier peut l'annuler seul) ; sinon validation du pharmacien ou de l'assistant.</summary>
/// <summary>DetteEffaceeAnnulation : vente à crédit, dette de cette vente effacée par l'annulation.</summary>
public sealed record VenteAnnulableDto(
    long Id, string Numero, DateTime Date, string Vendeur, string? Patient, decimal Total, decimal PartPatient, decimal PartOrganisme,
    string? Mode, string Statut, bool CaisseOuverte, bool DansUnBordereau, IReadOnlyList<LigneVenteAnnulableDto> Lignes,
    decimal ARendreAnnulation = 0, string? FactureNumero = null, decimal SoldeCreditPatient = 0, long? VendeurId = null, long? PatientId = null, decimal DetteEffaceeAnnulation = 0);

/// <summary>Validateur* : identifiant et code comptoir du pharmacien ou de l'assistant (vente ancienne).</summary>
/// <summary>SolderDette : l'argent à rendre est retiré de la dette du patient (PatientDetteId : patient nommé, pour une vente sans patient).</summary>
public sealed record AnnulationVenteRequete(string? Motif, string? ValidateurIdentifiant, string? ValidateurCode, bool SolderDette = false, long? PatientDetteId = null,
    bool AccordClient = false);

public sealed record AnnulationVenteDto(string Numero, decimal ARendre, string? Mode, string? ValidePar, decimal DetteDiminuee = 0);

// ---- Retour client ----------------------------------------------------------------

public sealed record BoiteVendueDto(long Id, string NumeroSerie);

public sealed record LigneRetournableDto(long LigneVenteId, string Produit, string NumeroLot, int QuantiteVendue, int DejaRendue,
    decimal MontantPatient, IReadOnlyList<BoiteVendueDto> Boites);

public sealed record VenteRetournableDto(VenteAnnulableDto Vente, IReadOnlyList<LigneRetournableDto> Lignes);

/// <summary>Destination : stock (remis en stock sur son lot) ou destruction ; Boites : n° des boîtes identifiées rendues.</summary>
public sealed record LigneRetourClientDto(long LigneVenteId, int Quantite, string Destination, IReadOnlyList<long>? Boites);

/// <summary>
/// SolderDette : le patient doit de l'argent et le caissier a choisi de retirer le remboursement de sa dette (le surplus est rendu).
/// PatientDetteId : vente sans patient, le patient s'est nommé au retour et c'est sa dette qui est diminuée.
/// </summary>
public sealed record RetourClientRequete(string? Motif, IReadOnlyList<LigneRetourClientDto> Lignes, string? ValidateurIdentifiant, string? ValidateurCode,
    bool SolderDette = false, long? PatientDetteId = null, bool AccordClient = false);

public sealed record RetourClientDto(string Numero, decimal ARendre, string? Mode, int RemisEnStock, int ADetruire, string? ValidePar, decimal DetteDiminuee = 0);

// ---- Règlements des grossistes -------------------------------------------------------

public sealed record GrossisteAReglementDto(long Id, string Nom, decimal Solde, decimal ResteFactures, int FacturesOuvertes);

public sealed record FactureFournisseurDto(long ReceptionId, string? NumeroFacture, string? NumeroBl, DateTime Date, decimal Montant, decimal Regle, decimal Reste);

/// <summary>Moyen : especes, cheque, virement ou effet.</summary>
public sealed record ReglementPasseDto(long Id, DateOnly Date, decimal Montant, string Moyen, string? Reference, DateOnly? Echeance, decimal PartTiroir, string Employe,
    long? EmployeId = null);

/// <summary>Ligne du relevé de la dette envers un grossiste ; Montant positif : la dette augmente, négatif : elle diminue.</summary>
public sealed record MouvementGrossisteDto(DateTime Date, string Type, decimal Montant, decimal SoldeApres, string? Employe, string? Reference, long? EmployeId = null);

public sealed record SituationGrossisteDto(long Id, string Nom, decimal Solde, IReadOnlyList<FactureFournisseurDto> Factures, IReadOnlyList<ReglementPasseDto> Reglements,
    IReadOnlyList<MouvementGrossisteDto>? Mouvements = null, int MouvementsTotal = 0);

public sealed record ImputationDto(long ReceptionId, decimal Montant);

/// <summary>Imputations vides : réparties sur les factures les plus anciennes. PartTiroir : complément en espèces pris dans la caisse ouverte.</summary>
public sealed record ReglementRequete(DateOnly? Date, decimal Montant, string Moyen, string? Reference, string? Banque, DateOnly? Echeance,
    decimal PartTiroir, string? Commentaire, IReadOnlyList<ImputationDto>? Imputations, long? CompteId = null);

// ---- Crédits clients -------------------------------------------------------------------

public sealed record PatientEnCreditDto(long Id, string Nom, string? Telephone, decimal Solde, decimal Plafond, DateTime? DerniereVente);

public sealed record VenteACreditDto(string Numero, DateTime Date, decimal Montant, decimal Regle = 0, decimal Reste = 0);

/// <summary>Ligne du relevé de dette ; Montant positif : la dette augmente, négatif : elle diminue.</summary>
public sealed record MouvementCreditDto(DateTime Date, string Type, decimal Montant, decimal SoldeApres, string? Vente, string? Employe, string? Poste, string? Reference,
    long? EmployeId = null);

public sealed record SituationCreditDto(PatientEnCreditDto Patient, IReadOnlyList<VenteACreditDto> Ventes, IReadOnlyList<MouvementCreditDto>? Mouvements = null,
    decimal DetteReprise = 0, int MouvementsTotal = 0);

/// <summary>Mode : especes, carte ou cheque.</summary>
public sealed record ReglementCreditRequete(decimal Montant, string Mode);

/// <summary>Correction de la dette par le titulaire : nouvelle dette et motif obligatoire.</summary>
public sealed record CorrectionCreditRequete(decimal NouvelleDette, string? Motif);

public sealed record RecuCreditDto(string Numero, string Patient, decimal Montant, string Mode, decimal ResteDu);

/// <summary>Employé actif qui détient un droit ; CodeComptoir : peut reprendre la vente au comptoir par son code.</summary>
public sealed record DetenteurDroitDto(string NomComplet, bool CodeComptoir);

// ---- Dettes et créances de tous les tiers ----------------------------------------------

/// <summary>
/// Somme due. Sens : creance (due à la pharmacie) ou dette (due par elle). Categorie : patient, facture_patient, facture_entreprise,
/// facture_organisme, bordereau, grossiste, effet. Tranche : 0-30, 31-60 ou 61+ jours. EnAlerte : au-delà du délai, rejetée, ou effet à payer sous peu.
/// </summary>
public sealed record LigneSuiviDto(string Sens, string Categorie, string Tiers, string Libelle, DateOnly Depuis, DateOnly? Echeance, decimal Montant,
    bool Rejetee, int Jours, string Tranche, bool EnAlerte, long? TiersId = null);

public sealed record TotalTrancheDto(string Sens, string Tranche, int Nombre, decimal Montant);

public sealed record AlerteDetteDto(string Sens, string Categorie, int Nombre, decimal Montant, string Message);

public sealed record SuiviDettesDto(int DelaiJours, DateOnly Jour, decimal TotalCreances, decimal TotalDettes, IReadOnlyList<LigneSuiviDto> Lignes,
    IReadOnlyList<TotalTrancheDto> Tranches, IReadOnlyList<AlerteDetteDto> Alertes);

public sealed record DelaiAlerteDettesRequete(int Jours);

// ---- Barre d'état du poste ------------------------------------------------------------------

/// <summary>StatutSauvegarde : reussie, echouee, disque_absent ou null (jamais).</summary>
public sealed record EtatPosteDto(bool SauvegardeActive, DateTime? DerniereSauvegarde, string? StatutSauvegarde,
    long? CaisseSessionId, DateTime? CaisseOuverteLe);

// ---- Fiche contact d'un tiers --------------------------------------------------------------

/// <summary>Genre : « telephone », « email » (lien d'appel ou de courrier), « montant_du » (somme due mise en avant), sinon texte.</summary>
public sealed record InfoContactDto(string Libelle, string Valeur, string? Genre);
/// <summary>Type : patient, grossiste, organisme, client ou employe. CoordonneesMasquees : coordonnées d'un employé réservées à lui et au titulaire.</summary>
public sealed record FicheContactDto(string Type, long Id, string Nom, string Precision, bool Actif, IReadOnlyList<InfoContactDto> Coordonnees,
    IReadOnlyList<InfoContactDto> Situation, bool CoordonneesMasquees, CoordonneesContactDto? Saisie = null, bool Modifiable = false);
/// <summary>Coordonnées modifiables depuis la fiche contact (adresse : grossiste et entreprise seulement).</summary>
public sealed record CoordonneesContactDto(string? Telephone, string? Email, string? Adresse, int? DelaiPaiementJours = null);

// ---- Pagination commune aux listes ---------------------------------------------------------

/// <summary>Une page de résultats : Total compte toutes les lignes qui répondent aux filtres, pas seulement celles de la page.</summary>
public sealed record PageDto<T>(IReadOnlyList<T> Lignes, int Total, int Numero, int Taille);

// ---- Restauration (super admin) ----
public sealed record SauvegardeDisponibleDto(string Fichier, string Emplacement, DateTime Date, long Taille);
/// <summary>Base en service, disque externe des sauvegardes (branché ?), copie de la GED, sauvegardes restaurables (plus récente d'abord).</summary>
public sealed record ChoixRestaurationDto(string NomBase, bool BaseExiste, string? DossierExterne, bool DisqueBranche, string? SourceGed,
    List<SauvegardeDisponibleDto> Sauvegardes);
public sealed record RestaurationBaseRequete(string Fichier);
/// <summary>Restauration en cours ou dernière terminée : type (base, ged), étapes, résultat ou erreur.</summary>
public sealed record EtatRestaurationDto(bool EnCours, string? Type, DateTime? Debut, DateTime? Fin, List<string> Etapes, string? Resultat, string? Erreur);

// ---- Surveillance de la caisse (titulaire) ----
/// <summary>Signalé par le poste : ligne_retiree (produit, lot, boîtes, montant) ou tiroir_ouvert.</summary>
public sealed record EvenementCaisseRequete(string Type, long? ProduitId = null, long? LotId = null, int? Quantite = null, decimal? Montant = null);
public sealed record AlerteSurveillanceDto(long Id, string Type, string Niveau, long? UtilisateurId, string? Employe, DateOnly Jour, string Message,
    decimal? Valeur, string Statut, DateTime MiseAJourLe, string? TraiteePar, DateTime? TraiteeLe, string? ActionPrise);
public sealed record ParametresSurveillanceDto(int SeuilLignesRetirees, int SeuilTiroir, int SeuilAttentesAbandonnees, int SeuilRemises,
    int SeuilRemisesPatient, TimeOnly? HeureOuverture, TimeOnly? HeureFermeture,
    int SeuilAjustements = 5, int SeuilCasses = 3, int SeuilEchantillons = 3, int JoursComptageSensibles = 7,
    bool ReceptionAveugle = true, int JoursAvoirRetour = 30,
    decimal SeuilCreditNouveauPatient = 5000, int JoursPatientRecent = 7, decimal SeuilCreditsJour = 20000, int JoursCreditSansRemboursement = 60,
    int SeuilAutorisationsValideur = 10, int SeuilAutorisationsEmploye = 5, int MinutesPostesSimultanes = 10, int SeuilChangementsPoste = 3,
    int SeuilBaissesPrix = 1);
public sealed record ProduitSensibleDto(long ProduitId, string Nom, int Stock);
public sealed record MarquerSensibleRequete(bool Sensible);

// ---- Réception à l'aveugle : comptage, facture comparée, écarts ----
public sealed record ModeReceptionDto(bool Aveugle);
public sealed record LigneCompteeDto(long ProduitId, string Nom, string NumeroLot, DateOnly DatePeremption, int Quantite,
    string? Gtin = null, IReadOnlyList<string>? NumerosSerie = null);
public sealed record CompterLivraisonRequete(long FournisseurId, string? NumeroBl, IReadOnlyList<LigneCompteeDto> Lignes, IReadOnlyList<long>? ScansMobile = null);
/// <summary>Facture et Compte vides pour qui ne décide pas des écarts (réception à l'aveugle).</summary>
public sealed record EcartComptageDto(long ProduitId, string Nom, string NumeroLot, int? Facture, int? Compte);
/// <summary>Comptage en cours : jamais le nombre de boîtes comptées (la facture se saisit sans le voir) ; écarts une fois la facture saisie.</summary>
public sealed record ComptageResumeDto(long Id, long FournisseurId, string Fournisseur, string NumeroBl, string Statut, string Compteur, DateTime CompteLe,
    string? Factureur, DateTime? FactureLe, int NombreLots, IReadOnlyList<EcartComptageDto> Ecarts, int? BoitesManquantes, int? BoitesEnTrop, decimal? ValeurManquante);
/// <summary>Lot compté, à compléter avec la facture (quantité facturée, prix) : sans le nombre de boîtes comptées.</summary>
public sealed record LotAFacturerDto(long ProduitId, string Nom, string NumeroLot, DateOnly DatePeremption, string? Gtin);
public sealed record ComptageAFacturerDto(long Id, long FournisseurId, string NumeroBl, string Compteur, IReadOnlyList<LotAFacturerDto> Lots);
public sealed record ResultatFacturationDto(ReceptionEnregistreeDto? Reception, IReadOnlyList<EcartComptageDto> Ecarts);
public sealed record DecisionEcartReceptionRequete(bool Valider);
public sealed record RecompterLivraisonRequete(IReadOnlyList<LigneCompteeDto> Lignes);

// ---- Écarts dans le temps et risques par employé (titulaire) ----
public sealed record JourEcartsDto(DateOnly Jour, decimal Ecart, decimal Manque, decimal PerteStock, int BoitesAjustees, decimal ValeurFantomes,
    int Anomalies, int Urgentes, IReadOnlyList<string> Employes, IReadOnlyList<string> Postes);
public sealed record RisqueEmployeDto(long UtilisateurId, string Nom, bool Actif, int JoursPresent, int JoursAvecPerte, decimal PerteEnPresence,
    decimal MoyennePresent, decimal MoyenneAbsent, bool Signale, decimal ManqueCaisse, int Clotures, int Anomalies, int Urgentes, int Annulations,
    int RetoursClients, int Remises, decimal Credits, int AutorisationsDemandees, int AutorisationsDonnees, int Casses, int Echantillons, int BoitesAjustees);
/// <summary>JoursPerteMin et Facteur : règle du signalement (au moins JoursPerteMin jours de perte en sa présence, perte moyenne présent ≥ Facteur × absent).</summary>
public sealed record TableauRisquesDto(DateOnly Du, DateOnly Au, IReadOnlyList<JourEcartsDto> Jours, IReadOnlyList<RisqueEmployeDto> Employes,
    int JoursPerteMin, decimal Facteur, int JoursAbsentMin = 3);
