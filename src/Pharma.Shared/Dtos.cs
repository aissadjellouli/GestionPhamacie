namespace Pharma.Shared;

public sealed record ConnexionRequete(string Identifiant, string MotDePasse);
/// <summary>DoitChangerMotDePasse : mot de passe provisoire, le poste ouvre l'écran de changement avant tout.</summary>
public sealed record ConnexionReponse(string Jeton, string NomComplet, DateTime ExpireLe, bool DoitChangerMotDePasse = false);

public sealed record ChangementMotDePasseRequete(string? Actuel, string? Nouveau, string? Confirmation);

/// <summary>Changement d'utilisateur au comptoir : code personnel de 4 à 6 chiffres.</summary>
public sealed record ConnexionCodeRequete(string Identifiant, string Code);

public sealed record VendeurComptoirDto(string Identifiant, string NomComplet);

/// <summary>
/// Employé proposé pour « Faire autoriser » : son nom et une référence opaque (« #numéro ») à renvoyer dans X-Pharma-Autorise-Par,
/// jamais son identifiant de connexion.
/// </summary>
public sealed record ValideurDto(string Reference, string NomComplet);

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

/// <summary>
/// Produit proposé dans la recherche de la caisse : PrixServi (lot qui serait servi, SHP compris) ; Remplacant (produit en rupture,
/// non sélectionnable) ; MoinsCher (même produit d'un autre laboratoire, moins cher, en stock).
/// </summary>
public sealed record ProduitComptoirDto(ProduitDto Produit, decimal PrixServi, ProduitAlternatifDto? Remplacant, ProduitAlternatifDto? MoinsCher);

/// <summary>Exact : même DCI, dosage et forme. Sinon autre dosage ou autre forme, à valider par le pharmacien. PrixPublic : prix servi.
/// ListeBlanche : autorisé par le pharmacien ; FabricationLocale : fabriqué en Algérie (2026-11-16).</summary>
public sealed record ProduitAlternatifDto(long ProduitId, string NomCommercial, decimal PrixPublic, int Stock,
    bool Exact = true, string? Dosage = null, string? Forme = null, string? Laboratoire = null,
    bool ListeBlanche = false, bool FabricationLocale = false);

/// <summary>Lots qui périment dans les N jours (cumulé), au prix d'achat.</summary>
public sealed record HorizonPeremptionDto(int Jours, int Lots, int Boites, decimal? Valeur);
/// <summary>ReglementsAPreparer et MontantAPreparer : null sans le droit de régler les grossistes.</summary>
public sealed record IndicateursAlertesDto(IReadOnlyList<HorizonPeremptionDto> Peremption, int? ReglementsAPreparer, decimal? MontantAPreparer);

public sealed record AlerteDto(
    long Id, string Type, string Niveau, long ProduitId, long? LotId,
    string Message, decimal? ValeurDzd, string Statut, DateTime MiseAJourLe,
    string Produit, string? NumeroLot, string? Emplacement = null);

/// <summary>Page d'alertes (filtrée par le serveur) ; nombre par onglet et par type, et valeur à risque, sur toutes les alertes ouvertes.</summary>
public sealed record AlertesPageDto(PageDto<AlerteDto> Page, Dictionary<string, int> ParFiltre, Dictionary<string, int> ParType, decimal? ValeurARisque);

public sealed record IndicateursStockDto(int ReferencesEnStock, decimal? ValeurAchat, int LotsDateAVerifier);

/// <summary>Statut : ok, rupture, critique, dormant ou surstock.</summary>
public sealed record LigneStockDto(
    long ProduitId, string NomCommercial, string? Dci, string? CodeBarre, string Classe,
    int Stock, int? CouvertureJours, DateOnly? PremierePeremption, int NombreLots,
    IReadOnlyList<string> Grossistes, IReadOnlyList<string> NumerosLots,
    string Statut, bool PeremptionProche, long? EmplacementId = null, string? Emplacement = null, long? ReserveId = null, string? Reserve = null);

public sealed record EtatStockDto(IndicateursStockDto Indicateurs, IReadOnlyList<LigneStockDto> Lignes);

/// <summary>Page du tableau de stock (filtrée par le serveur), indicateurs de tout le stock, propositions pour la saisie de la recherche.</summary>
public sealed record StockPageDto(IndicateursStockDto Indicateurs, PageDto<LigneStockDto> Page, List<string> Suggestions);

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
    string? Ticket, DateTime? DateVente, string? ChifaFacture, string? Patient,
    IReadOnlyList<EtapeParcoursDto> Etapes, long ProduitId);

/// <summary>Refus renvoyé au poste. Champ : champ de saisie en cause (data-champ côté poste), ou null.</summary>
/// <summary>Autorisable : refus de droit que le code comptoir d'un employé habilité peut lever (Permission : le droit, ou « role.titulaire »).</summary>
/// <summary>Seuil : remise refusée (caisse.remise), pourcentage demandé : seuls les employés dont le maximum le couvre sont proposés.</summary>
public sealed record ErreurDto(string Message, string? Champ = null, bool Autorisable = false, string? Permission = null, decimal? Seuil = null);

// ---- Caisse -----------------------------------------------------------------

public sealed record EtatCaisseDto(
    long? SessionId, DateTime? OuverteLe, decimal? FondCaisse, string? Poste,
    bool ComptageAveugle, decimal FondCaisseDefaut, decimal SeuilEcart, int DeconnexionMinutes,
    bool PeutVoirEcart, decimal? VentesEspeces, string? OuvertePar = null, bool ConnexionParCode = false, decimal PlafondCreditDefaut = 0,
    decimal SeuilEcartChifa = 5, int JoursAlerteFinDroit = EtatDroitsChifa.JoursAlerteParDefaut);

public sealed record OuvrirCaisseRequete(string? Poste, decimal? FondCaisse);

/// <summary>Boites : boîtes identifiées scannées (étiquette interne ou n° de série), rattachées à la vente.</summary>
/// <summary>
/// Ligne du panier : un produit et son lot (ProduitId + LotId), ou une prestation (TypePrestationId seul, script 105) ; exactement l'un des
/// deux (400 sinon).
/// </summary>
public sealed record LigneCommandeDto(long? ProduitId, long? LotId, int Quantite, IReadOnlyList<long>? Boites = null, string? RaisonLot = null,
    long? TypePrestationId = null);

/// <summary>Type de prestation vendu à la caisse sans stock (script 105) : prix TTC et taux de TVA figés sur la ligne de vente.</summary>
public sealed record TypePrestationDto(long Id, string Code, string Libelle, decimal Prix, decimal TauxTva, bool Actif,
    bool Remboursable = false, decimal? TarifReference = null);   // remboursable par un organisme (script 108)
/// <summary>Ajout ou modification d'un type de prestation (droit catalogue.creer).</summary>
public sealed record TypePrestationSaisie(string Code, string Libelle, decimal Prix, decimal TauxTva, bool Actif = true,
    bool Remboursable = false, decimal? TarifReference = null);
/// <summary>Indication thérapeutique (script 104) ; NbProduits : produits associés.</summary>
public sealed record IndicationDto(long Id, string Libelle, bool Actif, int NbProduits);
/// <summary>Ajout ou modification d'une indication (droit catalogue.creer) ; jamais supprimée, désactivée.</summary>
public sealed record IndicationSaisie(string Libelle, bool Actif = true);
/// <summary>Indications associées à un produit (remplace la liste existante).</summary>
public sealed record IndicationsProduitRequete(IReadOnlyList<long> IndicationIds);
/// <summary>Médecin obligatoire pour un psychotrope ; JetonScan : image envoyée par POST api/ordonnances/scans.</summary>
/// <summary>Porteur* : à remplir quand le porteur de l'ordonnance n'est pas le patient (psychotrope).</summary>
public sealed record OrdonnanceDto(long? MedecinId, DateOnly DatePrescription, string? Numero,
    string? JetonScan = null, string? TexteReconnu = null,
    string? PorteurNom = null, string? PorteurAdresse = null, string? PorteurPiece = null, int? DureeTraitementJours = null);

/// <summary>
/// Mode : especes, carte, cheque ou credit. CleEncaissement : clé du panier créée par le poste ; renvoyée à l'identique lors d'un
/// nouvel essai, elle rend la vente déjà enregistrée au lieu d'en créer une seconde.
/// </summary>
public sealed record CommandeVenteDto(
    IReadOnlyList<LigneCommandeDto> Lignes, bool TiersPayant, long? OrganismeId, decimal? TauxTiersPayant,
    long? PatientId, OrdonnanceDto? Ordonnance, decimal RemisePct, string Mode, decimal? MontantRecu, decimal? ResteACharge = null,
    ValidationBlocageDto? Blocage = null, string? VoletChifa = null, bool RemiseAdditionnee = false, Guid? CleEncaissement = null,
    IReadOnlyList<ChoixPrixChifaDto>? ChoixChifa = null, bool FactureeParChifa = false, string? NumeroFactureChifa = null,
    PersonneVenteDto? RemisA = null, PersonneVenteDto? Pour = null, string? MotifSansVoletCode = null, string? MotifSansVoletTexte = null,
    decimal MontantAvoir = 0);

/// <summary>
/// « Remis à » (vente avec ordonnance) ou « Pour » (vente sans ordonnance), 2026-11-15 : fiche d'un membre de la famille (PatientId)
/// ou nom libre (Nom, 200 caractères au plus). La vente et sa dette restent au nom du patient de la vente.
/// </summary>
public sealed record PersonneVenteDto(long? PatientId, string? Nom);

/// <summary>Volet Chifa, écart d'au moins le seuil : « aligne » (prix Chifa) ou « garde » (notre prix) ; PrixAttendu revérifié par le serveur.</summary>
public sealed record ChoixPrixChifaDto(long ProduitId, string Choix, decimal? PrixAttendu = null);

/// <summary>Ligne du panier (Index : ordre des lignes) reprise d'un volet Chifa dont le prix diffère du nôtre ; Choix : aligne_auto, aligne, garde, plus_eleve.</summary>
public sealed record EcartChifaLigneDto(int Index, long ProduitId, string Produit, decimal PrixChifa, decimal NotrePrix, decimal PrixApplique,
    string Choix, bool AChoisir);

/// <summary>Accord pour un produit bloqué : confirmé par l'employé habilité, sinon validé par le code comptoir d'un habilité.</summary>
public sealed record ValidationBlocageDto(bool Accepte, string? ValidateurIdentifiant = null, string? ValidateurCode = null);

/// <summary>SurOrdonnance : le panier contient un médicament de liste I ou II (ordonnance, ou droit de le vendre sans).</summary>
public sealed record ApercuVenteDto(
    decimal MontantBrut, decimal PartOrganisme, decimal MontantRemise, decimal PartPatient,
    decimal MontantTotal, bool OrdonnanceRequise, IReadOnlyList<decimal>? PrixLignes = null, decimal Shp = 0, decimal TotalMedicaments = 0,
    IReadOnlyList<string>? ProduitsBloques = null, bool SurOrdonnance = false, IReadOnlyList<SituationLotDto>? Situations = null,
    decimal MontantDon = 0, decimal ReductionsAuto = 0, IReadOnlyList<ReductionLigneDto>? Reductions = null,
    IReadOnlyList<EcartChifaLigneDto>? EcartsChifa = null, decimal EcartChifaPatient = 0, decimal SeuilEcartChifa = 0);

/// <summary>
/// Réductions d'une ligne (ordre des lignes envoyées) : remise automatique (%), pourcentage appliqué, montant ; DonPartiel : médicament,
/// la réduction est un don partiel du reste à charge.
/// </summary>
public sealed record ReductionLigneDto(decimal RemiseAutoPct, decimal PctApplique, decimal Montant, bool DonPartiel);

/// <summary>
/// Raison à demander pour le lot d'une ligne (même ordre que les lignes envoyées) : aucune, peremption (vérifiée, à valider par le
/// vendeur) ou obligatoire (à choisir : introuvable, abimee, demande_client). LotDelaisse : lot plus ancien en stock.
/// </summary>
public sealed record SituationLotDto(long LotId, string Situation, string? LotDelaisse, DateOnly? PeremptionDelaisse);

public sealed record VenteEncaisseeDto(
    long VenteId, string Numero, decimal MontantTotal, decimal PartOrganisme, decimal PartPatient,
    string Mode, decimal Rendu, decimal EcartChifaPatient = 0, string? CatalogueChifa = null, decimal PayeParAvoir = 0);

/// <summary>
/// Saisies par coupure : b2000, b1000, b500, b200, p100 (nombres), autres (montant total). Coupure inconnue refusée.
/// ConfirmerZero : clôture ou relève avec 0 DA compté confirmée explicitement (sinon refusée).
/// </summary>
public sealed record ClotureRequete(IReadOnlyDictionary<string, decimal> Saisies, bool ConfirmerZero = false);

public sealed record ClotureDto(
    long SessionId, decimal Compte, decimal? FondCaisse, decimal? VentesEspeces, decimal? Attendu,
    decimal? Ecart, bool? AuDelaDuSeuil, decimal? SeuilEcart, decimal VerseAuCoffre = 0, List<LigneTiroirDto>? LignesTiroir = null);
/// <summary>Ligne du récapitulatif : sortie (par motif) ou entrée du tiroir de la session (script 096), comprise dans l'attendu.</summary>
public sealed record LigneTiroirDto(string Motif, string Sens, decimal Montant);

/// <summary>Relève : clôture comptée de la session précédente et caisse rouverte avec ce montant.</summary>
public sealed record ReleveDto(ClotureDto Cloture, long NouvelleSessionId);

/// <summary>
/// État intermédiaire de la caisse ouverte : jamais d'espèces, de total ni d'attendu (comptage aveugle strict) ; montants nets
/// par carte, chèque, crédit et part du tiers payant ; comptages intermédiaires (écart seulement avec le droit de voir les écarts).
/// </summary>
public sealed record EtatIntermediaireDto(long SessionId, string Poste, DateTime OuverteLe, string? OuvertePar, decimal FondCaisse,
    int NombreVentes, decimal Carte, decimal Cheque, decimal Credit, decimal TiersPayant, List<VentesEmployeDto> ParEmploye,
    List<ComptageIntermediaireDto> Comptages, bool PeutVoirEcart, decimal Avoir = 0);
public sealed record VentesEmployeDto(string Employe, int Ventes);
public sealed record ComptageIntermediaireDto(DateTime Le, string Employe, decimal Compte, decimal? Ecart);

/// <summary>VersementCloture : manuel ou automatique (argent des caisses clôturées vers le coffre).</summary>
public sealed record ParametresCaisseDto(bool ComptageAveugle, decimal FondCaisseDefaut, decimal SeuilEcart, int DeconnexionMinutes, string? VersementCloture = "manuel",
    decimal PlafondCreditDefaut = 0, int SeuilAnnulations = 0, bool AutorisationParCode = true, decimal SeuilEcartChifa = 5,
    int DelaiVoletChifaJours = 7, decimal SeuilSortieTiroir = 2000, int JoursAlerteFinDroit = EtatDroitsChifa.JoursAlerteParDefaut,
    int DelaiDepotChifaJours = 7, int DelaiPaiementBordereauJours = 45, bool ExigerProduitsRepriseHistorique = true);

/// <summary>Date de fin de droits Chifa saisie à la caisse ou dans la liste de suivi (vide : effacée).</summary>
public sealed record FinDroitDto(DateOnly? DateFinDroit);

/// <summary>Ligne de Patients › « Droits expirés ou bientôt expirés » ; Etat : expires ou bientot.</summary>
public sealed record SuiviDroitChifaDto(long PatientId, string Nom, string? NumAssure, string? Rang, string? Organisme, DateOnly DateFinDroit,
    string Etat, DateTime? DerniereVente);

public sealed record EntreeJournalDto(DateTime Date, string Employe, string Action, string? Details, string? AutorisePar, long? EmployeId = null, long? AutoriseParId = null);

// ---- Patients, médecins, organismes -------------------------------------------

public sealed record PatientDto(
    long Id, string Nom, string? Prenom, string? Telephone, string? NumAssure,
    long? OrganismeId, string? Organisme, decimal PlafondCredit, decimal SoldeCredit, DateOnly? DateFinDroit = null, decimal SoldeAvoir = 0);

/// <summary>Fiche complète d'un patient (SoldeCredit en lecture seule).</summary>
public sealed record FichePatientDto(long Id, string Nom, string? Prenom, DateOnly? DateNaissance, string? Telephone, string? Email, string? NumAssure,
    long? OrganismeId, bool Chronique, decimal PlafondCredit, decimal SoldeCredit = 0, string? Lien = null, long? AssurePrincipalId = null,
    IReadOnlyList<ProchePatientDto>? Candidats = null, ProchePatientDto? Proposition = null, DateOnly? DateFinDroit = null);
/// <summary>
/// Fiche proche d'un patient (script 090) : Candidats = fiches qui peuvent être son assuré principal (même n° de base, assuré actuel) ;
/// Proposition = rattachement proposé (même n° de base, suffixe différent), à accepter ou non par le vendeur.
/// </summary>
public sealed record ProchePatientDto(long Id, string Nom, string? NumAssure, string? Lien);

public sealed record NouveauPatientDto(string Nom, string? Prenom, string? Telephone, string? NumAssure, long? OrganismeId, decimal PlafondCredit, string? Email = null);
/// <summary>Médecin prescripteur ; coordonnées facultatives (2026-11-15).</summary>
/// <remarks>Wilaya : code 01 à 69 (2026-11-16), facultatif.</remarks>
public sealed record MedecinDto(long Id, string Nom, string? Specialite, string? Telephone = null, string? Adresse = null, string? Email = null,
    string? NumeroOrdre = null, string? CodeCnas = null, string? Wilaya = null);
/// <summary>Création ou correction d'un médecin : le nom seul est obligatoire.</summary>
public sealed record NouveauMedecinDto(string Nom, string? Specialite, string? Telephone = null, string? Adresse = null, string? Email = null,
    string? NumeroOrdre = null, string? CodeCnas = null, string? Wilaya = null);
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
    string? Telephone = null, string? Email = null, bool PeutDonner = false, decimal RemiseMaxPct = 0, bool GereRemisesAuto = false);
public sealed record RoleAdminDto(long Id, string Code, string Nom, bool Systeme, IReadOnlyList<string> Permissions);
/// <summary>ParPersonne : droit réglé sur chaque employé (don, remise, remises automatiques), absent de la matrice des rôles.</summary>
public sealed record PermissionAdminDto(string Code, string Module, string Libelle, bool Sensible, bool ParPersonne = false);
/// <summary>Don et remise d'un employé, réglés par le titulaire.</summary>
public sealed record DonRemiseEmployeDto(bool PeutDonner, decimal RemiseMaxPct, bool GereRemisesAuto);
public sealed record EtatDroitsDto(IReadOnlyList<UtilisateurAdminDto> Utilisateurs, IReadOnlyList<RoleAdminDto> Roles, IReadOnlyList<PermissionAdminDto> Permissions);
public sealed record NouvelUtilisateurDto(string NomComplet, string Identifiant, string MotDePasse, long RoleId, string? Telephone = null, string? Email = null);
/// <summary>MotDePasse vide : inchangé.</summary>
public sealed record ModificationUtilisateurDto(string NomComplet, long RoleId, bool Actif, string? MotDePasse, string? Telephone = null, string? Email = null);
public sealed record DroitsRoleDto(long RoleId, IReadOnlyList<string> Permissions);

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

/// <summary>Solde (dette envers le grossiste) : null sans le droit « achats.regler » (titulaire : toujours).</summary>
public sealed record FournisseurDto(long Id, string RaisonSociale, string? Telephone, decimal? Solde, int? DelaiRepriseJours, string? Email = null,
    string? Adresse = null, string? Nif = null, string? Rc = null, bool Actif = true);
/// <summary>Grossiste créé (Id null) ou modifié.</summary>
public sealed record FicheGrossisteDto(long? Id, string? RaisonSociale, string? Telephone, string? Email, string? Adresse, string? Nif, string? Rc,
    int? DelaiRepriseJours, bool Actif = true);

/// <summary>Statut : brouillon, envoyee, recue_partielle, recue ou annulee. AuteurCentral : bon préparé par le serveur central (lot S5.4).</summary>
public sealed record CommandeAchatDto(
    long Id, string Numero, long FournisseurId, string Fournisseur, DateTime Date, string Statut, int NombreLignes, decimal? MontantHt,
    string? AuteurCentral = null);

public sealed record LigneCommandeAchatDto(long Id, long ProduitId, string Produit, int QteCommandee, int QteRecue, decimal? PrixAchatHt);
public sealed record CommandeAchatDetailDto(CommandeAchatDto Entete, IReadOnlyList<LigneCommandeAchatDto> Lignes);

public sealed record SuggestionCommandeDto(
    long ProduitId, string Produit, string Statut, int Stock, int QteSuggeree,
    decimal? DernierPrixHt, long? DernierFournisseurId, string? DernierFournisseur);

/// <summary>Produits à commander paginés ; grossistes des derniers achats pour le filtre ; SansAchat : au moins un produit jamais acheté.</summary>
public sealed record SuggestionsCommandePageDto(PageDto<SuggestionCommandeDto> Page, IReadOnlyList<CodeLibelleDto> Grossistes, bool SansAchat);

/// <summary>Sans prix, le dernier prix d'achat connu est repris.</summary>
public sealed record AjoutCommandeRequete(long FournisseurId, long ProduitId, int Quantite, decimal? PrixAchatHt);
/// <summary>PrixAchatHt null : prix de la ligne inchangé (employé sans le droit de voir les prix d'achat).</summary>
public sealed record ModificationLigneCommandeRequete(int Quantite, decimal? PrixAchatHt);

/// <summary>PrixAchatHt : avant remise, comme sur la facture. Shp null : selon la liste du produit.</summary>
public sealed record LigneReceptionDto(
    long ProduitId, string Nom, string NumeroLot, DateOnly DatePeremption, int Quantite, decimal PrixAchatHt,
    string? Gtin = null, IReadOnlyList<string>? NumerosSerie = null, bool LotSepare = false, bool EntrerBloque = false,
    decimal RemisePct = 0, decimal? Ppa = null, decimal? Shp = null,
    int Ug = 0, bool UgCoutReparti = false, bool RemplacerPrixCatalogue = false);

public sealed record ReceptionRequete(
    long FournisseurId, long? CommandeId, string? NumeroBl, string? NumeroFacture, IReadOnlyList<LigneReceptionDto> Lignes, decimal Timbre = 0,
    ValidationBlocageDto? Blocage = null, DateOnly? DateFacture = null);

/// <summary>Liste du médicament, prix public du catalogue, TVA et SHP proposé par boîte.</summary>
public sealed record InfoAchatProduitDto(long ProduitId, string Classe, decimal PrixPublic, decimal TauxTva, decimal ShpParBoite,
    bool BloqueAchat = false, string? MotifBlocage = null);

public sealed record LotRecuDto(long LotId, string Produit, string NumeroLot, DateOnly DatePeremption, int Quantite, int Etiquettes, bool LotExistant);
/// <summary>EcartsShp : lignes dont le SHP facturé diffère du SHP de la classe du produit (signalées, non bloquantes).</summary>
public sealed record ReceptionEnregistreeDto(long ReceptionId, decimal MontantTtc, IReadOnlyList<LotRecuDto> Lots, IReadOnlyList<string>? EcartsShp = null);

/// <summary>Numero : numéro relu (un scan fait en clavier QWERTY est remis en chiffres) ; DejaRecues : réceptions qui le portent déjà.</summary>
public sealed record DocumentReceptionDto(string? Numero, IReadOnlyList<ReceptionResumeDto> DejaRecues);

public sealed record ReceptionResumeDto(
    long Id, string Fournisseur, string? NumeroBl, string? NumeroFacture, string? NumeroCommande,
    DateTime Date, int NombreLots, int NombreBoites, decimal? MontantTtc, long? FournisseurId = null, DateOnly? DateFacture = null);

/// <summary>Entrée de stock (lot) en consultation : SHP par boîte (script 103), PPA du lot (null : prix du catalogue).</summary>
public sealed record EntreeStockDto(long LotId, long ProduitId, string Produit, string NumeroLot, DateOnly DatePeremption, int Quantite,
    decimal? Ppa, decimal Shp, string Fournisseur, DateTime DateReception, bool Bloque, int Corrections);

/// <summary>Correction d'une entrée de stock (droit stock.corriger_entree) : seuls les champs non nuls sont corrigés ; motif obligatoire.</summary>
public sealed record CorrectionLotSaisie(decimal? Ppa, string? NumeroLot, DateOnly? DatePeremption, decimal? Shp, int? QuantiteRestante, string Motif);

/// <summary>Une ligne du journal des corrections d'une entrée de stock (une par champ corrigé).</summary>
public sealed record CorrectionLotDto(long Id, string Champ, string? AncienneValeur, string? NouvelleValeur, string? Motif, DateTimeOffset CorrigeLe, string CorrigePar);

public sealed record DataMatrixRequete(string Code);
public sealed record BoiteLueDto(string Gtin, string? NumeroLot, DateOnly? Peremption, string? NumeroSerie, long? ProduitId, string? Produit);

public sealed record LotRetournableDto(
    long LotId, long ProduitId, string Produit, string NumeroLot, DateOnly DatePeremption,
    long FournisseurId, string Fournisseur, int Disponible, decimal? PrixAchatHt, bool Bloque, int BoitesIdentifiees);

/// <summary>Motif : peremption_proche, perime, defaut, erreur_livraison ou rappel_lot.</summary>
public sealed record LotARetournerDto(long LotId, int Quantite, IReadOnlyList<string> CodesBoites);
public sealed record PreparationRetourRequete(string Motif, IReadOnlyList<LotARetournerDto> Lots);

public sealed record LigneRetourDto(string Produit, string NumeroLot, int Quantite);

/// <summary>Statut : brouillon (préparé, stock sorti), envoye, avoir_recu ou refuse.</summary>
public sealed record RetourDto(
    long Id, string Numero, long FournisseurId, string Fournisseur, string Motif, string Statut,
    DateTime CreeLe, DateTime? EnvoyeLe, DateTime? AvoirRecuLe, decimal? MontantAttendu, decimal? MontantAvoir,
    IReadOnlyList<LigneRetourDto> Lignes);

public sealed record AvoirRequete(decimal Montant);
public sealed record RefusRetourRequete(string? Motif);

public sealed record LotAEtiqueterDto(long LotId, string Produit, string NumeroLot, DateOnly DatePeremption, int Disponible, int BoitesSansIdentite);
public sealed record EtiquettesRequete(long LotId, int Nombre);

/// <summary>Codes créés, planche PDF (base 64) et texte ZPL pour une imprimante thermique.</summary>
public sealed record EtiquettesDto(IReadOnlyList<string> Codes, string PdfBase64, string Zpl);

// ---- Notifications (titulaire seulement) -------------------------------------

/// <summary>
/// Le mot de passe SMTP n'est jamais renvoyé : seul son enregistrement est indiqué. SignalerSansDestinataire : alerte (ou rapport du
/// soir) partie vers aucun destinataire vérifié signalée au bandeau de l'accueil du titulaire (défaut : oui).
/// </summary>
public sealed record ReglagesNotificationDto(
    string? SmtpServeur, int SmtpPort, bool SmtpTls, string? SmtpExpediteur, string? SmtpIdentifiant, bool MotDePasseEnregistre,
    string? ModemPort, TimeOnly HeureRapport, DateOnly? DernierRapport, decimal? SmsSeuilEcart, bool SmsLotBloque, bool SmsSauvegarde,
    bool SignalerSansDestinataire = true);

/// <summary>NouveauMotDePasse : null = inchangé, chaîne vide = effacé.</summary>
public sealed record ModificationReglagesNotificationDto(
    string? SmtpServeur, int SmtpPort, bool SmtpTls, string? SmtpExpediteur, string? SmtpIdentifiant, string? NouveauMotDePasse,
    string? ModemPort, TimeOnly HeureRapport, decimal? SmsSeuilEcart, bool SmsLotBloque, bool SmsSauvegarde, bool SignalerSansDestinataire = true);

/// <summary>Types : rapport_quotidien, ecart_caisse, lot_bloque, sauvegarde.</summary>
public sealed record DestinataireDto(
    long Id, string Nom, string? Email, string? Telephone, bool CanalEmail, bool CanalSms, IReadOnlyList<string> Types, bool Valide, bool Actif);

public sealed record SaisieDestinataireDto(
    string? Nom, string? Email, string? Telephone, bool CanalEmail, bool CanalSms, IReadOnlyList<string> Types, bool Actif);

public sealed record ValidationDestinataireDto(bool Valide);
/// <summary>Code reçu par le destinataire, saisi par le titulaire.</summary>
public sealed record CodeVerificationDto(string? Code);

/// <summary>Statut : en_attente, envoye ou echec.</summary>
public sealed record MessageSortantDto(
    long Id, string Canal, string Destinataire, string Adresse, string Type, string? Objet, string Corps, string Statut,
    int Tentatives, DateTime? ProchaineTentative, string? DerniereErreur, DateTime CreeLe, DateTime? EnvoyeLe);

public sealed record NombreDto(int Nombre);

/// <summary>Alerte au titulaire qui n’a pas pu partir (bandeau de l’accueil du titulaire).</summary>
public sealed record AlerteNonEnvoyeeDto(long Id, string Type, string Objet, string Raison, DateTime CreeLe);

/// <summary>Alertes non vues : nombre total et les plus récentes.</summary>
public sealed record AlertesNonEnvoyeesDto(int Nombre, IReadOnlyList<AlerteNonEnvoyeeDto> Dernieres);

/// <summary>Marque vues les alertes non envoyées jusqu’à cet identifiant (le plus récent affiché).</summary>
public sealed record AlertesVuesRequete(long JusquA);

/// <summary>
/// Écart de caisse d'une session clôturée (accueil du titulaire) : Ecart négatif = manque, positif = excédent. Employe : celui qui a
/// clôturé la caisse (ou fait la relève) si ClotureePar, sinon (clôture sans trace au journal) celui qui l'a ouverte. ClotureLe : date de
/// la clôture ou de la relève (jamais celle de l'ouverture, donnée à part : OuverteLe). Releve : la session a été close par une relève.
/// </summary>
public sealed record EcartCaisseRecentDto(long SessionId, string Poste, string Employe, DateTime ClotureLe, decimal Ecart, bool ClotureePar = true,
    DateTime? OuverteLe = null, bool Releve = false);

/// <summary>Écarts de caisse récents non vus : nombre total et les plus récents.</summary>
public sealed record EcartsCaisseRecentsDto(int Nombre, IReadOnlyList<EcartCaisseRecentDto> Derniers);

// ---- Sauvegardes ------------------------------------------------------------

/// <summary>RetentionGedJours : jours de conservation de la copie dans la GED (null : réglage du serveur, 7 jours par défaut).</summary>
public sealed record ReglagesSauvegardeDto(bool Actif, string? Dossier, TimeOnly Heure, int Conservation, DateOnly? DerniereExecution,
    int? RetentionGedJours = null, int RetentionGedParDefaut = 7, int? NettoyageGedAnnees = null, string? DossierGed = null,
    int AlerteSansDisqueJours = 7);

/// <summary>Document rangé dans la GED du serveur : chemin « Type\nom » sous le dossier de la GED.</summary>
public sealed record DocumentGedDto(string Chemin);

/// <summary>Statut : reussie, echouee ou disque_absent.</summary>
public sealed record EntreeSauvegardeDto(
    long Id, DateTime Debut, DateTime? Fin, string Statut, string Declenchement, string? Fichier, long? TailleOctets, string? Message, string? Utilisateur);

/// <summary>Base et Serveur : base de données que le serveur utilise et sauvegarde (à titre d'information, sans mot de passe).
/// Le journal se lit à part, par page (api/admin/sauvegardes/journal).</summary>
/// <summary>DerniereReussie : dernière sauvegarde réussie sur le disque externe ; SansDisqueEnRetard : plus vieille que le délai d'alerte réglé.</summary>
public sealed record EtatSauvegardesDto(ReglagesSauvegardeDto Reglages, string? Base = null, string? Serveur = null,
    DateTime? DerniereReussie = null, bool SansDisqueEnRetard = false);
public sealed record ResultatSauvegardeDto(string Statut, string? Fichier, long? TailleOctets, string Message);
/// <summary>Copie de secours (PC de secours, clé USB, autre disque) : réglages, dernière copie vérifiée, dernier échec, dernier test.</summary>
public sealed record CopieSecoursDto(string? Dossier, int AlerteHeures, DateTime? DerniereCopie, string? Fichier, long? Taille, string? Sha256,
    DateTime? DernierEchec, string? MessageEchec, DateTime? DernierTest, bool? DernierTestReussi, string? MessageTest, bool EnRetard);
public sealed record ReglagesCopieSecoursDto(string? Dossier, int AlerteHeures);
public sealed record ResultatTestRestaurationDto(bool Reussi, string? Fichier, string Message);

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

/// <summary>
/// Produit créé ou modifié ; Information : message à afficher (classe imposée à un psychotrope ou stupéfiant saisi hors registre,
/// produit « à vérifier »), sinon null.
/// </summary>
public sealed record ProduitEnregistreDto(long Id, string? Information = null);

/// <summary>
/// Fiche modifiable d'un produit (Classe en lecture seule). Contenance : unités par boîte saisies (facultatif, 1 à 10000) ;
/// ContenanceNomenclature et ConditionnementNomenclature : lecture seule, utilisées seulement si la saisie est vide.
/// </summary>
public sealed record FicheProduitModifiableDto(long Id, string NomCommercial, string? Dci, string? Dosage, string? Forme, string? CodeBarre,
    decimal PrixPublic, decimal? TarifReference, bool Remboursable, decimal TauxTva, bool Actif, string Classe = "libre",
    int? Contenance = null, int? ContenanceNomenclature = null, string? ConditionnementNomenclature = null);

/// <summary>Lot en stock proposé à la sortie (périmé ou cassé).</summary>
public sealed record LotEnStockDto(long LotId, long ProduitId, string Produit, string NumeroLot, DateOnly DatePeremption, int Quantite, bool Bloque, bool SousRegistre);
/// <summary>Motif : perime ou casse.</summary>
public sealed record SortieStockRequete(long LotId, int Quantite, string? Motif, string? Commentaire);

/// <summary>Blocage d'un produit à l'achat et/ou à la vente (046).</summary>
public sealed record BlocageProduitDto(long ProduitId, string Nom, bool BloqueAchat, bool BloqueVente, string? Motif);
public sealed record BlocageProduitRequete(bool BloqueAchat, bool BloqueVente, string? Motif);

public sealed record CategorieProduitDto(string Code, string Libelle, bool Medicament);

/// <summary>Remise automatique (produit OU catégorie) ; Etat : en_cours, a_venir, terminee ou arretee.</summary>
public sealed record RemiseAutomatiqueDto(long Id, long? ProduitId, string? Produit, string? Categorie, string? CategorieLibelle, decimal Pourcentage,
    DateOnly Debut, DateOnly Fin, string? Motif, string CreePar, DateTime CreeLe, DateTime? ArreteeLe, string? ArreteePar, string Etat, bool DuCentral = false);
public sealed record RemiseAutomatiqueSaisieDto(long? ProduitId, string? Categorie, decimal Pourcentage, DateOnly Debut, DateOnly Fin, string? Motif);
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
public sealed record TableauInventaireDto(IReadOnlyList<RayonInventaireDto> Rayons, string? RayonPropose, ParametresInventaireDto Parametres, IReadOnlyList<EcartAValiderDto> EcartsAValider,
    EtatSensiblesDto? Sensibles = null);   // historique : à part, par page (api/inventaires/historique)
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
    int Retours = 0, decimal MontantRetours = 0, decimal VentesMedicaments = 0, decimal VentesAutres = 0, decimal Shp = 0,
    decimal DonsPartiels = 0, decimal RemisesAutomatiques = 0);
/// <summary>
/// Ventes du jour de l'accueil (droits rapports.ventes et caisse.voir_ecart) et même jour de la semaine précédente arrêté à la même heure
/// (Heure, heure du serveur). Marge : null sans rapports.marges.
/// </summary>
public sealed record VentesDuJourDto(DateOnly Jour, TimeOnly Heure, SyntheseVentesDto Aujourdhui, DateOnly JourReference, SyntheseVentesDto Reference, MargeBruteDto? Marge);
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
/// <summary>Clôtures de la période, paginées et filtrées par le serveur ; employés et postes de la période pour préremplir les filtres.</summary>
public sealed record CloturesPageDto(PageDto<ClotureRapportDto> Page, bool EcartsVisibles, List<string> Employes, List<string> Postes);

public sealed record ClotureRapportDto(string Employe, string Poste, DateTime OuverteLe, DateTime? FermeeLe,
    decimal? Attendu, decimal? Compte, decimal? Ecart, int Annulations, int Remises, int Retours = 0, long? EmployeId = null);
/// <summary>Periode : jour, semaine ou mois. Marge : null sans le droit « rapports.marges ».</summary>
public sealed record RapportActiviteDto(string Periode, DateOnly Du, DateOnly Au, string Libelle, SyntheseVentesDto Synthese, MargeBruteDto? Marge,
    IReadOnlyList<VentesJourDto> ParJour, IReadOnlyList<ProduitVenduDto> TopProduits, IReadOnlyList<ClotureRapportDto> Clotures, bool EcartsVisibles,
    bool ParSemaine = false, IReadOnlyList<RetourRapportDto>? Retours = null, SortiesRapportDto? Sorties = null,
    TvaSortiesDto? Tva = null, MargeApresSortiesDto? ApresSorties = null);

/// <summary>TVA d'achat à reverser selon le motif de la sortie sans vente (réglage du titulaire).</summary>
public sealed record TvaSortiesDto(bool Perime, bool Casse, bool Don);
/// <summary>Marge brute - valeur d'achat HT des pertes et dons - TVA à reverser (droit de voir les marges).</summary>
public sealed record MargeApresSortiesDto(decimal MargeBrute, decimal PertesHt, decimal TvaAReverser, decimal Montant);

/// <summary>Boîte(s) sortie(s) du stock sans vente : Motif « perime », « casse » ou « echantillon ». Valeur : coût d'achat HT, null sans le droit de voir les marges.</summary>
public sealed record SortieSansVenteDto(DateTime Date, string Motif, string Produit, string Lot, int Quantite, decimal? Valeur, string Employe,
    string? Reference, long? EmployeId = null, decimal? TvaAchat = null);
public sealed record TotalSortiesDto(string Cle, int Perimes, int Casses, int Dons, int Boites, decimal? Valeur);
/// <summary>Sorties de stock sans vente de la période : total, par employé, par produit et détail.</summary>
public sealed record SortiesRapportDto(TotalSortiesDto Total, IReadOnlyList<TotalSortiesDto> ParEmploye, IReadOnlyList<TotalSortiesDto> ParProduit,
    IReadOnlyList<SortieSansVenteDto> Detail);

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
/// <summary>ExigerProduitsHistorique / InformationProduitsHistorique : option du titulaire sur le fichier des produits pour l'historique Chifa seul.</summary>
public sealed record ScanRepriseDto(string Pilote, string? Version, IReadOnlyList<TableSourceDto> Tables, IReadOnlyList<PropositionRepriseDto> Propositions,
    IReadOnlyList<ChampRepriseDto> Champs, bool ExigerProduitsHistorique = true, string? InformationProduitsHistorique = null);
public sealed record CorrespondanceTableDto(string Cible, string Table, IReadOnlyDictionary<string, string> Colonnes);
/// <summary>ProfilId : profil de reprise dont les transformations (séparateur décimal, format de date, codes de classe) s'appliquent (114).</summary>
public sealed record ExtractionRepriseRequete(string ChaineConnexion, string Logiciel, IReadOnlyList<CorrespondanceTableDto> Correspondances, long? ProfilId = null);
/// <summary>
/// Profil de reprise d'un logiciel précédent (114). Statut : a_completer (modèle vide, format réel inconnu) ou verifie.
/// SeparateurDecimal : auto, virgule, point ; FormatDate : motif proposé par FormatsProfilRepriseDto ; CodesClasse : code de l'ancien logiciel → classe.
/// </summary>
public sealed record ProfilRepriseDto(long Id, string Logiciel, string Version, string Statut, string? Remarque,
    IReadOnlyList<CorrespondanceTableDto> Correspondances, string SeparateurDecimal, string? FormatDate, IReadOnlyDictionary<string, string> CodesClasse,
    DateTime? ModifieLe = null, string? ModifiePar = null);
/// <summary>Formats de date et classes proposés à l'écran des profils, et champs de reprise de chaque cible.</summary>
public sealed record FormatsProfilRepriseDto(IReadOnlyList<string> FormatsDate, IReadOnlyList<string> Classes, IReadOnlyList<ChampRepriseDto> Champs);
/// <summary>Vente Chifa de l'ancien logiciel, en lecture seule (114) : aucune vente n'est recréée.</summary>
public sealed record HistoriqueChifaDto(long Id, long SessionId, string Logiciel, string NumFacture, DateOnly DateFacture, string? NumAssure, string? NomAssure,
    decimal? MontantTotal, decimal? PartChifa, decimal? PartAssure, string Organisme = "CHIFA");
public sealed record ExtractionRepriseDto(long SessionId, IReadOnlyDictionary<string, int> Lignes, int Doublons, int Bloquants);
/// <summary>LancePar : qui a lancé l'extraction ; ChargeLe, ChargePar : chargement définitif (date et auteur).</summary>
public sealed record SessionRepriseDto(long Id, string Logiciel, DateTime Debut, string Statut, string? LancePar = null, DateTime? ChargeLe = null, string? ChargePar = null);

// ---- Super admin ----
/// <summary>Avant une action sensible : action prévue et mot de passe ressaisi ; un code part sur le téléphone du super admin.</summary>
public sealed record ValidationSuperAdminRequete(string Action, string MotDePasse);
/// <summary>Code envoyé : par quel canal, à quel destinataire (masqué), jusqu'à quand.</summary>
/// <summary>Code envoyé : canaux utilisés et destinataires masqués ; Message = phrase à montrer (canal en échec dit).</summary>
public sealed record DemandeValidationDto(Guid Id, string Canal, string Destinataire, DateTime ExpireLe, string? Message = null);
public sealed record CompteSuperAdminDto(string Identifiant, string? Telephone, string? Email);
public sealed record ProduitAConfirmerDto(string RefSource, string Nom, string? Dosage, string? CodeBarre, long? ProduitPropose, string? NomPropose, decimal? Score);
public sealed record AnomalieRepriseDto(string Entite, string? RefSource, string Gravite, string Message);
public sealed record VerificationRepriseDto(string Indicateur, decimal Ancien, decimal Nouveau, decimal Ecart);
public sealed record EtatRepriseDto(SessionRepriseDto Session, IReadOnlyDictionary<string, int> Lignes, IReadOnlyDictionary<string, int> Rapprochement,
    IReadOnlyList<ProduitAConfirmerDto> AConfirmer, IReadOnlyList<AnomalieRepriseDto> Anomalies, IReadOnlyList<VerificationRepriseDto> Verification);
/// <summary>Decision : confirmer, autre (ProduitId obligatoire) ou creer.</summary>
public sealed record DecisionProduitRepriseRequete(string RefSource, string Decision, long? ProduitId);

/// <summary>Doublon de n° d'assuré de la reprise, à valider (092). Statut : a_valider, garde_existant, remplace, ayant_droit ; Dette : rattachee, abandonnee.</summary>
public sealed record DoublonImportDto(long Id, long SessionId, string Logiciel, string RefSource, int? Ligne, string Nom, string? Prenom, string? Telephone,
    string NumAssure, decimal SoldeCredit, string Origine, long PatientExistantId, string ExistantNom, string? ExistantNumAssure, string? ExistantTelephone,
    decimal ExistantSolde, string Statut, string? Dette, long? PatientCreeId, string? DecidePar, DateTime? DecideLe);
/// <summary>Decision : garder_existant, remplacer, ayant_droit ; Dette : rattacher, abandonner (obligatoire si dette reprise) ; NumAssure : ayant droit seulement.</summary>
public sealed record DecisionDoublonImportRequete(string Decision, string? Dette, string? NumAssure);

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
    string? PorteurNom, string? PorteurAdresse, string? PorteurPiece, string Employe, long? PatientId = null,
    string? Annulation = null, int? DureeTraitementJours = null);

public sealed record ProduitRegistreDto(long Id, string Nom);

/// <summary>SoldeReporte : solde d'ouverture du registre inscrit ce mois-là, distinct des entrées.</summary>
public sealed record BalanceMensuelleDto(long ProduitId, string Produit, int Annee, int Mois, int Entrees, int Sorties, int? SoldeFin, int SoldeReporte = 0);

/// <summary>Inscriptions du registre paginées et filtrées par le serveur (produit, période, opération) ; opérations présentes pour le filtre.</summary>
public sealed record InscriptionsRegistreDto(PageDto<LigneRegistreDto> Page, IReadOnlyList<CodeLibelleDto> Operations, bool NomsMasques);

/// <summary>En-tête de l'écran du registre : nombre d'inscriptions et balance mensuelle de la période (sans limite de durée) ;
/// les inscriptions se lisent par page (InscriptionsRegistreDto).</summary>
public sealed record RegistreDto(DateOnly Du, DateOnly Au, string? Produit, int Inscriptions,
    IReadOnlyList<BalanceMensuelleDto> Balance, bool NomsMasques);

// ---- Ticket de caisse ------------------------------------------------------------

public sealed record LigneTicketDto(string Produit, int Quantite, decimal PrixUnitaire, decimal Montant, decimal PartOrganisme, decimal Shp = 0, bool Medicament = true,
    decimal Reduction = 0, decimal PctReduction = 0, bool DonPartiel = false, decimal? TauxTva = null)
{
    /// <summary>Libellé imprimé : une prestation (TauxTva renseigné, script 105) porte son taux de TVA.</summary>
    public string Designation => TauxTva is { } t
        ? $"{Produit} (prestation, TVA {t.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"))} %)" : Produit;
}

/// <summary>Mode : especes, carte, cheque ou credit ; Statut : validee ou annulee.</summary>
public sealed record TicketDto(
    string Numero, DateTime Date, string Vendeur, string? Poste, string Statut,
    string? Organisme, decimal? TauxTiersPayant, IReadOnlyList<LigneTicketDto> Lignes,
    decimal MontantBrut, decimal Remise, decimal PartOrganisme, decimal PartPatient, string? Mode,
    string? Pharmacie, string? Adresse, string? Telephone, string? Nif, IReadOnlyList<string>? Retours = null, decimal DonPartiel = 0,
    decimal EcartChifa = 0, string? Client = null, decimal MajorationChifa = 0, decimal PayeParAvoir = 0);

// ---- Affichage (clair, sombre, automatique) --------------------------------------

/// <summary>Affichage : auto (suit Windows), clair ou sombre.</summary>
public sealed record AffichageDto(string Affichage);

// ---- Ventes en attente -------------------------------------------------------------

/// <summary>Motif : texte du motif de mise en attente (null : automatique ou ancienne) ; ProduitsDus : motif « produit manquant ».</summary>
public sealed record VenteEnAttenteDto(long Id, string Libelle, int Lignes, decimal Total, string Employe, DateTime CreeLe,
    string? Motif = null, IReadOnlyList<ProduitDuDto>? ProduitsDus = null);

/// <summary>
/// Contenu : panier du poste en JSON (PanierAttenteDto), rendu tel quel à la reprise. Motif (script 094) : obligatoire au bouton « Attente »,
/// absent des mises en attente automatiques (départ de la caisse, changement d'utilisateur).
/// </summary>
public sealed record MiseEnAttenteRequete(string? Libelle, int Lignes, decimal Total, string Contenu,
    string? MotifCode = null, string? MotifTexte = null, IReadOnlyList<ProduitDuDto>? ProduitsDus = null);

/// <summary>Réception mise en attente. Mode : reception, facture, comptage ou recompte (ces deux-là repris par EmployeId seul).</summary>
public sealed record ReceptionEnAttenteDto(long Id, string Libelle, string Mode, int Lignes, long EmployeId, string Employe, string Poste, DateTime CreeLe);

/// <summary>Contenu : saisie de l'écran Réceptions en JSON, rendue telle quelle à la reprise ; Scans : scans du mobile qu'elle contient.</summary>
public sealed record MiseEnAttenteReceptionRequete(string? Libelle, string Mode, int Lignes, string Contenu, IReadOnlyList<long>? Scans);

/// <summary>Boites : boîtes identifiées scannées (rattachées à la vente) ; RaisonLot et LotConseilleServi : choix du caissier sur le lot.</summary>
public sealed record LignePanierAttenteDto(long ProduitId, string Nom, decimal Prix, LotFefoDto? Lot, int Quantite, bool BoiteScannee, int StockProduit,
    IReadOnlyList<long>? Boites = null, string? RaisonLot = null, bool LotConseilleServi = false,
    long? TypePrestationId = null, decimal? TauxTva = null);   // prestation (script 105) : ProduitId 0, sans lot

/// <summary>Panier mis en attente par le poste : lignes et règlement en cours de saisie.</summary>
public sealed record PanierAttenteDto(
    IReadOnlyList<LignePanierAttenteDto> Lignes, bool TiersPayant, long OrganismeId, int Taux, PatientDto? Patient,
    MedecinDto? Medecin, DateOnly DatePrescription, string? NumeroOrdonnance, decimal RemisePct, string? JetonScan, string? TexteReconnu,
    string? VoletChifa = null, decimal? ResteSaisi = null, bool PorteurAutre = false, string? PorteurNom = null, string? PorteurAdresse = null,
    string? PorteurPiece = null, bool RemiseAdditionnee = false, long? RemiseProcheId = null, string? RemiseProcheNom = null, string? RemiseNom = null,
    IReadOnlyList<ChoixChifaAttenteDto>? ChoixChifa = null,
    int? DureeTraitementJours = null);   // durée du traitement saisie (2026-11-16) : gardée avec la vente, rendue à la reprise ; format ancien : vide

/// <summary>Choix de prix d'une ligne du volet Chifa gardé avec la vente en attente (2026-11-15) : aligne, garde ou plus_eleve.</summary>
public sealed record ChoixChifaAttenteDto(long ProduitId, string Choix);

/// <summary>
/// Choix de prix Chifa d'une vente mise en attente (décision du titulaire, 2026-11-15) : gardés avec le panier et rétablis à la reprise
/// sans les refaire ; le serveur les revérifie à l'encaissement (droit, seuil, prix attendu). Vente en attente d'un format ancien
/// (sans choix) : reprise sans les choix.
/// </summary>
public static class ChoixChifaAttente
{
    public static readonly IReadOnlySet<string> Connus = new HashSet<string> { "aligne", "garde", "plus_eleve" };

    /// <summary>Choix à garder avec le panier : aucun sans volet.</summary>
    public static IReadOnlyList<ChoixChifaAttenteDto>? Garder(string? voletBrut, IReadOnlyDictionary<long, string> choix) =>
        voletBrut is null || choix.Count == 0 ? null : choix.Select(c => new ChoixChifaAttenteDto(c.Key, c.Value)).ToList();

    /// <summary>Choix rétablis à la reprise : volet relu, choix connus, produits encore dans le panier ; sinon aucun.</summary>
    public static Dictionary<long, string> Restaurer(PanierAttenteDto panier, bool voletRelu)
    {
        var resultat = new Dictionary<long, string>();
        if (!voletRelu || panier.VoletChifa is null || panier.ChoixChifa is null) return resultat;
        var produits = panier.Lignes.Select(l => l.ProduitId).ToHashSet();
        foreach (var c in panier.ChoixChifa)
            if (c is not null && Connus.Contains(c.Choix) && produits.Contains(c.ProduitId)) resultat[c.ProduitId] = c.Choix;
        return resultat;
    }
}

/// <summary>
/// Détail facultatif d'une demande « Faire autoriser », affiché au valideur sous le motif (tableau : en-têtes et lignes de texte).
/// Permission : la demande ne le montre que si le refus porte sur ce droit.
/// </summary>
public sealed record DetailAutorisationDto(string Titre, IReadOnlyList<string> Colonnes, IReadOnlyList<IReadOnlyList<string>> Lignes, string? Permission = null);

/// <summary>Détail « Faire autoriser » d'un choix de prix Chifa (2026-11-15) : lignes en écart dont le prix est choisi.</summary>
public static class DetailAutorisationChifa
{
    /// <summary>Produit, prix Chifa, notre prix, écart par boîte, choix demandé ; null s'il n'y a aucune ligne à faire autoriser.</summary>
    public static DetailAutorisationDto? Construire(IEnumerable<EcartChifaLigneDto>? ecarts, IReadOnlyDictionary<long, string> choix, Func<decimal, string> dinars)
    {
        var lignes = new List<IReadOnlyList<string>>();
        foreach (var e in ecarts ?? [])
        {
            if (!e.AChoisir || !choix.TryGetValue(e.ProduitId, out var c)) continue;
            var libelle = c switch { "aligne" => "S’aligner sur Chifa", "garde" => "Garder notre prix", _ => null };
            if (libelle is null) continue;
            IReadOnlyList<string> ligne = [e.Produit, dinars(e.PrixChifa), dinars(e.NotrePrix), dinars(Math.Abs(e.PrixChifa - e.NotrePrix)), libelle];
            // Produit servi sur plusieurs lots au même prix : une seule ligne
            if (!lignes.Any(l => l.SequenceEqual(ligne))) lignes.Add(ligne);
        }
        return lignes.Count == 0 ? null
            : new DetailAutorisationDto("Prix des lignes du volet Chifa", ["Produit", "Prix Chifa", "Notre prix", "Écart par boîte", "Choix demandé"], lignes, ActionChoixPrixChifa);
    }

    /// <summary>Action refusée par le serveur (CodeComptoir.ActionChoixPrixChifa) : le détail ne s'affiche que pour ce refus.</summary>
    public const string ActionChoixPrixChifa = "prix.modifier:choix_prix_chifa";
}

// ---- Annulation d'une vente ------------------------------------------------------

public sealed record LigneVenteAnnulableDto(string Produit, int Quantite, decimal Montant, string? Lot = null);

/// <summary>Ventes trouvées en scannant la boîte rapportée (Exacte : la boîte désigne sa vente).</summary>
public sealed record VentesParBoiteDto(bool Exacte, string Produit, string? Lot, string? Note, IReadOnlyList<VenteAnnulableDto> Ventes);

/// <summary>CaisseOuverte : vente récente (le caissier peut l'annuler seul) ; sinon validation du pharmacien ou de l'assistant.</summary>
/// <summary>DetteEffaceeAnnulation : vente à crédit, dette de cette vente effacée par l'annulation.</summary>
public sealed record VenteAnnulableDto(
    long Id, string Numero, DateTime Date, string Vendeur, string? Patient, decimal Total, decimal PartPatient, decimal PartOrganisme,
    string? Mode, string Statut, bool CaisseOuverte, bool DansUnBordereau, IReadOnlyList<LigneVenteAnnulableDto> Lignes,
    decimal ARendreAnnulation = 0, string? FactureNumero = null, decimal SoldeCreditPatient = 0, long? VendeurId = null, long? PatientId = null, decimal DetteEffaceeAnnulation = 0,
    string? Remise = null, string? RegularisationSort = null, decimal RegularisationDue = 0, decimal RegularisationTropPaye = 0,
    decimal TropPayeCredit = 0, decimal PayeParAvoir = 0, decimal AvoirRecreditable = 0);

/// <summary>Validateur* : identifiant et code comptoir du pharmacien ou de l'assistant (vente ancienne).</summary>
/// <summary>SolderDette : l'argent à rendre est retiré de la dette du patient (PatientDetteId : patient nommé, pour une vente sans patient).</summary>
public sealed record AnnulationVenteRequete(string? Motif, string? ValidateurIdentifiant, string? ValidateurCode, bool SolderDette = false, long? PatientDetteId = null,
    bool AccordClient = false, List<ImputationTropPayeDto>? TropPayeImputations = null, decimal? TropPayeRembourse = null, decimal? TropPayeAvoir = null);

/// <summary>Trop-payé d'une régularisation annulée imputé sur une autre vente à crédit du patient (choix du patient, script 098).</summary>
public sealed record ImputationTropPayeDto(long VenteId, decimal Montant);

public sealed record AnnulationVenteDto(string Numero, decimal ARendre, string? Mode, string? ValidePar, decimal DetteDiminuee = 0,
    decimal RegularisationDetteAnnulee = 0, decimal RegularisationImputee = 0, decimal RegularisationRembourse = 0, long? BonSortieId = null,
    string? BonSortieNumero = null, decimal TropPayeImpute = 0, decimal AvoirCree = 0);

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
/// MotifCode (script 104) : motif obligatoire de la liste réglable « retour_client » ; Motif : précision facultative, obligatoire pour « Autre ».
/// </summary>
public sealed record RetourClientRequete(string? Motif, IReadOnlyList<LigneRetourClientDto> Lignes, string? ValidateurIdentifiant, string? ValidateurCode,
    bool SolderDette = false, long? PatientDetteId = null, bool AccordClient = false, string? MotifCode = null);

public sealed record RetourClientDto(string Numero, decimal ARendre, string? Mode, int RemisEnStock, int ADetruire, string? ValidePar, decimal DetteDiminuee = 0,
    decimal AvoirRecredite = 0);

// ---- Règlements des grossistes -------------------------------------------------------

public sealed record GrossisteAReglementDto(long Id, string Nom, decimal Solde, decimal ResteFactures, int FacturesOuvertes);

public sealed record FactureFournisseurDto(long ReceptionId, string? NumeroFacture, string? NumeroBl, DateTime Date, decimal Montant, decimal Regle, decimal Reste);

/// <summary>Moyen : especes, cheque, virement, prelevement ou effet (affiché « traite »).</summary>
public sealed record ReglementPasseDto(long Id, DateOnly Date, decimal Montant, string Moyen, string? Reference, DateOnly? Echeance, decimal PartTiroir, string Employe,
    long? EmployeId = null, bool Annule = false, DateTime? AnnuleLe = null, string? MotifAnnulation = null);

/// <summary>Ligne du relevé de la dette envers un grossiste ; Montant positif : la dette augmente, négatif : elle diminue.</summary>
public sealed record MouvementGrossisteDto(DateTime Date, string Type, decimal Montant, decimal SoldeApres, string? Employe, string? Reference, long? EmployeId = null);

public sealed record SituationGrossisteDto(long Id, string Nom, decimal Solde, IReadOnlyList<FactureFournisseurDto> Factures, IReadOnlyList<ReglementPasseDto> Reglements,
    IReadOnlyList<MouvementGrossisteDto>? Mouvements = null, int MouvementsTotal = 0);

public sealed record ImputationDto(long ReceptionId, decimal Montant);

/// <summary>
/// Imputations vides : réparties sur les factures les plus anciennes. PartTiroir : complément en espèces pris dans la caisse ouverte.
/// Source (titulaire seul) : « titulaire » ou « pret:ID », facture payée par un prêt ou un apport, hors trésorerie (Moyen ignoré).
/// </summary>
public sealed record ReglementRequete(DateOnly? Date, decimal Montant, string Moyen, string? Reference, string? Banque, DateOnly? Echeance,
    decimal PartTiroir, string? Commentaire, IReadOnlyList<ImputationDto>? Imputations, long? CompteId = null, string? Source = null);

/// <summary>
/// Traite remise à un grossiste. CompteId / Banque : banque choisie à la remise (débitée à l'échéance sans action). PayeeLe /
/// MoyenPaiement : payée (moyen especes, cheque, virement, prelevement, ou debit_echeance), null sinon.
/// </summary>
public sealed record TraiteAPayerDto(long ReglementId, string? Numero, long FournisseurId, string Grossiste, string? Reference, string? BanqueGrossiste,
    DateOnly DateRemise, DateOnly Echeance, decimal Montant, long? CompteId, string? Banque, DateOnly? PayeeLe = null, string? MoyenPaiement = null);

/// <summary>Une page de traites (filtrée par le serveur) ; SoldeCoffre : pour le titulaire seulement (complément du tiroir proposé).</summary>
public sealed record TraitesAPayerDto(PageDto<TraiteAPayerDto> Traites, decimal? SoldeCoffre);

/// <summary>
/// Moyen : especes, cheque, virement ou prelevement ; CompteId obligatoire hors espèces ; PartTiroir : complément pris dans la caisse ouverte.
/// Source : « titulaire » ou « pret:ID », traite payée par un prêt ou un apport, hors trésorerie (Moyen ignoré).
/// </summary>
public sealed record PaiementTraiteRequete(DateOnly? Date, string? Moyen, long? CompteId, string? Reference, decimal PartTiroir, string? Source = null);

// ---- Crédits clients -------------------------------------------------------------------

/// <summary>Regularisation : ticket de la dernière régularisation Chifa mise à la charge du patient (« Régularisation Chifa (T-…) »).</summary>
public sealed record PatientEnCreditDto(long Id, string Nom, string? Telephone, decimal Solde, decimal Plafond, DateTime? DerniereVente, string? Regularisation = null);

public sealed record VenteACreditDto(string Numero, DateTime Date, decimal Montant, decimal Regle = 0, decimal Reste = 0, long Id = 0, string? Facture = null,
    string? Remise = null);

/// <summary>Ligne du relevé de dette ; Montant positif : la dette augmente, négatif : elle diminue.</summary>
public sealed record MouvementCreditDto(DateTime Date, string Type, decimal Montant, decimal SoldeApres, string? Vente, string? Employe, string? Poste, string? Reference,
    long? EmployeId = null, string? Remise = null);

public sealed record SituationCreditDto(PatientEnCreditDto Patient, IReadOnlyList<VenteACreditDto> Ventes, IReadOnlyList<MouvementCreditDto>? Mouvements = null,
    decimal DetteReprise = 0, int MouvementsTotal = 0);

/// <summary>Mode : especes, carte ou cheque. Ventes : ventes à crédit cochées (2026-11-15), le montant leur est imputé ; vide : dette la plus ancienne d'abord.</summary>
public sealed record ReglementCreditRequete(decimal Montant, string Mode, IReadOnlyList<long>? Ventes = null);

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

/// <summary>Tiers proposé dans le filtre de la liste : Valeur à renvoyer (identifiant, ou « nom:… » sans identifiant), nom affiché.</summary>
public sealed record TiersSuiviDto(string Valeur, string Nom);

/// <summary>Liste des sommes dues filtrée et paginée par le serveur ; Total : montant de tout le filtre (toutes pages).</summary>
public sealed record SuiviDettesLignesDto(PageDto<LigneSuiviDto> Page, decimal Total, IReadOnlyList<TiersSuiviDto> Tiers);

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
/// <summary>Signalé par le poste : ligne_retiree (produit, lot, boîtes, montant) ou tiroir_ouvert (motif obligatoire, « autre » avec texte).</summary>
public sealed record EvenementCaisseRequete(string Type, long? ProduitId = null, long? LotId = null, int? Quantite = null, decimal? Montant = null,
    string? MotifCode = null, string? MotifTexte = null, SaisieTiroirDto? Tiroir = null);
/// <summary>Motif du tiroir à effet (script 096) : montant et détail exigé (catégorie et libellé, grossiste et BL ou nom, patient et raison).</summary>
public sealed record SaisieTiroirDto(decimal? Montant, string? Categorie = null, string? Libelle = null, long? FournisseurId = null,
    string? Beneficiaire = null, string? Reference = null, long? PatientId = null, string? Raison = null, long? RegularisationVenteId = null);
/// <summary>Bon de sortie (ou d'entrée) créé par un motif à effet ; vide pour un motif sans effet.</summary>
public sealed record OuvertureTiroirDto(long? MouvementId, string? Numero);
/// <summary>Sortie ou entrée du tiroir (contrôle interne). Corrigee : une écriture inverse existe ; CorrigeId : ce mouvement en est une.</summary>
public sealed record MouvementTiroirDto(long Id, string Numero, DateTime Le, long UtilisateurId, string Employe, string? Poste, string Motif, string Sens,
    decimal Montant, string? Categorie, string? Libelle, string? Beneficiaire, string? Reference, string? Patient, string? Raison, string? ValidePar,
    long? CorrigeId, string? MotifCorrection, bool Corrigee, long? ReglementFournisseurId = null);
public sealed record PageMouvementsTiroirDto(List<MouvementTiroirDto> Lignes, int Total, int Numero, int Taille, decimal TotalSorties, decimal TotalEntrees);
public sealed record CorrectionTiroirRequete(string? Motif);
public sealed record AlerteSurveillanceDto(long Id, string Type, string Niveau, long? UtilisateurId, string? Employe, DateOnly Jour, string Message,
    decimal? Valeur, string Statut, DateTime MiseAJourLe, string? TraiteePar, DateTime? TraiteeLe, string? ActionPrise);
public sealed record ParametresSurveillanceDto(int SeuilLignesRetirees, int SeuilTiroir, int SeuilAttentesAbandonnees, int SeuilRemises,
    int SeuilRemisesPatient, TimeOnly? HeureOuverture, TimeOnly? HeureFermeture,
    int SeuilAjustements = 5, int SeuilCasses = 3, int SeuilEchantillons = 3, int JoursComptageSensibles = 7,
    bool ReceptionAveugle = true, int JoursAvoirRetour = 30,
    decimal SeuilCreditNouveauPatient = 5000, int JoursPatientRecent = 7, decimal SeuilCreditsJour = 20000, int JoursCreditSansRemboursement = 60,
    int SeuilAutorisationsValideur = 10, int SeuilAutorisationsEmploye = 5, int MinutesPostesSimultanes = 10, int SeuilChangementsPoste = 3,
    int SeuilBaissesPrix = 1, int SeuilSortiesTiroir = 5);
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

// ---- Intégrité des journaux (titulaire) ----
/// <summary>Une chaîne (journal et clé de chaîne) et sa dernière vérification ; Statut : ok, rupture, erreur, ou null si jamais vérifiée.</summary>
public sealed record ChaineIntegriteDto(string Journal, string Libelle, string Algorithme, string? CleChaine, DateTime? VerifieLe, bool Planifiee,
    long Lignes, int Sceaux, string? Statut, long? RangRupture, string? Motif, string? Attendu, string? Trouve);
public sealed record TableauIntegriteDto(IReadOnlyList<ChaineIntegriteDto> Chaines, int HeureVerification, string AlgorithmeSceau,
    string EmpreinteCle, string FormatExportSceau);

// ---- Volet Chifa repris à la caisse par son QR code ----
public sealed record LireVoletChifaRequete(string Texte);
public sealed record LigneVoletChifaDto(string Code, int Quantite, decimal PrixUnitaire, long? ProduitId, string? Produit, decimal? PrixCatalogue);
/// <summary>Brut : contenu du QR, renvoyé avec l'encaissement (relu par le serveur). Champ5, Champ6, DateSecondaire : sens à confirmer avec l'éditeur de Chifa.</summary>
public sealed record VoletChifaDto(string NumeroFacture, DateOnly DateFacture, string Assure, string Champ5, string Champ6, DateOnly? DateSecondaire,
    decimal MontantOrdonnance, IReadOnlyList<LigneVoletChifaDto> Lignes, long? PatientId, string? Patient, long? OrganismeId, string Brut,
    string? PatientNumAssure = null, long? AssurePrincipalProposeId = null, string? AssurePrincipalPropose = null);
public sealed record AssocierCodeChifaRequete(string Code, long ProduitId);
/// <summary>Patient d'un volet Chifa à créer : QR relu par le serveur ; Nom vide : nom provisoire « Assuré n°/suffixe », à compléter.</summary>
public sealed record CreerPatientVoletRequete(string Texte, string? Nom, long? OrganismeId, string? Lien = null, long? AssurePrincipalId = null);
/// <summary>Cree : faux si la fiche du même n° d'assuré existait déjà ; ACompleter : nom provisoire.</summary>
public sealed record PatientVoletDto(long Id, string Nom, string NumAssure, long? OrganismeId, bool Cree, bool ACompleter);

// ---- Circuit « la caisse d'abord » (2026-11-15) : volet rapproché d'une vente déjà encaissée ----
/// <summary>Ecart : concorde, prix_sous_seuil (acceptés) ; prix, quantite, absent_vente, absent_volet, code_non_associe (en rouge).</summary>
public sealed record LigneRapprochementDto(string? Code, long? ProduitId, string Produit, int? QuantiteVolet, int? QuantiteVente, decimal? PrixChifa,
    decimal? NotrePrix, string Ecart, string Libelle, bool EnRouge);
/// <summary>Vente candidate : MemeAssure (n° + suffixe du volet), Concorde (aucune différence en rouge), parts de l'organisme (vente, prix Chifa).</summary>
public sealed record VenteCandidateDto(long VenteId, string Numero, DateTime Date, string? Patient, string? NumAssure, string? Organisme, string Vendeur,
    decimal Total, bool MemeAssure, bool Concorde, int Score, decimal PartOrganisme, decimal? PartOrganismeChifa, decimal EcartPartOrganisme,
    IReadOnlyList<LigneRapprochementDto> Lignes, bool MemeCarte = false, bool EnRetard = false);
/// <summary>Volet lu et ventes candidates (la meilleure d'abord) ; aucune : le volet crée la vente comme avant.</summary>
public sealed record RapprochementVoletDto(string NumeroFacture, DateOnly DateFacture, string Assure, DateOnly? DateOrdonnance, decimal MontantVolet,
    decimal Seuil, int JoursCandidats, IReadOnlyList<VenteCandidateDto> Candidates);
/// <summary>Volet d'un autre assuré que le patient choisi : le vendeur garde le patient (journalisé).</summary>
public sealed record GarderPatientVoletRequete(string Texte, long PatientId, string? Patient, string? NumAssure);
public sealed record RattacherVoletRequete(string Texte, long VenteId, string? Motif, string? Regularisation = null);
/// <summary>ARendre : trop-payé à rendre au patient par le tiroir (« Rembourser », 2026-11-15), avec la vente et le patient.</summary>
public sealed record VoletRattacheDto(string Vente, bool AvecEcart, decimal EcartPartOrganisme, string? Catalogue, string? Regularisation = null,
    decimal ARendre = 0, long VenteId = 0, long? PatientId = null, string? Patient = null);
public sealed record VenteAttenteVoletDto(long Id, string Numero, DateTime Date, string? Patient, string? NumAssure, string? Organisme, string Vendeur,
    decimal Total, decimal PartOrganisme, string? Motif = null, bool EnRetard = false);
public sealed record VoletAvecEcartDto(long VenteId, string Numero, DateTime DateVente, string? Patient, string NumeroFacture, DateTime? RattacheLe,
    string? RattachePar, string Motif, string? Detail, decimal? EcartPartOrganisme, string? Regularisation = null,
    long? PatientId = null, decimal ARendre = 0, string? BonRendu = null);

// ---- Avoir patient (script 106) -------------------------------------------------------

/// <summary>Mouvement de l'avoir d'un patient. Type : credit (l'avoir augmente) ou debit ; Origine : annulation_vente, retour_client (script 109), trop_paye_regularisation, saisie, paiement, remboursement.</summary>
public sealed record AvoirMouvementDto(long Id, string Type, decimal Montant, decimal SoldeApres, long? VenteId, string? NumeroVente, string? Motif,
    DateTimeOffset CreeLe, string CreePar, string Origine = "", string? BonSortie = null);
/// <summary>Avoir d'un patient : solde (jamais négatif) et une page de mouvements (les plus récents d'abord).</summary>
public sealed record AvoirPatientDto(decimal Solde, PageDto<AvoirMouvementDto> Mouvements);
/// <summary>Remboursement d'un avoir par le tiroir (sortie « Remboursement d'un client », même droit et même seuil que les sorties).</summary>
public sealed record RemboursementAvoirRequete(decimal Montant, string? Motif);
