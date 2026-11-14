# Règles métier de Pharma Officine

Inventaire des règles métier **telles que le logiciel les applique aujourd'hui** (code et base, scripts `001` à `056`), pour tout le logiciel. Chaque règle cite le fichier où elle est appliquée.

- Les droits sont désignés par leur code (`stock.ajuster`…) ; leur libellé et leur attribution par défaut sont dans la partie « Sécurité et droits ».
- « (à confirmer) » : comportement constaté dans le code dont l'intention n'est pas établie ; à valider par le titulaire.
- À tenir à jour à chaque nouvelle règle (même modification que le code), comme `docs/base-de-donnees.md` pour les scripts.

## Sommaire

- [Caisse et ventes](#caisse-et-ventes)
- [Ordonnances et médicaments réglementés](#ordonnances-et-médicaments-réglementés)
- [Annulations et retours](#annulations-et-retours)
- [Crédits et dettes des patients](#crédits-et-dettes-des-patients)
- [Patients, médecins et contacts](#patients-médecins-et-contacts)
- [Stock](#stock)
- [Alertes de stock](#alertes-de-stock)
- [Catalogue](#catalogue)
- [Traçabilité](#traçabilité)
- [Psychotropes et registre](#psychotropes-et-registre)
- [Mobile (scanner)](#mobile-scanner)
- [Achats](#achats)
- [Factures clients](#factures-clients)
- [Trésorerie](#trésorerie)
- [Chifa / CNAS et tiers payant](#chifa--cnas-et-tiers-payant)
- [Sécurité et droits](#sécurité-et-droits)
- [Administration](#administration)
- [Journal et audit](#journal-et-audit)
- [Super administrateur (hors base)](#super-administrateur-hors-base)
- [Sauvegardes et restauration](#sauvegardes-et-restauration)
- [GED (documents)](#ged-documents)
- [Reprise de données (import depuis l'ancien logiciel)](#reprise-de-données-import-depuis-lancien-logiciel)
- [Essai (retours du pharmacien)](#essai-retours-du-pharmacien)
- [Notifications au titulaire](#notifications-au-titulaire)
- [Contrôle interne (caisse et stock)](#contrôle-interne-caisse-et-stock)
- [Rapports](#rapports)
- [Pilotage (tableau de bord du titulaire)](#pilotage-tableau-de-bord-du-titulaire)
- [Suivi des dettes et créances](#suivi-des-dettes-et-créances)
- [Points relevés à l'inventaire](#points-relevés-à-linventaire)

## Points relevés à l'inventaire

Écarts constatés en dressant cet inventaire ; rien n'a été changé dans le code à ce titre, sauf mention contraire.

- **Statut de boîte « donnee »** (échantillon ou don) : ajouté au code et aux libellés ; une boîte donnée rapportée en caisse n'est jamais remboursée (corrigé avec cet inventaire).
- **Droit `caisse.supprimer_ligne`** : défini, mais aucun contrôle ne l'exige ; retirer une ligne du panier est seulement signalé à la surveillance de la caisse. (à confirmer)
- **Droits `chifa.importer` et `chifa.bordereaux`** : définis, mais aucune fonction ne les exige. (à confirmer)
- **Import Chifa** : les fichiers lus sont archivés dans la GED, puis la lecture s'arrête (format des fichiers non connu) ; aucun code ne crée ni n'envoie de bordereau, seul leur paiement est enregistré en trésorerie.
- **Lot choisi au scan d'un code fabricant** (fonction SQL `lot_fefo`) : ne tient pas compte des lots bloqués. (à confirmer)
- **État du stock** (vue `v_etat_stock`) : compte les lots bloqués ; seuls les lots périmés sont exclus.
- **Date de péremption confirmée à l'inventaire** : refusée seulement si elle est passée de plus d'un an, alors que le commentaire du code dit « ne peut pas être déjà passée ». (à confirmer)
- **Prix public** : le code exige un prix supérieur à 0, la base accepte 0.
- **Calcul des anomalies de surveillance** : lancé après chaque opération concernée et, depuis la revue de code, toutes les heures par le serveur (tâche de fond) ; un échec est inscrit au journal du serveur (corrigé).
- **Tableau de bord du titulaire** : lit les alertes générales ; les anomalies du contrôle interne n'y figurent pas (elles sont dans l'écran Contrôle interne). (à confirmer)
- **Nombre de droits** : `docs/securite-et-droits.md` en annonce 26, le logiciel en compte 32.
- **Droits de l'assistant** : diffèrent entre l'ancien script `006_droits_utilisateurs.sql` et le script de départ `database/init/seed_referentiel.sql` ; le tableau des droits suit le script de départ.
- **Indicateur « sensible » d'un droit** : sans effet constaté en dehors de l'affichage. (à confirmer)
- **Réglage des imprimantes** : fait sur chaque poste, sans vérification de droit côté serveur. (à confirmer)
- **Notification d'écart de caisse** : ce document disait « tout écart non nul est signalé au titulaire » ; le code ne prévient qu'au-delà du seuil SMS (à défaut, le seuil de la caisse). Règle réécrite d'après le code.

## Caisse et ventes

### Poste et caisse (session)
- **Aucune écriture d'argent sur une caisse clôturée** (vente, paiement, annulation, retour, part du tiroir d'un règlement grossiste, argent porté au coffre pendant la journée) : la base la refuse, même lancée sur un autre poste pendant le comptage (« Cette caisse vient d'être clôturée »). — `database/migration/064_caisse_cloturee.sql`
- **Sans nom de poste, pas de caisse** : le poste doit être nommé (`PHARMA_POSTE`) pour ouvrir, encaisser, annuler, rembourser, mettre en attente. — `src/Pharma.Application/Caisse/Sessions.cs`
- **Une seule caisse ouverte par poste (un tiroir)** : une seconde ouverture est refusée (« vendez dessus, ou faites une relève ») ; contrôlé aussi en base (index unique `ux_session_poste_ouverte`). — `src/Pharma.Application/Caisse/Sessions.cs`, `database/migration/022_caisse_partagee.sql`
- **La caisse est partagée** : plusieurs employés vendent sur la caisse du poste ; chaque vente garde son vendeur, la caisse reste au nom de celui qui l'a ouverte. — `database/migration/022_caisse_partagee.sql`
- **Ouvrir, clôturer et faire la relève demandent le droit de vendre** (`caisse.vendre`). — `src/Pharma.Api/Endpoints/CaisseEndpoints.cs`
- **Fond de caisse jamais négatif** : s'il n'est pas saisi, le fond par défaut des paramètres est pris (5 000 DA à l'installation). — `src/Pharma.Domain/Caisse/SessionCaisse.cs`, `database/migration/009_caisse.sql`
- **On n'encaisse que sur une caisse ouverte** : « Ouvrez la caisse du poste avant d'encaisser » ; si la caisse est clôturée pendant l'encaissement, la vente est refusée. — `src/Pharma.Application/Caisse/Encaisser.cs`, `src/Pharma.Infrastructure/Caisse/VentesEcritureSql.cs`
- **Ventes suspendues pendant un inventaire complet « pharmacie fermée »** : elles reprennent à la clôture de l'inventaire. — `src/Pharma.Application/Stock/InventaireComplet.cs`
- **Numéro de ticket suivi** : T-000001, T-000002…, jamais remis à zéro. — `database/migration/009_caisse.sql`

### Clôture, comptage et relève
- **Comptage par coupure** : billets de 2 000, 1 000, 500, 200 DA, pièces de 100 DA (nombres entiers), et « autres pièces » saisies en montant total ; aucune valeur négative. — `src/Pharma.Domain/Caisse/SessionCaisse.cs`
- **Espèces attendues = fond de caisse + espèces nettes de la session** : espèces encaissées (ventes et remboursements de crédit en espèces) moins l'argent rendu en espèces (annulations, retours), moins le complément pris dans le tiroir pour payer un grossiste, moins l'argent porté au coffre pendant la session. — `src/Pharma.Infrastructure/Caisse/SessionsCaisseSql.cs`
- **Écart = compté − attendu** : négatif, c'est un **manque** ; positif, un **excédent**. Au-delà du seuil d'écart (500 DA à l'installation), en valeur absolue, **le manque comme l'excédent** sont inscrits au journal (`ecart_caisse`) : un excédent signale souvent une vente non enregistrée ou une monnaie mal rendue. — `src/Pharma.Domain/Caisse/SessionCaisse.cs`, `src/Pharma.Application/Caisse/Sessions.cs`, `database/migration/009_caisse.sql`
- **Le message de clôture nomme la nature de l'écart** : « Manque de … DA, au-delà du seuil » en rouge (erreur), « Excédent de … DA, au-delà du seuil » en orange (mise en garde) ; même libellé dans le rapport de clôture (PDF) et dans la notification au titulaire (« Manque de caisse » ou « Excédent de caisse »). — `src/Pharma.Domain/Caisse/SessionCaisse.cs` (`NatureEcart`), `src/Pharma.Desktop/Composants/Pages/Cloture.razor`, `src/Pharma.Infrastructure/Impression/DocumentsPdf.cs`, `src/Pharma.Application/Notifications/Notifications.cs`
- **Écart signalé au titulaire (notification) seulement au-delà du seuil SMS** (à défaut, le seuil d'écart de la caisse), manque ou excédent ; un échec de notification n'annule pas la clôture. — `src/Pharma.Application/Caisse/Sessions.cs`, `src/Pharma.Application/Notifications/Notifications.cs`
- **Comptage aveugle (activé par défaut)** : l'attendu reste caché tant que la caisse n'est pas comptée. — `src/Pharma.Application/Caisse/Sessions.cs`, `database/migration/009_caisse.sql`
- **Attendu et écart visibles seulement avec le droit `caisse.voir_ecart`** : sans ce droit, seul le total compté est montré (écran et rapport PDF). — `src/Pharma.Application/Caisse/Sessions.cs`
- **Une caisse clôturée ne se modifie plus** : refus en base (déclencheur `trg_session_fermee`) ; une clôture déjà faite est refusée. — `database/migration/009_caisse.sql`, `src/Pharma.Domain/Caisse/SessionCaisse.cs`
- **Clôture à relancer si une opération arrive pendant le comptage** : les espèces attendues sont relues ; si elles ont changé, la clôture est refusée. — `src/Pharma.Infrastructure/Caisse/SessionsCaisseSql.cs`
- **Relève (changement d'équipe, facultative)** : la caisse est comptée et clôturée, puis une nouvelle s'ouvre aussitôt avec l'argent compté comme fond ; inscrite au journal (`releve_caisse`). — `src/Pharma.Application/Caisse/Sessions.cs`
- **« ⇄ Changer » avec une caisse ouverte sur le poste propose la relève** (« Faire la relève avant de changer d'utilisateur ? ») : « Faire la relève » ouvre l'écran de relève, « Changer sans relève » change directement, « Annuler » reste. Proposée seulement à qui a le droit de vendre, et plus après une relève faite par l'employé (bouton « ⇄ Changer d'utilisateur maintenant » à la fin de la relève). La clôture (comptage aveugle) n'est jamais transmise au suivant : il recompte. — `src/Pharma.Desktop/Composants/Mise/MiseEnPage.razor`, `src/Pharma.Desktop/Composants/Pages/Cloture.razor`
- **État de la caisse (sans compter ni clôturer, droit de vendre)** : depuis l'ouverture, nombre de ventes, montants nets par carte, chèque, crédit et part du tiers payant, ventes par employé, comptages intermédiaires. **Comptage aveugle strict : jamais les espèces, le total des ventes ni l'attendu** (ils donneraient l'argent attendu dans le tiroir), même pour le titulaire. — `src/Pharma.Application/Caisse/EtatIntermediaire.cs`, `src/Pharma.Desktop/Composants/Pages/EtatCaisse.razor`
- **Comptage intermédiaire** (État de la caisse › « Faire un comptage intermédiaire », droit de vendre) : le tiroir est compté en cours de journée **sans clôturer** (la caisse reste ouverte, les ventes continuent) ; le compté, l'attendu et l'écart à cet instant sont enregistrés et inscrits au journal (`comptage_intermediaire`). Le résultat s'affiche **comme à la clôture** : détail et écart pour qui peut voir les écarts (`caisse.voir_ecart`), sinon seulement le montant compté. À ne pas confondre avec la relève, qui clôture la période et rouvre la caisse. — `src/Pharma.Application/Caisse/EtatIntermediaire.cs`, `database/migration/070_comptage_intermediaire.sql`
- **Rapport de clôture** : seulement pour une caisse clôturée ; accessible à celui qui l'a ouverte, à ceux qui y ont vendu, ou à qui a le droit `caisse.voir_ecart`. — `src/Pharma.Application/Caisse/Sessions.cs`

### Paramètres de caisse
- **Réglés avec le droit `admin.parametres`**, chaque modification inscrite au journal (avant/après). — `src/Pharma.Api/Endpoints/AdminEndpoints.cs`, `src/Pharma.Application/Caisse/Sessions.cs`
- **Fond par défaut et seuil d'écart ≥ 0** (défauts : 5 000 DA et 500 DA). — `src/Pharma.Application/Caisse/Sessions.cs`, `database/migration/009_caisse.sql`
- **Seuil d'annulations entre 0 et 100** (0 = pas d'alerte, valeur par défaut 0). — `src/Pharma.Application/Caisse/Sessions.cs`, `database/migration/047_recette_corrections.sql`
- **Plafond de crédit proposé à la création d'un patient ≥ 0** (0 par défaut). — `src/Pharma.Application/Caisse/Sessions.cs`, `database/migration/045_prix_lot_ug_categories.sql`
- **Déconnexion automatique entre 0 (désactivée) et 240 minutes** (0 par défaut en base). — `src/Pharma.Application/Caisse/Sessions.cs`, `database/migration/009_caisse.sql`
- **Versement des caisses au coffre : « manuel » (défaut) ou « automatique »**. — `src/Pharma.Application/Caisse/Sessions.cs`, `database/migration/030_tresorerie.sql`
- **Autorisation par code comptoir activée par défaut**, désactivable par le titulaire. — `database/migration/047_recette_corrections.sql`, `src/Pharma.Api/Securite/AutorisationParCode.cs`

### Code comptoir et autorisation sur place
- **Tant que le mot de passe provisoire n'est pas changé, le code comptoir ne vaut rien** : ni pour se connecter au comptoir, ni pour autoriser un collègue ; choisir son code est refusé. — `src/Pharma.Application/Securite/ConnexionComptoir.cs`
- **Connecté par code comptoir, le code d'un collègue n'ouvre aucun écran hors du comptoir** (règlements, paramètres…) ni aucune action du titulaire : la portée de la connexion est gardée. — `src/Pharma.Api/Securite/PermissionsComptoir.cs`
- **Une action autorisée par le code d'un collègue garde ce collègue comme validateur** (annulation d'une vente ancienne, retour client), pas l'employé connecté. — `src/Pharma.Application/Caisse/AnnulerVente.cs`, `src/Pharma.Application/Caisse/RetourClient.cs`
- **Code comptoir de 4 à 6 chiffres**. — `src/Pharma.Domain/Securite/CodeComptoir.cs`
- **L'écran de connexion s'ouvre directement dans le bon mode** : « Code comptoir » si la caisse du poste est ouverte, sinon « Mot de passe » ; au premier lancement du poste, il attend la réponse du serveur avant d'afficher le formulaire (pas de bascule d'un mode à l'autre), au plus 3 secondes, puis « Mot de passe » si le serveur ne répond pas ; ensuite (changement d'utilisateur), il affiche aussitôt le formulaire du dernier état connu et relit l'état en arrière-plan. L'autre mode reste à un clic quand la caisse est ouverte. — `src/Pharma.Desktop/Composants/Pages/Connexion.razor`
- **5 erreurs bloquent le code 5 minutes** ; le blocage est inscrit au journal (`code_comptoir_bloque`). — `src/Pharma.Domain/Securite/CodeComptoir.cs`, `src/Pharma.Application/Securite/ConnexionComptoir.cs`
- **Une action refusée faute de droit peut être autorisée par le code d'un autre employé qui a ce droit** : seulement pour les droits que le valideur a acceptés au poste (un code donné pour vendre sans ordonnance n'autorise pas un dépassement de plafond) ; jamais par l'employé lui-même ; inscrit au journal (« autorisé par »). — `src/Pharma.Api/Securite/AutorisationParCode.cs`
- **La fenêtre « Faire autoriser » ne propose jamais l'employé qui demande** (2026-11-14) : seulement les autres employés actifs qui ont un code comptoir et le droit. — `src/Pharma.Application/Securite/ConnexionComptoir.cs` (`ListerValidateurs`)

### Panier et produits vendus
- **Panier vide : encaissement refusé**. — `src/Pharma.Domain/Ventes/Vente.cs`, `src/Pharma.Application/Caisse/Encaisser.cs`
- **Quantité d'au moins une boîte par ligne** (aussi en base : `quantite > 0`). — `src/Pharma.Domain/Ventes/Vente.cs`, `database/migration/001_schema.sql`
- **Produit inexistant ou inactif : refusé**. — `src/Pharma.Application/Caisse/Encaisser.cs`
- **Produit repris de l'ancien logiciel « à vérifier » : invendable** tant que le titulaire ne l'a pas vérifié (Nomenclature nationale, « Produits à vérifier »). — `src/Pharma.Application/Caisse/Encaisser.cs`
- **Lot bloqué, périmé (péremption le jour même ou passée) ou épuisé : vente refusée** ; bloqué et périmé sont aussi refusés en base (déclencheur `verifier_lot_vendable`). — `src/Pharma.Domain/Stock/Lot.cs`, `database/migration/007_tracabilite_boite.sql`
- **Pas plus de boîtes que le stock du lot** (lignes du même lot cumulées) ; si un autre poste vient de vendre la dernière boîte, l'encaissement est refusé (« Rescannez le produit »). — `src/Pharma.Domain/Ventes/Vente.cs`, `src/Pharma.Infrastructure/Caisse/VentesEcritureSql.cs`
- **Produit bloqué à la vente** : il faut une validation explicite, par l'employé s'il a le droit `produits.bloquer`, sinon par le code comptoir d'un employé qui l'a ; la vente est inscrite au journal (`vente_produit_bloque`, avec le validateur). — `src/Pharma.Application/Catalogue/BlocageProduit.cs`, `src/Pharma.Application/Caisse/Encaisser.cs`
- **Retirer une ligne ou des boîtes du panier est signalé à la surveillance** (boîtes, montant). — `src/Pharma.Desktop/Composants/Pages/Caisse.razor`
- **Le droit `caisse.supprimer_ligne` existe mais aucun contrôle ne l'utilise dans le code lu** (à confirmer). — `src/Pharma.Domain/Securite/Permissions.cs`

### Choix du lot (premier périmé, premier sorti)
- **Code-barres du fabricant : le lot qui périme le premier est servi** ; une boîte identifiée (étiquette interne ou Data Matrix) sert son propre lot. — `src/Pharma.Application/Caisse/ControlerBoite.cs`
- **Boîte scannée plus récente qu'un autre lot : le caissier est prévenu de servir d'abord l'autre lot** ; alerte rouge si cet autre lot est proche de sa péremption (seuils rouge/orange), orange sinon ; l'emplacement (comptoir ou réserve) est indiqué. — `src/Pharma.Domain/Stock/RegleFefo.cs`, `src/Pharma.Application/Caisse/ControlerBoite.cs`
- **Boîte scannée déjà vendue, retournée ou détruite : refusée** ; lot bloqué (rappel) ou périmé : vente interdite. — `src/Pharma.Application/Caisse/ControlerBoite.cs`
- **Lot plus récent servi alors qu'un lot plus ancien est en stock : raison obligatoire à l'encaissement** — « péremption plus proche » (seulement si le logiciel l'a vérifiée sur les dates : le lot servi périme avant tous les autres, dates non « à vérifier »), sinon au choix du vendeur : boîte de l'ancien lot introuvable, abîmée, ou demande du client. — `src/Pharma.Domain/Ventes/RaisonLot.cs`, `database/migration/053_lot_delaisse.sql`
- **« Péremption plus proche » refusée si le logiciel ne l'a pas vérifiée** ; une raison inconnue est refusée (aussi en base : `ck_ligne_vente_raison_lot`). — `src/Pharma.Domain/Ventes/RaisonLot.cs`, `database/migration/053_lot_delaisse.sql`
- **Ancien lot délaissé : alerte au titulaire** quand un même vendeur a vendu au moins 5 boîtes du lot récent (seuil par défaut), ou plusieurs vendeurs ensemble au moins 10 boîtes, hors raison « péremption » ; urgente dans le second cas, si une vente porte « introuvable » ou si l'ancien lot approche de sa péremption ; un échec de l'alerte n'annule pas la vente. — `database/migration/053_lot_delaisse.sql`, `src/Pharma.Application/Caisse/Encaisser.cs`

### Boîtes identifiées (traçabilité)
- **Une même boîte ne peut être scannée deux fois, ni plus de boîtes que de boîtes vendues sur le lot**. — `src/Pharma.Application/Caisse/Encaisser.cs`, `database/migration/007_tracabilite_boite.sql`
- **Une boîte vendue doit être en stock et appartenir au lot de la ligne** (sinon « contrefaçon ou double scan possible »). — `database/migration/007_tracabilite_boite.sql`
- **Scan de chaque boîte vendue obligatoire si le réglage l'impose** (désactivé par défaut, `parametre_tracabilite.boite_obligatoire`). — `database/migration/007_tracabilite_boite.sql`

### Prix et calcul de la vente
- **Prix d'une boîte = PPA du lot (prix vignette), à défaut prix public du catalogue, plus le SHP par boîte**. — `src/Pharma.Domain/Ventes/Vente.cs`
- **Les prix et le stock sont toujours recalculés par le serveur** (aperçu et encaissement), y compris à la reprise d'une vente en attente. — `src/Pharma.Application/Caisse/Encaisser.cs`, `database/migration/026_ventes_attente.sql`

### Remise, remise automatique et don partiel (2026-11-14)
- **Réductions calculées ligne par ligne, sur la part du patient hors SHP** (le SHP, honoraire du pharmacien, n'est jamais réduit) ; chaque montant arrondi au centime. — `src/Pharma.Domain/Ventes/LigneVente.cs`, `src/Pharma.Domain/Ventes/Vente.cs`
- **Sur un produit hors médicaments, la réduction est une remise ; sur un produit de la catégorie « médicament », c'est un don partiel du reste à charge**, jamais une remise (remboursable ou non). Vente : `montant_remise` (remises) et `montant_don` (dons partiels) ; ligne : `reduction_auto`, `reduction_manuelle`, `don_partiel`. — `src/Pharma.Domain/Ventes/Vente.cs`, `database/migration/072_remises_automatiques.sql`
- **Remise automatique** : décidée pour un produit ou une catégorie sur une période (Stock › Remises automatiques), elle s'applique d'elle-même en **vente libre** seulement, **jamais sur un produit remboursable** ; celle du produit l'emporte sur celle de sa catégorie. Elle ne demande ni droit ni validation au vendeur. — `src/Pharma.Domain/Ventes/Vente.cs` (`AppliquerRemisesAutomatiques`), `src/Pharma.Infrastructure/Caisse/RemisesAutomatiquesSql.cs`
- **Remise du vendeur : un pourcentage entier de 0 à 100 % sur toute la vente** (aussi en base) ; à la caisse, chiffres seulement, 2 au plus sauf 100, une frappe qui dépasserait 100 est refusée ; 0 par défaut, effacé dès qu'on tape (contenu sélectionné à l'entrée dans le champ), et remis si le champ est vidé ; recalcul en quittant le champ. — `src/Pharma.Domain/Ventes/Vente.cs`, `src/Pharma.Desktop/Composants/Pages/Caisse.razor`
- **Remise du vendeur et remise automatique** : case « + remise auto » (proposée quand un produit du panier a une remise automatique). Cochée, les pourcentages s'additionnent (10 % + 5 % = 15 %, au plus 100 %) ; décochée (par défaut), la remise du vendeur ne vaut que pour les produits sans remise automatique. — `src/Pharma.Domain/Ventes/LigneVente.cs` (`CalculerReductions`)
- **Le champ remise est proposé à tout vendeur.** Remise du vendeur sur un produit hors médicaments : dans son **maximum** (réglé sur l'employé par le titulaire), sinon le code d'un collègue **dont le maximum couvre ce pourcentage** est demandé à l'encaissement. Le maximum porte sur la part du vendeur, pas sur la remise automatique. — `src/Pharma.Application/Caisse/Encaisser.cs` (`ExigerReductionsAsync`), `src/Pharma.Api/Securite/PermissionsComptoir.cs`
- **Remise du vendeur sur un médicament (don partiel)** : droit de **don** de l'employé, sinon code d'un collègue qui l'a. — `src/Pharma.Application/Caisse/Encaisser.cs`
- **L'aperçu (calcul du panier) ne refuse jamais une remise** : le contrôle du droit se fait à l'encaissement, qui propose alors l'autorisation sur place. — `src/Pharma.Application/Caisse/Encaisser.cs`
- **Caisse : chaque ligne réduite montre le montant initial barré, « remise » ou « don », le pourcentage (« auto » pour une remise automatique) et le net** ; les totaux montrent le total, les remises (dont automatiques) et le don partiel, puis la part du patient. — `src/Pharma.Desktop/Composants/Pages/Caisse.razor`
- **Journal** : `remise` (pourcentage, montant de la remise du vendeur, additionnée ou non), `don_partiel` (remise du vendeur sur des médicaments), `remise_automatique_appliquee` (montant, dont don partiel), chacun avec « autorisé par » s'il y a lieu. — `src/Pharma.Infrastructure/Caisse/VentesEcritureSql.cs`

### Remises automatiques (gestion)
- **Écran Stock › Remises automatiques** : liste ouverte à qui consulte le stock (ou vend) ; filtres (en cours et à venir par défaut, en cours, à venir, terminées ou arrêtées, toutes ; texte) et pagination sur le poste. — `src/Pharma.Desktop/Composants/Mise/RemisesAutomatiques.razor`
- **Créer, modifier, arrêter : titulaire, ou employé à qui il a donné le droit « Peut gérer les remises automatiques »** (`caisse.remises_auto`, réglé sur l'employé). — `src/Pharma.Application/Caisse/RemisesAutomatiques.cs`
- **Un produit OU une catégorie ; pourcentage entier de 1 à 100 ; du … au … inclus (fin au plus tôt le jour du début, et pas déjà passée) ; motif facultatif de 200 caractères au plus.** — `src/Pharma.Application/Caisse/RemisesAutomatiques.cs`, `database/migration/072_remises_automatiques.sql`
- **Jamais sur un produit remboursable** (refusé à la création). Une catégorie peut être « médicament » : la réduction y est alors un don partiel. — `src/Pharma.Application/Caisse/RemisesAutomatiques.cs`
- **Deux remises non arrêtées sur la même cible ne peuvent pas se chevaucher dans le temps.** — `src/Pharma.Infrastructure/Caisse/RemisesAutomatiquesSql.cs` (`ChevaucheAsync`)
- **Une remise arrêtée ne s'applique plus aux ventes suivantes ; elle reste dans la liste (« Arrêtée par … ») et ne se modifie plus.** — `src/Pharma.Application/Caisse/RemisesAutomatiques.cs`
- **Création, modification (avec l'avant) et arrêt sont inscrits au journal** (`remise_automatique_creee`, `remise_automatique_modifiee`, `remise_automatique_arretee`). — `src/Pharma.Application/Caisse/RemisesAutomatiques.cs`

### Tiers payant (Chifa) au comptoir
- **Tiers payant : l'assuré (patient) et l'organisme (CNAS, CASNOS) sont obligatoires** (aussi en base : contrainte sur `vente`). — `src/Pharma.Domain/Ventes/Vente.cs`, `database/migration/001_schema.sql`
- **Taux de remboursement choisi au comptoir : 80 % ou 100 % (80 % par défaut à l'écran)** ; le serveur exige un taux supérieur à 0 et au plus 100 %, sauf si le reste à charge est saisi. — `src/Pharma.Desktop/Composants/Pages/Caisse.razor`, `src/Pharma.Domain/Ventes/Vente.cs`
- **Part de l'organisme par ligne = base de remboursement (tarif de référence, plafonné au prix de vente) × quantité × taux, arrondie au centime** (comme les factures Chifa) ; le patient paie le reste. — `src/Pharma.Domain/Ventes/LigneVente.cs`
- **Produit non remboursable ou sans tarif de référence : le patient paie tout**, même en tiers payant. — `src/Pharma.Domain/Ventes/Vente.cs`
- **Reste à charge saisi par le caissier (tiers payant seulement)** : entre 0 et le montant de la vente ; la part de l'organisme est la différence, répartie sur les lignes au prorata (centime, l'écart d'arrondi sur la dernière ligne) ; le taux choisi est alors ignoré. — `src/Pharma.Domain/Ventes/Vente.cs`
- **Organisme qui paie tout : aucun paiement n'est enregistré pour le patient**. — `src/Pharma.Infrastructure/Caisse/VentesEcritureSql.cs`

### Encaissement et modes de paiement
- **Modes : espèces, carte, chèque, ou crédit** (bouton « Mettre en crédit »). — `src/Pharma.Domain/Ventes/Reglement.cs`, `src/Pharma.Desktop/Composants/Pages/Caisse.razor`
- **Un seul mode de paiement par vente**. — `src/Pharma.Domain/Ventes/Reglement.cs`
- **Espèces : le montant reçu doit couvrir le montant à payer** ; non saisi, c'est l'appoint exact ; la monnaie rendue = reçu − à payer. — `src/Pharma.Domain/Ventes/Reglement.cs`
- **Carte ou chèque : le montant réglé est exactement la part du patient**. — `src/Pharma.Domain/Ventes/Reglement.cs`
- **La vente, le stock, le paiement, l'ordonnance, le registre et la dette sont enregistrés ensemble ou pas du tout**. — `src/Pharma.Infrastructure/Caisse/VentesEcritureSql.cs`
- **Après chaque vente, la surveillance de la caisse est recalculée** (ventes hors horaires, remises en série). — `src/Pharma.Application/Caisse/Encaisser.cs`
- **Caisse : « À payer par le patient » est mis en avant** (2026-11-14) : encadré vert sur toute la largeur de la colonne de paiement, montant en très grand. — `src/Pharma.Desktop/wwwroot/css/app.css`
- **Caisse : le rendu de la vente en cours est mis en avant** (à côté du montant reçu, en grand, sur fond ambre dès qu'il y a de la monnaie à rendre) ; jamais de cumul de la session (comptage aveugle). — `src/Pharma.Desktop/Composants/Pages/Caisse.razor`
- **Colonne de paiement de la caisse : large de 460 px environ, sans défilement** (la zone des produits prend le reste) ; boutons « Encaisser » et « Mettre en crédit » jamais écrasés. — `src/Pharma.Desktop/wwwroot/css/app.css`
- **F7 place le curseur dans la recherche de produit de la caisse.** — `src/Pharma.Desktop/wwwroot/js/poste.js`

### Ticket
- **Aucune donnée du patient sur le ticket** ; il porte le nom, l'adresse, le téléphone et le NIF de la pharmacie, le vendeur, le poste, l'organisme et le taux, les lignes, la remise, les parts organisme/patient et le mode. — `src/Pharma.Application/Caisse/Ticket.cs`
- **Remise par produit sur le ticket** (2026-11-14) : sous chaque ligne réduite, « Remise » ou « Don » avec le pourcentage et le montant ; en bas, « Montant initial », « Remises », « Don partiel », puis « À PAYER ». — `src/Pharma.Desktop/Services/ImprimanteTickets.cs`, `src/Pharma.Infrastructure/Caisse/TicketSql.cs`

### Ventes en attente
- **Quitter la Caisse avec un panier rempli (menu, lien, « ← Retour », Alt + ←) le met en attente automatiquement**, comme le bouton « Mettre en attente » : rien n'est perdu ni signalé « retiré ». « ← Retour » sur la Caisse reprend cette vente, sauf si un autre poste l'a déjà reprise (message). Si la mise en attente échoue (20 ventes déjà en attente sur le poste, serveur injoignable) ou pendant un encaissement, on reste sur la Caisse avec le message. — `src/Pharma.Desktop/Composants/Pages/Caisse.razor` (`QuitterAsync`, `ReprendreAuRetourAsync`)
- **Une vente mise en attente garde tout ce qui a été saisi** : boîtes scannées (rattachées à la vente), raisons de lot, reste à charge, porteur de l'ordonnance ; rien n'est à ressaisir à la reprise. — `src/Pharma.Desktop/Composants/Pages/Caisse.razor`
- **« Mettre en attente » et « Reprendre » ne partent qu'une fois** (double clic sans effet) : pas de vente en attente en double, pas de panier perdu. — `src/Pharma.Desktop/Composants/Pages/Caisse.razor`
- **« Changer d'utilisateur » avec un panier rempli met la vente en attente** (libellé « … · laissée par *employé* »), sans rien signaler « retiré » ; le **prochain employé qui se connecte sur ce poste** (le même ou un autre, avec le droit de vendre) arrive directement à la Caisse et la vente y est reprise, avec le message « Vente reprise : *employé* l'a laissée en attente en changeant d'utilisateur ». Si elle a été reprise ou encaissée entre-temps, un message le dit ; sinon elle reste dans les ventes en attente du poste. — `src/Pharma.Desktop/Composants/Pages/Caisse.razor` (`PanierAbandonne`, `ReprendreLaisseeAsync`), `src/Pharma.Desktop/Services/ApiClient.cs` (`SessionPoste`), `src/Pharma.Desktop/Composants/Pages/Connexion.razor`
- **Tâches longues laissées au « Changer d'utilisateur » : le suivant les reprend sans rien refaire.** Facture en préparation (nouvelle, correction, remplacement), retour fournisseur en préparation et réception en cours sont confiés au poste ; le prochain employé connecté voit le bandeau « Tâches laissées en cours sur ce poste » (libellé, qui, à quelle heure) et « Reprendre » rouvre l'écran avec la saisie intacte. Il ne voit que ce qu'il a le droit de faire (`factures.gerer`, `achats.retour_fournisseur`, `achats.receptionner`) ; un comptage à l'aveugle n'est proposé qu'à son auteur ou à qui décide des écarts. Facture et retour sont gardés dans la mémoire du poste (perdus si le poste redémarre) ; la réception est en attente sur le serveur (visible aussi dans « Réceptions en attente »). « Plus tard » masque le bandeau, la liste reste gardée. Inventaire et bons de commande n'en ont pas besoin : ils sont enregistrés au fur et à mesure. — `src/Pharma.Desktop/Services/TachesLaissees.cs`, `src/Pharma.Desktop/Composants/Mise/MiseEnPage.razor`
- **Panier abandonné en se déconnectant ou par inactivité : boîtes signalées « retirées du panier »** au contrôle interne. — `src/Pharma.Desktop/Composants/Pages/Caisse.razor`
- **Mettre de côté, reprendre ou abandonner une vente demande le droit de vendre** (`caisse.vendre`). — `src/Pharma.Api/Endpoints/CaisseEndpoints.cs`
- **Au plus 20 ventes en attente par poste**. — `src/Pharma.Application/Caisse/VentesEnAttente.cs`
- **Panier vide : rien à mettre en attente** ; panier trop volumineux (plus de 200 000 caractères) refusé. — `src/Pharma.Application/Caisse/VentesEnAttente.cs`
- **Libellé par défaut « Vente en attente », 80 caractères au plus** (au-delà, tronqué). — `src/Pharma.Application/Caisse/VentesEnAttente.cs`
- **Une vente en attente est gardée sur le serveur et peut être reprise par n'importe quel employé du poste** ; reprise, elle quitte l'attente (une seconde reprise est refusée). — `src/Pharma.Application/Caisse/VentesEnAttente.cs`, `database/migration/026_ventes_attente.sql`
- **Une vente en attente abandonnée est enregistrée pour la surveillance** (qui, quand, montant) ; l'abandon exige toujours l'employé qui abandonne. — `src/Pharma.Application/Caisse/VentesEnAttente.cs`

### Contrôle interne : caisse (titulaire)
- **Ouvrir le tiroir sans vente demande le droit `caisse.ouvrir_tiroir`** ; chaque ouverture est enregistrée et inscrite au journal ; l'entrée est grisée sans ce droit. Elle est dans la colonne d'actions de la Caisse et demande une confirmation (« Ouvrir le tiroir sans vente ? Ce sera inscrit au contrôle interne. ») : un clic par erreur n'ouvre rien. — `src/Pharma.Application/Surveillance/Surveillance.cs`, `src/Pharma.Desktop/Composants/Pages/Caisse.razor`
- **Anomalies par employé et par jour, seuils par défaut** : 5 boîtes retirées du panier, 3 ouvertures du tiroir sans vente, 3 ventes en attente abandonnées, 5 ventes avec remise ; « urgente » à partir du double du seuil. — `database/migration/054_surveillance_caisse.sql`
- **Remises répétées à un même patient** : 3 ventes avec remise en 30 jours (défaut), urgente au double. — `database/migration/054_surveillance_caisse.sql`
- **Ventes hors horaires d'ouverture : anomalie urgente**, seulement si les horaires sont réglés (heure locale du serveur). — `database/migration/054_surveillance_caisse.sql`
- **Les anomalies ne sont visibles que du titulaire et ne sont jamais supprimées** (closes par « traitée »). — `database/migration/054_surveillance_caisse.sql`

## Ordonnances et médicaments réglementés

### Ordonnance à la vente
- **Une ordonnance est toujours rattachée à un patient** (aussi en base : `ordonnance.patient_id` obligatoire). — `src/Pharma.Application/Caisse/Encaisser.cs`, `database/migration/001_schema.sql`
- **La date de prescription ne peut pas être dans le futur**. — `src/Pharma.Application/Caisse/Encaisser.cs`
- **Image d'ordonnance scannée introuvable sur le serveur : à rescanner**. — `src/Pharma.Application/Caisse/Encaisser.cs`
- **Patient obligatoire selon le réglage de traçabilité** (par défaut « ordonnance » : dès qu'une ordonnance est rattachée ou qu'un psychotrope/stupéfiant est vendu) ; autres réglages : jamais, psychotrope, toujours. — `database/migration/007_tracabilite_boite.sql`
- **À l'écran, le patient est exigé en tiers payant, pour un psychotrope, une ordonnance scannée, ou un médicament de liste I/II avec médecin**. — `src/Pharma.Desktop/Composants/Pages/Caisse.razor`

### Vente sans ordonnance (liste I ou II)
- **Médicament de liste I ou II sans ordonnance (ni médecin prescripteur ni ordonnance scannée) : droit `caisse.vendre_sans_ordonnance` exigé**, sinon autorisation par le code comptoir d'un employé qui l'a. — `src/Pharma.Application/Caisse/Encaisser.cs`
- **Ce droit est donné au départ au titulaire et au pharmacien assistant** ; à attribuer au vendeur dans la matrice des droits. — `database/migration/049_vente_sans_ordonnance.sql`
- **Chaque vente sans ordonnance est inscrite au journal** (`vente_sans_ordonnance` : ticket, produits). — `src/Pharma.Application/Caisse/Encaisser.cs`
- **Ce droit ne s'applique jamais aux psychotropes et stupéfiants**. — `database/migration/049_vente_sans_ordonnance.sql`, `src/Pharma.Domain/Securite/Permissions.cs`

### Psychotropes et stupéfiants
- **La délivrance d'un psychotrope n'est jamais autorisée par le code d'un collègue** : un employé qui a le droit reprend la vente (« Changer d'utilisateur ») ; le registre nomme toujours celui qui délivre. — `src/Pharma.Api/Securite/PermissionsComptoir.cs`
- **Délivrance réservée au droit `psychotropes.delivrer`** : sans ce droit, le message nomme les employés qui l'ont pour qu'ils reprennent la vente (« Changer d'utilisateur »). — `src/Pharma.Application/Caisse/Encaisser.cs`
- **Ordonnance obligatoire pour tous, patient identifié et médecin prescripteur obligatoires**. — `src/Pharma.Domain/Ventes/Vente.cs`, `src/Pharma.Application/Caisse/Encaisser.cs`
- **Porteur de l'ordonnance autre que le patient : nom et prénom, adresse et pièce d'identité obligatoires** (200 caractères au plus chacun ; arrêté du 29 juin 2013, art. 7) ; contrôlé aussi en base. — `src/Pharma.Application/Caisse/Encaisser.cs`, `database/migration/024_registre_psychotropes.sql`
- **Chaque vente est inscrite au registre des psychotropes (sortie) avec son ordonnance**, et au journal (`delivrance_psychotrope`). — `src/Pharma.Infrastructure/Caisse/VentesEcritureSql.cs`, `database/migration/001_schema.sql`
- **Le registre ne peut être ni modifié ni supprimé**. — `database/migration/001_schema.sql`

### Ordonnance scannée et lecture automatique
- **Image d'ordonnance : JPEG ou PNG (reconnu par le contenu du fichier), non vide, 10 Mo au plus**. — `src/Pharma.Application/Ordonnances/Ordonnances.cs`
- **La lecture automatique ne décide rien : le caissier valide chaque ligne proposée**. — `src/Pharma.Application/Ordonnances/Ordonnances.cs`
- **Lecture : 60 lignes au plus, 3 produits proposés par ligne, ressemblance minimale de 0,45** ; une ligne de moins de 4 lettres ou de plus de 200 caractères est écartée ; un produit lu deux fois n'est proposé qu'une fois. — `src/Pharma.Application/Ordonnances/Ordonnances.cs`
- **Nombre de boîtes écrit sur la ligne (« 2 boîtes », « 1 bte ») proposé s'il est entre 1 et 20**. — `src/Pharma.Application/Ordonnances/Ordonnances.cs`
- **Analyser et scanner une ordonnance demandent le droit `caisse.vendre`**. — `src/Pharma.Api/Endpoints/OrdonnancesEndpoints.cs`

## Annulations et retours

### Annulation d'une vente
- **Vente à crédit annulée ou rendue : seul ce que le patient a vraiment payé sur cette vente lui est rendu** ; la dette baisse au plus de ce qui reste impayé sur la vente. Les remboursements soldent les dettes les plus anciennes d'abord ; une dette corrigée ou abandonnée par le titulaire n'est pas un paiement (rien ne sort de la caisse). — `src/Pharma.Domain/Ventes/RegleAnnulation.cs`, `src/Pharma.Infrastructure/Caisse/AnnulationsVenteSql.cs`
- **Annulation et retour en même temps sur la même vente : le second refuse** (« un retour vient d'être enregistré ») ; jamais de double remboursement, jamais une boîte rendue deux fois. — `src/Pharma.Infrastructure/Caisse/AnnulationsVenteSql.cs`, `src/Pharma.Infrastructure/Caisse/RetoursClientSql.cs`
- **Retour d'une ligne en partie scannée** : les boîtes identifiées sont cochées, les autres (vendues sans scan) sont rendues par quantité, au plus celles encore chez le patient. — `src/Pharma.Application/Caisse/RetourClient.cs`
- **Motif obligatoire : 3 à 300 caractères** (aussi en base : au moins 3 caractères). — `src/Pharma.Domain/Ventes/RegleAnnulation.cs`, `database/migration/027_annulation_vente.sql`
- **Seule une vente encaissée peut être annulée** ; une vente déjà annulée est refusée (une seule annulation, même depuis deux postes). — `src/Pharma.Domain/Ventes/RegleAnnulation.cs`, `src/Pharma.Infrastructure/Caisse/AnnulationsVenteSql.cs`
- **Vente déjà placée sur un bordereau d'organisme : annulation refusée**, à traiter par la régularisation Chifa. — `src/Pharma.Domain/Ventes/RegleAnnulation.cs`
- **Vente figurant sur une facture émise ou rejetée : annulation refusée** ; retirer d'abord la vente de la facture, ou faire un retour client. — `src/Pharma.Domain/Ventes/RegleAnnulation.cs`, `src/Pharma.Infrastructure/Caisse/AnnulationsVenteSql.cs`
- **Pas de limite de durée pour annuler**. — `src/Pharma.Domain/Ventes/RegleAnnulation.cs`
- **Vente récente (sa caisse est encore ouverte) : le caissier l'annule seul**. — `src/Pharma.Domain/Ventes/RegleAnnulation.cs`
- **Vente ancienne (caisse clôturée) : droit `caisse.annuler_vente` de l'employé, sinon validation sur place par le code comptoir d'un employé qui l'a** ; en base, une annulation ancienne a toujours un validateur. — `src/Pharma.Application/Caisse/AnnulerVente.cs`, `database/migration/027_annulation_vente.sql`
- **Montant rendu = part payée par le patient moins ce qui a déjà été rendu par des retours**. — `src/Pharma.Domain/Ventes/RegleAnnulation.cs`
- **Vente à crédit annulée : la dette diminue de ce qui reste dû ; ce que le patient a déjà remboursé lui est rendu en espèces**. — `src/Pharma.Domain/Ventes/RegleAnnulation.cs`, `src/Pharma.Application/Caisse/AnnulerVente.cs`
- **Vente payée à un patient qui a une dette : le remboursement peut être retiré de sa dette, seulement avec l'accord du client** (case cochée) ; seul le surplus est rendu. — `src/Pharma.Domain/Ventes/RegleAnnulation.cs`, `src/Pharma.Application/Caisse/AnnulerVente.cs`
- **Vente sans patient : le client peut se nommer pour que sa dette soit diminuée** ; refusé si la vente a déjà un patient. — `src/Pharma.Application/Caisse/AnnulerVente.cs`
- **Argent rendu en espèces : il sort de la caisse ouverte du poste qui annule** (caisse ouverte exigée) ; la clôture d'une caisse passée n'est jamais modifiée. — `src/Pharma.Application/Caisse/AnnulerVente.cs`
- **Le stock est remis sur le même lot, sauf les boîtes déjà rendues** ; les boîtes identifiées reviennent en stock avec leur historique. — `src/Pharma.Infrastructure/Caisse/AnnulationsVenteSql.cs`
- **Annulation refusée si la dette du patient a changé entre-temps** (à recharger). — `src/Pharma.Infrastructure/Caisse/AnnulationsVenteSql.cs`
- **Chaque annulation est inscrite au journal** (`vente_annulee` : motif, montant, remboursement, dette diminuée, validateur, accord du client). — `src/Pharma.Application/Caisse/AnnulerVente.cs`
- **Trop d'annulations sur une caisse : journal (`annulations_excessives`) et notification du titulaire**, une fois, quand le seuil réglé est dépassé (seuil 0 = pas d'alerte). — `src/Pharma.Application/Caisse/AnnulerVente.cs`
- **Recherche d'une vente à annuler : par n° de ticket, ou les ventes de la caisse ouverte du poste, par page (les plus récentes d'abord, toutes, sans limite à 20)**. Les patients en crédit (Règlement d'un crédit) sont aussi paginés par le serveur, plus grosses dettes d'abord, sans limite à 50. — `src/Pharma.Application/Caisse/AnnulerVente.cs`

### Retour de médicaments par le patient
- **Motif obligatoire : 3 à 300 caractères**. — `src/Pharma.Application/Caisse/RetourClient.cs`, `database/migration/028_retour_client.sql`
- **Retour seulement sur une vente encaissée non annulée**. — `src/Pharma.Domain/Ventes/RegleRetour.cs`
- **Pas de retour partiel sur une vente en tiers payant** (sur bordereau ou avec part organisme) : annuler la vente complète puis la refaire sans le produit rendu. — `src/Pharma.Domain/Ventes/RegleRetour.cs`
- **Au moins un produit, chacun une seule fois, appartenant à la vente**. — `src/Pharma.Application/Caisse/RetourClient.cs`
- **Quantité rendue : au moins 1, au plus vendue moins déjà rendue** (vérifié à nouveau à l'enregistrement contre deux retours simultanés). — `src/Pharma.Domain/Ventes/RegleRetour.cs`, `src/Pharma.Infrastructure/Caisse/RetoursClientSql.cs`
- **Pour chaque produit, le pharmacien choisit : remis en stock (même lot) ou à détruire**. — `src/Pharma.Domain/Ventes/RegleRetour.cs`
- **Boîtes identifiées : cocher exactement les boîtes rendues parmi celles vendues**. — `src/Pharma.Application/Caisse/RetourClient.cs`
- **Montant rendu au prorata de ce que le patient a payé (remise comprise), arrondi au centime** ; jamais plus que ce qui reste de la part payée ; au dernier retour, le solde exact. — `src/Pharma.Domain/Ventes/RegleRetour.cs`
- **Depuis les réductions par ligne (2026-11-14), chaque produit rendu est remboursé sur sa part nette** (part du patient de la ligne moins sa remise ou son don partiel) ; les ventes antérieures gardent le prorata de la remise de la vente. — `src/Pharma.Infrastructure/Caisse/RetoursClientSql.cs`
- **Droit `caisse.retour_especes`, sinon validation par le code comptoir d'un employé qui l'a**. — `src/Pharma.Application/Caisse/RetourClient.cs`
- **Exception : remboursement entièrement retiré de la dette avec l'accord du client** (rien ne sort de la caisse) : le caissier valide seul. — `src/Pharma.Application/Caisse/RetourClient.cs`
- **Vente à crédit : la dette diminue ; ce que le patient a déjà remboursé lui est rendu en espèces**. — `src/Pharma.Application/Caisse/RetourClient.cs`
- **Vente payée à un patient endetté : déduction de la dette seulement avec l'accord du client** ; vente sans patient : le client peut se nommer. — `src/Pharma.Application/Caisse/RetourClient.cs`
- **Argent rendu (vente en espèces ou à crédit) : caisse ouverte du poste exigée**. — `src/Pharma.Application/Caisse/RetourClient.cs`
- **Produit à détruire : il rentre puis sort du stock (mouvement « destruction »)** ; la boîte identifiée passe « détruite » ; pour un psychotrope, le registre inscrit le retour puis la destruction. — `src/Pharma.Infrastructure/Caisse/RetoursClientSql.cs`, `database/migration/028_retour_client.sql`
- **Chaque retour est inscrit au journal** (`retour_client` : motif, montant, rendu, dette diminuée, remis en stock, à détruire, validateur). — `src/Pharma.Application/Caisse/RetourClient.cs`

### Remboursement sans ticket (scan de la boîte rapportée)
- **Boîte identifiée rattachée à sa vente : c'est cette vente, et aucune autre**. — `src/Pharma.Application/Caisse/RechercherVenteParBoite.cs`
- **Boîte déjà rendue : ne peut pas être remboursée une seconde fois** ; boîte détruite ou rendue au grossiste : non remboursable. — `src/Pharma.Application/Caisse/RechercherVenteParBoite.cs`
- **Sinon, recherche des ventes du produit (et du lot s'il est connu) sur 7 jours par défaut (1 à 365)**, avec au moins une boîte pas encore rendue, 50 ventes au plus. — `src/Pharma.Application/Caisse/RechercherVenteParBoite.cs`, `src/Pharma.Infrastructure/Caisse/AnnulationsVenteSql.cs`
- **Patient indiqué : seulement ses ventes ; sinon seulement les ventes sans patient** (on ne rembourse pas la vente d'un autre patient). — `src/Pharma.Application/Caisse/RechercherVenteParBoite.cs`
- **Code inconnu de la pharmacie : refusé**. — `src/Pharma.Application/Caisse/RechercherVenteParBoite.cs`

## Crédits et dettes des patients

### Vente à crédit
- **Mettre une vente en crédit demande le droit `credit.accorder`** (sinon code comptoir d'un employé qui l'a). — `src/Pharma.Application/Caisse/Encaisser.cs`, `src/Pharma.Desktop/Composants/Pages/Caisse.razor`
- **Le crédit est toujours accordé à un patient choisi**. — `src/Pharma.Application/Caisse/Encaisser.cs`
- **Dette + part du patient au-delà du plafond : droit `credit.depasser_plafond` exigé** (autorisable par code comptoir). — `src/Pharma.Application/Caisse/Encaisser.cs`, `src/Pharma.Domain/Ventes/Patient.cs`
- **Plafond d'un patient : 0 par défaut en base** ; à la création depuis la caisse, le plafond proposé est celui des paramètres (0 par défaut). — `database/migration/001_schema.sql`, `src/Pharma.Desktop/Composants/Pages/Caisse.razor`
- **Part du patient nulle (organisme qui paie tout) : la dette ne bouge pas**. — `src/Pharma.Application/Caisse/Encaisser.cs`

### Remboursement d'une dette au comptoir
- **Caisse du poste ouverte exigée** : l'argent entre dans cette caisse. — `src/Pharma.Application/Caisse/ReglementCredit.cs`
- **Mode : espèces, carte ou chèque ; montant supérieur à 0 ; le patient doit avoir une dette ; pas plus que ce qu'il doit**. — `src/Pharma.Domain/Ventes/RegleCredit.cs`
- **Reçu numéroté RC-000001, RC-000002…** ; règlement inscrit au journal (`reglement_credit`, reste dû). — `src/Pharma.Infrastructure/Caisse/CreditsClientsSql.cs`, `database/migration/047_recette_corrections.sql`, `src/Pharma.Application/Caisse/ReglementCredit.cs`
- **Refus si la dette a changé entre-temps** (deux postes ne peuvent pas encaisser deux fois). — `src/Pharma.Infrastructure/Caisse/CreditsClientsSql.cs`
- **Les remboursements soldent d'abord la dette la plus ancienne** : dette reprise de l'ancien logiciel, puis ventes à crédit par date. — `database/migration/038_releve_credit.sql`
- **Consultation des crédits, relevé et règlement : droit `caisse.vendre`** ; le relevé PDF imprimé porte tous les mouvements. — `src/Pharma.Api/Endpoints/ReglementsEndpoints.cs`, `src/Pharma.Application/Caisse/ReglementCredit.cs`

### Correction d'une dette
- **Réservée au titulaire** (erreur de reprise, geste commercial, créance abandonnée). — `src/Pharma.Application/Caisse/ReglementCredit.cs`
- **Motif de 5 à 300 caractères ; nouvelle dette ≥ 0 (au centime) et différente de l'actuelle** ; refus si la dette a changé depuis sa lecture. — `src/Pharma.Application/Caisse/ReglementCredit.cs`, `src/Pharma.Infrastructure/Caisse/CreditsClientsSql.cs`
- **Inscrite au relevé (« correction ») et au journal, avec la dette avant et après**. — `src/Pharma.Application/Caisse/ReglementCredit.cs`

### Relevé de dette
- **Chaque changement de dette est inscrit au relevé, même fait hors du logiciel** : ouverture, reprise, vente à crédit, remboursement, retour, annulation, correction ; sans précision, un changement est noté « correction ». — `database/migration/038_releve_credit.sql`
- **Le relevé ne se modifie pas et ne s'efface pas** : on corrige par un nouveau mouvement. — `database/migration/038_releve_credit.sql`
- **Patient créé avec une dette (reprise) : dette d'ouverture, soldée en premier**. — `database/migration/038_releve_credit.sql`
- **Dette ou créance signalée à partir de 30 jours d'ancienneté** (réglable de 1 à 365 jours). — `database/migration/039_suivi_dettes.sql`

## Patients, médecins et contacts

### Patients
- **Nom obligatoire ; plafond de crédit jamais négatif**. — `src/Pharma.Application/Patients/Patients.cs`
- **Date de naissance jamais dans le futur**. — `src/Pharma.Application/Patients/Patients.cs`
- **Créer, consulter et modifier un patient : droit `caisse.vendre`**. — `src/Pharma.Api/Endpoints/PatientsEndpoints.cs`
- **La dette ne se modifie pas depuis la fiche patient**. — `src/Pharma.Application/Patients/Patients.cs`
- **Chaque champ modifié est inscrit au journal avec l'ancienne et la nouvelle valeur** (`patient_modifie`). — `src/Pharma.Application/Patients/Patients.cs`
- **La création d'un patient garde l'employé qui l'a créé et est inscrite au journal** (`patient_cree` : nom, plafond de crédit). Les patients antérieurs ou repris n'ont pas de créateur. — `src/Pharma.Application/Patients/Patients.cs`, `database/migration/058_surveillance_comptes.sql`
- **Recherche au comptoir à partir de 2 caractères** (nom, prénom, téléphone, n° d'assuré), 20 résultats au plus. — `src/Pharma.Application/Patients/Patients.cs`

### Médecins prescripteurs
- **Nom obligatoire** ; créer ou corriger un médecin demande le droit `caisse.vendre` ; la correction est inscrite au journal (`medecin_modifie`). — `src/Pharma.Application/Patients/Patients.cs`, `src/Pharma.Api/Endpoints/PatientsEndpoints.cs`
- **Recherche à partir de 2 caractères, 20 résultats au plus**. — `src/Pharma.Application/Patients/Patients.cs`

### Coordonnées (téléphone, e-mail, adresse)
- **Téléphone : chiffres, espaces, +, -, ., parenthèses ; au moins 6 chiffres ; 30 caractères au plus**, contrôlé à la création d'un patient comme à sa modification. — `src/Pharma.Application/Contacts/Contacts.cs`, `src/Pharma.Application/Patients/Patients.cs`
- **Téléphone repris de l'ancien logiciel accepté tel quel tant qu'on n'y touche pas**. — `src/Pharma.Application/Contacts/Contacts.cs`, `src/Pharma.Application/Patients/Patients.cs`
- **E-mail de la forme nom@domaine.ext, sans espace, 120 caractères au plus**. — `src/Pharma.Application/Contacts/Contacts.cs`
- **Adresse : 300 caractères au plus ; un patient n'a pas d'adresse** (grossiste et entreprise seulement). — `src/Pharma.Application/Contacts/Contacts.cs`

### Fiche contact
- **Fiche d'un patient : droit `caisse.vendre` ; d'une entreprise cliente : droit `factures.gerer` ; grossiste, organisme, employé : tout employé connecté**. — `src/Pharma.Application/Contacts/Contacts.cs`
- **Coordonnées d'un employé visibles seulement par lui-même, le titulaire ou qui a le droit `admin.utilisateurs`**. — `src/Pharma.Application/Contacts/Contacts.cs`
- **Modifier les coordonnées depuis la fiche : patient `caisse.vendre`, grossiste `achats.commander`, entreprise `factures.gerer`** ; organismes et employés seulement dans leurs écrans d'administration ; modification inscrite au journal (`coordonnees_modifiees`). — `src/Pharma.Application/Contacts/Contacts.cs`
- **Délai de paiement modifiable depuis la fiche pour une entreprise cliente seulement**. — `src/Pharma.Application/Contacts/Contacts.cs`

## Stock

### Lots : identité et numéro
- **Un lot est identifié par le produit, le grossiste, le numéro de lot et la date de péremption** : deux lots identiques sur ces quatre éléments sont impossibles (contrainte `ux_lot_fournisseur_date`). — `database/init/schema.sql`
- **Le numéro de lot est enregistré en majuscules, sans espaces autour** : « am55 » et « AM55 » désignent le même lot ; un numéro vide est refusé (« Numéro de lot vide »). — `src/Pharma.Domain/Stock/RegleLot.cs`, déclencheur `normaliser_numero_lot` dans `database/init/schema.sql`
- **Le grossiste d'un lot doit être celui de sa réception** : sinon refus « Le fournisseur du lot ne correspond pas à celui de la réception ». — déclencheur `verifier_fournisseur_lot`, `database/init/schema.sql`
- **La quantité disponible d'un lot ne peut jamais être négative** (contrainte `quantite_disponible >= 0`). — `database/init/schema.sql`
- **Le prix public facturé d'un lot (vignette, PPA) est facultatif et jamais négatif** : vide, c'est le prix public du catalogue qui s'applique. — `src/Pharma.Domain/Stock/Lot.cs`, contrainte `ck_lot_ppa`
- **Chaque boîte garde le prix d'achat de sa livraison** : à défaut de prix fourni, la boîte prend le prix de la dernière entrée de ce lot dans cette réception, sinon le prix d'achat du lot ; le prix d'une boîte n'est jamais négatif. — déclencheur `completer_reception_boite`, `database/init/schema.sql`
- **La valeur d'un lot en stock se calcule boîte par boîte** : boîtes identifiées à leur propre prix d'achat, boîtes sans identité au prix d'achat du lot. — vue `v_valeur_lot`, `database/init/schema.sql`

### Lots : relivraison du même lot (réception)
- **Le même lot relivré (même produit, même grossiste, même n°, même péremption) complète le lot existant** au lieu d'en créer un nouveau. — `src/Pharma.Domain/Stock/RegleLot.cs`
- **Un lot repris de l'ancien logiciel avec une date provisoire (« date à vérifier ») est complété s'il est le seul lot de ce numéro**, même si la date saisie diffère. — `src/Pharma.Domain/Stock/RegleLot.cs`
- **Même n° de lot déjà en stock avec une autre date de péremption : la réception est refusée tant que l'utilisateur n'a pas choisi** : corriger la date, ou « Créer un lot séparé ». — `src/Pharma.Domain/Stock/RegleLot.cs`
- **Lot bloqué (rappel) relivré : la réception est refusée tant que l'utilisateur n'a pas choisi** : mettre les boîtes de côté, ou « Entrer en stock bloqué » ; un nouveau lot créé dans ce cas est créé bloqué. — `src/Pharma.Domain/Stock/RegleLot.cs`

### Lot vendable
- **Un lot n'est pas vendable s'il est bloqué, périmé ou épuisé**, vérifiés dans cet ordre. — `src/Pharma.Domain/Stock/Lot.cs`
- **Un lot est périmé dès le jour de sa date de péremption** (date de péremption ≤ aujourd'hui). — `src/Pharma.Domain/Stock/Lot.cs`, `database/init/schema.sql`
- **La base refuse toute ligne de vente sur un lot bloqué ou périmé** : « Lot bloqué (motif), vente interdite » ou « Lot périmé depuis le jj/mm/aaaa, vente interdite », même si le contrôle du logiciel a été contourné. — déclencheur `verifier_lot_vendable`, `database/init/schema.sql`

### FEFO (premier périmé, premier sorti) et choix du lot en caisse
- **Le lot servi par défaut est celui qui périme le plus tôt parmi les lots vendables** ; à date égale, le plus ancien enregistré. — `src/Pharma.Domain/Stock/Lot.cs`, `src/Pharma.Application/Catalogue/ObtenirLotFefo.cs`
- **Quand le premier lot ne suffit pas, le lot suivant dans l'ordre FEFO est proposé** (les lots déjà au panier du poste sont écartés). — `src/Pharma.Application/Catalogue/ObtenirLotFefo.cs`
- **Les lots périmés ou bloqués encore en stock sont listés au comptoir avec leur raison** (et le motif de blocage), pour expliquer pourquoi ils ne sont pas servis. — `src/Pharma.Application/Catalogue/ObtenirLotFefo.cs` (droit `caisse.vendre` à l'API)
- **Le stock vendable affiché au comptoir exclut les boîtes périmées et bloquées**. — `src/Pharma.Application/Catalogue/ObtenirLotFefo.cs`
- **Un code-barres fabricant scanné renvoie le lot qui périme le plus tôt avec du stock et non périmé** ; ce calcul en base ne tient pas compte du blocage (à confirmer si voulu). — fonction `lot_fefo`, `database/init/schema.sql`
- **Une boîte identifiée scannée en caisse (étiquette interne ou Data Matrix) est servie de son propre lot** ; si un autre lot vendable du même produit périme avant elle, le caissier est averti de servir d'abord ce lot. — `src/Pharma.Domain/Stock/RegleFefo.cs`, `src/Pharma.Application/Caisse/ControlerBoite.cs`
- **Couleur de cet avertissement : rouge si le lot à servir d'abord est en péremption proche (niveau rouge ou orange), orange sinon** ; l'emplacement (ou « réserve … ») où trouver ce lot est indiqué. — `src/Pharma.Domain/Stock/RegleFefo.cs`, `src/Pharma.Application/Caisse/ControlerBoite.cs`
- **En caisse, une boîte identifiée qui n'est plus en stock est refusée** (« déjà vendue, retournée ou détruite ») ; une boîte d'un lot bloqué ou périmé est refusée (vente interdite) ; une boîte dont le lot est épuisé dans le logiciel est refusée (« Vérifiez le stock »). Droit `caisse.vendre`. — `src/Pharma.Application/Caisse/ControlerBoite.cs`
- **Servir un lot plus récent alors qu'un lot plus ancien vendable est en stock (reçu avant ou périmant avant) exige une raison à l'encaissement** : « péremption plus proche », « boîte de l'ancien lot introuvable », « boîte de l'ancien lot abîmée » ou « demande du client ». — `src/Pharma.Domain/Ventes/RaisonLot.cs`, fonction `situation_lot`, contrainte `ck_ligne_vente_raison_lot`
- **La raison « péremption plus proche » n'est jamais choisie librement** : elle n'est acceptée que si le logiciel vérifie sur les dates enregistrées que le lot servi périme avant tous les autres (et qu'aucune date n'est « à vérifier ») ; le vendeur la valide seulement. — `src/Pharma.Domain/Ventes/RaisonLot.cs`, fonction `situation_lot`

### Ancien lot délaissé
- **Un ancien lot est « délaissé » quand il reste vendable en stock alors que des boîtes d'un lot plus récent du même produit sont vendues et qu'aucune boîte de l'ancien lot n'est vendue depuis**. Lot plus récent : reçu après ou périmant après, et ne périmant pas avant l'ancien. — fonction `lots_delaisses`, `database/init/schema.sql`
- **Les ventes justifiées par « péremption plus proche » ne comptent pas** pour détecter un lot délaissé. — fonction `lots_delaisses`
- **Le comptage démarre à l'arrivée des deux lots, ou à la dernière alerte « lot délaissé » traitée par le titulaire**. — fonction `lots_delaisses`
- **Alerte déclenchée si un même vendeur atteint le seuil individuel, ou si au moins 2 vendeurs atteignent ensemble le seuil collectif** : par défaut 5 boîtes (individuel) et 10 boîtes (collectif), chacun au moins 1 boîte. — fonction `lots_delaisses`, table `parametre_alerte`, `src/Pharma.Application/Administration/Seuils.cs`
- **L'alerte est « urgente » si le seuil collectif est atteint, si un vendeur a invoqué « boîte de l'ancien lot introuvable », ou si l'ancien lot périme dans le délai rouge général** ; « attention » sinon. Son montant est la valeur de l'ancien lot au prix d'achat. — fonction `lots_delaisses`
- **Le calcul est refait après chaque vente pour les produits vendus** ; une nouvelle alerte, ou une alerte qui passe à « urgent », est notifiée au titulaire. — fonction `alerter_lots_delaisses`, `src/Pharma.Application/Alertes/Alertes.cs`
- **L'alerte « ancien lot délaissé » est réservée au titulaire** (elle nomme les vendeurs) : les autres employés ne la voient pas, ne peuvent ni la reporter ni la traiter. — `src/Pharma.Application/Alertes/Alertes.cs`, `src/Pharma.Infrastructure/Alertes/AlertesEcriture.cs`
- **Chaque employé ne voit que les alertes sur lesquelles il peut agir** (choix du 2026-11-13) : liste, compteur « Stock » du menu, tuile « Alertes urgentes » de l'accueil, et report ou traitement (refusés par le serveur sur une alerte qu'il ne voit pas). Rupture et stock critique : `achats.commander` ; surstock et dormant : `achats.commander` ou `stock.ajuster` ; périmé : `stock.declarer_perime` ou `achats.retour_fournisseur` ; péremption proche et risque d'écoulement : `caisse.vendre` (vendre d'abord) ou `achats.retour_fournisseur` ; ancien lot délaissé : titulaire seul ; le titulaire voit tout. Un vendeur (vendre, réceptionner) ne voit donc que la péremption proche et le risque d'écoulement. — `src/Pharma.Application/Alertes/Alertes.cs` (`AlertesSelonDroits`), `src/Pharma.Infrastructure/Lecture/AlertesLecture.cs`, `src/Pharma.Infrastructure/Alertes/AlertesEcriture.cs`
- **Depuis cette alerte, seul le titulaire peut lancer l'inventaire du produit** : comptage à l'aveugle de tous ses lots en stock (rayon et réserve) ; si un inventaire de ce produit est déjà en cours, il est repris ; refus si aucun lot en stock. — `src/Pharma.Application/Stock/Inventaire.cs` (`ExigerTitulaireAsync`)

### Blocage d'un lot (rappel)
- **Un n° de lot rappelé reste bloqué pour une livraison d'un autre grossiste** : à la réception, ces boîtes sont mises de côté ou entrées en stock bloqué, jamais vendables sans choix. — `src/Pharma.Domain/Stock/RegleLot.cs`, `src/Pharma.Infrastructure/Achats/AchatsSql.cs`
- **Un rappel exige le produit, le numéro de lot et un motif** ; le numéro est normalisé (majuscules). Droit `stock.declarer_perime` (exigé par l'API). — `src/Pharma.Application/Tracabilite/RappelerLot.cs`, `src/Pharma.Api/Endpoints/TracabiliteEndpoints.cs`
- **Le rappel bloque ce n° de lot chez tous les grossistes où il est encore vendable, en une seule opération**, et trace un événement « blocage » sur chaque boîte identifiée en stock de ces lots. — fonction `rappel_lot`, `src/Pharma.Infrastructure/Tracabilite/RappelLotSql.cs`
- **Un lot déjà bloqué partout n'est pas rebloqué** : refus « déjà rappelé … rien n'a été modifié », sans journal ni alerte ; la liste des patients reste consultable. — `src/Pharma.Application/Tracabilite/RappelerLot.cs`
- **Le rappel donne la liste des ventes validées du lot** (patient et téléphone, ou « Vente anonyme », date, ticket, boîtes), y compris celles d'avant l'étiquetage des boîtes, et l'emplacement où retirer les boîtes. — fonction `ventes_lot`, `src/Pharma.Infrastructure/Tracabilite/RappelLotSql.cs`
- **Chaque rappel est inscrit au journal des actions sensibles et le titulaire est alerté** (l'échec de l'alerte n'annule pas le blocage). — `src/Pharma.Infrastructure/Tracabilite/RappelLotSql.cs`, `src/Pharma.Application/Tracabilite/RappelerLot.cs`
- **La consultation d'un lot (rappelé ou non) ne modifie rien** : état du blocage, motif du premier rappel, patients concernés ; même droit `stock.declarer_perime`. — `src/Pharma.Application/Tracabilite/RappelerLot.cs`
- **Le motif d'un blocage de lot n'est jamais envoyé au téléphone** (il peut citer un patient) : le mobile affiche seulement « Le motif est sur le poste ». — `src/Pharma.Application/Mobile/Mobile.cs`

### Sorties de stock : périmé, cassé, don
- **La liste des lots d'un produit et la lecture d'un lot sont ouvertes à `stock.consulter`, `stock.donner` ou `stock.declarer_perime`** : un vendeur qui a seulement le droit de donner peut choisir le lot. — `src/Pharma.Api/Endpoints/CatalogueEndpoints.cs`
- **Trois motifs de sortie seulement : périmé, cassé, échantillon/gratuité/don**. — `src/Pharma.Application/Stock/SortieStock.cs`
- **Déclarer des boîtes périmées ou cassées exige le droit `stock.declarer_perime` ; un don (échantillon, gratuité, personne dans le besoin) exige le droit de don** (`stock.donner`, réglé sur chaque employé par le titulaire depuis le 2026-11-14). — `src/Pharma.Application/Stock/SortieStock.cs`, `database/migration/071_don_remise_par_personne.sql`
- **Le motif « don » est proposé à tout employé qui ouvre la sortie de stock** : sans le droit de don, le code d'un employé qui l'a est demandé à l'enregistrement (autorisation sur place) ; la sortie est ouverte aux droits `stock.consulter`, `stock.declarer_perime` ou `stock.donner`. — `src/Pharma.Desktop/Composants/Mise/SortieStock.razor`, `src/Pharma.Api/Endpoints/CatalogueEndpoints.cs`
- **La quantité sortie va de 1 au stock du lot**. — `src/Pharma.Application/Stock/SortieStock.cs`
- **Commentaire limité à 200 caractères ; obligatoire pour une casse (ce qui s'est passé) et pour un don (à qui et pourquoi)**. — `src/Pharma.Application/Stock/SortieStock.cs`
- **Un produit sous registre (psychotrope, stupéfiant) n'est jamais donné en échantillon**. — `src/Pharma.Application/Stock/SortieStock.cs`
- **Si le stock du lot a changé entre-temps, la sortie est refusée** (« rechargez la liste »). — `src/Pharma.Infrastructure/Lecture/SortiesStockSql.cs`
- **Les boîtes identifiées sorties sont choisies parmi les plus anciennes du lot** : statut « détruite » (périmé, cassé) ou « donnée » (don), avec un événement de boîte. — `src/Pharma.Infrastructure/Lecture/SortiesStockSql.cs`
- **Pour un psychotrope, la base inscrit automatiquement la sortie au registre** : « Destruction (périmé) » ou « Casse ». — déclencheur `inscrire_mouvement_registre`, `database/init/schema.sql`
- **Chaque sortie est inscrite au journal** ; les casses et les dons relancent la surveillance des employés. — `src/Pharma.Application/Stock/SortieStock.cs`
- **Les rapports (écran, PDF, CSV) ont une section « Sorties de stock sans vente »** : périmés, casses, dons et échantillons de la période, en boîtes et en valeur d'achat HT, au total, par employé, par produit et en détail (date, lot, employé, bénéficiaire ou raison). Les retours clients détruits et les manquants à la réception n'y figurent pas (ils ont leur propre suivi). — `src/Pharma.Application/Rapports/Rapports.cs`, `src/Pharma.Infrastructure/Lecture/RapportsSql.cs`
- **Valeur d'une sortie = boîtes × prix d'achat HT retenu le jour de la sortie** (prix de la réception du lot, sinon prix du lot) ; la TVA n'y est pas comptée. Cette valeur est réservée au droit `rapports.marges` ; sans ce droit, seules les boîtes sont montrées. — `src/Pharma.Application/Rapports/Rapports.cs`, déclencheur de `mouvement_stock` (`database/init/schema.sql`)
- **Le tableau de bord du titulaire (Pilotage, mobile) montre les pertes et dons du jour et du mois** (boîtes par motif, valeur d'achat HT). — `src/Pharma.Infrastructure/Lecture/PilotageSql.cs`
- **Une sortie sans vente n'est pas un mouvement d'argent : elle n'apparaît pas en trésorerie**. — `src/Pharma.Infrastructure/Lecture/RapportsSql.cs`
- **Marge après pertes et dons = marge brute − valeur d'achat HT des boîtes sorties sans vente − TVA à reverser sur ces boîtes** ; tuile et lignes du PDF et du CSV, réservées au droit `rapports.marges`. — `src/Pharma.Application/Rapports/Rapports.cs`
- **TVA à reverser sur une sortie = prix d'achat HT retenu le jour de la sortie × taux de TVA du produit** (0 pour un produit sans TVA), seulement pour les motifs réglés « oui » (périmé, casse, don ou échantillon). — `src/Pharma.Infrastructure/Lecture/RapportsSql.cs`, `src/Pharma.Application/Rapports/Rapports.cs`
- **Le réglage par motif est « non » par défaut, modifiable par le titulaire seul et inscrit au journal** (`tva_sorties`, avec l'ancien réglage) ; règle fiscale à confirmer avec le comptable. — `database/migration/061_tva_sorties.sql`, `src/Pharma.Application/Rapports/Rapports.cs`, `docs/a-faire.md`

### Inventaire tournant (par rayon)
- **Un lot n'est jamais dans deux inventaires en cours à la fois** (rayon, produit, produits sensibles) : il n'est pas ajouté à un second inventaire, sinon son écart serait ajusté deux fois ; un seul comptage « Produits sensibles » en cours, même lancé sur deux postes. — `src/Pharma.Infrastructure/Lecture/InventairesSql.cs`
- **Un lot appartient au rayon de l'emplacement de vente de son produit, à défaut à celui de sa réserve, sinon au rayon « (Sans emplacement) »** ; on compte toutes ses boîtes, réserve comprise. — `src/Pharma.Infrastructure/Lecture/InventairesSql.cs`
- **Commencer, compter, scanner, terminer ou annuler un inventaire exige le droit `stock.ajuster`** ; le tableau des inventaires exige `stock.consulter`. — `src/Pharma.Application/Stock/Inventaire.cs`, `src/Pharma.Api/Endpoints/InventaireEndpoints.cs`
- **Un seul inventaire en cours par rayon** : commencer un rayon déjà en cours reprend l'inventaire existant (index `ux_inventaire_en_cours`) ; un rayon sans lot en stock est refusé. — `src/Pharma.Application/Stock/Inventaire.cs`, `database/init/schema.sql`
- **Rayon proposé chaque jour** : d'abord un comptage commencé à terminer, puis un rayon qui a des lots « date à vérifier », puis le rayon jamais compté ou compté il y a le plus longtemps (puis ordre alphabétique) ; un rayon vide n'est jamais proposé. — `src/Pharma.Domain/Stock/RegleInventaire.cs`
- **Comptage à l'aveugle** : pendant le comptage, le stock théorique et les écarts ne sont jamais montrés. — `src/Pharma.Application/Stock/Inventaire.cs`
- **Quantité comptée entre 0 et 100 000 boîtes**. — `src/Pharma.Domain/Stock/RegleInventaire.cs`, contrainte `ligne_inventaire_compte_check`
- **Le stock théorique est relevé au moment où la ligne est comptée**. — `src/Pharma.Infrastructure/Lecture/InventairesSql.cs`
- **Un scan pendant l'inventaire ajoute une boîte à la ligne de son lot** ; le code doit désigner un lot précis (étiquette interne, code interne du lot ou Data Matrix avec n° de lot), sinon saisie à la main ; une boîte d'un autre rayon est refusée (« rangez-la à sa place »). — `src/Pharma.Application/Stock/Inventaire.cs`
- **Deux téléphones qui scannent le même lot au même moment comptent chacun leur boîte** (ajout fait en base). — `src/Pharma.Infrastructure/Lecture/InventairesSql.cs`
- **Une boîte identifiée n'est comptée qu'une fois par inventaire** (« déjà comptée … passez à la suivante ») ; elle est notée « vue à l'inventaire ». — `src/Pharma.Infrastructure/Lecture/InventairesSql.cs`, index `ux_boite_vue_inventaire`
- **Date de péremption confirmée pendant le comptage : retenue seulement pour un lot « date à vérifier »** ; une date antérieure de plus d'un an à aujourd'hui est refusée (« invraisemblable »). À la fin, la date du lot est remplacée et le lot n'est plus « à vérifier ». — `src/Pharma.Domain/Stock/RegleInventaire.cs`, `src/Pharma.Infrastructure/Lecture/InventairesSql.cs`
- **Terminer exige que tous les lots soient comptés** (saisir 0 si un lot est introuvable). — `src/Pharma.Application/Stock/Inventaire.cs`
- **Écart = compté − théorique ; valeur de l'écart = écart × prix d'achat unitaire du lot, arrondie au centime**. — `src/Pharma.Application/Stock/Inventaire.cs`
- **Un écart est ajusté tout de suite s'il reste sous les deux seuils : nombre de boîtes ET valeur au prix d'achat** ; sinon il attend la décision du titulaire. Seuils par défaut : 2 boîtes et 2 000 DA. — `src/Pharma.Domain/Stock/RegleInventaire.cs`, table `parametre_inventaire`
- **L'ajustement s'applique au stock actuel du lot** (des ventes ont pu avoir lieu depuis le comptage), sans jamais descendre sous 0, et laisse un mouvement « ajustement d'inventaire ». — `src/Pharma.Infrastructure/Lecture/InventairesSql.cs`
- **Un écart au-delà des seuils est validé (stock ajusté) ou refusé (à recompter) par le titulaire seul** ; un écart déjà traité est refusé. — `src/Pharma.Application/Stock/Inventaire.cs` (`ExigerTitulaireAsync`)
- **Le titulaire est prévenu (SMS ou e-mail) quand des écarts attendent sa décision** ; l'échec de l'envoi n'empêche pas de terminer. — `src/Pharma.Application/Stock/Inventaire.cs`
- **Seul le titulaire règle les seuils d'inventaire, jamais négatifs**. — `src/Pharma.Application/Stock/Inventaire.cs`
- **Un inventaire terminé ne peut plus être compté, terminé ni annulé**. — `src/Pharma.Application/Stock/Inventaire.cs`
- **Fin d'inventaire, écart validé, ajustements : chacun est inscrit au journal** et relance la surveillance (boîtes fantômes, boîtes manquantes ajustées). — `src/Pharma.Application/Stock/Inventaire.cs`
- **Pour un psychotrope, un ajustement d'inventaire est inscrit au registre** : « Écart d'inventaire (+) » ou « (−) ». — déclencheur `inscrire_mouvement_registre`

### Inventaire complet (campagne)
- **Lancer, clôturer, annuler l'inventaire complet et imprimer son état sont réservés au titulaire**. — `src/Pharma.Application/Stock/InventaireComplet.cs`
- **Au lancement, le titulaire choisit : pharmacie ouverte (les ventes continuent) ou fermée (la caisse refuse toute vente jusqu'à la clôture)**. — `src/Pharma.Domain/Stock/RegleInventaireComplet.cs`, `src/Pharma.Application/Stock/InventaireComplet.cs`
- **Un seul inventaire complet en cours à la fois, et aucun inventaire tournant ne doit être en cours au lancement**. — `src/Pharma.Infrastructure/Lecture/CampagnesInventaireSql.cs`, index `ux_campagne_en_cours`
- **Pendant la campagne, chaque rayon compté lui est rattaché** ; les règles de comptage sont celles de l'inventaire tournant. — `src/Pharma.Infrastructure/Lecture/InventairesSql.cs`
- **Clôture refusée tant qu'un rayon qui a du stock n'est pas compté et terminé, ou qu'un gros écart attend le titulaire**. — `src/Pharma.Domain/Stock/RegleInventaireComplet.cs`
- **À la clôture, l'état du stock est figé lot par lot (quantité × prix d'achat du lot)** avec le nombre de lots, de boîtes et la valeur totale. — `src/Pharma.Infrastructure/Lecture/CampagnesInventaireSql.cs`
- **L'état d'inventaire (PDF) ne s'imprime qu'après la clôture**. — `src/Pharma.Application/Stock/InventaireComplet.cs`
- **Annuler la campagne annule les rayons encore en cours ; les rayons terminés gardent leurs corrections**. — `src/Pharma.Infrastructure/Lecture/CampagnesInventaireSql.cs`

### Produits sensibles
- **Seul le titulaire choisit les produits sensibles et consulte leur liste** ; chaque changement est inscrit au journal. — `src/Pharma.Application/Surveillance/Surveillance.cs`
- **Les produits sensibles se comptent à part, dans un inventaire « Produits sensibles »** (tous leurs lots en stock, à l'aveugle) ; droit `stock.ajuster` ; si un comptage est déjà en cours il est repris ; refus s'il n'y a rien à compter. — `src/Pharma.Application/Stock/Inventaire.cs`
- **Une anomalie « comptage des produits sensibles en retard » est levée si le dernier comptage dépasse le délai réglé** : 7 jours par défaut, réglable de 1 à 90 jours par le titulaire. — fonction `surveillance_candidats`, `src/Pharma.Application/Surveillance/Surveillance.cs`

### Contrôle interne : stock (anomalies au titulaire)
- **Boîtes manquantes ajustées en inventaire, casses et dons sont comptés par employé et par jour** ; anomalie dès le seuil (par défaut 5 boîtes ajustées, 3 casses, 3 dons), « urgente » au double du seuil. — fonction `surveillance_candidats`, table `parametre_surveillance`
- **Boîtes fantômes : à la fin d'un inventaire, les boîtes identifiées en stock d'un lot scanné mais jamais scannées sont signalées** (« elles ont peut-être quitté la pharmacie ») ; urgent à partir de 3 boîtes. — fonction `surveillance_candidats`

### Emplacements et rangement
- **Un emplacement = rayon (obligatoire), étagère et tiroir facultatifs, en majuscules, lettres et chiffres, 10 caractères au plus par niveau** ; code composé « A-02-3 ». — `src/Pharma.Domain/Stock/RegleEmplacement.cs`, contraintes `emplacement_code_check`
- **Un tiroir exige une étagère**. — `src/Pharma.Domain/Stock/RegleEmplacement.cs`, contrainte `emplacement_check`
- **Deux emplacements ne peuvent pas avoir le même code**. — `src/Pharma.Application/Stock/Emplacements.cs`, contrainte `emplacement_code_key`
- **Chaque emplacement est en zone de vente ou en réserve**. — contrainte `emplacement_zone_check`
- **Créer ou modifier un emplacement exige le droit `admin.parametres`** ; inscrit au journal. — `src/Pharma.Application/Stock/Emplacements.cs`
- **Ranger un produit exige le droit `stock.ajuster`** : un emplacement de vente (en zone de vente) et une réserve facultative (en zone de réserve), deux emplacements différents, actifs. — `src/Pharma.Application/Stock/Emplacements.cs`, `src/Pharma.Domain/Stock/RegleEmplacement.cs`, déclencheur `verifier_zones_produit`

### État du stock
- **Le stock vendable ne compte ni le périmé ni les lots bloqués** (rappel) : alertes de rupture et de stock critique, commande suggérée, recherche à la caisse, alternatives et rapprochement d'ordonnance. — `database/migration/065_stock_vendable_hors_bloque.sql`
- **Le stock d'un produit compte ses lots non périmés** (les lots bloqués y sont inclus) ; seuls les produits actifs apparaissent. — vue `v_etat_stock`
- **Consommation journalière = boîtes vendues (ventes validées) sur les 90 derniers jours ÷ 90** (fenêtre réglable). — vue `v_consommation`, table `parametre_alerte`
- **Seuil d'alerte d'un produit = stock minimum saisi, sinon consommation × (délai grossiste + stock de sécurité)** : par défaut 2 + 7 jours. — vue `v_etat_stock`
- **Statut d'un produit, dans cet ordre** : rupture (stock 0 et seuil > 0), critique (stock sous le seuil), dormant (stock sans vente depuis 90 jours), surstock (plus de 180 jours de stock), sinon ok. — vue `v_etat_stock`
- **Quantité suggérée à commander = plus grand de (stock visé, seuil) − stock**, stock visé = consommation × 30 jours par défaut. — vue `v_etat_stock`
- **La valeur du stock affichée ne compte que les lots non périmés, au prix réel de chaque boîte**. — `src/Pharma.Infrastructure/Lecture/StockLecture.cs`

## Alertes de stock

### Péremption : seuils et niveaux
- **Trois niveaux de seuils de péremption : produit, sinon grossiste, sinon réglage général** ; un seuil vide reprend celui du niveau au-dessus. Général par défaut : orange 180 jours, rouge 90 jours. — vue `v_risque_peremption`, `src/Pharma.Domain/Alertes/RegleSeuils.cs`
- **Chaque seuil vaut au moins 1 jour et le seuil rouge effectif doit être plus court que l'orange**. — `src/Pharma.Domain/Alertes/RegleSeuils.cs`, contraintes `ck_seuils_general`, `ck_seuils_produit`
- **Un même produit ne peut figurer deux fois dans les exceptions ; une exception sans aucun seuil est ignorée**. — `src/Pharma.Application/Administration/Seuils.cs`
- **Risque d'écoulement, stock visé et délai « dormant » valent au moins 1 jour ; délai grossiste et stock de sécurité jamais négatifs ; délai de reprise d'un grossiste au moins 1 jour ou vide**. — `src/Pharma.Application/Administration/Seuils.cs`
- **Régler les seuils exige le droit `admin.parametres`** ; les alertes sont recalculées aussitôt et la modification est inscrite au journal. — `src/Pharma.Application/Administration/Seuils.cs`, `src/Pharma.Api/Endpoints/AdminEndpoints.cs`
- **Niveau d'un lot : périmé (0 jour restant ou moins), rouge, orange, puis « risque d'écoulement »** si le lot périme dans l'horizon (365 jours par défaut) avec des boîtes que la consommation ne permettra pas de vendre à temps, sinon ok. — vue `v_risque_peremption`
- **Boîtes à risque = boîtes du lot − boîtes vendables avant péremption** (consommation × jours restants, après les lots qui périment avant) ; valorisées au prix d'achat. — vue `v_risque_peremption`

### Génération et suivi des alertes
- **Une alerte de péremption n'est créée que s'il y a quelque chose à faire** : lot périmé, ou boîtes qui ne seront pas vendues à temps au rythme des ventes. Un lot dont toutes les boîtes seront vendues à temps ne crée aucune alerte, ni dans le logiciel ni par e-mail (l'alerte déjà ouverte est close d'elle-même). — `database/migration/066_alertes_peremption_claires.sql`
- **Le message dit combien de boîtes, quelle valeur au prix d'achat et quoi faire** (mettre en avant, retourner au grossiste, proposer à la place d'un équivalent). — `database/migration/066_alertes_peremption_claires.sql`
- **Écran Alertes : une tuile chiffrée sans alerte affiche « 0 »** (ou « 0 DA » pour un montant) ; « RAS » est réservé au texte (« Urgentes : RAS, aucune alerte ouverte » dans la liste). Le rapport du soir ne montre pas les rubriques vides.
- **Accord automatique des « (s) »** : partout (écrans du poste, messages du serveur et de la base, application mobile, e-mails, SMS, PDF), « 0 ou 1 » donne le singulier et « 2 et plus » le pluriel : « 1 lot », « 3 lots », « 1 boîte ajoutée ». Un « (s) » sans nombre devant reste tel quel. — `src/Pharma.Domain/Commun/Accord.cs`, `src/Pharma.Desktop/wwwroot/js/poste.js` (`pharma.accord`), `src/Pharma.Api/wwwroot/mobile/mobile.js` — `src/Pharma.Desktop/Composants/Pages/Alertes.razor`, `src/Pharma.Application/Notifications/Notifications.cs`
- **Tableaux paginés et filtrés par le serveur : Stock, Alertes, Clôtures de caisse (Rapports), Dettes et créances, Registre des psychotropes (inscriptions : produit, période, opération), Lots à étiqueter (produit ou n° de lot, péremption jusqu'au), Achats › À commander (produit, état rupture ou critique, dernier grossiste ou « jamais acheté »), journal des sauvegardes (résultat, période), historique des inventaires (rayon, état, période ; auparavant les 20 derniers seulement)** ; tableaux de la période paginés à l'écran : Rapports › Retours clients et Détail des sorties, anomalies du Contrôle interne, remarques du Guide (filtrées par écran et genre) (et, par la règle des listes de `docs/charte-graphique.md`, tout tableau qui peut grandir). Dettes et créances : catégorie (rubrique), tiers (liste des tiers présents dans la catégorie pour les grossistes et les effets ; champ de saisie avec propositions dès 2 lettres pour les patients, les factures clients et les organismes des bordereaux), état (en alerte, dans le délai, rejetée), période sur la date depuis laquelle la somme est due ; total en DA de tout le filtre. — `src/Pharma.Application/Suivi/SuiviDettes.cs` (`FiltrageSuiviDettes`). Stock : recherche (nom, DCI, code-barres, lot, emplacement ; sans tenir compte des majuscules ni des accents) avec propositions pendant la saisie dès 2 caractères, filtre (à commander, péremption proche, psychotropes), rayon ; indicateurs de tout le stock. Alertes : onglets (toutes, urgentes, péremption, stock) et tuiles comptés sur toutes les alertes ouvertes, pas sur la page. Clôtures : employé et poste (listes préremplies avec ceux de la période), « avec un écart seulement » pour qui voit les écarts. — `src/Pharma.Application/Stock/ConsulterStock.cs`, `src/Pharma.Application/Alertes/Alertes.cs`, `src/Pharma.Application/Rapports/Rapports.cs`
- **Un tableau paginé n'affiche jamais une page qui n'existe plus** : quand la liste raccourcit (ligne traitée, filtre, données changées avant « ← Retour »), la page est ramenée à la dernière. Changer de produit ou de période (registre) repart de la première page de la balance ; ouvrir une autre session de reprise efface les filtres de ses contrôles ; compter un lot ou scanner une boîte garde les filtres et la page de la feuille d'inventaire ; le filtre « délai de reprise à renseigner » (Alertes et seuils) porte sur le délai enregistré, pas sur celui en cours de saisie. — `src/Pharma.Desktop/Composants/Mise/Pagination.razor`, `src/Pharma.Desktop/Composants/Pages/Inventaire.razor`, `Registre.razor`, `AdminReprise.razor`, `AdminSeuils.razor`
- **Les alertes sont recalculées chaque jour à 7 h**, et sur demande (droit `admin.parametres`). — `src/Pharma.Api/Taches/AlertesQuotidiennes.cs`, `src/Pharma.Api/Endpoints/AlertesEndpoints.cs`
- **Types d'alerte** : péremption, périmé, risque d'écoulement, rupture, stock critique, dormant, surstock, ancien lot délaissé. — contrainte `alerte_type_check`
- **Urgence** : périmé, rouge et rupture sont « urgents » ; critique, orange et risque d'écoulement sont « attention » ; dormant et surstock sont « info ». — fonction `generer_alertes`
- **Une seule alerte ouverte par type, produit et lot** ; elle est mise à jour, et redevient « nouvelle » si son niveau change, même reportée. — fonction `generer_alertes`, index `ux_alerte_ouverte`
- **Une alerte dont la cause a disparu est close automatiquement (« résolue »)**. — fonction `generer_alertes`
- **Consulter, reporter et traiter les alertes exige le droit `stock.consulter`**. — `src/Pharma.Api/Endpoints/AlertesEndpoints.cs`
- **Reporter une alerte : la date doit être postérieure à aujourd'hui** ; seule une alerte nouvelle ou vue se reporte, et elle réapparaît le jour choisi. — `src/Pharma.Domain/Alertes/RegleAlerte.cs`, `src/Pharma.Infrastructure/Alertes/AlertesEcriture.cs`, fonction `generer_alertes`
- **Traiter une alerte la clôt avec l'action prise (200 caractères au plus, au-delà le texte est coupé)** ; si la cause existe encore au calcul suivant, une nouvelle alerte est créée. — `src/Pharma.Application/Alertes/ActionsAlertes.cs`
- **Liste triée par urgence, puis par montant en jeu** ; la pastille compte les alertes urgentes nouvelles ou vues. — `src/Pharma.Infrastructure/Lecture/AlertesLecture.cs`
- **Indicateurs de péremption à 30, 60 et 180 jours** (cumulés) : lots non bloqués en stock qui périment après aujourd'hui, avec boîtes et valeur au prix d'achat. — `src/Pharma.Application/Alertes/IndicateursAlertes.cs`
- **Règlements grossistes à préparer : affichés seulement avec le droit `achats.regler`**. — `src/Pharma.Application/Alertes/IndicateursAlertes.cs`

## Catalogue

### Création d'un produit
- **Créer un produit exige le droit `catalogue.creer`** ; inscrit au journal. — `src/Pharma.Application/Catalogue/CreerProduit.cs`
- **Obligatoires : nom commercial (120 caractères au plus), DCI, dosage, forme, prix public, classe**. — `src/Pharma.Domain/Catalogue/RegleProduit.cs`
- **La DCI est créée si elle n'existe pas encore** (comparaison sans tenir compte des majuscules) ; deux DCI ne peuvent pas avoir le même nom. — `src/Pharma.Infrastructure/Lecture/CatalogueEcritureSql.cs`, contrainte `dci_nom_key`
- **Pas deux produits avec le même nom, dosage et forme** (sans tenir compte des majuscules). — `src/Pharma.Application/Catalogue/CreerProduit.cs`, index `ux_produit_nom_dosage_forme`
- **Pas deux produits avec le même code-barres ni le même n° d'enregistrement**. — `src/Pharma.Application/Catalogue/CreerProduit.cs`, contraintes `produit_code_barre_key`, `produit_num_enregistrement_key`
- **Le code-barres est un EAN-13 avec une clé de contrôle correcte** ; un GTIN-14 commençant par 0 (Data Matrix) est ramené à 13 chiffres. — `src/Pharma.Domain/Catalogue/RegleProduit.cs`, `src/Pharma.Domain/Commun/Ean13.cs`
- **Les codes commençant par 20 sont réservés aux étiquettes internes de l'officine** et refusés comme code-barres de produit. — `src/Pharma.Domain/Catalogue/RegleProduit.cs`
- **Seuil d'alerte saisi entre 0 et 100 000 boîtes ; vide = seuil automatique**. — `src/Pharma.Domain/Catalogue/RegleProduit.cs`, contrainte `produit_stock_minimum_positif`
- **La recherche de DCI commence à 2 caractères (10 résultats au plus)**. — `src/Pharma.Application/Catalogue/CreerProduit.cs`

### Prix et remboursement
- **La fiche produit affiche le prix public HT, pour information** : prix public ÷ (1 + TVA du produit), arrondi au centime. — `src/Pharma.Desktop/Composants/Mise/ModificationProduit.razor`
- **Le prix public doit être supérieur à 0** (la base accepte 0 au minimum). — `src/Pharma.Domain/Catalogue/RegleProduit.cs`, `src/Pharma.Application/Catalogue/ModifierProduit.cs`, contrainte `produit_prix_public_check`
- **Le tarif de référence, s'il est saisi, doit être supérieur à 0 ; un produit remboursable a obligatoirement un tarif de référence**. — `src/Pharma.Domain/Catalogue/RegleProduit.cs`
- **Changer le prix public d'un produit exige en plus le droit `prix.modifier`** ; l'ancien et le nouveau prix sont inscrits au journal. — `src/Pharma.Application/Catalogue/ModifierProduit.cs`

### TVA
- **Taux de TVA entre 0 et 100 %** ; 0 par défaut. — `src/Pharma.Domain/Catalogue/RegleProduit.cs`, `database/init/schema.sql`

### Modification d'un produit
- **Modifier un produit exige le droit `catalogue.creer`** ; chaque champ changé est inscrit au journal (ancienne et nouvelle valeur). — `src/Pharma.Application/Catalogue/ModifierProduit.cs`
- **La classe réglementaire ne se change pas depuis la fiche produit** (elle se confirme à part, par le titulaire). — `src/Pharma.Application/Catalogue/ModifierProduit.cs`
- **Un produit désactivé n'est plus proposé à la vente ni aux commandes** ; la caisse refuse un produit inactif. — `src/Pharma.Application/Catalogue/ModifierProduit.cs`, `src/Pharma.Application/Caisse/Encaisser.cs`

### Classe réglementaire et liste
- **Classes possibles : libre, liste I, liste II, psychotrope, stupéfiant** (libre par défaut). — `src/Pharma.Domain/Catalogue/ClasseReglementaire.cs`, contrainte `produit_classe_check`
- **Psychotropes et stupéfiants sont « sous registre »** ; liste I et liste II sont « sur ordonnance ». — `src/Pharma.Domain/Catalogue/Produit.cs`
- **La liste d'un psychotrope (I ou II) ne s'applique qu'aux psychotropes** ; elle est effacée pour les autres classes. — `src/Pharma.Domain/Catalogue/RegleProduit.cs`, `src/Pharma.Application/Catalogue/ClassementProduit.cs`
- **Supplément d'honoraire (SHP) par boîte : 2,50 DA pour liste I et stupéfiant, 1,50 DA pour liste II ; pour un psychotrope, selon sa liste (0 si non renseignée)**. — `src/Pharma.Domain/Achats/RegleFactureAchat.cs`
- **Changer la classe d'un produit est réservé au titulaire** et inscrit au journal ; le produit n'est alors plus « à vérifier ». — `src/Pharma.Application/Catalogue/NomenclatureNationale.cs`
- **Un produit qui passe sous registre avec du stock reçoit un solde d'ouverture au registre, lot par lot, une seule fois par produit**. — `src/Pharma.Infrastructure/Catalogue/NomenclatureSql.cs`
- **Un produit repris de l'ancien logiciel sans n° d'enregistrement est « à vérifier » et ne peut pas être vendu** tant que le titulaire n'a pas confirmé sa classe. — `src/Pharma.Domain/Catalogue/Produit.cs`, `src/Pharma.Application/Caisse/Encaisser.cs`

### Catégories
- **Chaque produit a une catégorie de la liste** (médicament par défaut) : médicament, dermo-cosmétique, hygiène, bébé et maman, compléments alimentaires, phyto/aromathérapie, matériel médical, paramédical. — `database/migration/045_prix_lot_ug_categories.sql`, contrainte `fk_produit_categorie`
- **Seule la catégorie « médicament » compte dans les totaux « médicaments » de la caisse** ; les autres comptent en « autres produits ». — `src/Pharma.Application/Catalogue/ClassementProduit.cs`, table `categorie_produit`
- **Changer la catégorie ou la liste d'un psychotrope exige le droit `catalogue.creer`** ; inscrit au journal (avant/après). — `src/Pharma.Application/Catalogue/ClassementProduit.cs`

### Blocage d'un produit
- **Un produit peut être bloqué à l'achat (ni commande, ni réception), à la vente (refusé en caisse), ou les deux**. — `src/Pharma.Application/Catalogue/BlocageProduit.cs`
- **Bloquer ou débloquer exige le droit `produits.bloquer`** (titulaire et assistant par défaut) ; motif obligatoire, 300 caractères au plus, effacé au déblocage ; inscrit au journal. — `src/Pharma.Application/Catalogue/BlocageProduit.cs`, `database/migration/046_blocage_produit.sql`
- **Un produit bloqué ne passe en caisse ou en réception qu'avec la validation d'une personne qui a le droit `produits.bloquer`** : l'employé lui-même s'il l'a, sinon le pharmacien ou l'assistant avec son code comptoir. — `src/Pharma.Application/Catalogue/BlocageProduit.cs`

### Équivalents et recherche
- **Quand un produit manque, on propose d'abord les équivalents exacts (même DCI, dosage, forme) actifs et en stock** ; à défaut, la même DCI dans un autre dosage ou une autre forme, à valider par le pharmacien ; **tri du moins cher au plus cher** (2026-11-14), puis par stock décroissant. — `src/Pharma.Application/Catalogue/ListerAlternatives.cs`
- **Prix comparé : prix servi** = prix d'une boîte du lot qui serait servi (premier à périmer, vendable : PPA du lot, sinon prix du catalogue) plus le SHP du produit. — `src/Pharma.Infrastructure/Lecture/EquivalentsComptoirSql.cs`
- **Recherche de la caisse par nom ou DCI** (2026-11-14) : produits en stock d'abord (ordre de pertinence gardé) ; **produits sans stock vendable en bas, grisés, non sélectionnables, marqués « Rupture de stock »**, avec dessous leur **remplaçant** cliquable (équivalent exact le moins cher, sinon autre dosage ou forme « à valider par le pharmacien »), ou « Aucun remplaçant en stock ». — `src/Pharma.Application/Catalogue/RechercherAuComptoir.cs`, `src/Pharma.Desktop/Composants/Pages/Caisse.razor`
- **Même produit d'un autre laboratoire moins cher** (même DCI, dosage et forme, en stock) : suggéré sous le produit cherché, sans répéter le nom du médicament (« Moins cher · laboratoire · stock · prix (− N DA) », « autre laboratoire » si le laboratoire n'est pas renseigné), cliquable ; le produit cherché garde sa place et reste sélectionnable (demande explicite du client). — `src/Pharma.Application/Catalogue/RechercherAuComptoir.cs`
- **Entrée dans la recherche ajoute le premier produit seulement s'il est en stock** ; sinon la liste reste ouverte avec la rupture et son remplaçant. Une boîte identifiée scannée (étiquette interne, Data Matrix) est servie comme avant. — `src/Pharma.Desktop/Composants/Pages/Caisse.razor`
- **Ligne en rupture au panier** (vente en attente reprise, par exemple) : toujours « Rupture de stock » ; les lots périmés ou bloqués encore en stock sont cités en petit (« lot A2391 périmé : à retirer du stock ») ; plus de bandeau rouge pour une rupture. — `src/Pharma.Desktop/Composants/Pages/Caisse.razor`
- **Le bandeau qui explique un lot écarté (périmé, bloqué) s'efface dès qu'on tape une nouvelle recherche, ou quand le produit concerné quitte le panier.** — `src/Pharma.Desktop/Composants/Pages/Caisse.razor`
- **Recherche au comptoir : code-barres exact, puis nom ou DCI, 20 résultats par défaut, 50 au plus**. — `src/Pharma.Application/Catalogue/RechercherProduits.cs`

### Nomenclature nationale
- **Importer et consulter la nomenclature nationale est réservé au titulaire** ; fichier de 40 Mo au plus ; inscrit au journal. — `src/Pharma.Application/Catalogue/NomenclatureNationale.cs`
- **Un fichier n'est accepté comme nomenclature que s'il contient au moins 100 médicaments en vigueur** dans une feuille avec les colonnes « N°ENREGISTREMENT » et « NOM DE MARQUE ». — `src/Pharma.Domain/Catalogue/Nomenclature.cs`
- **Les colonnes sont reconnues par leur intitulé, pas leur position** ; feuille « Retraits » = retiré, « Non renouvelés » = non renouvelé, autres = en vigueur. — `src/Pharma.Domain/Catalogue/Nomenclature.cs`
- **Un nouvel import remplace entièrement l'ancien contenu** ; l'historique des imports est conservé. — `src/Pharma.Infrastructure/Catalogue/NomenclatureSql.cs`
- **Classe proposée par la nomenclature** : psychotrope ou stupéfiant si la DCI contient une substance surveillée, sinon liste I/II selon la colonne liste, sinon libre. — vue `v_nomenclature`
- **Produits à vérifier signalés au titulaire** : substance surveillée non classée sous registre, produit repris « à vérifier », produit retiré ou non renouvelé, n° d'enregistrement absent de la nomenclature. — vue `v_produit_a_verifier`
- **La recherche dans la nomenclature pour créer un produit exige le droit `catalogue.creer`** (à partir de 2 caractères, 15 résultats au plus, en vigueur d'abord). — `src/Pharma.Application/Catalogue/NomenclatureNationale.cs`

## Traçabilité

### Boîtes identifiées
- **Une boîte identifiée porte soit un Data Matrix (GTIN + n° de série), soit une étiquette interne** ; l'un des deux est obligatoire. — contrainte `ck_boite_identifiant`
- **Une même étiquette interne, et un même couple GTIN + n° de série, ne désignent qu'une seule boîte**. — contraintes `boite_code_etiquette_key`, `boite_gtin_numero_serie_key`
- **Statuts d'une boîte : en stock, vendue, retournée au grossiste, détruite, retour client, donnée**. — contrainte `boite_statut_check`
- **On ne peut pas étiqueter plus de boîtes qu'il n'y en a en stock dans le lot** (au moins 1 par demande). — fonction `creer_boites_internes`
- **Avancement de l'étiquetage = boîtes identifiées en stock ÷ boîtes en stock** (plafonné à 100 %). — `src/Pharma.Application/Tracabilite/ObtenirEtatEtiquetage.cs` (droit `stock.consulter`)

### Étiquettes et codes internes
- **L'écran Étiquettes internes est dans le domaine Stock** (`/stock/etiquettes`), ouvert aux droits `stock.consulter` ou `achats.receptionner` ; l'ancienne adresse des Achats y ramène. — `src/Pharma.Desktop/Composants/Pages/StockEtiquettes.razor`, `src/Pharma.Desktop/Services/Domaines.cs`
- **Les codes internes (boîte ou lot) sont des EAN-13 commençant par 20, suivis d'un numéro séquentiel sur 10 chiffres et d'une clé de contrôle**. — fonctions `creer_boites_internes`, `generer_code_interne`, `src/Pharma.Domain/Commun/Ean13.cs`
- **Un lot n'a qu'un seul code interne** : il est créé à la première demande puis toujours réutilisé. — fonction `generer_code_interne`
- **Lecture d'un code : d'abord étiquette de boîte, puis code interne de lot (même périmé, pour afficher le blocage), puis code-barres fabricant d'un produit actif**. — fonction `rechercher_par_code`

### Data Matrix GS1
- **Le Data Matrix est lu champ par champ : GTIN (01), péremption (17), lot (10), n° de série (21)** ; tout autre champ rend le code illisible ; sans GTIN, le code est refusé. — `src/Pharma.Domain/Commun/Gs1DataMatrix.cs`
- **Date de péremption au format AAMMJJ ; un jour « 00 » signifie le dernier jour du mois**. — `src/Pharma.Domain/Commun/Gs1DataMatrix.cs`
- **Le produit est retrouvé par son code-barres, ou par le GTIN-14 sans le 0 initial** ; le produit actif est préféré. — `src/Pharma.Infrastructure/Lecture/MobileSql.cs`

### Événements de boîte (historique)
- **Chaque boîte a un historique : réception, vente, retour client, retour grossiste, destruction, blocage, correction, inventaire, échantillon**. — contrainte `boite_evenement_type_check`
- **L'entrée d'une boîte en stock crée automatiquement l'événement « réception »**. — déclencheur `journaliser_entree_boite`
- **L'historique de traçabilité ne peut être ni modifié ni supprimé**. — déclencheur `trg_boite_evenement_immuable`
- **Vendre une boîte identifiée : elle doit être en stock et appartenir au lot de la ligne de vente**, et pas plus de boîtes que la quantité de la ligne ; sinon refus (« contrefaçon ou double scan possible »). — fonction `vendre_boite`
- **Si le réglage l'impose, chaque boîte vendue doit être scannée** (désactivé par défaut). — déclencheur `verifier_tracabilite_vente`, table `parametre_tracabilite`
- **Patient obligatoire selon le réglage : jamais, pour un psychotrope, pour toute ordonnance (par défaut, psychotropes compris), ou toujours**. — déclencheur `verifier_tracabilite_vente`, table `parametre_tracabilite`
- **Le parcours d'une boîte (grossiste, réception, vente, patient) se consulte par étiquette ou n° de série**, droit `stock.consulter`. — `src/Pharma.Application/Tracabilite/ConsulterParcoursBoite.cs`, vue `v_parcours_boite`

## Psychotropes et registre

### Inscription au registre
- **Tout mouvement de stock d'un psychotrope ou stupéfiant (hors vente) est inscrit automatiquement au registre** : entrée, retour client, retour grossiste (ou retour refusé réintégré), destruction (périmé), casse, écart d'inventaire + ou −. — déclencheur `inscrire_mouvement_registre`
- **La délivrance (vente) d'un produit sous registre est inscrite au registre avec le patient, le médecin, l'ordonnance et le porteur**. — `src/Pharma.Infrastructure/Caisse/VentesEcritureSql.cs`
- **Une délivrance au registre exige une ordonnance**. — contrainte `registre_psychotrope_check`
- **Porteur : si son nom est saisi, son adresse et sa pièce d'identité sont obligatoires**. — contrainte `ck_registre_porteur_complet`
- **Un mouvement de produit sous registre sans employé est refusé**. — déclencheur `inscrire_mouvement_registre`
- **Chaque ligne a un numéro d'ordre unique et continu** ; quantité > 0 ; solde du lot jamais négatif. — déclencheur `numeroter_registre`, contraintes du `registre_psychotrope`
- **Le registre ne peut être ni modifié ni supprimé**. — déclencheur `trg_registre_immuable`
- **Une entrée n'est pas inscrite deux fois pour le même lot**. — déclencheur `eviter_doublon_registre`

### Consultation et export
- **Consulter et exporter le registre exige le droit `psychotropes.registre`**. — `src/Pharma.Application/Stock/RegistrePsychotropes.cs`
- **Période par défaut : du 1er du mois à aujourd'hui ; la date de début précède la fin ; consultation à l'écran sans limite de durée** (inscriptions lues par page et balance mensuelle calculée par la base, mois en heure locale) ; **un export PDF ou CSV porte sur un an au plus (366 jours)** : au-delà, les boutons d'export sont désactivés et l'API refuse (« un fichier par année »). Auparavant, la consultation elle-même était limitée à un an. — `src/Pharma.Application/Stock/RegistrePsychotropes.cs` (`JoursMaxExport`), `src/Pharma.Infrastructure/Lecture/RegistreSql.cs`
- **Noms du patient et du porteur en clair pour le titulaire seul ; initiales pour les autres employés** (adresse et pièce masquées), à l'écran comme dans les exports. — `src/Pharma.Application/Stock/RegistrePsychotropes.cs`
- **Balance mensuelle par produit : entrées, sorties et solde de fin de mois** (arrêté du 23 août 1999, art. 4). — `src/Pharma.Application/Stock/RegistrePsychotropes.cs`
- **Le solde par produit se calcule à partir du solde d'ouverture du registre**. — vue `v_registre_psychotrope`
- **Export en PDF (à imprimer et signer) ou CSV** ; dans le CSV, une cellule qui commence par = + - @ est neutralisée pour Excel. — `src/Pharma.Application/Stock/RegistrePsychotropes.cs`

## Mobile (scanner)

### Contrôle en rayon
- **Identifier un code scanné exige le droit `stock.consulter`** ; un code vide est refusé ; un code inconnu (ni étiquette interne, ni code-barres du catalogue) est refusé ; un GTIN inconnu demande de créer le produit sur le poste. — `src/Pharma.Application/Mobile/Mobile.cs`
- **Alertes affichées au scan** : boîte plus en stock, lot bloqué (« retirez la boîte »), lot périmé (« retirez la boîte »), date à vérifier, lot du Data Matrix absent du stock (« boîte non réceptionnée ? »). — `src/Pharma.Application/Mobile/Mobile.cs`
- **Code fabricant (sans lot) : tous les lots bloqués, périmés ou à dater du produit sont signalés**, pour que l'employé lise le lot sur la boîte. — `src/Pharma.Application/Mobile/Mobile.cs`

### Confirmation de la date de péremption
- **Confirmer la date lue sur la boîte exige le droit `stock.ajuster`** ; seulement pour un lot « date à vérifier » (sinon refus « déjà confirmée ») ; date antérieure de plus d'un an refusée ; inscrit au journal. — `src/Pharma.Application/Mobile/Mobile.cs`

### Scans de réception
- **Scanner les boîtes au déballage exige le droit `achats.receptionner`** ; les scans restent en attente jusqu'à leur import sur le poste. — `src/Pharma.Application/Mobile/Mobile.cs`
- **Une boîte au n° de série déjà scannée (en attente) est refusée**. — `src/Pharma.Application/Mobile/Mobile.cs`, index `ux_scan_reception_serie`
- **Une boîte qui porte déjà une étiquette interne est refusée en réception** (« elle est déjà en stock »). — `src/Pharma.Application/Mobile/Mobile.cs`
- **Seul un scan pas encore importé peut être supprimé**. — `src/Pharma.Application/Mobile/Mobile.cs`
- **À l'import, les scans sont regroupés en une ligne par produit, lot et péremption**, avec les n° de série lus. — `src/Pharma.Application/Mobile/Mobile.cs`
- **Si la réception est abandonnée sur le poste, les scans reviennent en attente**, sauf une boîte sérialisée déjà rescannée ou déjà entrée en stock. — `src/Pharma.Infrastructure/Lecture/MobileSql.cs`

## Achats

### Droits d'accès aux achats
- **Consulter les grossistes, les bons de commande, les réceptions, les retours et leurs PDF demande l'un des trois droits d'achat** : `achats.commander`, `achats.receptionner` ou `achats.retour_fournisseur`. — `src/Pharma.Api/Endpoints/AchatsEndpoints.cs`
- **Les suggestions de commande, la création ou modification d'un grossiste et toute action sur un bon de commande demandent le droit `achats.commander`.** — `src/Pharma.Api/Endpoints/AchatsEndpoints.cs`, `src/Pharma.Application/Achats/Achats.cs`
- **Réceptionner, analyser une livraison, chercher un n° de BL ou de facture déjà saisi, lire les infos d'achat et imprimer les étiquettes internes demandent le droit `achats.receptionner`.** La liste des lots à étiqueter accepte aussi `stock.consulter`. — `src/Pharma.Api/Endpoints/AchatsEndpoints.cs`
- **Préparer, envoyer, solder (avoir) ou refuser un retour fournisseur demande le droit `achats.retour_fournisseur`.** — `src/Pharma.Application/Achats/Achats.cs`
- **Consulter et enregistrer les règlements des grossistes demande le droit `achats.regler`** : à sa création, il est donné au titulaire et au pharmacien assistant. — `src/Pharma.Application/Achats/Reglements.cs`, `database/migration/029_reglements.sql`
- **Droits de départ (premier paramétrage)** : le pharmacien assistant reçoit tout sauf l'administration, `rapports.marges`, `caisse.voir_ecart` et `prix.modifier` ; le vendeur reçoit `achats.receptionner` et `chifa.importer` (et pas `achats.commander`) ; le titulaire reçoit tout. — `database/migration/006_droits_utilisateurs.sql`

### Grossistes
- **La raison sociale est obligatoire, sans doublon** : deux grossistes ne peuvent pas avoir le même nom (la casse est ignorée). Droit `achats.commander`. — `src/Pharma.Application/Achats/Achats.cs`, `src/Pharma.Infrastructure/Achats/GrossistesSql.cs`
- **Le délai de reprise des produits proches de la péremption, s'il est indiqué, va de 1 à 365 jours.** En base, il doit être supérieur à 0. — `src/Pharma.Application/Achats/Achats.cs`, `database/migration/010_administration.sql`
- **Le téléphone et l'adresse e-mail sont contrôlés** : un numéro ou un e-mail au format invalide est refusé. — `src/Pharma.Application/Contacts/Contacts.cs`
- **Un grossiste désactivé n'est plus proposé** : on ne peut plus lui commander ni réceptionner sa livraison (« réactivez-le dans l'onglet Grossistes ») ; son historique est conservé. — `src/Pharma.Application/Achats/Achats.cs`, `database/migration/047_recette_corrections.sql`
- **La dette d'un grossiste ne se modifie pas depuis sa fiche** : elle ne bouge qu'avec les réceptions, les avoirs et les règlements. — `src/Pharma.Infrastructure/Achats/GrossistesSql.cs`
- **Chaque création ou modification de grossiste est inscrite au journal** (nom, téléphone, e-mail, NIF, délai de reprise, actif). — `src/Pharma.Application/Achats/Achats.cs`
- **Les seuils de péremption (orange, rouge) propres à un grossiste priment sur le réglage général**, mais ceux du produit priment sur ceux du grossiste. Seuils > 0. — `database/migration/021_prix_boite.sql`, `database/migration/005_seuils_peremption.sql`

### Bons de commande
- **À la réception, le bon de commande se choisit parmi tous les bons à recevoir du grossiste** (envoyés ou reçus en partie), quel que soit leur âge : la liste est demandée au serveur par statut, page après page jusqu'au total (auparavant, seuls les 200 bons les plus récents, tous statuts confondus, étaient lus, et un bon ancien encore ouvert pouvait manquer). — `src/Pharma.Desktop/Composants/Achats/OngletReceptions.razor` (`ChargerCommandesARecevoirAsync`)
- **Un bon de commande est numéroté C-000001, C-000002…** (numéro unique). — `database/migration/012_achats.sql`
- **Statuts possibles : brouillon, envoyée, reçue partiellement, reçue, annulée.** — `database/migration/001_schema.sql`
- **Un grossiste n'a qu'un seul brouillon à la fois** : ajouter un produit le complète, ou le crée s'il n'existe pas. — `src/Pharma.Infrastructure/Achats/AchatsSql.cs`
- **Un produit bloqué à l'achat ne peut pas être commandé** (motif du blocage affiché). Il faut d'abord le débloquer. — `src/Pharma.Application/Achats/Achats.cs`
- **Sans prix saisi, le dernier prix d'achat est repris** : celui de ce grossiste d'abord, sinon celui de n'importe quel grossiste ; sans achat précédent, le prix doit être saisi. — `src/Pharma.Application/Achats/Achats.cs`, `src/Pharma.Infrastructure/Achats/AchatsSql.cs`
- **Ajouter un produit déjà présent sur le brouillon additionne les quantités et remplace le prix par le dernier saisi.** — `src/Pharma.Infrastructure/Achats/AchatsSql.cs`
- **Quantité commandée de 1 à 10 000 par ligne, prix d'achat jamais négatif, un même produit une seule fois par bon, bon jamais vide à l'émission.** — `src/Pharma.Domain/Achats/RegleAchats.cs`
- **Seul un brouillon se modifie** ; une quantité mise à 0 retire la ligne. — `src/Pharma.Application/Achats/Achats.cs`
- **Le montant du bon est la somme des quantités × prix HT, recalculé à chaque changement de ligne.** — `src/Pharma.Infrastructure/Achats/AchatsSql.cs`
- **Émettre un bon le fait passer de « brouillon » à « envoyée » et produit le bon de commande PDF** ; l'émission est inscrite au journal. — `src/Pharma.Application/Achats/Achats.cs`
- **Un bon peut être réimprimé quel que soit son statut.** — `src/Pharma.Application/Achats/Achats.cs`
- **On ne peut annuler qu'un bon « brouillon » ou « envoyée » dont rien n'a encore été reçu.** L'annulation est inscrite au journal. — `src/Pharma.Application/Achats/Achats.cs`
- **Les suggestions de commande listent les produits en rupture ou en stock critique**, avec leur dernier prix et leur dernier grossiste ; les produits bloqués à l'achat en sont exclus. Ruptures d'abord, puis par nom ; tableau paginé et filtré par le serveur (produit sans tenir compte des majuscules ni des accents, état, dernier grossiste parmi ceux des produits listés, ou « jamais acheté »). « Ajouter » retire la ligne de la page affichée ; le produit revient au rechargement tant que son stock reste en rupture ou critique (un bon en brouillon ne le retire pas). — `src/Pharma.Infrastructure/Achats/AchatsSql.cs`, `src/Pharma.Application/Achats/Achats.cs` (`ListerSuggestionsCommande.PageAsync`), `database/migration/046_blocage_produit.sql`

### Réception d'une livraison
- **Le n° de bon de livraison (BL) est obligatoire ; le n° de facture est facultatif.** — `src/Pharma.Domain/Achats/RegleAchats.cs`
- **Une livraison contient au moins une ligne ; chaque ligne est un lot d'un produit.** — `src/Pharma.Domain/Achats/RegleAchats.cs`
- **Le n° de lot est obligatoire (40 caractères au plus) et lu sans tenir compte des majuscules/minuscules ni des espaces en bordure** (« am55 » = « AM55 »). — `src/Pharma.Domain/Achats/RegleAchats.cs`, `src/Pharma.Domain/Stock/RegleLot.cs`
- **Un lot déjà périmé est refusé** : la date de péremption doit être postérieure à aujourd'hui (un lot qui périme le jour même est refusé). — `src/Pharma.Domain/Achats/RegleAchats.cs`
- **Quantité reçue de 1 à 10 000 boîtes par ligne (UG comprises) ; prix d'achat jamais négatif.** — `src/Pharma.Domain/Achats/RegleAchats.cs`
- **Un même lot ne peut figurer que sur une ligne de la livraison** : il faut regrouper les quantités. — `src/Pharma.Application/Achats/Achats.cs`
- **Réception liée à une commande : la commande doit être du même grossiste et au statut « envoyée » ou « reçue partiellement ».** — `src/Pharma.Application/Achats/Achats.cs`
- **Après la réception, la commande passe « reçue » si toutes ses lignes sont livrées, sinon « reçue partiellement »** ; les quantités reçues comptent les UG. — `src/Pharma.Infrastructure/Achats/AchatsSql.cs`
- **Le nom du produit enregistré sur les lots et étiquettes est celui du catalogue**, pas celui saisi. — `src/Pharma.Application/Achats/Achats.cs`
- **Un produit bloqué à l'achat ne se réceptionne qu'avec la validation d'une personne habilitée** (droit `produits.bloquer`, par défaut titulaire et assistant), soit l'utilisateur lui-même, soit un collègue avec son code comptoir ; la validation est inscrite au journal. — `src/Pharma.Application/Achats/Achats.cs`, `src/Pharma.Application/Catalogue/BlocageProduit.cs`
- **Avertissement de doublon** : en saisissant ou scannant un n° de BL ou de facture, le logiciel signale les réceptions du même grossiste portant déjà ce numéro (simple avertissement, une facture pouvant couvrir plusieurs livraisons). — `src/Pharma.Application/Achats/Achats.cs`
- **Un numéro scanné par une douchette réglée en clavier QWERTY sur un poste AZERTY est relu automatiquement** (numéro sans chiffre mais avec au moins 3 caractères de la rangée des chiffres AZERTY). — `src/Pharma.Application/Achats/Achats.cs`
- **La réception (lots, boîtes, mouvements, quantités reçues et dette du grossiste) est enregistrée en une seule fois** : tout ou rien. — `src/Pharma.Infrastructure/Achats/AchatsSql.cs`
- **Chaque réception est inscrite au journal** (BL, nombre de lots, HT, remise, TVA, timbre, net à payer, SHP, UG). — `src/Pharma.Application/Achats/Achats.cs`

### Réception à l'aveugle (comptage, puis facture)
- **Réception en deux étapes quand le réglage « Réception à l'aveugle » est actif (actif par défaut)** : la saisie directe d'une livraison est alors refusée. — `src/Pharma.Application/Achats/Achats.cs`, `database/migration/057_reception_aveugle.sql`
- **Étape 1, comptage : les boîtes du carton sont comptées (scan Data Matrix, saisie ou scans du mobile) sans voir le bon de livraison, ni prix ni bon de commande.** Droit `achats.receptionner` ; grossiste actif ; BL, lot et péremption contrôlés comme une réception ; un même lot une seule fois. Inscrit au journal (`comptage_reception`). — `src/Pharma.Application/Achats/ComptageReception.cs`, `src/Pharma.Domain/Achats/RegleComptageReception.cs`
- **Un même BL d'un même grossiste n'est compté qu'une fois à la fois** : un second comptage est refusé tant que le premier est à facturer, en écart ou à recompter (comparaison sans casse ni espaces).
- **Annuler un comptage** : avant la facture, droit `achats.receptionner` ; en écart ou à recompter, seulement qui décide des écarts (`achats.valider_ecart`) ; rien n'entre en stock ; journal `comptage_annule`. — `src/Pharma.Application/Achats/ComptageReception.cs`
- **Étape 2, facture : les lots comptés (produit, lot, péremption) sont repris sans le nombre de boîtes comptées ; on saisit la facture (boîtes facturées UG comprises, prix).** Le BL et le grossiste sont ceux du comptage ; la facture d'un comptage ne se saisit qu'une fois. — `src/Pharma.Application/Achats/ComptageReception.cs`, `src/Pharma.Api/Endpoints/AchatsEndpoints.cs`
- **Comparaison lot par lot (produit et n° de lot, sans tenir compte des majuscules ni des espaces)** : un lot compté laissé à 0 ou absent de la facture compte comme non facturé. — `src/Pharma.Domain/Achats/RegleComptageReception.cs`
- **Sans écart, la livraison entre en stock comme une réception ordinaire**, avec les Data Matrix du comptage. — `src/Pharma.Application/Achats/ComptageReception.cs`
- **Avec écart, rien n'entre en stock : la réception est bloquée** ; la facture est gardée telle que saisie, sans jamais garder de code comptoir ; anomalie et alerte au titulaire. — `src/Pharma.Application/Achats/ComptageReception.cs`
- **L'écart est décidé par le titulaire ou un employé qui a le droit `achats.valider_ecart`** (titulaire et assistant par défaut, attribuable à un vendeur) : « Valider » ou « Faire recompter ».
- **Personne ne décide d'un écart qu'il a compté ou dont il a saisi la facture, sauf le titulaire.**
- **Les quantités facturées et comptées d'un écart ne sont montrées qu'à qui décide des écarts** : le réceptionnaire, le recompteur et celui qui saisit la facture voient seulement les lots en écart.
- **« Faire recompter » : la livraison passe « à recompter » ; un nouveau comptage à l'aveugle (lots repris sans aucune quantité) est comparé à la facture déjà saisie, qui est gardée** ; l'écart recalculé (ou nul) attend de nouveau la décision. Journal `comptage_recompte`.
- **Les livraisons comptées restent affichées et traitables même si la réception à l'aveugle est désactivée.** — `src/Pharma.Application/Achats/ComptageReception.cs`
- **Écart validé : le stock prend les boîtes comptées, la dette du grossiste suit la facture.** L'entrée en stock, la sortie des boîtes manquantes et le rattachement du comptage forment une seule transaction (tout ou rien) ; s'il n'y a pas assez de boîtes sans Data Matrix pour sortir les manquantes, rien n'est enregistré. — `src/Pharma.Domain/Achats/RegleComptageReception.cs`
- **Boîtes facturées absentes du carton : entrées puis aussitôt sorties du stock** (boîte « manquante », mouvement `manquant_reception`, « à réclamer au grossiste ») ; les boîtes sans Data Matrix les plus chères sortent d'abord ; pour un psychotrope, le registre inscrit un écart en moins. — `src/Pharma.Infrastructure/Achats/ManquantsReception.cs`, `database/migration/057_reception_aveugle.sql`
- **Boîtes en trop (comptées, non facturées) : entrées comme unités gratuites à 0 DA** ; un lot compté absent de la facture entre entièrement à 0 DA. — `src/Pharma.Domain/Achats/RegleComptageReception.cs`
- **Celui qui valide l'écart réceptionne** : il lui faut aussi `achats.receptionner`, et `produits.bloquer` si la livraison contient un produit bloqué à l'achat. — `src/Pharma.Application/Achats/ComptageReception.cs`
- **Écart validé ou renvoyé au recomptage par un autre que le titulaire : le titulaire est prévenu** (e-mail, SMS selon ses réglages ; urgent s'il manque des boîtes). — `src/Pharma.Application/Achats/ComptageReception.cs`
- **Une boîte « manquante » ne peut être ni vendue ni remboursée.** — `src/Pharma.Application/Caisse/RechercherVenteParBoite.cs`

### Réceptions en attente
- **Une réception en cours de saisie n'est jamais perdue** : quitter l'onglet Réceptions (autre onglet d'Achats, autre écran, « ← Retour »), se déconnecter, changer d'utilisateur ou être déconnecté par inactivité la met en attente sur le serveur ; le bouton « ⏸ Mettre en attente » fait de même pour commencer une autre livraison. « ← Retour » sur Réceptions la reprend. Si la mise en attente échoue, on reste sur l'écran avec le message. — `src/Pharma.Desktop/Composants/Achats/OngletReceptions.razor`, `database/migration/069_receptions_attente.sql`
- **Tout ce qui a été saisi part avec elle** : grossiste, commande, n° de BL et de facture, timbre, lignes (lot, péremption, quantités, UG, prix, remise, PPA, SHP, numéros de série), choix sur les conflits, scans du mobile ; rien n'entre en stock avant la validation. — `src/Pharma.Desktop/Composants/Achats/OngletReceptions.razor`
- **Reprise depuis n'importe quel poste par tout employé qui réceptionne** (`achats.receptionner`) ; reprise une seule fois (une seconde reprise est refusée) ; reprendre met d'abord en attente la saisie en cours. — `src/Pharma.Application/Achats/ReceptionsEnAttente.cs`
- **Comptage à l'aveugle (comptage ou recompte) en attente : repris ou abandonné par l'employé qui a compté, ou par qui décide des écarts** (`achats.valider_ecart` : titulaire, assistant, ou tout rôle à qui il est donné) ; jamais par la personne qui saisira la facture sans voir le compté. Un comptage n'est donc jamais bloqué si son auteur ne revient pas. — `src/Pharma.Application/Achats/ReceptionsEnAttente.cs`
- **Abandon d'une réception en attente** : ses scans du mobile reviennent dans la liste d'import, et l'abandon est inscrit au journal des actions (`reception_attente_abandonnee` : qui, quand, quel BL, qui l'avait mise en attente). — `src/Pharma.Application/Achats/ReceptionsEnAttente.cs`
- **Au plus 20 réceptions en attente**, libellé de 80 caractères au plus (mode, BL, grossiste). — `src/Pharma.Application/Achats/ReceptionsEnAttente.cs`

### Lots à la réception
- **Un lot = produit + n° de lot + grossiste + date de péremption** (unicité en base). Un même n° reçu de deux grossistes donne deux lots distincts. — `database/migration/021_prix_boite.sql`, `database/migration/003_provenance.sql`
- **Le même lot relivré par le même grossiste avec la même date complète le lot existant** (pas de nouveau lot). Un lot unique repris avec une date « à vérifier » est aussi complété et prend la date saisie. — `src/Pharma.Domain/Stock/RegleLot.cs`, `src/Pharma.Infrastructure/Achats/AchatsSql.cs`
- **Même n° de lot déjà en stock avec une autre date : la réception est refusée tant que l'utilisateur n'a pas choisi** de corriger la date ou de « Créer un lot séparé ». — `src/Pharma.Domain/Stock/RegleLot.cs`
- **N° de lot bloqué (rappel) : refusé tant que l'utilisateur n'a pas choisi** de mettre les boîtes de côté ou de les « Entrer en stock bloqué » ; un nouveau lot entré ainsi est bloqué avec le motif « Même n° de lot qu'un lot rappelé ». — `src/Pharma.Domain/Stock/RegleLot.cs`, `src/Pharma.Infrastructure/Achats/AchatsSql.cs`
- **Avant validation, le logiciel signale ligne par ligne ces deux conflits** (date différente, lot bloqué). — `src/Pharma.Application/Achats/Achats.cs`
- **Le grossiste d'un lot est forcément celui de sa réception** (contrôle en base). — `database/migration/003_provenance.sql`

### Boîtes, Data Matrix et étiquettes internes
- **À la réception, le GTIN du Data Matrix doit être celui du produit de la ligne** ; inconnu ou d'un autre produit : refus (« le Data Matrix scanné est celui de … »). — `src/Pharma.Application/Achats/Achats.cs`
- **Data Matrix lu (caisse, mobile, inventaire) : à n° de lot égal, le lot de la date de péremption lue** (champ 17). — `src/Pharma.Infrastructure/Lecture/MobileSql.cs`
- **Un Data Matrix non reconnu (GTIN introuvable) est refusé** ; le produit est retrouvé par son code-barres (GTIN-14 = EAN-13 précédé de 0). — `src/Pharma.Application/Achats/Achats.cs`, `src/Pharma.Infrastructure/Achats/AchatsSql.cs`
- **Des n° de série scannés exigent le GTIN, ne peuvent pas dépasser le nombre de boîtes reçues et ne peuvent pas être scannés deux fois.** — `src/Pharma.Domain/Achats/RegleAchats.cs`
- **Une boîte (GTIN + n° de série) n'entre qu'une fois dans le logiciel** : une boîte déjà enregistrée est refusée (« ce BL a peut-être déjà été saisi »). — `src/Pharma.Infrastructure/Achats/AchatsSql.cs`, `database/migration/003_provenance.sql`
- **Aucune boîte n'entre sans identité** : chaque boîte sans Data Matrix reçoit une étiquette interne (code EAN-13 commençant par 20) dans la même opération. — `src/Pharma.Infrastructure/Achats/AchatsSql.cs`, `database/migration/021_prix_boite.sql`
- **Impression d'étiquettes pour un lot : de 1 à 500 étiquettes, sans dépasser le nombre de boîtes encore sans identité.** — `src/Pharma.Application/Achats/Achats.cs`, `database/migration/021_prix_boite.sql`
- **Les étiquettes d'une livraison peuvent être réimprimées pour ses boîtes encore en stock** (refus si aucune). — `src/Pharma.Application/Achats/Achats.cs`

### Facture d'achat : prix, remise, TVA, timbre
- **Chaque ligne de réception affiche le PA TTC et le PV HT, pour information** : PA TTC = prix HT net de remise × (1 + TVA du produit) ; PV HT = PPA facturé ÷ (1 + TVA du produit) ; arrondis au centime. Ils ne changent ni la dette ni le coût du stock. — `src/Pharma.Desktop/Composants/Achats/OngletReceptions.razor`
- **Le prix d'achat saisi est le prix unitaire HT avant remise, comme sur la facture du grossiste.** — `src/Pharma.Domain/Achats/RegleAchats.cs`
- **Remise de ligne de 0 à 100 % ; PPA, SHP et timbre jamais négatifs.** — `src/Pharma.Domain/Achats/RegleFactureAchat.cs`
- **Prix net d'une boîte = prix HT × (1 − remise %), arrondi au centime** : c'est le coût de la boîte en stock. — `src/Pharma.Domain/Achats/RegleFactureAchat.cs`
- **Seules les boîtes facturées comptent dans les montants de la facture** (boîtes reçues moins les UG). — `src/Pharma.Domain/Achats/RegleFactureAchat.cs`
- **Totaux : HT = somme (facturées × prix HT) ; remise = somme des remises de ligne ; net HT = HT − remise ; TVA = calculée sur le net HT au taux du catalogue de chaque produit.** — `src/Pharma.Domain/Achats/RegleFactureAchat.cs`
- **Net à payer au grossiste = net HT + TVA + timbre** : c'est ce montant qui s'ajoute à sa dette. — `src/Pharma.Domain/Achats/RegleFactureAchat.cs`, `database/migration/044_reception_detaillee.sql`
- **Le SHP et le PPA sont indiqués à part et ne sont pas dus au grossiste.** — `src/Pharma.Domain/Achats/RegleFactureAchat.cs`
- **Marge affichée (colonne « MGE ») = PPA ÷ prix d'achat − 1**, en %, seulement si PPA et prix sont connus. — `src/Pharma.Domain/Achats/RegleFactureAchat.cs`
- **Chaque ligne est gardée telle qu'écrite sur la facture** (prix avant remise, remise, prix net, PPA, SHP, TVA, UG). Les réceptions plus anciennes n'ont que leur total. — `database/migration/044_reception_detaillee.sql`

### SHP (supplément d'honoraire du pharmacien)
- **SHP par boîte selon la liste du médicament : liste I et stupéfiants 2,50 DA ; liste II 1,50 DA ; hors liste 0.** — `src/Pharma.Domain/Achats/RegleFactureAchat.cs`
- **Un psychotrope suit la liste (I ou II) indiquée sur sa fiche ; non renseignée, son SHP est 0.** — `src/Pharma.Domain/Achats/RegleFactureAchat.cs`, `database/migration/045_prix_lot_ug_categories.sql`
- **Le SHP non saisi sur une ligne de réception est proposé selon la liste du produit ; il reste modifiable.** — `src/Pharma.Application/Achats/Achats.cs`

### Unités gratuites (UG)
- **Les UG sont comprises entre 0 et le nombre de boîtes reçues.** — `src/Pharma.Domain/Achats/RegleFactureAchat.cs`
- **Coût des UG au choix sur chaque ligne : « à 0 DA »** (les boîtes gratuites entrent à 0, les payées au prix net) **ou « réparti »** (chaque boîte reçue coûte montant facturé ÷ toutes les boîtes, arrondi au centime). Par défaut : à 0 DA. — `src/Pharma.Domain/Achats/RegleAchats.cs`, `database/migration/045_prix_lot_ug_categories.sql`
- **Avec UG à 0 DA, les dernières boîtes de la ligne sont considérées comme les gratuites** (mouvement de stock séparé « (UG) »). — `src/Pharma.Domain/Achats/RegleAchats.cs`, `src/Pharma.Infrastructure/Achats/AchatsSql.cs`

### Prix d'achat et prix public à la réception
- **Chaque boîte garde le prix d'achat de sa livraison ; le lot garde le prix de sa première livraison** (pas de prix moyen), qui sert aux boîtes sans identité. — `database/migration/021_prix_boite.sql`, `src/Pharma.Infrastructure/Achats/AchatsSql.cs`
- **Valeur d'un lot = prix réel de ses boîtes identifiées en stock + prix du lot pour les autres.** — `database/migration/021_prix_boite.sql`
- **Le PPA facturé est gardé sur le lot** ; la caisse vend la boîte au PPA de son lot, sinon au prix du catalogue. — `database/migration/045_prix_lot_ug_categories.sql`
- **Remplacer le prix public du catalogue par le PPA facturé exige le droit `prix.modifier` et un PPA > 0** ; le changement de prix est inscrit au journal (avant, après, BL). — `src/Pharma.Application/Achats/Achats.cs`, `src/Pharma.Domain/Achats/RegleFactureAchat.cs`

### Retours fournisseurs
- **Un retour est numéroté R-000001, R-000002…** — `database/migration/012_achats.sql`
- **Motifs possibles : péremption proche, périmé, défaut, erreur de livraison, rappel de lot.** — `src/Pharma.Domain/Achats/RegleAchats.cs`, `database/migration/003_provenance.sql`
- **Un retour contient au moins un lot, chaque lot une seule fois, et une quantité de 1 jusqu'au stock disponible du lot.** — `src/Pharma.Application/Achats/Achats.cs`, `src/Pharma.Domain/Achats/RegleAchats.cs`
- **Une boîte ne repart que chez le grossiste qui l'a livrée** : des lots de grossistes différents exigent un retour par grossiste (contrôle aussi en base). — `src/Pharma.Application/Achats/Achats.cs`, `database/migration/003_provenance.sql`
- **Les boîtes identifiées doivent être scannées** : il faut scanner au moins (quantité à retourner − boîtes sans identité du lot), sans dépasser la quantité, sans scanner deux fois la même boîte, et seulement des boîtes en stock dans ce lot. — `src/Pharma.Application/Achats/Achats.cs`
- **Le stock sort dès la préparation du retour (brouillon)** : les boîtes sont mises de côté. — `src/Pharma.Application/Achats/Achats.cs`, `src/Pharma.Infrastructure/Achats/AchatsSql.cs`
- **Montant attendu de l'avoir = somme (quantité × prix d'achat HT du lot)**, arrondi au centime par lot ; le prix pris est la valeur unitaire réelle du lot. — `src/Pharma.Application/Achats/Achats.cs`, `src/Pharma.Infrastructure/Achats/AchatsSql.cs`
- **Étapes permises : brouillon → envoyé ; brouillon → annulé (refusé) ; envoyé → avoir reçu ; envoyé → refusé par le grossiste.** Toute autre étape est refusée. — `src/Pharma.Domain/Achats/RegleAchats.cs`
- **L'envoi produit le bon de retour PDF.** — `src/Pharma.Application/Achats/Achats.cs`
- **Le montant de l'avoir reçu ne peut pas être négatif ; il est déduit de la dette du grossiste.** — `src/Pharma.Application/Achats/Achats.cs`, `src/Pharma.Infrastructure/Achats/AchatsSql.cs`
- **Un retour annulé ou refusé remet le stock et les boîtes identifiées en stock**, avec une trace dans l'historique de chaque boîte. — `src/Pharma.Infrastructure/Achats/AchatsSql.cs`
- **Chaque étape d'un retour est inscrite au journal** (préparé, envoyé, avoir reçu, refusé). — `src/Pharma.Application/Achats/Achats.cs`

- **Retour envoyé sans avoir après 30 jours (délai réglable, 1 à 365) : anomalie au titulaire**, rattachée à qui a préparé le retour ; urgente au double du délai. — `database/migration/057_reception_aveugle.sql`
- **Avoir reçu inférieur au montant attendu (de plus d'un centime) : anomalie au titulaire.** — `database/migration/057_reception_aveugle.sql`, `src/Pharma.Application/Achats/Achats.cs`

### Dette envers les grossistes et relevé
- **La dette d'un grossiste augmente du net à payer de chaque réception et diminue de chaque règlement et de chaque avoir de retour.** — `src/Pharma.Infrastructure/Achats/AchatsSql.cs`, `src/Pharma.Infrastructure/Achats/ReglementsFournisseursSql.cs`
- **Chaque changement de la dette est écrit automatiquement dans le relevé du grossiste** (ouverture, reprise, réception, règlement, avoir, correction), avec le solde après. Un changement fait hors logiciel apparaît comme « correction ». — `database/migration/039_suivi_dettes.sql`
- **Le relevé de la dette ne se modifie pas et ne s'efface pas** : on corrige par un nouveau mouvement. — `database/migration/039_suivi_dettes.sql`
- **Facture d'un grossiste (réception) : reste à payer = net à payer − règlements imputés.** — `database/migration/029_reglements.sql`
- **La dette qui dépasse les factures encore ouvertes est présentée comme « ancien solde (hors factures du logiciel) ».** — `database/migration/039_suivi_dettes.sql`

### Règlements des grossistes
- **Un règlement est numéroté RG-000001, RG-000002…** — `database/migration/047_recette_corrections.sql`
- **Moyens acceptés : espèces, chèque, virement, effet.** — `src/Pharma.Domain/Achats/RegleReglement.cs`, `database/migration/029_reglements.sql`
- **Montant supérieur à 0, date jamais dans le futur (par défaut : aujourd'hui).** — `src/Pharma.Domain/Achats/RegleReglement.cs`
- **Chèque, virement et effet exigent leur n° ou référence ; un effet exige sa date d'échéance.** — `src/Pharma.Domain/Achats/RegleReglement.cs`, `database/migration/029_reglements.sql`
- **On ne peut pas régler plus que la dette envers le grossiste** (tolérance 0,005 DA ; dette négative comptée 0). — `src/Pharma.Application/Achats/Reglements.cs`
- **Le règlement est imputé facture par facture** : aux factures choisies, sinon automatiquement aux plus anciennes d'abord ; le surplus éventuel réduit l'ancien solde, sans facture. — `src/Pharma.Domain/Achats/RegleReglement.cs`, `src/Pharma.Application/Achats/Reglements.cs`
- **Chaque montant imputé est > 0, au plus le reste de sa facture, sur une facture de ce grossiste ; le total imputé ne dépasse pas le montant réglé.** — `src/Pharma.Domain/Achats/RegleReglement.cs`
- **Espèces : l'argent sort du coffre ; un complément peut être pris dans le tiroir de la caisse ouverte du poste.** Seul ce complément diminue l'attendu de la caisse. — `src/Pharma.Application/Achats/Reglements.cs`, `database/migration/029_reglements.sql`
- **Le complément pris dans le tiroir n'est permis qu'en espèces, n'est jamais négatif, ne dépasse pas le montant réglé et exige une caisse ouverte sur le poste.** — `src/Pharma.Domain/Achats/RegleReglement.cs`, `src/Pharma.Application/Achats/Reglements.cs`
- **Chèque, virement ou effet : la banque choisie est débitée** (aucun compte rattaché à un paiement en espèces). — `src/Pharma.Application/Achats/Reglements.cs`, `database/migration/030_tresorerie.sql`
- **Si la dette ou une facture change pendant la saisie (autre poste), le règlement est refusé** (« rechargez la situation »). — `src/Pharma.Infrastructure/Achats/ReglementsFournisseursSql.cs`
- **Chaque règlement est inscrit au journal** (grossiste, montant, moyen, référence, part tiroir, nombre de factures). — `src/Pharma.Application/Achats/Reglements.cs`

## Factures clients

### Droits
- **Préparer, émettre, consulter, rejeter, corriger, remplacer une facture, émettre un avoir, gérer le suivi et les clients facturés demandent le droit `factures.gerer`** (donné au titulaire, qui l'accorde à qui il veut). — `src/Pharma.Application/Factures/Factures.cs`, `database/migration/031_facture_client.sql`
- **Créer ou modifier un organisme (CNAS, CASNOS, militaires…) est réservé au titulaire**, car les organismes servent aussi au tiers payant. — `src/Pharma.Application/Factures/Factures.cs`
- **Rejeter, corriger ou remplacer une facture déjà payée en entier est réservé au titulaire.** — `src/Pharma.Application/Factures/Factures.cs`

- **Un e-mail d'alerte commence par l'objet de l'alerte en gras**, suivi du texte (paragraphes gardés) ; une version texte seule accompagne toujours la version mise en forme. Le rapport quotidien garde sa propre mise en page. — `src/Pharma.Application/Notifications/Notifications.cs`

### Destinataires
- **Une facture va à un patient, à une entreprise (client facturé) ou à un organisme.** — `src/Pharma.Domain/Factures/RegleFacture.cs`, `database/migration/031_facture_client.sql`
- **Le destinataire doit exister ; une entreprise ou un organisme inactif ne peut pas recevoir de facture.** — `src/Pharma.Application/Factures/Factures.cs`, `src/Pharma.Infrastructure/Factures/FacturesSql.cs`
- **Le nom et les coordonnées du destinataire sont figés sur la facture à l'émission.** — `database/migration/031_facture_client.sql`
- **Client facturé : raison sociale obligatoire et unique.** — `src/Pharma.Application/Factures/Factures.cs`, `src/Pharma.Infrastructure/Factures/FacturesSql.cs`
- **Organisme : sigle et nom obligatoires ; sigle unique, mis en majuscules.** — `src/Pharma.Application/Factures/Factures.cs`, `src/Pharma.Infrastructure/Factures/FacturesSql.cs`
- **Délai de paiement d'un client ou d'un organisme : de 1 à 365 jours, 30 jours par défaut.** — `src/Pharma.Domain/Factures/RegleFacture.cs`, `database/migration/045_prix_lot_ug_categories.sql`
- **Les mentions de la pharmacie imprimées sur ses factures (adresse, NIF, RC, NIS, article d'imposition) manquantes sont signalées.** — `src/Pharma.Application/Factures/Factures.cs`

### Contenu et montants
- **Une facture se compose de ventes entières et/ou de lignes libres ; elle n'est jamais vide et son total TTC est supérieur à 0.** — `src/Pharma.Domain/Factures/RegleFacture.cs`
- **Seules les ventes validées sont facturables**, pour leur quantité nette des retours clients (montants au prorata). — `src/Pharma.Application/Factures/Factures.cs`, `database/migration/031_facture_client.sql`
- **Part facturée : l'organisme reçoit la part prise en charge ; le patient et l'entreprise reçoivent la part payée par le patient.** — `src/Pharma.Domain/Factures/RegleFacture.cs`
- **Facture patient : toutes les ventes doivent être au nom de ce patient ; facture organisme : toutes prises en charge par cet organisme.** — `src/Pharma.Application/Factures/Factures.cs`
- **Une vente n'est facturée qu'une fois par part** (patient / organisme), tant que la facture n'est pas remplacée ; une vente sans rien à facturer pour cette part est refusée. — `src/Pharma.Application/Factures/Factures.cs`, `src/Pharma.Infrastructure/Factures/FacturesSql.cs`
- **Une ligne par produit vendu ; les lignes à montant nul pour la part concernée sont omises.** — `src/Pharma.Domain/Factures/RegleFacture.cs`
- **La remise de la vente devient une ou plusieurs lignes négatives (part patient), réparties par taux de TVA au prorata**, pour que la TVA baisse avec la remise. — `src/Pharma.Domain/Factures/RegleFacture.cs`
- **Le don partiel d'une vente (réductions sur des médicaments) devient de même des lignes négatives « Don partiel (vente …) »**, et le montant proposé à la facturation le déduit. — `src/Pharma.Domain/Factures/RegleFacture.cs`, `src/Pharma.Infrastructure/Factures/FacturesSql.cs`, `src/Pharma.Api/Endpoints/FacturesEndpoints.cs`
- **Ligne libre : désignation obligatoire, quantité et prix TTC supérieurs à 0, TVA de 0 à 100 %.** — `src/Pharma.Domain/Factures/RegleFacture.cs`
- **Les prix sont TTC : HT = TTC ÷ (1 + taux), arrondi ligne par ligne ; TVA = TTC − HT.** — `src/Pharma.Domain/Factures/RegleFacture.cs`
- **« Déjà payé » = ce qui a été payé au comptoir pour ces ventes (hors crédit, moins les remboursements de retours), au plus la part patient ; 0 pour un organisme.** — `src/Pharma.Infrastructure/Factures/FacturesSql.cs`, `src/Pharma.Application/Factures/Factures.cs`
- **Le montant est aussi écrit en toutes lettres** (dinars et centimes). — `src/Pharma.Domain/Factures/MontantEnLettres.cs`

### Dates et numérotation
- **Date d'établissement : jamais dans le futur (par défaut : aujourd'hui) ; période facturée jamais inversée.** — `src/Pharma.Application/Factures/Factures.cs`
- **Échéance : celle saisie, sinon date d'établissement + délai de paiement du destinataire (patient : 30 jours) ; jamais avant l'établissement. Un avoir n'a pas d'échéance.** — `src/Pharma.Application/Factures/Factures.cs`, `database/migration/040_facture_echeance.sql`
- **Numéros sans trou, par année : F-2026-0001 pour les factures, AV-2026-0001 pour les avoirs**, attribués une émission à la fois. — `src/Pharma.Domain/Factures/RegleFacture.cs`, `src/Pharma.Infrastructure/Factures/FacturesSql.cs`

### Rejet, correction, remplacement
- **Seule une facture émise peut être rejetée, avec un motif obligatoire ; un avoir ne se rejette pas.** — `src/Pharma.Domain/Factures/RegleFacture.cs`
- **Un rejet peut être levé (l'organisme revient sur son rejet) avec un motif obligatoire** : la facture redevient émise sans modification ; le rejet reste dans l'historique. — `src/Pharma.Application/Factures/Factures.cs`, `src/Pharma.Domain/Factures/RegleFacture.cs`
- **Seule une facture rejetée peut être corrigée ou remplacée, pour le même destinataire.** — `src/Pharma.Domain/Factures/RegleFacture.cs`, `src/Pharma.Application/Factures/Factures.cs`
- **Correction : même numéro, version + 1, la facture redevient émise ; la version précédente reste consultable et imprimable.** — `src/Pharma.Application/Factures/Factures.cs`, `src/Pharma.Infrastructure/Factures/FacturesSql.cs`
- **Remplacement : nouvelle facture avec un nouveau numéro ; l'ancienne passe « remplacée » et sort des comptes.** — `src/Pharma.Application/Factures/Factures.cs`
- **Une facture rejetée puis payée en entier est affichée « soldée ».** — `src/Pharma.Domain/Factures/RegleFacture.cs`

### Avoirs
- **Un avoir se fait sur une facture en vigueur (pas sur un avoir ni une facture remplacée), avec un motif obligatoire et un montant > 0.** — `src/Pharma.Domain/Factures/RegleFacture.cs`
- **Le total des avoirs d'une facture ne dépasse pas son montant TTC.** — `src/Pharma.Domain/Factures/RegleFacture.cs`
- **L'avoir est daté du jour, au même destinataire ; son HT suit la proportion HT/TTC de la facture.** — `src/Pharma.Application/Factures/Factures.cs`

### Suivi et encaissement
- **Reste à encaisser = TTC − déjà payé − avoirs − règlements reçus en trésorerie − règlements de crédit du patient sur les ventes facturées** (jamais négatif ; 0 pour un avoir ou une facture remplacée). — `database/migration/038_releve_credit.sql`, `database/migration/031_facture_client.sql`
- **Le pharmacien peut retirer une facture du suivi de trésorerie (motif obligatoire) ou l'y remettre** ; elle reste valable et imprimable. Un avoir n'est pas suivi. — `src/Pharma.Application/Factures/Factures.cs`, `src/Pharma.Domain/Factures/RegleFacture.cs`
- **Une facture est « échue » quand son échéance est dépassée et qu'il reste à encaisser.** — `src/Pharma.Infrastructure/Factures/FacturesSql.cs`, `database/migration/040_facture_echeance.sql`

### Historique protégé
- **Aucune facture ni aucun avoir ne peut être supprimé**, même par une requête directe en base. — `database/migration/043_historique_factures.sql`
- **Une facture remplacée et ses lignes ne peuvent plus être modifiées ; une version archivée ne peut être ni modifiée ni supprimée.** — `database/migration/043_historique_factures.sql`
- **Chaque émission, rejet, levée de rejet, correction, remplacement, avoir, retrait ou remise en suivi est inscrit au journal.** — `src/Pharma.Application/Factures/Factures.cs`

## Trésorerie

### Droits
- **Toute la trésorerie (consulter, comptes, mouvements, transferts, remises de chèques, versements de caisse) demande le droit `tresorerie.gerer`** (donné au titulaire, qui l'accorde à qui il veut). — `src/Pharma.Application/Tresorerie/Tresorerie.cs`, `database/migration/030_tresorerie.sql`

### Comptes (coffre et banques)
- **Il existe un seul coffre, créé d'office ; il reste actif et ne reçoit pas les paiements par carte.** — `database/migration/030_tresorerie.sql`, `src/Pharma.Application/Tresorerie/Tresorerie.cs`
- **Les banques sont déclarées avec un nom obligatoire et unique, un solde et une date de départ.** — `src/Pharma.Application/Tresorerie/Tresorerie.cs`, `src/Pharma.Infrastructure/Tresorerie/TresorerieSql.cs`
- **Une seule banque reçoit les cartes** : en désigner une autre retire ce rôle à la précédente. Elle doit rester active. — `src/Pharma.Infrastructure/Tresorerie/TresorerieSql.cs`, `src/Pharma.Application/Tresorerie/Tresorerie.cs`
- **Solde d'un compte = solde de départ + mouvements depuis la date de départ.** — `database/migration/036_solde_tresorerie_echeance.sql`
- **Mouvements d'un compte (coffre ou banque) : paginés et filtrés par le serveur** (période, employé). Les totaux Entrée et Sortie portent sur tout le filtre, toutes pages confondues ; la liste des employés du filtre est celle des employés ayant des mouvements sur la période et le compte affichés (un mouvement calculé, sans employé, n'y figure pas). — `src/Pharma.Application/Tresorerie/Tresorerie.cs` (`PageMouvementsAsync`)

### Mouvements saisis
- **Trois types saisis à la main : dépense, encaissement divers, versement d'un organisme.** — `src/Pharma.Domain/Tresorerie/RegleTresorerie.cs`
- **Montant > 0 avec deux décimales au plus ; date ni future, ni antérieure au départ du compte (par défaut : aujourd'hui).** — `src/Pharma.Domain/Tresorerie/RegleTresorerie.cs`
- **Le compte doit être actif.** — `src/Pharma.Application/Tresorerie/Tresorerie.cs`
- **Une dépense exige une catégorie : loyer et charges, électricité/eau/gaz, salaires et cotisations, impôts et taxes, fournitures et consommables, entretien et réparations, autre.** — `src/Pharma.Domain/Tresorerie/RegleTresorerie.cs`
- **Une dépense ne peut pas dépasser le solde du compte.** — `src/Pharma.Application/Tresorerie/Tresorerie.cs`
- **Dépense et encaissement exigent un libellé ; un versement d'organisme exige l'organisme** (libellé automatique « Versement CNAS · bordereau … » s'il est vide). — `src/Pharma.Domain/Tresorerie/RegleTresorerie.cs`, `src/Pharma.Infrastructure/Tresorerie/TresorerieSql.cs`
- **Un encaissement ou un versement d'organisme peut solder des factures clients**, dans l'ordre choisi, chacune au plus de son reste ; une dépense ne solde pas de facture. Pour un versement d'organisme, toutes les factures doivent être adressées à cet organisme. — `src/Pharma.Application/Tresorerie/Tresorerie.cs`, `src/Pharma.Domain/Factures/RegleFacture.cs`
- **Une facture déjà soldée ou retirée du suivi entre-temps est refusée** (« rechargez la liste »). — `src/Pharma.Infrastructure/Tresorerie/TresorerieSql.cs`
- **Chaque mouvement saisi est inscrit au journal.** — `src/Pharma.Application/Tresorerie/Tresorerie.cs`

### Transferts
- **Un transfert relie deux comptes différents, tous deux actifs** (dépôt d'espèces du coffre à la banque, retrait, virement entre banques). — `src/Pharma.Domain/Tresorerie/RegleTresorerie.cs`, `src/Pharma.Application/Tresorerie/Tresorerie.cs`
- **Le montant ne dépasse pas le solde du compte de départ ; la date n'est pas antérieure au départ le plus récent des deux comptes.** — `src/Pharma.Application/Tresorerie/Tresorerie.cs`

### Chèques reçus
- **Les chèques reçus (ventes validées, remboursements de crédit) restent « à remettre » jusqu'à leur remise en banque.** — `database/migration/030_tresorerie.sql`
- **Une remise de chèques se fait sur un compte bancaire actif, avec au moins un chèque coché, à une date non future.** — `src/Pharma.Application/Tresorerie/Tresorerie.cs`
- **Un chèque ne peut être remis qu'une fois** (refus si un autre poste vient de le remettre). — `src/Pharma.Infrastructure/Tresorerie/TresorerieSql.cs`, `database/migration/030_tresorerie.sql`

### Argent des caisses vers le coffre
- **Après clôture : au plus l'argent compté à la clôture, montant > 0, une seule fois par caisse.** Les relèves ne sont pas proposées. — `src/Pharma.Domain/Tresorerie/RegleTresorerie.cs`, `src/Pharma.Infrastructure/Tresorerie/TresorerieSql.cs`, `database/migration/030_tresorerie.sql`
- **Pendant la journée : depuis la caisse ouverte du poste, montant > 0 et au plus fond de caisse + ventes en espèces** ; l'attendu de la caisse diminue d'autant. — `src/Pharma.Application/Tresorerie/Tresorerie.cs`
- **Versement à la clôture « manuel » (par défaut) ou « automatique », réglé par le titulaire** ; en automatique, l'argent compté moins le fond de caisse par défaut part au coffre (jamais négatif). — `database/migration/030_tresorerie.sql`, `src/Pharma.Domain/Tresorerie/RegleTresorerie.cs`
- **Montant proposé pour une caisse clôturée = compté (ou attendu) moins le fond de caisse par défaut, jamais négatif.** — `database/migration/030_tresorerie.sql`

### Mouvements calculés automatiquement
- **Paiements par carte du jour : entrée sur la banque « carte » ; remboursements par carte (annulations, retours) : sortie de cette banque.** — `database/migration/030_tresorerie.sql`, `database/migration/036_solde_tresorerie_echeance.sql`
- **Règlements grossistes : espèces sortent du coffre (hors complément du tiroir) ; chèque et virement sortent de la banque choisie à la date du règlement.** — `database/migration/030_tresorerie.sql`
- **Un effet ne diminue le solde de la banque qu'à son échéance (échéance atteinte).** — `database/migration/036_solde_tresorerie_echeance.sql`

## Chifa / CNAS et tiers payant

### Import Chifa
- **Le logiciel ne remplace pas Chifa_Officine : il lit seulement les fichiers exportés (factures signées), sans accès à la base de la CNAS.** — `docs/chifa.md`, `src/Pharma.Infrastructure/Chifa/ChifaFichiersConnecteur.cs`
- **L'import des factures signées n'est pas encore opérationnel** : le format des fichiers n'est pas connu, la lecture s'arrête avec un message d'erreur. — `src/Pharma.Infrastructure/Chifa/ChifaFichiersConnecteur.cs`
- **Les fichiers lus (modifiés depuis la date demandée) sont archivés dans la GED (GED\Chifa), sans rien écraser** ; un fichier déjà archivé de même taille n'est pas recopié. — `src/Pharma.Infrastructure/Chifa/ChifaFichiersConnecteur.cs`
- **Les droits `chifa.importer` et `chifa.bordereaux` existent mais ne sont exigés par aucune fonction à ce jour (à confirmer).** — `src/Pharma.Domain/Securite/Permissions.cs`, `database/migration/006_droits_utilisateurs.sql`

### Bordereaux
- **Un bordereau a un numéro unique, un organisme, une période (fin ≥ début) et un statut : ouvert, envoyé, payé, rejet partiel.** — `database/migration/001_schema.sql`
- **Aucune fonction du logiciel ne crée ni n'envoie de bordereau à ce jour (à confirmer)** ; seul son paiement est saisi en trésorerie. — `src/Pharma.Infrastructure/Tresorerie/TresorerieSql.cs`
- **Un versement d'organisme peut solder un bordereau « envoyé » de cet organisme, en le marquant « payé » ou « rejet partiel »** (choix obligatoire). — `src/Pharma.Application/Tresorerie/Tresorerie.cs`, `src/Pharma.Infrastructure/Tresorerie/TresorerieSql.cs`
- **Reste dû sur un bordereau = montant total − versements reçus ; un bordereau envoyé ou en rejet partiel non soldé est suivi comme créance.** — `database/migration/040_facture_echeance.sql`
- **Une vente déjà sur un bordereau envoyé ne s'annule pas dans le logiciel** : elle passe par la régularisation Chifa. — `src/Pharma.Domain/Ventes/RegleAnnulation.cs`

### Tiers payant à la vente
- **Vente en tiers payant : l'assuré et l'organisme (CNAS, CASNOS) sont obligatoires, ainsi qu'un taux de remboursement de 0 exclu à 100 %** (sauf reste à charge saisi). — `src/Pharma.Domain/Ventes/Vente.cs`, `database/migration/001_schema.sql`
- **Part de l'organisme = min(tarif de référence, prix) × quantité × taux, arrondie au centime** (comme les factures Chifa : 1 160,82 DA × 80 % = 928,66 DA) ; seuls les produits remboursables avec tarif de référence sont pris en charge. — `src/Pharma.Domain/Ventes/LigneVente.cs`, `src/Pharma.Domain/Ventes/Vente.cs`
- **Le caissier peut saisir le reste à charge du patient (de 0 au montant de la vente)** : la part de l'organisme est la différence, répartie au prorata des lignes ; le taux choisi n'est alors pas appliqué. — `src/Pharma.Domain/Ventes/Vente.cs`, `database/migration/045_prix_lot_ug_categories.sql`
- **Une vente en tiers payant n'accepte pas de retour partiel** : il faut annuler la vente complète puis la refaire. — `src/Pharma.Domain/Ventes/RegleRetour.cs`
- **Organismes présents au départ : CNAS et CASNOS** ; le titulaire peut en ajouter. — `database/migration/001_schema.sql`, `src/Pharma.Application/Factures/Factures.cs`

### Volet Chifa repris par son QR code
- **Scanner le QR d'un volet Chifa à la caisse reprend la vente sans ressaisie** : tiers payant CNAS, assuré retrouvé par son n° (chiffres seuls, avec ou sans suffixe), produits reconnus ajoutés avec leurs quantités (lots servis comme d'habitude). Droit `caisse.vendre`. — `src/Pharma.Application/Caisse/VoletChifa.cs`, `src/Pharma.Desktop/Composants/Pages/Caisse.razor`
- **Format lu : n° de facture ; date ; n° d'assuré ; suffixe ; deux champs gardés tels quels ; seconde date ; puis une ligne « code, quantité, prix unitaire » par produit**. Tout autre contenu est refusé (« pas un QR de volet Chifa reconnu »). Format déduit d'un seul volet : à confirmer avec l'éditeur de Chifa. — `src/Pharma.Domain/Ventes/QrChifa.cs`, `docs/a-faire.md`
- **Un QR scanné par une douchette réglée en QWERTY sur un poste AZERTY est relu** (comme les n° de BL). — `src/Pharma.Application/Caisse/VoletChifa.cs`
- **Un code produit Chifa inconnu est associé une fois à un produit actif du catalogue, puis reconnu à chaque volet** ; l'association (ou sa modification) est inscrite au journal (`code_chifa_associe`). — `src/Pharma.Application/Caisse/VoletChifa.cs`, `database/migration/060_volet_chifa.sql`
- **Un prix du volet différent du prix du catalogue est signalé** ; la vente reste au prix du lot servi. — `src/Pharma.Desktop/Composants/Pages/Caisse.razor`
- **Un volet n'est repris qu'une fois** (refus avec le n° de la vente qui l'a repris) ; une vente annulée libère son volet. **Un seul volet par vente** : un second scan du même volet, ou d'un autre volet, dans le même panier est refusé. — `database/migration/060_volet_chifa.sql`, `src/Pharma.Infrastructure/Caisse/VentesEcritureSql.cs`
- **Un volet ne se reprend qu'en tiers payant** ; le serveur relit le QR à l'encaissement et garde sur la vente le n° de facture, la date et le QR brut. Une vente mise en attente garde son volet. — `src/Pharma.Application/Caisse/Encaisser.cs`

### Suivi des dettes et créances (achats, factures, organismes)
- **Le suivi regroupe : factures clients à encaisser (rejetées comprises, hors factures remplacées ou retirées du suivi), bordereaux non payés, factures de grossistes à payer, ancien solde des grossistes, effets remis dont l'échéance n'est pas passée.** — `database/migration/040_facture_echeance.sql`
- **Une dette ou une créance est signalée au-delà d'une ancienneté réglable de 1 à 365 jours (30 par défaut).** — `database/migration/039_suivi_dettes.sql`

## Sécurité et droits
- **Le menu n'affiche que les écrans permis** : un écran sans droit n'apparaît pas (au lieu d'être grisé), un groupe du menu (Finances, Pilotage) sans écran permis non plus ; ⚙ Administration n'apparaît qu'à qui y a droit. Le serveur revérifie toujours chaque droit. — `src/Pharma.Desktop/Composants/Mise/MiseEnPage.razor`, `docs/charte-graphique.md` (« En-tête et menu »)
- **« ← Retour » ne ramène jamais sur l'écran d'un autre employé** : l'historique des écrans est vidé à la connexion, à la déconnexion, au changement d'utilisateur et par inactivité ; le premier changement de mot de passe obligatoire et les écrans du super administrateur (restauration, reprise) ne sont jamais rouverts par l'historique. — `src/Pharma.Desktop/Services/HistoriqueNavigation.cs`, `docs/navigation.md`
- **Écran d'arrivée selon les droits** : après la connexion (ou le premier changement de mot de passe), au clic sur le logo et quand un écran est refusé, l'employé qui a un droit de gestion (titulaire, `achats.commander`, `achats.valider_ecart`, `stock.ajuster`, `rapports.ventes`, `tresorerie.gerer`, `factures.gerer`, `admin.parametres`) arrive sur le tableau de bord ; les autres (vendeur, stagiaire), sur la Caisse s'ils ont `caisse.vendre`. Une vente laissée en attente au changement d'utilisateur ramène toujours à la Caisse. — `src/Pharma.Desktop/Services/Domaines.cs`
- **Menu du haut : les domaines ; un écran sans droit n'apparaît ni dans le menu, ni dans la recherche Ctrl + K, ni sur le tableau de bord** (le serveur revérifie toujours le droit). Le menu de gauche liste les écrans permis du domaine. — `src/Pharma.Desktop/Services/Domaines.cs`, `Composants/Mise/NavDomaine.razor`, `Composants/Mise/RechercheGlobale.razor`
- **Une rubrique ou une action sans droit n'est pas montrée (plus grisée)**, ni un groupe vide ; une action proposée sous une liste vide (« étape suivante ») n'apparaît que si l'employé peut l'ouvrir. — `Composants/Mise/NavAdmin.razor`, `Composants/Pages/Achats.razor`, `Composants/Caisse/NavCaisse.razor`, `Composants/Mise/ActionSuivante.razor`
- **Compteurs du travail en attente lus selon les droits** (réceptions : `achats.receptionner` ; bons en brouillon : `achats.commander` ; factures à encaisser et échues : `factures.gerer` ; sommes dues en alerte : `tresorerie.gerer`), au plus toutes les 30 secondes ; un compte illisible n'affiche rien. — `src/Pharma.Desktop/Services/CompteursAttente.cs`
- **Pendant un comptage à l'aveugle, aucun lien ne mène au stock** : les noms de produits et de lots ne sont cliquables qu'en dehors de la feuille de comptage (écarts à valider, résultat, registre). — `Composants/Mise/LienProduit.razor`, `Composants/Pages/Inventaire.razor`
- **Connexions révoquées** : changer ou réinitialiser un mot de passe, désactiver un compte, effacer un code comptoir ou restaurer la base ferme aussitôt les connexions déjà ouvertes (l'API relit l'état du compte à chaque appel). — `database/migration/063_revocation_connexions.sql`, `src/Pharma.Api/Securite/ValidationJeton.cs`
- **Les routes sans droit particulier refusent le mot de passe provisoire et le super admin**, sauf ce qui leur reste permis (changer son mot de passe, ses droits, le thème, l'état du poste, l'identité de la pharmacie). — `src/Pharma.Api/Securite/PermissionFilter.cs`

### Rôles

- **Il existe quatre rôles de départ : Pharmacien titulaire, Pharmacien assistant, Vendeur, Stagiaire.** : codes `titulaire`, `assistant`, `vendeur`, `stagiaire`. — `database/init/seed_referentiel.sql`
- **Le rôle titulaire est un rôle « système » : il a toujours tous les droits.** : la matrice des droits ne peut ni lui en retirer ni lui en ajouter. — `src/Pharma.Application/Administration/Utilisateurs.cs`
- **La base refuse toute modification des droits du titulaire.** : déclencheur `trg_proteger_role_systeme` (« Les droits du titulaire ne peuvent pas être modifiés »). — `database/migration/006_droits_utilisateurs.sql`
- **La base refuse de modifier ou de supprimer le rôle titulaire lui-même.** : déclencheur `trg_role_systeme`. — `database/migration/010_administration.sql`
- **Il doit toujours rester au moins un titulaire actif.** : la base refuse de désactiver, de changer de rôle ou de supprimer le dernier titulaire actif (déclencheur `trg_garder_un_titulaire`). — `database/migration/006_droits_utilisateurs.sql`
- **La remise maximale n'est plus réglée par rôle mais sur chaque employé** (2026-11-14) ; `role.remise_max_pct` reste en base pour le rejeu des scripts mais n'est plus lu. À la mise à jour, chaque employé a reçu le maximum de son rôle (assistant 10 %, vendeur et stagiaire 0 %). — `database/migration/071_don_remise_par_personne.sql`
- **Les droits de départ ne sont posés qu'à la première installation (aucun compte encore créé).** : une réexécution du script ne touche jamais aux droits modifiés ensuite par le titulaire. — `database/init/seed_referentiel.sql`

### Matrice des droits

- **Le titulaire (ou toute personne ayant le droit `admin.utilisateurs`) règle, rôle par rôle, la liste des droits**, sauf les droits réglés par personne (ci-dessous), absents de la matrice et refusés s'ils y sont envoyés. — `src/Pharma.Api/Endpoints/AdminEndpoints.cs`, `src/Pharma.Application/Administration/Utilisateurs.cs`

### Don et remise réglés par personne (2026-11-14)

- **Le don, la remise maximale et la gestion des remises automatiques se règlent employé par employé, par le titulaire seul** (fiche de l'employé, Utilisateurs et droits) : « Peut faire un don » (`stock.donner`), « Remise maximale (%) » (entier de 0 à 100 ; 0 = pas de remise, `caisse.remise` accordé dès 1 %), « Peut gérer les remises automatiques » (`caisse.remises_auto`). — `src/Pharma.Application/Administration/Utilisateurs.cs` (`ReglerDonRemise`), `database/migration/071_don_remise_par_personne.sql`
- **Le titulaire a toujours le don, 100 % de remise et la gestion des remises automatiques**, sans réglage (refusé si on tente de le changer). — `src/Pharma.Application/Administration/Utilisateurs.cs`, fonction `a_permission`
- **Ces trois droits sont lus sur l'employé, les autres sur son rôle** (fonction `a_permission`, colonne `permission.par_personne`) ; un employé désactivé n'en a aucun. — `database/migration/071_don_remise_par_personne.sql`
- **Reprise à la mise à jour : chaque employé a reçu ce que son rôle lui donnait** (don pour l'assistant, maximum du rôle) ; rien n'a changé le jour de la mise à jour. — `database/migration/071_don_remise_par_personne.sql`
- **La liste des employés affiche « Don · remise » de chacun ; chaque changement est inscrit au journal avec l'avant et l'après** (`don_remise_employe`). — `src/Pharma.Desktop/Composants/Pages/AdminDroits.razor`, `src/Pharma.Application/Administration/Utilisateurs.cs`
- **Sans le droit, ou au-delà de son maximum, l'employé fait valider par le code d'un collègue qui l'a** (remise : jusqu'au maximum de ce collègue). — `src/Pharma.Api/Securite/AutorisationParCode.cs`
- **Le rôle titulaire est ignoré lors de l'enregistrement de la matrice.** : il garde toujours tous les droits. — `src/Pharma.Application/Administration/Utilisateurs.cs`
- **Un droit inconnu du logiciel est refusé dans la matrice.** : « Permission inconnue ». — `src/Pharma.Application/Administration/Utilisateurs.cs`
- **Chaque modification de la matrice est inscrite au journal, avec les droits ajoutés et retirés de chaque rôle.** : action `droits_modifies`. — `src/Pharma.Application/Administration/Utilisateurs.cs`
- **Le droit est vérifié par le serveur, en base, à chaque demande.** : un droit retiré s'applique tout de suite (fonction SQL `a_permission`). — `src/Pharma.Infrastructure/Securite/PermissionService.cs`
- **Un employé désactivé n'a plus aucun droit.** : `a_permission` ne retient que les comptes actifs. — `database/migration/006_droits_utilisateurs.sql`
- **Le poste grise ce que l'employé n'a pas le droit de faire, mais le serveur revérifie toujours.** — `src/Pharma.Api/Endpoints/AuthEndpoints.cs`
- **Certaines actions sont réservées au pharmacien titulaire, quel que soit le réglage des droits.** : exemples dans le périmètre : notifications (réglages, destinataires, test, file), création ou modification d'un compte titulaire. — `src/Pharma.Application/Securite/Ports.cs`
- **Chaque droit porte un indicateur « sensible » affiché dans l'administration.** : son effet au-delà de l'affichage n'est pas trouvé dans le code (à confirmer). — `database/migration/006_droits_utilisateurs.sql`

### Droits par défaut

Valeurs posées à la première installation ; le titulaire peut ensuite les changer (sauf pour le rôle titulaire). Depuis le 2026-11-14, `caisse.remise`, `stock.donner` et `caisse.remises_auto` ne sont plus dans cette matrice : ils se règlent sur chaque employé (section précédente) ; à la première installation, seul le titulaire les a.

| Droit | Libellé | Titulaire | Assistant | Vendeur | Stagiaire |
| --- | --- | :-: | :-: | :-: | :-: |
| `caisse.vendre` | Encaisser une vente | ✔ | ✔ | ✔ | ✔ |
| `caisse.supprimer_ligne` | Supprimer une ligne après scan | ✔ | ✔ | – | – |
| `caisse.annuler_vente` | Annuler une vente validée | ✔ | ✔ | – | – |
| `caisse.retour_especes` | Rembourser un retour en espèces | ✔ | ✔ | – | – |
| `caisse.ouvrir_tiroir` | Ouvrir le tiroir sans vente | ✔ | ✔ | – | – |
| `caisse.voir_ecart` | Voir le montant attendu et les écarts | ✔ | – | – | – |
| `caisse.vendre_sans_ordonnance` | Vendre sans ordonnance un médicament de liste I ou II | ✔ | ✔ | – | – |
| `credit.accorder` | Mettre une vente en crédit | ✔ | ✔ | ✔ | – |
| `credit.depasser_plafond` | Dépasser le plafond de crédit d'un client | ✔ | ✔ | – | – |
| `psychotropes.delivrer` | Délivrer un psychotrope | ✔ | ✔ | – | – |
| `psychotropes.registre` | Consulter le registre | ✔ | ✔ | – | – |
| `stock.consulter` | Consulter le stock | ✔ | ✔ | ✔ | ✔ |
| `stock.ajuster` | Corriger le stock (inventaire) | ✔ | ✔ | – | – |
| `stock.declarer_perime` | Déclarer un produit périmé ou cassé | ✔ | ✔ | – | – |
| `prix.modifier` | Modifier un prix de vente | ✔ | – | – | – |
| `produits.bloquer` | Bloquer un produit à l'achat ou à la vente | ✔ | ✔ | – | – |
| `catalogue.creer` | Créer un produit au catalogue | ✔ | ✔ | – | – |
| `achats.commander` | Passer une commande | ✔ | ✔ | – | – |
| `achats.receptionner` | Réceptionner une livraison | ✔ | ✔ | ✔ | – |
| `achats.regler` | Régler les factures des grossistes | ✔ | ✔ | – | – |
| `achats.retour_fournisseur` | Faire un retour fournisseur | ✔ | ✔ | – | – |
| `achats.valider_ecart` | Valider un écart entre le comptage et la facture d'une livraison | ✔ | ✔ | – | – |
| `tresorerie.gerer` | Gérer la trésorerie (coffre, banques, dépenses) | ✔ | – | – | – |
| `factures.gerer` | Émettre et gérer les factures clients | ✔ | – | – | – |
| `chifa.importer` | Importer les factures Chifa | ✔ | ✔ | ✔ | – |
| `chifa.bordereaux` | Gérer les bordereaux et rejets | ✔ | ✔ | – | – |
| `rapports.ventes` | Voir les rapports de ventes | ✔ | ✔ | – | – |
| `rapports.marges` | Voir les prix d'achat et les marges | ✔ | – | – | – |
| `admin.parametres` | Modifier les paramètres | ✔ | – | – | – |
| `admin.utilisateurs` | Gérer les utilisateurs et les droits | ✔ | – | – | – |
| `admin.sauvegardes` | Gérer les sauvegardes | ✔ | – | – | – |

Sources : `database/init/seed_referentiel.sql`, `src/Pharma.Domain/Securite/Permissions.cs`. Remarque : `docs/securite-et-droits.md` annonce « 26 permissions » ; le code et le script en comptent 34 depuis le script 071 (dont 3 réglés par personne) (document à mettre à jour).

### Comptes des employés

- **Un compte par employé, identifié par un identifiant unique.** : un identifiant déjà utilisé est refusé. — `src/Pharma.Infrastructure/Administration/AdministrationSql.cs`
- **L'identifiant compte 3 à 30 caractères : lettres minuscules sans accent, chiffres, point, tiret ou souligné.** : il est mis en minuscules automatiquement. — `src/Pharma.Domain/Securite/RegleCompte.cs`
- **Le nom de l'employé est obligatoire.** — `src/Pharma.Application/Administration/Utilisateurs.cs`
- **Le téléphone et l'e-mail de l'employé sont facultatifs et contrôlés s'ils sont saisis.** — `src/Pharma.Application/Administration/Utilisateurs.cs`
- **Seul un titulaire peut créer un compte de pharmacien titulaire.** — `src/Pharma.Application/Administration/Utilisateurs.cs`
- **Seul un titulaire peut modifier le compte d'un titulaire, ou donner le rôle titulaire à un employé.** — `src/Pharma.Application/Administration/Utilisateurs.cs`
- **Un compte ne se supprime pas : il se désactive.** : aucune suppression d'employé n'est proposée par l'API (constaté dans `AdminEndpoints.cs`). — `src/Pharma.Api/Endpoints/AdminEndpoints.cs`
- **La création et chaque modification d'un compte sont inscrites au journal.** : actions `utilisateur_cree`, `utilisateur_modifie` (rôle, actif, mot de passe changé oui/non, coordonnées changées oui/non). — `src/Pharma.Application/Administration/Utilisateurs.cs`
- **La date de dernière connexion de chaque employé est notée et affichée dans l'administration.** — `src/Pharma.Application/Securite/Ports.cs`
- **Créer et gérer les comptes demande le droit `admin.utilisateurs`.** — `src/Pharma.Api/Endpoints/AdminEndpoints.cs`

### Mots de passe

- **Un mot de passe compte au moins 8 caractères et ne peut pas être fait que d'espaces.** — `src/Pharma.Domain/Securite/RegleCompte.cs`
- **Les mots de passe sont enregistrés hachés (BCrypt), jamais en clair.** — `src/Pharma.Infrastructure/Securite/VerificateurBcrypt.cs`
- **Un compte créé par l'administration reçoit un mot de passe provisoire à changer à la première connexion.** — `src/Pharma.Infrastructure/Administration/AdministrationSql.cs`
- **Un mot de passe réinitialisé par l'administration doit lui aussi être changé à la connexion suivante.** — `src/Pharma.Infrastructure/Administration/AdministrationSql.cs`
- **Le compte « titulaire » de la mise en service est créé avec un mot de passe provisoire à changer à la première connexion.** : valeur non recopiée ici. — `database/init/seed_referentiel.sql`
- **Tant que le mot de passe provisoire n'est pas changé, l'employé n'a aucun droit : il ne peut que changer son mot de passe.** — `src/Pharma.Api/Securite/PermissionFilter.cs`
- **Pour changer son mot de passe, l'employé doit saisir l'actuel, puis le nouveau deux fois.** — `src/Pharma.Application/Securite/ChangerMonMotDePasse.cs`
- **Le nouveau mot de passe compte au moins 8 caractères, doit être confirmé à l'identique et différer de l'actuel.** — `src/Pharma.Domain/Securite/RegleMotDePasse.cs`
- **Un compte désactivé ne peut pas changer son mot de passe.** — `src/Pharma.Application/Securite/ChangerMonMotDePasse.cs`
- **Chaque changement de mot de passe par l'employé est inscrit au journal.** : action `mot_de_passe_change`, en précisant s'il s'agit de la première connexion. — `src/Pharma.Application/Securite/ChangerMonMotDePasse.cs`
- **Après le changement, une nouvelle session est ouverte avec les droits du rôle.** — `src/Pharma.Application/Securite/ChangerMonMotDePasse.cs`

### Connexion et verrouillage

- **La connexion se fait par identifiant et mot de passe ; seul un compte actif peut se connecter.** — `src/Pharma.Application/Securite/SeConnecter.cs`
- **Un échec de connexion ne dit pas si c'est l'identifiant ou le mot de passe qui est faux.** : message unique « Identifiant ou mot de passe incorrect ». — `src/Pharma.Api/Endpoints/AuthEndpoints.cs`
- **Après 5 mots de passe faux, l'identifiant est bloqué 5 minutes.** — `src/Pharma.Application/Securite/EssaisConnexion.cs`
- **Le blocage s'applique même à un identifiant qui n'existe pas.** : la réponse ne révèle pas quels comptes existent. — `src/Pharma.Application/Securite/SeConnecter.cs`
- **Le compteur d'erreurs repart de zéro après 15 minutes sans nouvel essai, après un blocage terminé, ou après une connexion réussie.** — `src/Pharma.Application/Securite/EssaisConnexion.cs`
- **Le blocage d'un compte existant est inscrit au journal.** : action `connexion_bloquee`. — `src/Pharma.Application/Securite/SeConnecter.cs`
- **Les compteurs d'erreurs sont gardés en mémoire du serveur : un redémarrage du serveur les remet à zéro.** — `src/Pharma.Application/Securite/EssaisConnexion.cs`
- **Chaque connexion réussie est tracée avec le poste et la méthode (mot de passe ou code comptoir)** (table `connexion_employe`) ; elle relance le calcul des anomalies (employé sur deux postes à la fois). — `src/Pharma.Application/Securite/SeConnecter.cs`, `src/Pharma.Application/Securite/ConnexionComptoir.cs`, `database/migration/058_surveillance_comptes.sql`

### Sessions et postes

- **Une session (jeton) dure 12 heures par défaut.** : réglable par `Jwt:DureeHeures`. — `src/Pharma.Api/Securite/JetonService.cs`
- **Hors poste de développement, le serveur refuse de démarrer si sa clé de signature est absente, trop courte (moins de 64 caractères) ou restée à la valeur d'exemple.** — `src/Pharma.Api/Securite/JetonService.cs`
- **La déconnexion automatique après inactivité se règle entre 0 (désactivée) et 240 minutes.** : réglage des paramètres de caisse ; 0 par défaut. — `src/Pharma.Application/Caisse/Sessions.cs`
- **Chaque poste envoie son nom au serveur ; ce nom identifie la caisse du poste.** — `src/Pharma.Api/Securite/PermissionsComptoir.cs`
- **La barre d'état du poste (dernière sauvegarde, caisse ouverte) est visible par tout employé connecté, sans montant ni message d'erreur.** — `src/Pharma.Application/Administration/EtatPoste.cs`
- **Chaque employé choisit son affichage (automatique, clair ou sombre) sans droit particulier, même connecté par code comptoir.** — `src/Pharma.Application/Securite/Affichage.cs`

### Code comptoir

- **Chaque employé peut choisir un code comptoir personnel de 4 à 6 chiffres.** — `src/Pharma.Domain/Securite/CodeComptoir.cs`
- **Un code aux chiffres tous identiques (ex. 1111) ou en suite (ex. 1234, 9876) est refusé.** — `src/Pharma.Domain/Securite/CodeComptoir.cs`
- **Pour choisir ou changer son code, l'employé doit être connecté avec son mot de passe (pas par code).** — `src/Pharma.Application/Securite/ConnexionComptoir.cs`
- **Le code comptoir est enregistré haché.** — `src/Pharma.Application/Securite/ConnexionComptoir.cs`
- **Un code oublié ou divulgué est effacé par une personne ayant le droit `admin.utilisateurs` ; l'employé en choisit ensuite un nouveau.** : inscrit au journal (`code_comptoir`, action « efface »). — `src/Pharma.Application/Securite/ConnexionComptoir.cs`
- **La définition d'un code est inscrite au journal.** : action `code_comptoir`, « defini ». — `src/Pharma.Application/Securite/ConnexionComptoir.cs`
- **Le code comptoir sert à reprendre la caisse d'un poste sans la fermer (changement d'employé au comptoir).** — `src/Pharma.Application/Securite/ConnexionComptoir.cs`
- **La connexion par code n'est possible que sur un poste dont la caisse est ouverte.** : sinon il faut le mot de passe. — `src/Pharma.Application/Securite/ConnexionComptoir.cs`
- **La connexion par code exige le droit `caisse.vendre`.** — `src/Pharma.Application/Securite/ConnexionComptoir.cs`
- **Connecté par code, l'employé ne garde que les droits du comptoir : tous les droits `caisse.*` et `credit.*`, plus `psychotropes.delivrer`, `stock.consulter` et `stock.donner`** (le don partiel du reste à charge se fait à la caisse, 2026-11-14). : jamais les écrans réservés au titulaire ; pour le reste, il doit se reconnecter avec son mot de passe. — `src/Pharma.Domain/Securite/CodeComptoir.cs`
- **Après 5 codes faux, le code est bloqué 5 minutes.** : l'employé peut toujours se connecter avec son mot de passe. — `src/Pharma.Domain/Securite/CodeComptoir.cs`
- **Les essais simultanés sont comptés un par un : ils ne peuvent pas dépasser le nombre d'essais permis.** — `src/Pharma.Infrastructure/Securite/CodesComptoirSql.cs`
- **Le blocage d'un code comptoir est inscrit au journal, avec le nom du poste.** : action `code_comptoir_bloque`. — `src/Pharma.Application/Securite/ConnexionComptoir.cs`
- **Un code juste remet le compteur d'erreurs à zéro.** — `src/Pharma.Application/Securite/ConnexionComptoir.cs`
- **L'écran de connexion n'affiche aucun nom ni identifiant d'employé : l'employé tape son identifiant.** — `src/Pharma.Api/Endpoints/AuthEndpoints.cs`

### Autorisation sur place

- **Une action refusée faute de droit peut être autorisée sur place par le code comptoir d'un autre employé présent qui a ce droit.** — `src/Pharma.Api/Securite/AutorisationParCode.cs`
- **Une action réservée au titulaire peut être autorisée sur place par le code comptoir d'un titulaire.** — `src/Pharma.Api/Securite/AutorisationParCode.cs`
- **L'autorisation sur place est activée par défaut et peut être désactivée par le titulaire (Administration › Caisse).** : réglage `autorisation_par_code`, droit `admin.parametres`. — `database/migration/047_recette_corrections.sql`
- **Un employé ne peut pas s'autoriser lui-même avec son propre code.** — `src/Pharma.Api/Securite/AutorisationParCode.cs`
- **Seuls les droits acceptés par le valideur au poste sont accordés.** : un code saisi pour vendre sans ordonnance n'autorise pas, dans la même opération, un dépassement du plafond de crédit. — `src/Pharma.Api/Securite/AutorisationParCode.cs`
- **Le code du valideur est vérifié une seule fois par opération, avec les mêmes règles d'erreurs et de blocage que le code comptoir.** — `src/Pharma.Api/Securite/AutorisationParCode.cs`
- **Si le valideur n'a pas le droit demandé, le refus le dit clairement.** : « … n'a pas ce droit : choisissez un employé qui l'a ». — `src/Pharma.Api/Securite/AutorisationParCode.cs`
- **Le poste propose uniquement les employés actifs qui ont un code comptoir et le droit demandé, jamais l'employé qui demande.** — `src/Pharma.Application/Securite/ConnexionComptoir.cs`
- **Remise au-delà du maximum de l'employé : le valideur doit avoir un maximum qui couvre ce pourcentage** ; sinon « … peut accorder jusqu'à N % : choisissez un employé dont le maximum le permet ». — `src/Pharma.Api/Securite/AutorisationParCode.cs`, `src/Pharma.Api/Securite/PermissionsComptoir.cs`
- **L'autorisation sur place est inscrite au journal (droit, action, nom du valideur), et l'action elle-même porte « autorisé par ».** : action `autorisation_code`. — `src/Pharma.Api/Securite/AutorisationParCode.cs`
- **Un employé au mot de passe provisoire ne peut pas bénéficier d'une autorisation sur place.** — `src/Pharma.Api/Securite/PermissionsComptoir.cs`

## Administration

### Paramètres de la pharmacie

- **Modifier l'identité de la pharmacie (nom, titulaire, adresse, téléphone, NIF, n° Chifa, RC, NIS, article d'imposition) demande le droit `admin.parametres`.** — `src/Pharma.Api/Endpoints/AdminEndpoints.cs`
- **Tout employé connecté peut lire l'identité de la pharmacie (nom dans l'en-tête des postes).** — `src/Pharma.Api/Endpoints/AdminEndpoints.cs`
- **Chaque modification de l'identité est inscrite au journal avec les valeurs avant et après.** : action `pharmacie_modifiee`. — `src/Pharma.Application/Administration/Pharmacie.cs`
- **Les paramètres de caisse, les seuils d'alerte et les seuils de stock demandent le droit `admin.parametres` et sont inscrits au journal (avant / après).** : actions `parametres_caisse`, `seuils_alerte`, `seuil_stock`. — `src/Pharma.Application/Administration/Seuils.cs`

### Langue

- **Seul le français est disponible pour l'instant ; l'arabe et l'anglais sont refusés à l'enregistrement.** : la base accepte `ar`, `fr`, `en`, mais l'application n'accepte que `fr`. — `src/Pharma.Application/Administration/Pharmacie.cs`

### Impression

- **Les imprimantes (tickets, étiquettes) et l'afficheur client se règlent sur chaque poste, pas sur le serveur.** : fichier `C:\ProgramData\PharmaOfficine\poste.json` du PC ; aucun droit serveur n'est vérifié pour ce réglage (à confirmer). — `src/Pharma.Desktop/Services/ReglagesPoste.cs`
- **Tickets et reçus ne sont jamais envoyés à une imprimante « fichier » par défaut** (Microsoft Print to PDF, XPS, OneNote, port FILE: ou PORTPROMPT:) : sans imprimante de tickets choisie pour le poste, si l'imprimante par défaut de Windows en est une, rien n'est imprimé (une fenêtre « Enregistrer » bloquerait le comptoir) et l'écran le dit, avec le chemin du réglage ; l'écran Imprimantes et étiquettes l'avertit aussi. Une imprimante « fichier » choisie exprès dans le réglage est respectée, et l'écran Imprimantes et étiquettes prévient qu'elle demandera où enregistrer chaque ticket. Les autres impressions ne sont pas concernées : les documents (bons, relevés, rapports, factures, registre) s'ouvrent en PDF dans la visionneuse de Windows, les étiquettes partent à l'imprimante thermique ou en planche PDF. — `src/Pharma.Desktop/Services/ImprimanteTickets.cs`

### Journal des actions (consultation)

- **Consulter le journal des actions demande le droit `admin.parametres`.** — `src/Pharma.Api/Endpoints/AdminEndpoints.cs`
- **Le journal se filtre par période, par employé et par catégorie d'actions** : « Modifications » (prix, fiches produit et patient, comptes et droits, réglages) ou « Autorisations par code et blocages » ; une catégorie inconnue est refusée. Du plus récent au plus ancien, par pages de 5 à 200 lignes (10 par défaut). — `src/Pharma.Application/Commun/IJournal.cs`, `src/Pharma.Application/Caisse/Sessions.cs`, `src/Pharma.Application/Commun/Pagination.cs`
- **Les modifications sont affichées « champ : ancienne valeur → nouvelle valeur ».** — `src/Pharma.Desktop/Services/Libelles.cs`
- **Un compte modifié garde au journal son rôle et son état d'avant** (`role_avant`, `actif_avant`, et le nom s'il change). — `src/Pharma.Application/Administration/Utilisateurs.cs`
- **Une période inversée est refusée.** — `src/Pharma.Application/Caisse/Sessions.cs`

## Journal et audit

- **Les actions sensibles sont inscrites dans un journal : employé, action, date, détails, droit utilisé et « autorisé par ».** — `src/Pharma.Application/Commun/IJournal.cs`
- **Le journal des actions ne peut être ni modifié ni supprimé.** : déclencheur `trg_journal_immuable`. — `database/migration/006_droits_utilisateurs.sql`
- **« Autorisé par » n'est rempli que si la personne qui autorise est différente de celle qui agit.** — `src/Pharma.Infrastructure/Securite/JournalSql.cs`
- **Le journal des sauvegardes ne peut être ni modifié ni supprimé.** : déclencheur `trg_journal_sauvegarde_immuable`. — `database/migration/014_sauvegardes.sql`
- **Un refus pour règle métier ou droit manquant est renvoyé au poste avec un message clair, sans être traité comme une panne.** — `src/Pharma.Api/Securite/ErreursMetier.cs`

## Super administrateur (hors base)

### Compte

- **Le compte super admin est hors de la base : dans un fichier chiffré du PC serveur, illisible sur un autre PC.** : chiffrement Windows en portée machine ; fichier accessible seulement au système, aux administrateurs et au compte qui l'a créé. — `src/Pharma.Infrastructure/Securite/CompteSuperAdminFichier.cs`
- **Le compte se crée ou se modifie uniquement sur le PC serveur, par la commande « Pharma.Api.exe --super-admin ».** — `src/Pharma.Api/Securite/ConsoleSuperAdmin.cs`
- **Identifiant d'au moins 4 caractères, mot de passe d'au moins 12 caractères confirmé à l'identique.** — `src/Pharma.Application/SuperAdmin/SuperAdmin.cs`
- **Un téléphone mobile algérien (05, 06, 07 + 8 chiffres, ou +213) et un e-mail sont obligatoires.** : l'e-mail sert de secours si le SMS ne part pas. — `src/Pharma.Application/SuperAdmin/SuperAdmin.cs`
- **Pour modifier un compte existant, il faut le mot de passe actuel et un code envoyé au téléphone actuel.** — `src/Pharma.Api/Securite/ConsoleSuperAdmin.cs`
- **Un nouveau numéro de téléphone doit être vérifié par un code reçu par SMS sur ce numéro (jamais par e-mail).** : 3 essais de saisie. — `src/Pharma.Api/Securite/ConsoleSuperAdmin.cs`
- **À la création, si le SMS ne peut pas partir (modem absent), le compte peut être créé quand même après confirmation explicite.** — `src/Pharma.Api/Securite/ConsoleSuperAdmin.cs`
- **Le téléphone et l'e-mail ne sont jamais affichés en entier (ex. 06•••••512, t•••@exemple.dz).** — `src/Pharma.Domain/Notifications/RegleNotifications.cs`

### Connexion et périmètre

- **Le super admin se connecte avec son identifiant et son mot de passe ; un refus ne dit pas pourquoi.** — `src/Pharma.Application/SuperAdmin/SuperAdmin.cs`
- **Après 5 mots de passe faux, la connexion du super admin est bloquée 5 minutes.** — `src/Pharma.Application/SuperAdmin/SuperAdmin.cs`
- **La session du super admin dure 1 heure.** — `src/Pharma.Api/Securite/JetonService.cs`
- **Le poste déconnecte le super admin après 10 minutes sans activité.** — `src/Pharma.Desktop/Services/ApiClient.cs`
- **Le super admin n'a accès qu'à la restauration et à la reprise de données, jamais aux écrans des employés.** — `src/Pharma.Api/Securite/PermissionFilter.cs`
- **Les connexions, refus et validations du super admin sont notés dans le journal du serveur.** — `src/Pharma.Api/Endpoints/SuperAdminEndpoints.cs`

### Double validation (mot de passe + code)

- **Avant chaque action sensible, le super admin ressaisit son mot de passe, puis reçoit un code à 6 chiffres.** — `src/Pharma.Application/SuperAdmin/SuperAdmin.cs`
- **Actions concernées : extraire, charger ou annuler une reprise, restaurer la base, restaurer la GED, changer le téléphone du super admin.** — `src/Pharma.Application/SuperAdmin/SuperAdmin.cs`
- **Le code part par SMS (modem GSM), sinon par e-mail.** — `src/Pharma.Application/SuperAdmin/SuperAdmin.cs`
- **Le code part même si la base est détruite, grâce à une copie des réglages d'envoi gardée avec le compte.** : la copie n'est remplacée par les réglages de la base qu'après un envoi réussi avec eux. — `src/Pharma.Application/SuperAdmin/SuperAdmin.cs`
- **Le code vaut 5 minutes, ne sert qu'une fois et seulement pour l'action demandée.** — `src/Pharma.Application/SuperAdmin/SuperAdmin.cs`
- **Après 3 codes faux, les validations sont bloquées 15 minutes et les demandes en cours sont annulées.** — `src/Pharma.Application/SuperAdmin/SuperAdmin.cs`
- **Les codes sont gardés en mémoire du serveur, jamais en base.** — `src/Pharma.Application/SuperAdmin/SuperAdmin.cs`
- **Le texte du code rappelle de ne le donner à personne.** — `src/Pharma.Application/SuperAdmin/SuperAdmin.cs`
- **Si aucun code ne peut partir, l'action est impossible ; le message renvoie aux réglages du modem et de l'e-mail, ou au script de restauration du PC serveur.** — `src/Pharma.Application/SuperAdmin/SuperAdmin.cs`

## Sauvegardes et restauration

### Sauvegardes

- **Gérer les sauvegardes demande le droit `admin.sauvegardes`.** — `src/Pharma.Api/Endpoints/NotificationsEndpoints.cs`
- **La sauvegarde automatique est désactivée par défaut ; elle se lance une fois par jour, après l'heure réglée (22:00 par défaut).** — `database/migration/014_sauvegardes.sql`
- **On ne peut pas activer la sauvegarde sans avoir choisi un dossier (chemin complet, ex. E:\SauvegardesPharmacie).** — `src/Pharma.Application/Sauvegardes/Sauvegardes.cs`
- **Une sauvegarde peut aussi être lancée à la main (« Sauvegarder maintenant »).** — `src/Pharma.Api/Endpoints/NotificationsEndpoints.cs`
- **Le nombre de sauvegardes gardées sur le disque externe va de 1 à 365 (14 par défaut) ; les plus anciennes sont supprimées.** — `src/Pharma.Application/Sauvegardes/Sauvegardes.cs`
- **Chaque sauvegarde réussie est aussi copiée dans la GED du serveur, gardée de 1 à 365 jours (7 par défaut).** — `src/Pharma.Application/Sauvegardes/Sauvegardes.cs`
- **Les documents de la GED (nouveaux et modifiés seulement) sont copiés sur le disque externe avec la base ; rien n'y est supprimé.** — `src/Pharma.Application/Sauvegardes/Sauvegardes.cs`
- **Disque externe absent : une copie de secours de la base est quand même faite dans la GED du serveur.** — `src/Pharma.Application/Sauvegardes/Sauvegardes.cs`
- **Disque absent ou sauvegarde échouée : le titulaire est prévenu (SMS et e-mail).** — `src/Pharma.Application/Sauvegardes/Sauvegardes.cs`
- **Chaque sauvegarde est inscrite au journal des sauvegardes : réussie, échouée ou disque absent ; automatique ou manuelle.** Le journal se consulte en entier, par page, filtré par résultat et période (auparavant les 30 dernières seulement). — `src/Pharma.Application/Sauvegardes/Sauvegardes.cs`
- **Les documents de la GED ne sont jamais supprimés, sauf si le titulaire règle une durée de nettoyage (1 à 100 ans).** — `src/Pharma.Application/Sauvegardes/Sauvegardes.cs`
- **Chaque changement des réglages de sauvegarde est inscrit au journal.** : action `reglages_sauvegarde`. — `src/Pharma.Application/Sauvegardes/Sauvegardes.cs`
- **Le dossier des sauvegardes est recopié avec le compte du super admin, pour être retrouvé même si la base est détruite.** — `src/Pharma.Application/Sauvegardes/Sauvegardes.cs`

### Restauration (super admin)

- **Seul le super admin peut restaurer la base ou la GED, après double validation.** — `src/Pharma.Application/SuperAdmin/Restauration.cs`
- **On ne peut restaurer qu'une sauvegarde proposée dans la liste (disque externe ou GED du serveur).** — `src/Pharma.Application/SuperAdmin/Restauration.cs`
- **Une seule restauration à la fois.** — `src/Pharma.Application/SuperAdmin/Restauration.cs`
- **Le fichier est vérifié (lisible et complet) avant la restauration.** — `src/Pharma.Infrastructure/Sauvegardes/RestaurationPostgres.cs`
- **Avant de restaurer, une copie de sécurité de la base actuelle est faite dans la GED du serveur.** — `src/Pharma.Application/SuperAdmin/Restauration.cs`
- **La base actuelle n'est jamais supprimée : elle est gardée sous le nom « …_avant_restauration_AAAAMMJJ_HHMM ».** — `src/Pharma.Infrastructure/Sauvegardes/RestaurationPostgres.cs`
- **Une sauvegarde ancienne est mise à la version du logiciel pendant la restauration.** — `src/Pharma.Infrastructure/Sauvegardes/RestaurationPostgres.cs`
- **Un échec avant la dernière étape ne touche pas la base en service.** — `src/Pharma.Infrastructure/Sauvegardes/RestaurationPostgres.cs`
- **Pendant la restauration de la base, les postes sont mis en attente : seules les routes du super admin répondent.** — `src/Pharma.Api/Program.cs`
- **Après restauration, les opérations faites après la date de la sauvegarde sont à ressaisir, et les employés se reconnectent.** — `src/Pharma.Application/SuperAdmin/Restauration.cs`
- **La restauration de la GED recopie les documents du disque externe sans rien supprimer et sans remplacer un document plus récent.** — `src/Pharma.Application/SuperAdmin/Restauration.cs`
- **La restauration est inscrite au journal de la base, au nom du titulaire actif, avec le super admin et le canal du code de validation.** : actions `restauration_base`, `restauration_ged`. — `src/Pharma.Application/SuperAdmin/Restauration.cs`

## GED (documents)

- **La GED est un dossier du serveur (par défaut C:\ProgramData\PharmaOfficine\GED), rangé par type de document.** — `src/Pharma.Application/Ged/Ged.cs`
- **Types acceptés depuis les postes : Factures, Rapports, Clotures, Registre, Commandes, Retours, Etiquettes, Inventaires, Credits.** : un autre type est refusé. — `src/Pharma.Application/Ged/Ged.cs`
- **La GED contient aussi les ordonnances scannées, les fichiers Chifa et les copies des sauvegardes.** — `src/Pharma.Application/Ged/Ged.cs`
- **Un document vide ou de plus de 30 Mo est refusé.** — `src/Pharma.Application/Ged/Ged.cs`
- **Le nom du document est nettoyé (sans chemin ni caractère interdit) et limité à 150 caractères.** — `src/Pharma.Application/Ged/Ged.cs`
- **Un document n'est jamais écrasé : même nom et même contenu, rien n'est ajouté ; même nom et contenu différent, l'ancienne version est rangée, datée, dans « Versions ».** — `src/Pharma.Infrastructure/Ged/GedFichiers.cs`
- **Tout employé connecté peut déposer un document dans la GED.** — `src/Pharma.Api/Endpoints/GedEndpoints.cs`

## Reprise de données (import depuis l'ancien logiciel)

- **Seul le super admin fait la reprise de données.** — `src/Pharma.Application/Reprise/Reprise.cs`
- **Extraire, charger et annuler une reprise demandent chacun la double validation (mot de passe + code).** — `src/Pharma.Application/Reprise/Reprise.cs`
- **L'ancienne base est lue en lecture seule.** — `src/Pharma.Application/Reprise/Reprise.cs`
- **À l'écran, les exemples de données personnelles (noms de patients, téléphones, adresses, n° d'assuré…) sont masqués.** — `src/Pharma.Domain/Reprise/DetectionReprise.cs`
- **La correspondance des tables et colonnes est seulement proposée : elle doit être confirmée ou corrigée par une personne.** — `src/Pharma.Domain/Reprise/DetectionReprise.cs`
- **La table des produits est indispensable ; une seule table par type de données (produits, stock, grossistes, clients).** — `src/Pharma.Application/Reprise/Reprise.cs`
- **Les champs obligatoires doivent avoir leur colonne : identifiant et désignation du produit ; produit et quantité du stock ; identifiant et raison sociale du grossiste ; identifiant et nom du client.** — `src/Pharma.Domain/Reprise/DetectionReprise.cs`
- **Le nom et la version de l'ancien logiciel sont obligatoires (rapport de reprise).** — `src/Pharma.Application/Reprise/Reprise.cs`
- **Un produit sans correspondance sûre doit être confirmé, rapproché d'un autre produit, ou créé.** — `src/Pharma.Application/Reprise/Reprise.cs`
- **Le chargement est refusé tant qu'il reste une anomalie bloquante.** : ex. nombre ambigu (« 12.500 »), quantité illisible ou négative, stock d'un produit absent, doublon de produit à créer, solde illisible. — `database/migration/048_reprise_corrections.sql`
- **Un nombre ambigu n'est jamais deviné : il faut le corriger dans le fichier source puis recharger.** — `database/migration/048_reprise_corrections.sql`
- **Un lot déjà périmé est importé et bloqué à la vente.** — `database/migration/048_reprise_corrections.sql`
- **Une date de péremption absente est signalée, à vérifier à l'inventaire de démarrage.** — `database/migration/048_reprise_corrections.sql`
- **Un produit créé absent de la nomenclature nationale est « à vérifier » et non vendable tant que le titulaire n'a pas confirmé sa classe.** — `database/migration/048_reprise_corrections.sql`
- **La classe réglementaire retenue est la plus stricte entre la nomenclature et l'ancien logiciel.** — `database/migration/048_reprise_corrections.sql`
- **Le chargement se fait en une seule fois (tout ou rien), puis les totaux sont comparés entre l'ancienne et la nouvelle base.** — `src/Pharma.Application/Reprise/Reprise.cs`
- **Une reprise déjà chargée ou annulée ne peut plus être modifiée, chargée ni annulée.** — `src/Pharma.Application/Reprise/Reprise.cs`
- **Les données chargées (stock, registre des psychotropes…) sont attribuées au pharmacien titulaire ; la reprise garde qui l'a lancée et quand.** — `database/migration/052_reprise_super_admin.sql`
- **Extraction, chargement et annulation sont inscrits au journal avec le super admin et le canal du code de validation.** : actions `reprise_extraction`, `reprise_chargement`, `reprise_annulation`. — `src/Pharma.Application/Reprise/Reprise.cs`

## Essai (retours du pharmacien)

- **Tout employé connecté peut laisser une remarque : problème, suggestion ou question.** — `src/Pharma.Application/Essai/RetoursEssai.cs`
- **La remarque est obligatoire et limitée à 4 000 caractères ; l'écran concerné est noté (« Général » par défaut).** — `src/Pharma.Application/Essai/RetoursEssai.cs`
- **Les remarques sont inscrites au journal des actions.** : action `retour_essai`. — `src/Pharma.Application/Essai/RetoursEssai.cs`
- **Chaque employé voit ses propres remarques ; le titulaire les voit toutes (500 dernières au plus).** — `src/Pharma.Application/Essai/RetoursEssai.cs`

## Notifications au titulaire

### Accès et réglages

- **Les notifications (réglages, destinataires, test, file des messages) sont réservées au pharmacien titulaire.** : aucun droit de la matrice ne suffit. — `src/Pharma.Application/Notifications/Notifications.cs`
- **Deux canaux : e-mail par le serveur SMTP de la pharmacie, SMS par un modem GSM branché sur le serveur ; aucun service cloud.** — `database/migration/013_notifications.sql`
- **Le mot de passe SMTP est chiffré, lisible seulement sur le PC serveur qui l'a enregistré, et n'est jamais renvoyé aux postes.** — `src/Pharma.Application/Notifications/Notifications.cs`
- **Serveur SMTP et adresse de l'expéditeur se renseignent ensemble ou pas du tout ; port SMTP entre 1 et 65535.** — `src/Pharma.Application/Notifications/Notifications.cs`
- **Le port du modem a la forme COMn (ex. COM3) ; vide, aucun SMS n'est envoyé.** — `src/Pharma.Application/Notifications/Notifications.cs`
- **Chaque modification des réglages est inscrite au journal.** : action `reglages_notification`. — `src/Pharma.Application/Notifications/Notifications.cs`

### Destinataires

- **Seules les personnes enregistrées par le titulaire reçoivent des messages, et seulement une fois leur adresse vérifiée.** — `src/Pharma.Domain/Notifications/RegleNotifications.cs`
- **Le message de test est le seul qui part vers une adresse non vérifiée ; le titulaire marque ensuite le destinataire « vérifié ».** — `src/Pharma.Application/Notifications/Notifications.cs`
- **Changer l'e-mail ou le téléphone d'un destinataire retire sa vérification.** — `src/Pharma.Application/Notifications/Notifications.cs`
- **Un destinataire désactivé ne reçoit plus rien.** — `src/Pharma.Domain/Notifications/RegleNotifications.cs`
- **La base refuse tout message vers un destinataire désactivé, non vérifié (sauf test) ou à une autre adresse que celle enregistrée.** : déclencheur `trg_message_destinataire`. — `database/migration/013_notifications.sql`
- **Un destinataire a un nom, au moins un canal, une adresse valide pour chaque canal choisi, et au moins un type de message.** : mobile algérien 05, 06, 07 + 8 chiffres, ou +213. — `src/Pharma.Domain/Notifications/RegleNotifications.cs`
- **Chaque ajout ou modification de destinataire est inscrit au journal.** : action `destinataire_notification`. — `src/Pharma.Application/Notifications/Notifications.cs`

### Messages

- **Types de messages : rapport quotidien, écart de caisse, lot bloqué, sauvegarde, écart d'inventaire, ancien lot délaissé, anomalie de caisse (surveillance).** — `src/Pharma.Domain/Notifications/RegleNotifications.cs`
- **Le rapport quotidien part une seule fois par jour, après l'heure réglée (21:00 par défaut), par e-mail seulement.** — `src/Pharma.Application/Notifications/Notifications.cs`
- **Le rapport quotidien contient : chiffre du jour, part tiers payant, annulations, écarts de caisse par employé, nouvelles alertes de péremption et ruptures.** — `src/Pharma.Application/Notifications/Notifications.cs`
- **L'alerte d'écart de caisse ne part que si l'écart dépasse le seuil SMS réglé, sinon le seuil d'écart de la caisse.** — `src/Pharma.Application/Notifications/Notifications.cs`
- **Le SMS de lot bloqué, de lot délaissé et de surveillance dépend de la case « SMS lot bloqué » ; celui de sauvegarde de la case « SMS sauvegarde » ; l'e-mail part toujours.** — `src/Pharma.Application/Notifications/Notifications.cs`
- **Les SMS sont sans accents et limités à 160 caractères (un seul SMS).** — `src/Pharma.Domain/Notifications/RegleNotifications.cs`
- **Un message non envoyé est réessayé après 1, 5, 15 puis 60 minutes, ensuite toutes les heures ; au-delà de 7 jours il est abandonné.** — `src/Pharma.Domain/Notifications/RegleNotifications.cs`
- **La file est traitée toutes les 30 secondes, 50 messages au plus à la fois, les SMS avant les e-mails.** — `src/Pharma.Infrastructure/Notifications/NotificationsSql.cs`
- **L'écran de la file affiche les 100 derniers messages.** — `src/Pharma.Application/Notifications/Notifications.cs`

### Confidentialité

- **Aucune donnée de patient ni d'ordonnance dans un e-mail ou un SMS : chiffres, noms d'employés et de postes, produits et numéros de lot seulement.** — `src/Pharma.Application/Notifications/Notifications.cs`
- **Le motif d'un rappel de lot (saisi librement) n'est jamais envoyé ; le message renvoie au logiciel pour la liste des patients à contacter.** — `src/Pharma.Application/Notifications/Notifications.cs`
- **Le rapport quotidien est construit à partir de données agrégées, sans colonne patient.** — `src/Pharma.Application/Notifications/Notifications.cs`

## Contrôle interne (caisse et stock)

### Accès réservé au titulaire
- **Seul le titulaire consulte les anomalies du contrôle interne** : l'écran et l'API `/surveillance` exigent le rôle titulaire (marque `role.titulaire`). — `src/Pharma.Application/Surveillance/Surveillance.cs`, `src/Pharma.Api/Endpoints/SurveillanceEndpoints.cs`
- **Seul le titulaire traite une anomalie** (la marque « traitée »). — `src/Pharma.Application/Surveillance/Surveillance.cs`
- **Seul le titulaire lit et modifie les réglages du contrôle interne** (seuils, horaires, fréquence du comptage des produits sensibles). Ils sont dans Administration › Contrôle interne (`admin/controle-interne`), groupés en Caisse (avec les horaires d'ouverture), Stock et réception (avec la réception à l'aveugle), Crédits et accès ; l'écran Contrôle interne ne garde que les anomalies, les écarts et risques et les produits sensibles. — `src/Pharma.Application/Surveillance/Surveillance.cs`
- **Seul le titulaire consulte et choisit les produits sensibles.** — `src/Pharma.Application/Surveillance/Surveillance.cs`
- **Le rôle titulaire n'est pas reconnu pour une connexion par code comptoir, ni tant que le mot de passe doit être changé** : sauf autorisation « sur place » (mécanisme `AutoriseSurPlaceAsync`, détail à confirmer). — `src/Pharma.Api/Securite/PermissionsComptoir.cs`

### Événements de caisse (ce qui se passe au comptoir sans laisser de vente)
- **Trois types d'événements sont enregistrés** : ligne retirée du panier (`ligne_retiree`), tiroir ouvert sans vente (`tiroir_ouvert`), vente en attente abandonnée (`attente_abandonnee`). Un panier non encaissé ni mis en attente, perdu en quittant l'écran de caisse, est signalé comme lignes retirées. — `database/migration/054_surveillance_caisse.sql`
- **Chaque événement garde l'employé, le poste, la caisse (session) du poste, et l'heure** ; selon le type : produit, lot, nombre de boîtes, montant, détail. — `database/migration/054_surveillance_caisse.sql`
- **Le poste ne peut signaler lui-même que deux types : ligne retirée et tiroir ouvert** : tout autre type est refusé (« Événement de caisse inconnu »). — `src/Pharma.Application/Surveillance/Surveillance.cs`
- **Signaler un événement demande le droit `caisse.vendre`.** — `src/Pharma.Api/Endpoints/SurveillanceEndpoints.cs`
- **Ouvrir le tiroir sans vente demande en plus le droit `caisse.ouvrir_tiroir`** : sans lui, l'ouverture est refusée. — `src/Pharma.Application/Surveillance/Surveillance.cs`
- **Chaque ouverture du tiroir sans vente est inscrite au journal des actions** (action `tiroir_ouvert`, avec le poste). — `src/Pharma.Application/Surveillance/Surveillance.cs`
- **Une ligne retirée doit indiquer le produit et un nombre de boîtes supérieur à 0** : sinon elle est refusée. — `src/Pharma.Application/Surveillance/Surveillance.cs`
- **Un montant négatif d'événement n'est pas enregistré** (montant laissé vide). — `src/Pharma.Application/Surveillance/Surveillance.cs`
- **La caisse rattachée à l'événement est la session ouverte sur le poste de l'employé** (aucune si le poste n'est pas indiqué). — `src/Pharma.Application/Surveillance/Surveillance.cs`
- **Supprimer une vente mise en attente enregistre une « vente en attente abandonnée »** : avec son montant total et son numéro. — `src/Pharma.Application/Caisse/VentesEnAttente.cs`

### Déclenchement du calcul des anomalies
- **Le calcul porte toujours sur la journée en cours, selon la date du serveur PostgreSQL** (celle à laquelle les ventes sont horodatées). — `src/Pharma.Application/Surveillance/Surveillance.cs`
- **Le calcul est relancé après** : chaque vente encaissée, chaque événement de caisse (ligne retirée, tiroir, attente abandonnée), chaque fin d'inventaire, chaque écart d'inventaire validé, chaque déclaration de casse ou d'échantillon (pas pour un périmé), chaque facture saisie sur un comptage (avec ou sans écart), chaque avoir inférieur au montant attendu, chaque connexion réussie (mot de passe ou code), chaque baisse de prix depuis la fiche produit. — `src/Pharma.Application/Caisse/Encaisser.cs`, `src/Pharma.Application/Caisse/VentesEnAttente.cs`, `src/Pharma.Application/Stock/Inventaire.cs`, `src/Pharma.Application/Stock/SortieStock.cs`
- **Le calcul ne bloque jamais la vente ni l'opération** : elle est déjà enregistrée ; une erreur du contrôle interne est inscrite au journal du serveur. — `src/Pharma.Application/Surveillance/Surveillance.cs`
- **Les journées et les horaires sont ceux de l'heure du serveur PostgreSQL** : son fuseau doit être réglé sur l'heure de la pharmacie. — `database/migration/054_surveillance_caisse.sql`
- **Le calcul est aussi lancé au démarrage du serveur puis toutes les heures**, même sans activité (retour sans avoir, crédit jamais remboursé, comptage des produits sensibles en retard). — `src/Pharma.Application/Surveillance/Surveillance.cs`

### Types d'anomalies

| Type (code) | Ce qui la déclenche | Seuil par défaut | Niveau « urgent » | Réglage |
|---|---|---|---|---|
| Lignes retirées (`lignes_retirees`) | Boîtes retirées du panier avant paiement, par employé et par jour (somme des boîtes, 1 si non indiqué) | 5 boîtes | à partir de 10 boîtes (2 × seuil) | `seuil_lignes_retirees` (≥ 1) |
| Tiroir (`tiroir`) | Ouvertures du tiroir sans vente, par employé et par jour | 3 ouvertures | à partir de 6 | `seuil_tiroir` (≥ 1) |
| Attentes abandonnées (`attentes_abandonnees`) | Ventes en attente supprimées, par employé et par jour | 3 | à partir de 6 | `seuil_attentes_abandonnees` (≥ 1) |
| Hors horaires (`hors_horaires`) | Au moins une vente validée encaissée avant l'ouverture ou à partir de l'heure de fermeture, par employé et par jour | aucun seuil : dès 1 vente | toujours urgent | `heure_ouverture` / `heure_fermeture` (vides : pas de contrôle) |
| Remises (`remises`) | Ventes validées avec remise, par employé et par jour | 5 ventes | à partir de 10 | `seuil_remises` (≥ 1) |
| Remises au même patient (`remises_patient`) | Ventes validées avec remise au même patient sur 30 jours glissants (tous vendeurs) | 3 ventes | à partir de 6 | `seuil_remises_patient` (≥ 1) |
| Ajustements d'inventaire (`ajustements`) | Boîtes manquantes ajustées en inventaire (stock diminué), par employé qui a terminé ou validé, par jour | 5 boîtes | à partir de 10 | `seuil_ajustements` (≥ 1) |
| Casses (`casses`) | Boîtes déclarées cassées, par employé et par jour | 3 boîtes | à partir de 6 | `seuil_casses` (≥ 1) |
| Échantillons (`echantillons`) | Boîtes sorties en échantillon ou gratuité, par employé et par jour | 3 boîtes | à partir de 6 | `seuil_echantillons` (≥ 1) |
| Boîtes fantômes (`boites_fantomes`) | Inventaire terminé ce jour : boîtes identifiées en stock, d'un lot scanné, jamais scannées pendant ce comptage | aucun seuil : dès 1 boîte | à partir de 3 boîtes (valeur fixe, non réglable) | aucun |
| Produits sensibles en retard (`sensibles_en_retard`) | Comptage « Produits sensibles » jamais fait ou plus ancien que la fréquence réglée | 7 jours | jamais (toujours « attention ») | `jours_comptage_sensibles` (1 à 90) |
| Écart de réception (`ecart_reception`) | Facture saisie ce jour qui ne correspond pas au comptage (rattachée à celui qui a compté) | aucun seuil : dès 1 boîte | s'il manque des boîtes | `reception_aveugle` |
| Comptage et facture par la même personne (`reception_meme_personne`) | Livraisons comptées puis facturées par le même employé, par jour | aucun seuil : dès 1 livraison | jamais | `reception_aveugle` |
| Retour sans avoir (`retour_sans_avoir`) | Retour fournisseur « envoyé » sans avoir depuis le délai réglé | 30 jours | à partir de 60 jours (2 × délai) | `jours_avoir_retour` (1 à 365) |
| Avoir incomplet (`avoir_incomplet`) | Avoir reçu ce jour inférieur au montant attendu | dès 0,01 DA | jamais | aucun |
| Patient récent avec une grosse dette (`credit_nouveau_patient`) | Patient créé depuis moins de 7 jours (réglable) dont la dette atteint le seuil, crédit accordé ce jour ; rattachée à qui l'a créé | 5 000 DA | à partir de 10 000 DA | `seuil_credit_nouveau_patient`, `jours_patient_recent` (1 à 90) |
| Crédits accordés par un employé (`credits_employe`) | Montant des ventes à crédit accordées par un employé dans la journée | 20 000 DA | à partir de 40 000 DA | `seuil_credits_jour` (> 0) |
| Crédit jamais remboursé (`credit_sans_remboursement`) | Patient endetté sans aucun remboursement depuis son premier crédit, au-delà du délai ; cite les employés qui ont accordé les crédits | 60 jours | jamais | `jours_credit_sans_remboursement` (1 à 365) |
| Autorisations par code, qui autorise (`autorisations_valideur`) | Actions autorisées sur place par le code comptoir d'un employé, par jour | 10 | à partir de 20 | `seuil_autorisations_valideur` (≥ 1) |
| Autorisations par code, qui en profite (`autorisations_employe`) | Autorisations sur place dont un employé a eu besoin, par jour (droits et valideurs cités) | 5 | à partir de 10 | `seuil_autorisations_employe` (≥ 1) |
| Employé sur deux postes à la fois (`postes_simultanes`) | Passages d'un poste à l'autre (connexions et ventes) en moins de 10 minutes (réglable), par employé et par jour | 3 passages | à partir de 6 | `seuil_changements_poste` (≥ 1), `minutes_postes_simultanes` (1 à 120) |
| Baisses de prix (`baisses_prix`) | Prix de vente baissés par un autre que le titulaire (fiche produit ou PPA de réception), par jour ; cite produit, ancien et nouveau prix | 1 | à partir de 2 | `seuil_baisses_prix` (≥ 1) |

### Règles communes des anomalies
- **Deux niveaux existent : « attention » et « urgent ».** — `database/migration/054_surveillance_caisse.sql`
- **Une anomalie à seuil devient « urgente » quand la valeur atteint le double du seuil** (sauf hors horaires, boîtes fantômes, produits sensibles et anomalies des achats, voir le tableau). — `database/migration/055_surveillance_stock.sql`
- **Le seuil est atteint dès que la valeur est égale au seuil** (comparaison « supérieur ou égal »). — `database/migration/055_surveillance_stock.sql`
- **Une seule anomalie par type, par employé, par jour** (et par patient, par inventaire ou par date de dernier comptage selon le type). — `database/migration/054_surveillance_caisse.sql`
- **Une anomalie existante est mise à jour avec les chiffres du jour** (message, valeur, niveau) à chaque nouveau calcul. — `database/migration/054_surveillance_caisse.sql`
- **Une anomalie déjà traitée reste traitée** si ses chiffres augmentent, **sauf si elle devient urgente** : elle revient alors « à examiner » (le traitement précédent est gardé dans l'action prise). — `database/migration/054_surveillance_caisse.sql`
- **Une anomalie n'est jamais supprimée.** — `database/migration/054_surveillance_caisse.sql`
- **Les valeurs en dinars des casses, échantillons, ajustements et boîtes fantômes sont calculées au prix d'achat HT du lot.** — `database/migration/055_surveillance_stock.sql`

### Détail par type
- **Lignes retirées : le seuil compte les boîtes, pas les lignes** ; le message donne aussi le nombre de lignes et le montant retiré. — `database/migration/055_surveillance_stock.sql`
- **Attentes abandonnées : le message donne le montant total abandonné.** — `database/migration/055_surveillance_stock.sql`
- **Hors horaires : seules les ventes validées comptent** ; le message donne le nombre, le montant et l'heure de la première et de la dernière vente hors horaires. — `database/migration/055_surveillance_stock.sql`
- **Hors horaires : une vente à l'heure exacte de fermeture est hors horaires** ; une vente à l'heure exacte d'ouverture ne l'est pas. — `database/migration/055_surveillance_stock.sql`
- **Remises : seules les ventes validées avec un pourcentage de remise supérieur à 0 comptent.** — `database/migration/055_surveillance_stock.sql`
- **Remises au même patient : la période couvre le jour du calcul et les 29 jours précédents** (30 jours). — `database/migration/055_surveillance_stock.sql`
- **Remises au même patient : une seule anomalie par patient et par mois civil** (rattachée au 1er du mois), et pas de nouvelle anomalie si une autre a été créée pour ce patient dans les 30 jours (série à cheval sur deux mois) ; elle n'est rattachée à aucun employé et cite les vendeurs. — `database/migration/055_surveillance_stock.sql`
- **Ajustements : seuls les ajustements d'inventaire qui diminuent le stock comptent**, attribués à l'employé qui a terminé ou validé l'inventaire. — `database/migration/055_surveillance_stock.sql`
- **Boîtes fantômes : le contrôle porte sur les inventaires terminés le jour du calcul.** — `database/migration/055_surveillance_stock.sql`
- **Boîtes fantômes : seuls les lots comptés uniquement en scannant leurs boîtes identifiées sont contrôlés** (autant de boîtes vues que de boîtes comptées) ; un lot compté à la main, ou par un code qui ne désigne pas une boîte, n'a pas de boîtes fantômes. — `database/migration/055_surveillance_stock.sql`
- **Boîtes fantômes : seules les boîtes encore « en stock » et entrées avant le début du comptage sont concernées.** — `database/migration/055_surveillance_stock.sql`
- **Boîtes fantômes : une anomalie par inventaire**, rattachée à aucun employé, listant produit, lot et nombre de boîtes ; le message invite à les rechercher puis à faire leur sortie. — `database/migration/055_surveillance_stock.sql`
- **Une boîte n'est comptée qu'une fois par inventaire** ; une boîte se retrouve par son étiquette interne, ou par son GTIN et son n° de série ensemble. — `database/migration/055_surveillance_stock.sql`
- **Produits sensibles en retard : l'anomalie n'apparaît que s'il existe au moins un produit sensible actif.** — `database/migration/055_surveillance_stock.sql`
- **Produits sensibles en retard : retard si aucun comptage « Produits sensibles » n'a été terminé, ou si le dernier date de 7 jours ou plus (fréquence réglée)** : précisément, dernier comptage antérieur à (jour − fréquence + 1). — `database/migration/055_surveillance_stock.sql`
- **Produits sensibles en retard : une anomalie par date de dernier comptage** ; une fois traitée, elle ne réapparaît qu'après un nouveau comptage redevenu en retard (constat sur la clé de l'anomalie). — `database/migration/055_surveillance_stock.sql`

### Notification des anomalies
- **Un destinataire des notifications n'est vérifié que par le code à 6 chiffres qu'il a reçu** (15 minutes, 5 codes faux au plus, seul le haché est gardé) ; vérifier sans code est refusé, retirer la vérification reste possible ; envoi et vérification inscrits au journal. — `src/Pharma.Application/Notifications/VerificationDestinataire.cs`, `database/migration/068_verification_destinataire.sql`
- **Chaque e-mail est signé du nom de la pharmacie.** — `src/Pharma.Application/Notifications/Notifications.cs`
- **Le rapport du soir recalcule les alertes juste avant l'envoi et ne reprend que les alertes encore ouvertes** : une alerte devenue sans objet n'est pas envoyée. — `src/Pharma.Application/Notifications/Notifications.cs`, `src/Pharma.Infrastructure/Notifications/DonneesRapportSql.cs`
- **Chaque nouvelle anomalie est notifiée au titulaire** (e-mail, et SMS selon le réglage). — `src/Pharma.Application/Surveillance/Surveillance.cs`, `src/Pharma.Application/Notifications/Notifications.cs`
- **Une anomalie déjà connue est notifiée une seconde fois quand elle passe de « attention » à « urgent ».** — `database/migration/054_surveillance_caisse.sql`
- **L'objet du message est « URGENT : contrôle interne, point à examiner » ou « Contrôle interne : point à examiner »** (caisse, stock, réception, retours) ; le message renvoie à l'écran Contrôle interne du titulaire. — `src/Pharma.Application/Notifications/Notifications.cs`
- **Le SMS de surveillance n'est envoyé que si le réglage SMS des lots bloqués est activé.** — `src/Pharma.Application/Notifications/Notifications.cs`
- **Le texte notifié ne contient aucune donnée de patient** (le nom du patient n'apparaît que dans l'écran Contrôle interne). — `database/migration/055_surveillance_stock.sql`
- **Crédits suspects : le nom du patient n'apparaît que dans l'écran Contrôle interne**, jamais dans l'e-mail ni le SMS. — `database/migration/058_surveillance_comptes.sql`
- **Les destinataires sont ceux abonnés au type « surveillance »** ; à l'installation, ce type a été ajouté aux destinataires déjà abonnés au type « lot délaissé ». — `database/migration/054_surveillance_caisse.sql`

### Traitement des anomalies
- **Les anomalies sont listées en deux vues : à traiter (500 au plus) et traitées (200 au plus).** — `src/Pharma.Application/Surveillance/Surveillance.cs`
- **Ordre d'affichage : urgentes d'abord, puis du jour le plus récent, puis de la mise à jour la plus récente.** — `src/Pharma.Infrastructure/Surveillance/SurveillanceSql.cs`
- **Traiter une anomalie enregistre qui l'a traitée, quand, et l'action prise (facultative, 300 caractères au plus).** — `src/Pharma.Application/Surveillance/Surveillance.cs`
- **Une anomalie déjà traitée ne peut pas être traitée à nouveau** (« introuvable ou déjà traitée »). — `src/Pharma.Infrastructure/Surveillance/SurveillanceSql.cs`

### Réglages de la surveillance
- **Valeurs par défaut** : lignes retirées 5, tiroir 3, attentes abandonnées 3, remises 5, remises au même patient 3, ajustements 5, casses 3, échantillons 3, comptage des produits sensibles tous les 7 jours, horaires vides, réception à l'aveugle active, retour sans avoir après 30 jours, dette d'un patient récent 5 000 DA (patient de moins de 7 jours), crédits par employé 20 000 DA par jour, crédit jamais remboursé après 60 jours, autorisations par code 10 données et 5 demandées par jour, 3 passages d'un poste à l'autre en moins de 10 minutes, 1 baisse de prix. — `database/migration/054_surveillance_caisse.sql`, `database/migration/055_surveillance_stock.sql`
- **Chaque seuil doit valoir au moins 1.** — `src/Pharma.Application/Surveillance/Surveillance.cs`
- **La fréquence du comptage des produits sensibles va de 1 à 90 jours.** — `src/Pharma.Application/Surveillance/Surveillance.cs`
- **Le délai d'un retour sans avoir va de 1 à 365 jours.** — `src/Pharma.Application/Surveillance/Surveillance.cs`
- **La réception à l'aveugle se désactive dans les réglages (petite équipe)** : la réception redevient une saisie directe en une étape. — `src/Pharma.Application/Achats/Achats.cs`
- **Les horaires se renseignent ensemble (ouverture et fermeture) ou pas du tout** ; vides, les ventes hors horaires ne sont pas contrôlées. — `src/Pharma.Application/Surveillance/Surveillance.cs`
- **L'ouverture et la fermeture doivent être différentes ; une plage peut passer minuit** (08:00-00:30 : une vente entre 00:30 et 08:00 est hors horaires). — `src/Pharma.Application/Surveillance/Surveillance.cs`
- **Les seuils s'appliquent par employé et par jour** (par patient sur 30 jours pour les remises au même patient). — `database/migration/054_surveillance_caisse.sql`
- **Toute modification des réglages est inscrite au journal des actions** (action `reglages_surveillance`) : seulement les réglages changés, chacun avec l'ancienne et la nouvelle valeur ; rien n'est inscrit si rien ne change. — `src/Pharma.Application/Surveillance/Surveillance.cs`
- **Les montants de crédit surveillés doivent être supérieurs à 0 ; patient récent de 1 à 90 jours ; crédit jamais remboursé de 1 à 365 jours ; postes simultanés de 1 à 120 minutes.** — `src/Pharma.Application/Surveillance/Surveillance.cs`

### Produits sensibles
- **Un produit sensible est un produit cher, très demandé ou souvent volé, choisi par le titulaire** ; par défaut aucun produit ne l'est. — `database/migration/055_surveillance_stock.sql`
- **La liste des produits sensibles affiche leur stock** (somme des boîtes disponibles de leurs lots). — `src/Pharma.Infrastructure/Surveillance/SurveillanceSql.cs`
- **Marquer ou démarquer un produit sensible est inscrit au journal des actions** (`produit_sensible` / `produit_non_sensible`). — `src/Pharma.Application/Surveillance/Surveillance.cs`
- **Le comptage « Produits sensibles » demande le droit `stock.ajuster`** ; il porte sur tous les lots en stock des produits sensibles et se fait à l'aveugle. — `src/Pharma.Application/Stock/Inventaire.cs`
- **Sans produit sensible en stock, le comptage est refusé** (« rien à compter »). — `src/Pharma.Application/Stock/Inventaire.cs`

### Sorties surveillées (rappel du lien avec le stock)
- **Donner un échantillon ou une gratuité demande le droit `stock.donner`** ; déclarer une casse, le droit `stock.declarer_perime`. — `src/Pharma.Application/Stock/SortieStock.cs`
- **Un produit sous registre (psychotrope) n'est jamais donné en échantillon.** — `src/Pharma.Application/Stock/SortieStock.cs`
- **Un échantillon exige d'indiquer à qui et pourquoi ; une casse exige d'indiquer ce qui s'est passé (200 caractères au plus).** — `src/Pharma.Application/Stock/SortieStock.cs`

### Écarts et risques par employé
- **Écran réservé au titulaire** (Contrôle interne › Écarts et risques : par employé, ou jour par jour) ; période choisie, 30 derniers jours par défaut, 366 jours au plus, période inversée refusée. — `src/Pharma.Application/Surveillance/Risques.cs`
- **Perte d'un jour = manque de caisse (somme des écarts négatifs des clôtures du jour) + boîtes manquantes ajustées en inventaire ce jour-là, au prix d'achat.** Les boîtes fantômes et les anomalies du jour sont affichées à part. — `src/Pharma.Infrastructure/Surveillance/RisquesSql.cs`
- **Un employé est « présent » un jour s'il s'est connecté, a vendu, a ouvert une caisse ou a fait une action inscrite au journal ce jour-là** ; les postes du jour sont ceux de ses connexions, ventes et caisses. — `src/Pharma.Infrastructure/Surveillance/RisquesSql.cs`
- **Seules les journées avec activité, perte ou anomalie sont prises en compte** (une journée sans rien, pharmacie fermée, est omise). — `src/Pharma.Infrastructure/Surveillance/RisquesSql.cs`
- **Pour chaque employé : jours présent, jours de perte en sa présence, perte totale et moyenne les jours où il est là, perte moyenne les jours où il n'est pas là.** — `src/Pharma.Domain/Rapports/RegleRisques.cs`
- **Un employé est signalé quand il y a eu au moins 3 jours de perte en sa présence, au moins 3 jours sans lui pour comparer, et que sa perte moyenne de présence atteint 2 fois celle des jours d'absence** ; un employé présent tous les jours n'est jamais signalé. Un signalement est un indice, pas une preuve. — `src/Pharma.Domain/Rapports/RegleRisques.cs`
- **Employés listés : les comptes actifs et ceux présents sur la période** ; signalés en tête, puis par perte décroissante en leur présence. — `src/Pharma.Application/Surveillance/Risques.cs`, `src/Pharma.Infrastructure/Surveillance/RisquesSql.cs`
- **Indicateurs par employé sur la période** : manque à ses propres clôtures, anomalies (dont urgentes), annulations et retours clients, ventes avec remise, crédits accordés, autorisations par code demandées et données, boîtes cassées, données (échantillons) et ajustées en moins. — `src/Pharma.Infrastructure/Surveillance/RisquesSql.cs`
- **Les anomalies sont comptées le jour de leur création** (et non leur jour de rattachement : 1er du mois, date d'envoi d'un retour…). — `src/Pharma.Infrastructure/Surveillance/RisquesSql.cs`
- **Une perte d'inventaire apparaît le jour du comptage, pas le jour de la disparition** (à lire en tendance sur la période). — `src/Pharma.Desktop/Composants/Pages/Risques.razor`

## Rapports

### Accès et droits
- **Consulter ou exporter un rapport demande le droit `rapports.ventes`.** — `src/Pharma.Application/Rapports/Rapports.cs`
- **La marge brute n'apparaît qu'avec le droit `rapports.marges`.** — `src/Pharma.Application/Rapports/Rapports.cs`
- **Le montant attendu et l'écart des clôtures n'apparaissent qu'avec le droit `caisse.voir_ecart`** ; sinon seul le montant compté est montré. — `src/Pharma.Application/Rapports/Rapports.cs`

### Périodes
- **Quatre périodes : jour, semaine, mois, ou dates choisies.** — `src/Pharma.Domain/Rapports/PeriodeRapport.cs`
- **La semaine va du lundi au dimanche ; le mois du 1er au dernier jour ; bornes incluses.** — `src/Pharma.Domain/Rapports/PeriodeRapport.cs`
- **Sans période précisée, le rapport porte sur la semaine ; sans date, sur aujourd'hui.** — `src/Pharma.Api/Endpoints/RapportsEndpoints.cs`
- **Dates choisies : la fin ne peut pas précéder le début, ni être dans le futur.** — `src/Pharma.Domain/Rapports/PeriodeRapport.cs`
- **Dates choisies : un an au plus (366 jours).** — `src/Pharma.Domain/Rapports/PeriodeRapport.cs`
- **Période précédente ou suivante : même durée, juste avant ou juste après.** — `src/Pharma.Domain/Rapports/PeriodeRapport.cs`
- **Au-delà de 62 jours, les ventes sont regroupées par semaine** (datée du lundi, ou du premier jour de la période). — `src/Pharma.Domain/Rapports/PeriodeRapport.cs`, `src/Pharma.Application/Rapports/Rapports.cs`
- **Les jours sont ceux du fuseau du serveur PostgreSQL.** — `src/Pharma.Infrastructure/Lecture/RapportsSql.cs`

### Ventes
- **Seules les ventes validées comptent ; une vente annulée sort entièrement du chiffre** et est comptée à part (« ventes annulées »). — `src/Pharma.Infrastructure/Lecture/RapportsSql.cs`
- **Le chiffre d'affaires est TTC, remises déduites, et net des retours clients enregistrés pendant la période.** — `src/Pharma.Application/Rapports/Rapports.cs`
- **Un retour diminue le chiffre du jour où la caisse a repris les boîtes, pas celui du jour de la vente.** — `src/Pharma.Application/Rapports/Rapports.cs`
- **Seuls les retours sur des ventes non annulées sont déduits.** — `src/Pharma.Infrastructure/Lecture/RapportsSql.cs`
- **Le nombre de retours ne compte que les retours qui comportent des boîtes rendues** ; le montant déduit est la somme remboursée. — `src/Pharma.Infrastructure/Lecture/RapportsSql.cs`
- **Le rapport donne aussi : nombre de ventes, part organismes (Chifa) à recevoir, crédits clients accordés, remises accordées (produits hors médicaments), dons partiels du reste à charge (médicaments) et la part des remises automatiques** (écran, PDF, CSV). — `src/Pharma.Application/Rapports/Rapports.cs`, `src/Pharma.Infrastructure/Lecture/RapportsSql.cs`
- **Clôtures de caisse : une vente compte « avec remise » si elle a une remise ou un don partiel.** — `src/Pharma.Infrastructure/Lecture/RapportsSql.cs`
- **Le don partiel est une réduction de prix, pas une sortie de stock** : il n'entre pas dans « Sorties de stock sans vente » ni dans la TVA à reverser (question posée au comptable, voir `docs/a-faire.md`). — `src/Pharma.Infrastructure/Lecture/RapportsSql.cs`
- **Les crédits accordés sont les paiements « à crédit » des ventes validées de la période.** — `src/Pharma.Infrastructure/Lecture/RapportsSql.cs`
- **Ventes de médicaments et ventes d'autres produits sont séparées selon la catégorie du produit** ; un produit sans catégorie est compté comme médicament ; montants avant remises et retours. — `src/Pharma.Infrastructure/Lecture/RapportsSql.cs`
- **Le SHP (honoraire du pharmacien) est totalisé à part.** — `src/Pharma.Infrastructure/Lecture/RapportsSql.cs`
- **Le graphique compte chaque jour de la période, y compris les jours sans vente.** — `src/Pharma.Application/Rapports/Rapports.cs`
- **Les 10 produits les plus vendus sont classés par boîtes vendues (retours déduits), puis par chiffre.** — `src/Pharma.Application/Rapports/Rapports.cs`, `src/Pharma.Infrastructure/Lecture/RapportsSql.cs`
- **La remise d'une vente est répartie sur ses lignes au prorata de leur montant.** — `src/Pharma.Infrastructure/Lecture/RapportsSql.cs`

### Marges
- **Marge brute = chiffre d'affaires HT (remises déduites) − coût d'achat HT des boîtes vendues.** — `src/Pharma.Application/Rapports/Rapports.cs`
- **Le HT se calcule ligne par ligne avec le taux de TVA du produit.** — `src/Pharma.Infrastructure/Lecture/RapportsSql.cs`
- **Le coût d'une boîte identifiée est son prix d'achat réel ; sinon le prix d'achat de son lot.** — `src/Pharma.Infrastructure/Lecture/RapportsSql.cs`
- **Un retour de la période diminue le chiffre HT ; son coût n'est retiré que pour les boîtes remises en stock.** — `src/Pharma.Infrastructure/Lecture/RapportsSql.cs`
- **Le taux de marge est arrondi à 0,1 % ; il n'est pas affiché quand le chiffre HT est nul.** — `src/Pharma.Application/Rapports/Rapports.cs`

### Clôtures de caisse
- **Le rapport liste les caisses clôturées pendant la période**, de la plus récente à la plus ancienne. — `src/Pharma.Infrastructure/Lecture/RapportsSql.cs`
- **Pour chaque clôture : employé, poste, ouverture, clôture, attendu, compté, écart, nombre d'annulations, de ventes avec remise et de retours.** — `src/Pharma.Application/Rapports/Rapports.cs`

### Retours
- **La liste des retours comprend ceux enregistrés pendant la période et ceux, enregistrés un autre jour, portant sur une vente de la période.** — `src/Pharma.Application/Rapports/Rapports.cs`
- **Un retour enregistré hors période figure pour information : il ne change pas le chiffre de la période.** — `src/Pharma.Application/Rapports/Rapports.cs`
- **Chaque retour indique le ticket, le vendeur, l'employé et le poste du retour, les produits rendus et le montant.** — `src/Pharma.Application/Rapports/Rapports.cs`
- **La part du retour retirée de la dette du patient (au lieu d'être rendue) est affichée à part.** — `src/Pharma.Application/Rapports/Rapports.cs`

### Export
- **Le rapport s'exporte en PDF (impression, avec le nom de la pharmacie) ou en fichier Excel (CSV).** — `src/Pharma.Application/Rapports/Rapports.cs`
- **Le fichier Excel suit le format français : séparateur « ; », virgule décimale, UTF-8.** — `src/Pharma.Application/Rapports/Rapports.cs`
- **Une cellule de texte qui commence par = + - @ est protégée pour ne pas être lue comme une formule.** — `src/Pharma.Application/Rapports/Rapports.cs`
- **L'export applique les mêmes droits que la consultation** (marge et écarts masqués sans droit). — `src/Pharma.Application/Rapports/Rapports.cs`

## Pilotage (tableau de bord du titulaire)

### Accès
- **Le tableau de bord est réservé au titulaire** et en lecture seule. — `src/Pharma.Application/Pilotage/Pilotage.cs`, `src/Pharma.Api/Endpoints/PilotageEndpoints.cs`

### Contenu
- **Chiffre d'affaires du jour : ventes validées du jour moins les retours clients enregistrés le jour** (comme dans les rapports). — `src/Pharma.Infrastructure/Lecture/PilotageSql.cs`
- **Il affiche aussi le nombre de ventes, la part organismes et les crédits accordés du jour, et le chiffre d'hier (retours d'hier déduits).** — `src/Pharma.Infrastructure/Lecture/PilotageSql.cs`
- **Pour chaque caisse ouverte : poste, employé, heure d'ouverture, fond de caisse, espèces attendues, nombre de ventes.** — `src/Pharma.Infrastructure/Lecture/PilotageSql.cs`
- **Espèces attendues = fond de caisse + espèces encaissées − espèces rendues (annulations, retours) − complément grossiste pris au tiroir − sommes portées au coffre pendant la session** (même calcul qu'à la clôture). — `src/Pharma.Infrastructure/Lecture/PilotageSql.cs`
- **Les clôtures du jour sont affichées avec attendu, compté et écart.** — `src/Pharma.Infrastructure/Lecture/PilotageSql.cs`
- **Alertes : nombre d'alertes urgentes non traitées, et les 8 dernières alertes non traitées (urgentes d'abord).** — `src/Pharma.Infrastructure/Lecture/PilotageSql.cs`
- **Les anomalies de surveillance anti-vol n'y figurent pas** : le tableau lit la table des alertes générales, pas celle de la surveillance (constat ; à confirmer si c'est voulu). — `src/Pharma.Infrastructure/Lecture/PilotageSql.cs`
- **Les dettes et créances en retard s'ajoutent aux alertes** : « urgent » pour ce que doit la pharmacie, « attention » pour ce qu'on lui doit. — `src/Pharma.Application/Pilotage/Pilotage.cs`
- **Il affiche le nombre d'écarts d'inventaire à valider.** — `src/Pharma.Infrastructure/Lecture/PilotageSql.cs`
- **Il affiche le solde de chaque compte de trésorerie actif.** — `src/Pharma.Infrastructure/Lecture/PilotageSql.cs`
- **Il affiche le reste à encaisser des factures clients** (hors factures remplacées et hors suivi). — `src/Pharma.Infrastructure/Lecture/PilotageSql.cs`
- **Il signale un inventaire complet en cours, pharmacie ouverte ou fermée.** — `src/Pharma.Infrastructure/Lecture/PilotageSql.cs`

## Suivi des dettes et créances

### Accès et réglage
- **Consulter les dettes et créances demande le droit `tresorerie.gerer`.** — `src/Pharma.Application/Suivi/SuiviDettes.cs`
- **Le délai d'alerte vaut 30 jours par défaut ; seul le titulaire le modifie, entre 1 et 365 jours.** — `src/Pharma.Application/Suivi/SuiviDettes.cs`, `database/migration/039_suivi_dettes.sql`
- **Le changement de délai est inscrit au journal (avant / après).** — `src/Pharma.Application/Suivi/SuiviDettes.cs`

### Sommes suivies
- **Créances (dues à la pharmacie) : ventes à crédit non soldées, dette antérieure des patients, factures clients (patients, entreprises, organismes), bordereaux d'organismes envoyés non payés en entier.** — `database/migration/039_suivi_dettes.sql`
- **Dettes (dues par la pharmacie) : factures de grossistes non réglées, ancien solde des grossistes hors factures, effets remis dont l'échéance n'est pas passée.** — `database/migration/039_suivi_dettes.sql`
- **Les factures remplacées ou hors suivi ne sont pas suivies.** — `database/migration/040_facture_echeance.sql`
- **Une somme nulle ou négative est ignorée.** — `src/Pharma.Application/Suivi/SuiviDettes.cs`
- **L'ancienneté se compte depuis la date de la vente, de la facture, de l'envoi du bordereau ou de la réception.** — `database/migration/039_suivi_dettes.sql`
- **Le relevé de la dette envers les grossistes ne se modifie pas : on corrige par un nouveau mouvement.** — `database/migration/039_suivi_dettes.sql`

### Échéancier et alertes
- **Échéancier en trois tranches : 0-30 jours, 31-60 jours, plus de 60 jours** ; les effets n'y figurent pas. — `src/Pharma.Application/Suivi/SuiviDettes.cs`
- **Une facture client est en alerte quand son échéance est dépassée ou qu'elle est rejetée.** — `src/Pharma.Application/Suivi/SuiviDettes.cs`
- **Une facture client a toujours une échéance, jamais avant sa date ; les anciennes factures ont reçu date + 30 jours.** — `database/migration/040_facture_echeance.sql`
- **Un effet est en alerte quand son échéance arrive dans 7 jours ou moins.** — `src/Pharma.Application/Suivi/SuiviDettes.cs`
- **Les autres sommes (patients, bordereaux, grossistes) sont en alerte au-delà du délai réglé** ; un bordereau rejeté en partie est toujours en alerte. — `src/Pharma.Application/Suivi/SuiviDettes.cs`
- **Les alertes sont regroupées par catégorie de tiers, avec nombre et total.** — `src/Pharma.Application/Suivi/SuiviDettes.cs`
- **Le total des dettes exclut les effets déjà remis** (ils ont déjà diminué la dette). — `src/Pharma.Application/Suivi/SuiviDettes.cs`
- **Affichage : sommes en alerte d'abord, puis les plus anciennes.** — `src/Pharma.Application/Suivi/SuiviDettes.cs`

