# Points restants

Liste de travail tenue à jour à chaque lot. Cocher un point quand il est livré et vérifié dans le poste client.

## Prochaines demandes

- [ ] **Registre des psychotropes, suites** :
  - à vérifier : colonnes exactes des annexes 12 et 13 du décret 19-379 (modifié par le 21-196) pour les psychotropes ;
  - retrait des périmés (Alertes, « Retirer du stock ») : pas encore de sortie de stock ; le registre l'inscrira en « destruction » dès qu'elle existera ;
  - registre papier coté et paraphé : demander à la DSP de wilaya si le registre informatisé imprimé est accepté tel quel.
- [ ] **Rapports et retours** : une vente annulée sort des chiffres de son jour d’origine ; un retour client n’est pas encore déduit du chiffre d’affaires des rapports (à afficher en ligne « retours » ou à déduire : à décider).
- [ ] **Retour partiel en tiers payant** : aujourd’hui refusé (il faut annuler la vente complète) ; à revoir avec le format Chifa (régularisation de la part de l’organisme).
- [ ] **Prix dans la nomenclature** : le fichier officiel actuel ne porte ni prix ni code-barres ; le prix public reste à saisir. Barème des marges (décret 98-44) à confirmer avant tout calcul automatique.

- [ ] **Comparaison des tarifs des grossistes** : importer les tarifs de chaque grossiste et comparer les prix produit par produit (aide au choix du grossiste à la commande).
  - À fournir par le titulaire : un exemple de fichier de tarifs de chaque grossiste (Excel, PDF ou autre) pour connaître le format.
- [ ] **Parapharmacie** : distinguer les produits de parapharmacie dans le catalogue (préalable à la fidélisation parapharmacie de la V2).
  - À préciser par le titulaire : comment les reconnaître (classe du catalogue, liste fournie, autre).

## Scanner mobile : suites

- [ ] **iPhone** : la caméra passe par ZXing ; à essayer sur un vrai iPhone (et sur Firefox Android).
- [ ] Vérifier avec le vrai matériel : réglage GS1 des douchettes et terminaux laser, lecture des petits Data Matrix par la caméra.

## Questions pour la V2

- [ ] **Wasfa** : de quoi s'agit-il exactement (ordonnance préparée à l'avance ?), et qui l'envoie ?
- Autres sujets V2 déjà notés : commandes intelligentes (Ramadan, classement ABC), fidélisation parapharmacie, messagerie interne.

## À confirmer avec l'éditeur de Chifa (CNAS)

- [ ] **QR code du volet Chifa** (reprise de la vente à la caisse, script 060) : le format a été déduit d'**un seul volet réel**, `06930126;11/11/2026;690755003842;80;24;2;09/11/2026;07607,3,156.48;`. À faire confirmer :
  - le sens des champs 5 et 6 (`24`, `2`) et de la seconde date (`09/11/2026` : date de l'ordonnance ?) ;
  - le suffixe du n° d'assuré (`80`, imprimé « /80 ») : rang de l'ayant droit ?
  - le système de codes produits (`07607`) : code CNAS, nomenclature ? Une table officielle permettrait d'associer les produits sans le faire à la main ;
  - le format d'un volet de plusieurs produits (séparateur entre les lignes) ;
  - le taux de remboursement : absent du QR ; peut-il y figurer ?
  - la présence éventuelle d'une signature ou d'un contrôle d'intégrité.
- [ ] **Arrondi de la part de l'organisme** : passé au centime d'après les volets (1 160,82 × 80 % = 928,66) ; règle exacte à confirmer.
- [ ] **Qui imprime les volets** : Chifa_Officine, ou un logiciel privé relié à Chifa ? (question de l'agrément CNAS)
- [ ] **QR des factures de grossistes** : sur 3 grossistes, ils ne portent que le n° de facture (`26/F-015695`, `FA.26-327890ZZ1ZZ1`, `26/FA076277`) ; vérifier si d'autres grossistes y mettent les lignes (produits, lots, prix).

## Recette complète du 12/11/2026 : reste à faire

Recette par l'API (4 copies de base), passage de tous les écrans (39 écrans, 460 boutons, aucun plantage), envoi réel des alertes
par Gmail, et revue de code de toute la branche `develop`. Les défauts majeurs sont corrigés (scripts 063 à 067, voir « Livré
récemment »). Restent :

**Défauts mineurs**
- [ ] Alerte immédiate perdue sans trace quand l'envoi est refusé (destinataire non vérifié, base indisponible) : l'erreur est avalée (`RappelerLot`, clôture, inventaire…) ; à inscrire au journal du serveur et à signaler au titulaire.
- [ ] Erreurs techniques (500) au lieu d'un message clair : lot d'un autre produit à l'encaissement, règlement grossiste au millième, liste à null dans une requête, pagination très grande, reprise « Autre » sans produit.
- [ ] Remise négative acceptée sans message (ramenée à 0) ; coupure inconnue ignorée à la clôture (caisse fermée avec 0 compté) ; saisie énorme au comptage.
- [ ] Doublons selon les majuscules : client facturé, compte de trésorerie ; zone d'emplacement inconnue enregistrée « vente » ; emplacement créé « inactif » enregistré actif.
- [ ] Recherche d'un patient par téléphone : numéro exact, espaces compris (l'écran des crédits cherche par début de numéro).
- [ ] « Dernier prix d'achat » repris à 0 DA quand la dernière entrée est une unité gratuite.
- [ ] Concurrence : deux ventes sur les mêmes lots dans un ordre différent (erreur technique au lieu de « réessayez ») ; double clic sur « Encaisser » si le serveur répond très lentement ; deux dépenses de trésorerie simultanées ; un lot pris par deux inventaires créés au même instant ; statut de commande non revérifié en base.
- [ ] Poste : données du client précédent (formulaire « nouveau patient », taux) gardées à la vente suivante ; caméra jamais coupée ; afficheur client figé sur le dernier panier et « Payé » affiché pour une vente à crédit ; F12 encaisse sous un volet ouvert ; scan double rapide ; essai de l'afficheur appliqué sans enregistrer ; fermer le logiciel avec l'écran client ne l'arrête pas.
- [ ] Montant en lettres : « deux cents millions », « quatre-vingts millions » ; arrondi bancaire à quelques endroits (centime d'écart) ; avoir non arrondi au centime.
- [ ] Export : `format=csv` des rapports renvoie un PDF (seul `excel` donne le CSV) ; message « Toutes les boîtes ont déjà une identité » pour un lot inexistant.
- [ ] Journal des actions vidable par `TRUNCATE` (UPDATE et DELETE sont bien refusés) ; journal fichier arrêté à la première erreur d'écriture.
- [ ] Scripts : contraintes CHECK de 024 et 025 recréées sans garde ; rejeu de 053/054 qui remet les types de notification retirés ; `v_nomenclature` en `n.*` ; `initialiser_base_locale.ps1 -Recreer` sans confirmation.

**Points à trancher**
- [ ] Facture encaissée en trésorerie : la dette du patient (vente à crédit facturée) reste due → risque de double encaissement.
- [ ] Banque « carte » changée : l'historique des paiements par carte passe sur la nouvelle banque (soldes faux).
- [ ] Règlement grossiste par chèque sans banque choisie : accepté, aucun compte débité.
- [ ] Rappel de lot : boîtes rendues ou détruites encore comptées dans les patients à contacter.
- [ ] Registre des psychotropes : balance mensuelle (une délivrance antérieure au solde d'ouverture comptée en plus, données de démo).
- [ ] Lot délaissé jamais détecté si une boîte de l'ancien lot a été vendue après l'arrivée du nouveau ; écart d'inventaire égal au seuil corrigé sans validation (« sous les seuils » ?).
- [ ] Psychotrope créé en classe « libre » par l'assistant (droit `catalogue.creer`) : vendable sans registre jusqu'à vérification.
- [ ] Produit non remboursable dans une vente en tiers payant avec reste à charge saisi : part organisme répartie aussi sur lui.
- [ ] Notification d'écart de caisse : seulement au-delà du seuil (la règle dit « tout écart non nul est signalé »).
- [ ] Valeurs au prix d'achat visibles avec le seul droit `stock.consulter` (alertes), alors que les rapports les réservent à `rapports.marges`.

## À confirmer avec le comptable

- [ ] **TVA sur les pertes et dons** (script 061, Rapports › « TVA à reverser sur les pertes et dons ») : pour quels motifs la TVA d'achat déjà déduite doit-elle être reversée ? Boîtes périmées détruites (avec ou sans procès-verbal), casses, dons et échantillons. Le réglage est à « non » pour les trois motifs tant que la règle n'est pas confirmée ; le titulaire l'active motif par motif.
- [ ] **Base de calcul** : le logiciel prend le prix d'achat HT retenu le jour de la sortie × le taux de TVA de la fiche produit. Aujourd'hui tous les produits des bases d'essai sont à 0 % : vérifier les taux de la parapharmacie dans le catalogue réel.
- [ ] **TVA sur le don partiel du reste à charge** (2026-11-14, scripts 071-072) : une réduction de prix sur un médicament est enregistrée comme don partiel (`vente.montant_don`), pas comme sortie de stock ; aucune TVA n'est calculée dessus. À confirmer avec le comptable : faut-il en reverser une, et sur quelle base ?
- [ ] **Remise automatique et médicaments** (2026-11-14) : la remise automatique est refusée sur un produit remboursable ; sur un médicament non remboursable, elle est enregistrée comme don partiel. Règle à confirmer avec l'Ordre ou la CNAS avant de l'utiliser sur des médicaments.

## À vérifier sur le terrain

- [ ] **Chifa : éviter la double saisie** (Chifa_Officine est un programme Windows installé sur chaque poste de caisse) :
  - sur un poste de caisse : noter l'heure, faire une vente tiers payant de test dans Chifa, puis chercher les fichiers modifiés à cette heure-là (dossier d'installation de Chifa, `C:\ProgramData`) ;
  - fournir le dossier, un fichier anonymisé (faux nom, faux n° d'assuré) et le nom et la version de Chifa (« À propos ») ;
  - ensuite : notre logiciel lit ces fichiers et crée la vente tiers payant chez nous (patient, produits, parts CNAS et patient, n° de facture Chifa, stock et registre), sans ressaisie ; les cas incomplets passent « à vérifier ».

- [ ] **Reprise réelle** : essayer l'assistant sur une copie de la base de l'ancien logiciel du pilote (pilote ODBC, mot de passe, séparateur des exports CSV, stock tenu par lots ou non).
- [ ] **Étiquetage du stock existant** : 17 % des boîtes sont identifiées sur la base de test. Il faut étiqueter rayon par rayon (à la fin de chaque inventaire tournant, ou dans Achats > Étiquettes internes) avant que le mode strict ne s'active.
- [ ] **Data Matrix** : part des boîtes qui en portent un dans la pharmacie (cela dépend des laboratoires et de la réglementation).
- [ ] **Modem GSM** : code PIN, opérateur, longueur réelle des SMS.
- [ ] **Compte SMTP** : serveur, port et mode TLS du fournisseur de la pharmacie.
- [ ] **Disque externe de sauvegarde** : lettre de lecteur stable et droits d'écriture du service Windows.
- [ ] **Imprimante d'étiquettes thermique** : adresse IP (variable `PHARMA_IMPRIMANTE_ETIQUETTES`).
- [ ] **DPAPI** : le service de l'API doit tourner sur le PC serveur qui a enregistré le mot de passe SMTP.

## Livré récemment

- [x] Navigation : retour à l'écran précédent partout avec son état (docs/navigation.md) ; menu réorganisé sur une ligne (Finances, Pilotage, ⚙, menu à son nom, « ⇄ Changer ») ; « Page introuvable » au lieu d'un plantage sur une adresse inconnue.
- [x] Rien n'est perdu en quittant un écran : vente de la Caisse mise en attente (départ de l'écran, changement d'utilisateur, reprise par le suivant) ; réceptions en attente sur le serveur (069) ; retour et facture en préparation gardés pour la session.
- [x] Affichage : indicateur de chargement, tableaux en relief, liens et boutons mis en évidence ; Trésorerie et file des notifications paginées et filtrées par le serveur.

- [x] Passation au « ⇄ Changer » : relève de caisse proposée ; facture, retour fournisseur et réception en cours repris par le suivant (bandeau « Tâches laissées en cours sur ce poste »).
- [x] État de la caisse et comptage intermédiaire sans clôture (070) : comptage aveugle strict, écart affiché comme à la clôture pour qui peut le voir.

**Points ouverts de ces livraisons**
- [ ] Facture et retour fournisseur laissés au changement d'utilisateur : gardés dans la mémoire du poste, perdus si le poste redémarre.
- [ ] Abandon d'une réception en attente : inscrit au journal des actions ; à décider s'il doit aussi lever une anomalie dans le Contrôle interne.
- [ ] Mise en évidence des boutons au survol en thème clair : préciser ce qui ne convient pas (« pareil que sombre »).
- [ ] Vente mise en attente au changement d'utilisateur : si le serveur est injoignable à ce moment-là, le panier est perdu sans signalement (la session est déjà fermée).

- [x] Corrections de la recette du 12/11/2026 (défauts majeurs) : mot de passe provisoire et code comptoir ; révocation des connexions (063) ; psychotrope jamais autorisé par le code d'un collègue ; portée de la connexion par code ; vrai validateur d'une annulation ou d'un retour autorisés par code ; remboursement d'une vente à crédit limité à ce qui a été payé ; annulation et retour simultanés ; aucune écriture sur une caisse clôturée (064) ; lot rappelé bloqué pour tous les grossistes ; GTIN contrôlé à la réception ; Data Matrix : lot de la date lue ; retour des boîtes non scannées ; stock vendable sans les lots bloqués (065) ; vente en attente qui garde tout, double clic, boîtes retirées signalées à la déconnexion, impression du ticket, fiche contact et « Faire autoriser » fermées ; scripts 003, 901, 903, sauvegarde planifiée et restauration par script.
- [x] Alertes qui ont un sens : péremption seulement s'il y a quelque chose à faire (066), « RAS » à l'écran, rapport du soir sans rubrique vide, e-mails d'alerte une information par ligne.
- [x] « Surveillance » renommé « Contrôle interne » (écrans, e-mails, SMS, documentation ; 067).
- [x] Destinataires des notifications vérifiés par un code à 6 chiffres reçu par e-mail ou SMS (068) ; e-mails signés du nom de la pharmacie ; rapport du soir construit sur les seules alertes encore ouvertes, recalculées juste avant l'envoi.

- [x] Scripts de la base séparés en deux usages : `database/migration/` (scripts numérotés, mise à jour d'une base en service) et `database/init/` (création d'une base neuve à la mise en production) ; outils limités aux migrations `0NN_*.sql`.
- [x] Script 062 : vues `v_etat_stock`, `v_risque_peremption` et `v_mouvement_tresorerie` sans colonne figée ; une base neuve (`init/`) et une base mise à jour par les migrations ont exactement la même structure (vérifié sur une ancienne sauvegarde rejouée).
- [x] Vidéos exclues de git (tous formats), ni commitées ni rattachées à la solution.
- [x] Pertes et dons : section « Sorties de stock sans vente » des rapports (boîtes et valeur d'achat HT, par employé, par produit, en détail ; PDF et CSV), carte « Pertes et dons » du pilotage mobile, tuile « Marge après pertes et dons » avec la TVA à reverser réglée par motif (script 061).
- [x] Volet Chifa scanné à la caisse : vente reprise en tiers payant CNAS sans ressaisie, code produit Chifa associé une fois, volet repris une seule fois (script 060).
- [x] Part de l'organisme arrondie au centime ; PA TTC et PV HT affichés à la réception, prix public HT sur la fiche produit.
- [x] Registre des psychotropes complet (toutes entrées et sorties, numéro d'ordre sans trou, balance mensuelle, PDF et CSV) ; porteur de l'ordonnance saisi à la caisse.
- [x] Nomenclature nationale : import du fichier Excel du MIPH, création de produit depuis la nomenclature, psychotropes repérés par leur DCI.
- [x] Caisse partagée par poste, code comptoir et relève.

- [x] Assistant de reprise de données générique (ODBC) : source, correspondance proposée, rapprochement, contrôles, chargement, vérification.
- [x] Rapports : ventes, marge brute au prix réel, ventes par jour, produits les plus vendus, clôtures ; export PDF et Excel.
- [x] Prix d'achat réel par boîte (plus de moyenne) ; date différente ou lot bloqué à la réception : choix laissé à l'utilisateur.
- [x] Scanner mobile : une ligne retirée ou une réception abandonnée sur le poste remet ses scans en attente sur le mobile.
- [x] Scanner mobile : lecture caméra sur tous les mobiles grâce à ZXing (servi en local) quand le navigateur ne lit pas les Data Matrix.
- [x] Message d'alerte de stock clair (seuil appliqué, « aucune vente récente ») ; caisse : libellé « lot conseillé » ; dernier prix d'achat repris aussi pour les lots sans entrée en stock.
- [x] Champs obligatoires : astérisque rouge, champ en rouge et curseur sur le premier manquant, sur tous les formulaires du poste.
- [x] Seuil d'alerte de stock par produit, saisi par le pharmacien (Administration > Seuils), sinon automatique.
- [x] Caisse : stock restant affiché ; boîte scannée servie avec son lot et avertissement si un lot proche de la péremption doit partir avant.
- [x] Scanner mobile (lot 3) : contrôle en rayon, dates à vérifier, rangement, inventaire, scans de réception importés sur le poste ; HTTPS local (scripts/certificat_mobile.ps1).
- [x] Saisie assistée du produit (liste sous le champ, flèches et Entrée) dans Commandes et dans Réception.
- [x] Lot unique par produit, n° de lot et grossiste ; étiquette interne créée et imprimée pour chaque boîte sans Data Matrix à la réception.
- [x] Inventaire tournant (lot 2), emplacements (lot 1), notifications au titulaire et sauvegardes.
