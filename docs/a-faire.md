# Points restants

Liste de travail tenue à jour à chaque lot. Cocher un point quand il est livré et vérifié dans le poste client.

Dernière mise à jour : 2026-11-16 (bilan de la session du 13 au 16/11/2026).

## Livré mais pas encore commité

- [ ] **Synchronisation** : réglages descendants (scripts 130-131, central 004-005, table `reglage_central`) ; limite pendant un chargement et activation par groupes (central 006) ; familles nominatives (130), Pharmacie et Clients facturés (133, central 007), Banque élargie (135, central 008) ; conservation réglable au central (004) ; corrections de la revue globale et de l'audit de sécurité (136, central 009). Build et 1 918 tests verts, scripts rejoués deux fois sans effet.
- [ ] **Contenance** : enregistrement depuis la nomenclature et sachets / ampoules (scripts 132 à 134, motif corrigé au 136).
- [ ] **Écrans du super admin** : Reprise, Restauration, accès aux Profils de reprise.
- [ ] **Avant de commiter** : l'index git contient 61 fichiers indexés lors de la session précédente, dont 15 modifiés depuis et 3 nouveaux fichiers non indexés ; désindexer, puis indexer et commiter par sujet (synchronisation, contenance, écrans), sans `docs/taches/`.

## HTTPS sur le réseau de l'officine (décidé le 2026-11-16 : oui)

Option retenue : HTTPS seul sur le réseau, HTTP limité au PC serveur (`localhost:5080`). Voir `docs/mise-en-production.md`, étapes 3.2 et 3.5 bis.
- [ ] `scripts/certificat_mobile.ps1` : il écrit encore `http://0.0.0.0:5080` (HTTP ouvert au réseau) ; le limiter à `localhost`, HTTPS sur 5443, fermer le port 5080 aux autres postes dans le pare-feu.
- [ ] Distribution du certificat de la pharmacie aux postes et aux téléphones : aujourd'hui téléchargé en HTTP (`/mobile/autorite-pharmacie.crt`). **À décider** : petite porte HTTP limitée à ce fichier, ou clé USB / QR code.
- [ ] **À décider** : le certificat doit-il couvrir dès maintenant le 2e serveur (bascule) et l'adresse VPN (`docs/acces-distant.md`) ?
- [ ] Mise à jour de `docs/mise-en-production.md` (installation du certificat sur chaque poste, `PHARMA_API=https://<serveur>:5443/`, vérifications).
- [ ] Essai réel en HTTPS : connexion, vente, impression, scanner mobile ; API en production en HTTPS jamais vérifiée.
- [ ] Port 5432 de la base fermé aux autres postes : aucune vérification tracée.

## Décisions en attente du titulaire

- [ ] **Écrans de vente** :
  - seuils d'alerte des psychotropes (valeurs de départ proposées : 2 fois en 30 jours, 1 fois par ordonnance et par an, plus de 3 boîtes ou plus de 28 jours, prescripteur à moins d'une délivrance par an) ;
  - liste des 214 DCI d'antibiotiques ;
  - format de l'export pour les impôts et taux de TVA.
- [ ] **Majorations Chifa** : quels types en plus de « Garde » ? (question posée le 15/11, sans réponse ; voir `docs/chifa.md`).
- [ ] **Démo** : « page démo », « démo interactive » ou « préparation démo cloud » (Dockerfile, docker-compose local, mode démo).
- [ ] **Script 111b** (séparation des rôles PostgreSQL) : à passer par le titulaire avec un superutilisateur ; sans lui, une falsification est détectée mais pas empêchée (voir `docs/securite-et-droits.md`).
- [ ] **Réglages communs aux officines** : `regle_alerte_psychotrope` imposé ou proposé ? TVA bien imposée ? (voir `docs/conception-synchronisation.md` § 16).
- [ ] **Ancienne colonne `num_facture_chifa`** : suppression (opération destructive, script rédigé non exécuté) à décider à la mise à jour de l'officine.
- [ ] **Note « Pharma : de quoi s'agit-il »** (section « Questions pour la V2 » ci-dessous) : à reformuler ou supprimer.
- [ ] **Messages de démo sur `pharmacie_dev`** : purge proposée (seule `pharmacie_test` a été purgée) ; jamais sans validation explicite.
- [ ] **Message du commit `6f971e8`** : il cite un nom de tiers ; réécriture facultative, l'historique étant déjà poussé.

## Démarches du titulaire

- [ ] Hébergeur en Algérie agréé ARPCE et nom de domaine `.dz` (CERIST) : préalable au serveur central réel.
- [ ] Déclaration à l'ANPDP (rôles : responsable du traitement, sous-traitant) ; texte du consentement SMS à faire valider.
- [ ] Test d'intrusion par un prestataire externe avant la mise en service du serveur central.
- [ ] Clé de l'autorité de certification du serveur central : la garder hors ligne, clé intermédiaire en production.
- [ ] Lettre à la CNAS : rédigée, à envoyer (questions de la section « À confirmer avec l'éditeur de Chifa »).
- [ ] Wilayas 59 à 69 : sources à vérifier sur le Journal officiel.
- [ ] Exports réels de l'ancien logiciel et catalogues des grossistes (reprise et profils de reprise).
- [ ] Modèle de feuille CVM/MDN anonymisé.
- [ ] Avant la mise à jour de l'officine : passer les scripts 083 à 136, vérifier que le compte de la base en est propriétaire, lancer le contrôle des n° Chifa (voir `docs/mise-en-production.md`).

## Plus tard (sur accord explicite du titulaire)

- [ ] Synchronisation S3 (ordres du central vers l'officine : prix, réglages communs, brouillons de commande, employés ; purge de `sync_sortie`, bandeau), S4 (contrôle d'intégrité au central), S5 (diffusion des référentiels), S7 (écrans du central). Voir `docs/conception-synchronisation.md`.
- [ ] Découpage par officine des grandes tables du central, avant d'accueillir beaucoup d'officines.
- [ ] Multi-officines (lot 12) et partie cloud du lot 5 : après le choix de l'hébergeur.
- [ ] Restauration de la base d'une officine depuis le central (nouvelle époque + chargement) : non réalisée (`docs/conception-synchronisation.md` § 13.6).
- [ ] Compatibilité des versions du format d'échange : à coder à l'arrivée d'une version 2.
- [ ] Envoi des produits vers CHIFA-Officine et import de son fichier : selon la réponse de la CNAS.
- [ ] Effacement automatique au central : désactivé ; à activer après vérification des durées réglementaires.
- [ ] Nettoyage des données d'essai de `pharmacie_test` : sur une liste validée par le titulaire.
- [ ] **Lot 9, chiffrement** (colonnes sensibles, données au repos, sauvegardes) : en dernier, uniquement sur « go chiffrement ».

## À vérifier (aucune trace de vérification)

- [ ] Bases `pharmacie_recette_*` et `pharmacie_essai_restauration*` : en retard d'au moins le script 128 ; à mettre à jour ou supprimer (avec accord).
- [ ] Fichiers de l'autorité de certification d'essai restés dans un dossier temporaire de travail : à effacer.
- [ ] 1 à 3 échecs de connexion sécurisée (TLS) sous forte charge : cause non examinée.
- [ ] Contenance : un produit AMP. et un produit SACH. non remplis sans cause connue ; 4 019 cas douteux ; valeur proposée en grisé à vérifier à l'écran.
- [ ] Contrôles à l'écran non tracés : écran Synchronisation (libellé « Banque » allongé), bordereaux, « Durée du traitement » à la caisse, bandeau « produit redevenu disponible », parcours du 15/11 (majoration au rapprochement, annulation « rendu en espèces », ticket PDF).
- [ ] Étape 2 « Correspondance » de la reprise : défilement non mesuré (il faut une vraie base).
- [ ] Origine des écritures du 16/11 entre 03:01 et 03:03 dans `pharmacie_test` (scellement, intégrité, `patient_modifie`).
- [ ] Synchronisation : essai de bout en bout avec les vraies API après les corrections 136 / 009, dont le refus 429 au dépassement du plafond de chargement (seule la fonction SQL est testée).

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

- [ ] **Pharma** : de quoi s'agit-il exactement (ordonnance préparée à l'avance ?), et qui l'envoie ?
- Autres sujets V2 déjà notés : commandes intelligentes (Ramadan, classement ABC), fidélisation parapharmacie, messagerie interne.

## À confirmer avec l'éditeur de Chifa (CNAS)

- [ ] **QR code du volet Chifa** (reprise de la vente à la caisse, script 060) : le format a été déduit d'**un seul volet réel**, `06930126;11/11/2026;690755003842;80;24;2;09/11/2026;07607,3,156.48;`. À faire confirmer :
  - le sens des champs 5 et 6 (`24`, `2`) et de la seconde date (`09/11/2026` : date de l'ordonnance ?) ;
  - le suffixe du n° d'assuré (`80`, imprimé « /80 ») : rang de l'ayant droit ? Affiché « rang NN » depuis le script 102 (2026-11-15) : **sens du suffixe = rang de l'ayant droit, à confirmer sur cartes réelles** (deux cartes d'une même famille).
  - le système de codes produits (`07607`) : code CNAS, nomenclature ? Une table officielle permettrait d'associer les produits sans le faire à la main ;
  - le format d'un volet de plusieurs produits (séparateur entre les lignes) ;
  - le taux de remboursement : absent du QR ; peut-il y figurer ?
  - la présence éventuelle d'une signature ou d'un contrôle d'intégrité.
- [ ] **Organisme dans le QR du volet (CNAS / CASNOS)** (règle du 15/11/2026 : organisme de la fiche du patient, sinon CNAS, modifiable par le vendeur) : champ `24` (5e) peut-être le code du centre payeur ou de l'agence (non confirmé : le centre payeur fait 5 chiffres dans les déclarations employeurs) ; champ `2` (6e) peut-être le code de la caisse, un type de prestation ou une version de format ; hypothèse « même QR CNAS et CASNOS » à vérifier. Méthode : comparer un volet CASNOS, deux volets CNAS d'agences différentes, deux volets du même assuré ; regarder le centre payeur affiché par CHIFA-Officine ou par l'ancien logiciel ; demander le format à la CNAS. Si l'organisme est identifiable, rendre le choix automatique. Détails et sources : `docs/chifa.md`, section « Champs du QR à identifier (à vérifier) ».
- [ ] **Arrondi de la part de l'organisme** : passé au centime d'après les volets (1 160,82 × 80 % = 928,66) ; règle exacte à confirmer.
- [ ] **Qui imprime les volets** : Chifa_Officine, ou un logiciel privé relié à Chifa ? (question de l'agrément CNAS)
- [ ] **QR des factures de grossistes** : sur 3 grossistes, ils ne portent que le n° de facture (`26/F-015695`, `FA.26-327890ZZ1ZZ1`, `26/FA076277`) ; vérifier si d'autres grossistes y mettent les lignes (produits, lots, prix).

## Recette complète du 12/11/2026 : reste à faire

Recette par l'API (4 copies de base), passage de tous les écrans (39 écrans, 460 boutons, aucun plantage), envoi réel des alertes
par Gmail, et revue de code de toute la branche `develop`. Les défauts majeurs sont corrigés (scripts 063 à 067, voir « Livré
récemment »). Restent :

**Défauts mineurs**
- [x] Alerte immédiate perdue sans trace quand l'envoi est refusé (destinataire non vérifié, base indisponible) : l'erreur est avalée (`RappelerLot`, clôture, inventaire…) ; à inscrire au journal du serveur et à signaler au titulaire. (Corrigé le 2026-11-14 : jamais bloquant ; erreur inscrite au journal du serveur ; alerte qui n'a pas pu partir — mise en file impossible, aucun destinataire vérifié, envoi abandonné — mémorisée, script 077, et signalée au titulaire par un bandeau de l'Accueil, « Marquer comme vues ».)
- [x] Erreurs techniques (500) au lieu d'un message clair : reprise « Autre » sans produit. (Corrigé le 2026-11-14 : reprise « Autre » avec un produit inexistant — « Produit introuvable dans le catalogue » ; lot d'un autre produit à l'encaissement, règlement grossiste au millième, liste à null dans une requête, pagination très grande, refus techniques de la base — doublon, contrainte, nombre trop grand, conflit entre postes — traduits en message clair.)
- [ ] Remise négative acceptée sans message (ramenée à 0) ; coupure inconnue ignorée à la clôture (caisse fermée avec 0 compté) ; saisie énorme au comptage. (Corrigé le 2026-11-14 pour la clôture : coupure inconnue refusée ; au plus 100 000 billets ou pièces par coupure et 10 000 000 DA en « autres » ; clôture ou relève à 0 DA compté sur confirmation explicite. Reste : remise négative.)
- [x] Doublons selon les majuscules : client facturé, compte de trésorerie ; zone d'emplacement inconnue enregistrée « vente » ; emplacement créé « inactif » enregistré actif. (Corrigé le 2026-11-14 : doublon refusé avec un message clair, majuscules et espaces autour ignorés, index uniques sur lower(btrim()) au script 077 si la base n'a pas de doublon ; zone autre que « vente » ou « reserve » refusée ; emplacement créé inactif enregistré inactif.)
- [x] Recherche d'un patient par téléphone : numéro exact, espaces compris (l'écran des crédits cherche par début de numéro). (Corrigé le 2026-11-14 : même règle à la caisse et aux crédits — début de numéro à partir de 4 chiffres, espaces et séparateurs ignorés, « +213 » ou « 00213 » valent « 0 ».)
- [x] « Dernier prix d'achat » repris à 0 DA quand la dernière entrée est une unité gratuite. (Corrigé le 2026-11-14 : prix net de remise de la dernière entrée payante ; entrées à 0 DA ignorées, à la commande et dans les suggestions.)
- [x] Concurrence : un lot pris par deux inventaires créés au même instant ; statut de commande non revérifié en base. (Corrigé le 2026-11-14 : deux ventes sur les mêmes lots — lots verrouillés dans un ordre fixe, « réessayez » en cas de conflit ; double encaissement si le serveur répond très lentement — clé d'encaissement, script 073 ; deux dépenses ou transferts de trésorerie simultanés — compte verrouillé, solde relu, refus s'il ne suffit pas ; créations d'inventaire une à une — verrou en base ; statut de commande revérifié en base à l'écriture — lignes d'un brouillon, émission, annulation, réception — sinon « Commande modifiée entre-temps : rechargez. »)
- [x] Poste : données du client précédent (formulaire « nouveau patient », taux) gardées à la vente suivante ; caméra jamais coupée ; afficheur client figé sur le dernier panier et « Payé » affiché pour une vente à crédit ; F12 encaisse sous un volet ouvert ; scan double rapide ; essai de l'afficheur appliqué sans enregistrer ; fermer le logiciel avec l'écran client ne l'arrête pas.
- [x] Montant en lettres : « deux cents millions », « quatre-vingts millions » ; arrondi bancaire à quelques endroits (centime d'écart) ; avoir non arrondi au centime. (Corrigé le 2026-11-14 : million et milliard s'accordent ; tous les arrondis monétaires au centime, la moitié vers le haut — `Pharma.Domain.Commun.Arrondi` ; avoir client ou grossiste à plus de 2 décimales refusé.)
- [x] Export : `format=csv` des rapports renvoie un PDF (seul `excel` donne le CSV) ; message « Toutes les boîtes ont déjà une identité » pour un lot inexistant. (Corrigé le 2026-11-14 : « csv » ou « excel » donnent le CSV, « pdf » ou rien le PDF, tout autre format refusé ; « Lot introuvable ».)
- [x] Journal des actions vidable par `TRUNCATE` (UPDATE et DELETE sont bien refusés) ; journal fichier arrêté à la première erreur d'écriture. (Corrigé le 2026-11-14 : `TRUNCATE` refusé sur le journal des actions, le registre des psychotropes et le journal des sauvegardes, script 076 ; journal fichier : erreur traitée ligne par ligne, fichier rouvert après une courte attente, lignes perdues signalées.)
- [x] Scripts : contraintes CHECK de 024 et 025 recréées sans garde ; rejeu de 053/054 qui remet les types de notification retirés ; `v_nomenclature` en `n.*` ; `initialiser_base_locale.ps1 -Recreer` sans confirmation. (Corrigé le 2026-11-14, anciens scripts modifiés avec accord, base produite identique : contraintes gardées, types cochés une seule fois, colonnes nommées ; `-Recreer` de `initialiser_base_locale.ps1` et de `installer.ps1` : nom de la base à retaper, `-Force` pour l'automatisation.)

**Points à trancher**
- [x] Facture encaissée en trésorerie : la dette du patient (vente à crédit facturée) reste due → risque de double encaissement. (Tranché et corrigé le 2026-11-14 : l'encaissement solde la dette du patient pour ces ventes, même transaction, relevé de dette et journal ; une vente à crédit facturée n'est comptée qu'une fois dans le suivi des dettes.)
- [x] Banque « carte » changée : l'historique des paiements par carte passe sur la nouvelle banque (soldes faux). (Corrigé le 2026-11-14 : banque des cartes par période, `compte_carte_periode`, script 074 ; le changement vaut à partir du jour même.)
- [x] Règlement grossiste par chèque sans banque choisie : accepté, aucun compte débité. (Tranché et corrigé le 2026-11-14 : banque obligatoire pour chèque, virement et nouveau moyen prélèvement ; traite : banque facultative, paiement à l'échéance confirmé par le titulaire dans Achats › Traites à payer.)
- [x] Rappel de lot : boîtes rendues ou détruites encore comptées dans les patients à contacter. (Tranché et corrigé le 2026-11-14 : boîtes rendues déduites ; une vente entièrement rendue est masquée de la liste ; script 075.)
- [x] Registre des psychotropes : balance mensuelle (une délivrance antérieure au solde d'ouverture comptée en plus, données de démo). (Tranché et corrigé le 2026-11-14 : l'ouverture apparaît en « Solde reporté », distinct des entrées ; lignes antérieures à l'ouverture exclues de la balance ; écran, PDF et CSV.)
- [x] Lot délaissé jamais détecté si une boîte de l'ancien lot a été vendue après l'arrivée du nouveau ; écart d'inventaire égal au seuil corrigé sans validation (« sous les seuils » ?). (Tranché et corrigé le 2026-11-14 : ventes du lot récent comptées depuis la dernière vente de l'ancien lot, script 075 ; un écart égal au seuil attend la validation du titulaire.)
- [x] Psychotrope créé en classe « libre » par l'assistant (droit `catalogue.creer`) : vendable sans registre jusqu'à vérification. (Tranché et corrigé le 2026-11-14 : pour un autre que le titulaire, DCI de substance surveillée → classe imposée (psychotrope ou stupéfiant) et produit « à vérifier », invendable jusqu'à la confirmation du titulaire, message au créateur ; de même si un employé donne une telle DCI à un produit hors registre.)
- [x] Produit non remboursable dans une vente en tiers payant avec reste à charge saisi : part organisme répartie aussi sur lui. (Tranché et corrigé le 2026-11-14 : part de l'organisme sur les seules lignes remboursables avec tarif de référence ; reste saisi au moins égal aux lignes non remboursables.)
- [x] Notification d'écart de caisse : seulement au-delà du seuil (la règle dit « tout écart non nul est signalé »). (Tranché et corrigé le 2026-11-14 : tout écart non nul est inscrit au journal des actions et envoyé par e-mail ; le SMS seulement au-delà du seuil.)
- [x] Valeurs au prix d'achat visibles avec le seul droit `stock.consulter` (alertes), alors que les rapports les réservent à `rapports.marges`. (Tranché et corrigé le 2026-11-14 : réservées à `rapports.marges` ou au titulaire — alertes, péremption, valeur du stock ; non envoyées par le serveur sans le droit.)

**Lot 6 (2026-11-14) : points tranchés par le titulaire**
- [x] **Écarts de caisse à l'accueil**. (Tranché et livré le 2026-11-14 : 7 jours gardés ; l'employé affiché est celui qui a clôturé (compté) la caisse, lu au journal des actions (`ecart_caisse`) ; sans trace de la clôture, « ouverte par … ».)
- [x] **Classe imposée à la modification d'un produit**. (Tranché et livré le 2026-11-14 : à toute modification par un employé non titulaire, DCI changée ou non, classe de la substance surveillée ou de la nomenclature, la plus stricte, produit « à vérifier » ; comme à la création.)
- [x] **Prêteur hors trésorerie**. (Tranché et livré le 2026-11-14 : son nom se corrige à tout moment, ancien et nouveau nom au journal, colonnes d'import retenues reportées sur le nouveau nom ; banque suivie et type de prêteur toujours figés une fois le prêt utilisé.)
- [x] **Envoi en échec signalé à l'accueil**. (Tranché le 2026-11-14 : un seul signalement, après un jour ; pas de second à l'abandon — comportement vérifié et couvert par un test.)

**Lot 7 (2026-11-14) : points tranchés par le titulaire**
- [x] **E-mail d'écart de caisse : quel employé nommer**. (Tranché et livré le 2026-11-14 : celui qui a clôturé la caisse ou fait la relève, pas celui qui l'a ouverte ; « à la relève » / « Relève faite par » pour une relève ; même logique que le bandeau de l'accueil, qui affiche la date de la clôture ou de la relève et celle de l'ouverture à part.)
- [x] **Confirmation « hors registre » du titulaire**. (Tranché et livré le 2026-11-14 : mémorisée, script 080 ; un employé ne reclasse plus le produit et ne le remet pas « à vérifier » ; la confirmation tombe si la DCI change ; le produit n'est plus signalé « classe à corriger ».)
- [x] **Psychotrope dont la substance est un stupéfiant**. (Tranché et livré le 2026-11-14 : classé par un employé, monté en stupéfiant, à la création comme à la modification ; le titulaire décide lui-même, sa confirmation vaut.)

## À confirmer avec le comptable

- [ ] **TVA sur les pertes et dons** (script 061, Rapports › « TVA à reverser sur les pertes et dons ») : pour quels motifs la TVA d'achat déjà déduite doit-elle être reversée ? Boîtes périmées détruites (avec ou sans procès-verbal), casses, dons et échantillons. Le réglage est à « non » pour les trois motifs tant que la règle n'est pas confirmée ; le titulaire l'active motif par motif.
- [ ] **Base de calcul** : le logiciel prend le prix d'achat HT retenu le jour de la sortie × le taux de TVA de la fiche produit. Aujourd'hui tous les produits des bases d'essai sont à 0 % : vérifier les taux de la parapharmacie dans le catalogue réel.
- [ ] **TVA sur le don partiel du reste à charge** (2026-11-14, scripts 071-072) : une réduction de prix sur un médicament est enregistrée comme don partiel (`vente.montant_don`), pas comme sortie de stock ; aucune TVA n'est calculée dessus. À confirmer avec le comptable : faut-il en reverser une, et sur quelle base ?
- [ ] **Remise automatique et médicaments** (2026-11-14) : la remise automatique est refusée sur un produit remboursable ; sur un médicament non remboursable, elle est enregistrée comme don partiel. Règle à confirmer avec l'Ordre ou la CNAS avant de l'utiliser sur des médicaments.
- [ ] **TVA sur les intérêts et la marge des prêts** (2026-11-14, script 078, Financements) : la TVA portée par une échéance est aujourd'hui comptée, avec les intérêts et la marge, dans la dépense « Charges financières » (décision du titulaire). À confirmer avec le comptable : cette TVA est-elle récupérable (à suivre à part) ou une charge ? La marge d'un financement islamique (mourabaha) se traite-t-elle comme des intérêts ? Rien n'est calculé : le logiciel reprend les montants du tableau de la banque.

## À confirmer avec les banques (Financements)

- [ ] **Tableaux d'amortissement réels** : aucun exemple d'une banque algérienne n'a été fourni. Obtenir de chaque banque du titulaire un tableau (Excel, CSV ou PDF) pour vérifier l'import : colonnes, ligne d'en-tête, format des dates et des montants, lignes de total, PDF scanné ou texte. La correspondance des colonnes est réglée par le titulaire et retenue par banque ; la lecture d'un PDF ou d'un scan est approximative et toujours vérifiée.
- [ ] **Capital restant dû** : selon la banque, le tableau peut l'indiquer avant ou après l'échéance ; le logiciel le reprend tel quel (contrôle : jamais en hausse). Si une banque l'indique avant l'échéance, préciser s'il faut le recalculer.
- [x] **Prêt d'une banque qui n'est pas un compte de la trésorerie**. (Tranché et livré le 2026-11-14, script 079 : « Autre prêteur (nom) », nom saisi librement ; son argent n'entre dans aucun compte suivi et ne sert que directement — facture, traite, dépense ; échéances payées depuis le compte choisi ; colonnes d'import retenues par prêteur.)
- [ ] **Remboursement anticipé, renégociation, échéance payée en partie** : non prévus (une échéance se paie en entier, dans l'ordre). À préciser si besoin.
- [ ] **Dettes des financements dans Dettes et créances** : montrées au titulaire seul (prudence) ; à dire si un employé qui gère la trésorerie doit les voir.

## À vérifier sur le terrain

- [ ] **Chifa : éviter la double saisie** (Chifa_Officine est un programme Windows installé sur chaque poste de caisse) :
  - sur un poste de caisse : noter l'heure, faire une vente tiers payant de test dans Chifa, puis chercher les fichiers modifiés à cette heure-là (dossier d'installation de Chifa, `C:\ProgramData`) ;
  - fournir le dossier, un fichier anonymisé (faux nom, faux n° d'assuré) et le nom et la version de Chifa (« À propos ») ;
  - ensuite : notre logiciel lit ces fichiers et crée la vente tiers payant chez nous (patient, produits, parts CNAS et patient, n° de facture Chifa, stock et registre), sans ressaisie ; les cas incomplets passent « à vérifier ».

- [ ] **Reprise réelle** : essayer l'assistant sur une copie de la base de l'ancien logiciel du pilote (pilote ODBC, mot de passe, séparateur des exports CSV, stock tenu par lots ou non).
- [ ] **Étiquetage du stock existant** : 17 % des boîtes sont identifiées sur la base de test. Il faut étiqueter rayon par rayon (à la fin de chaque inventaire tournant, ou dans Stock › Étiquettes internes) avant que le mode strict ne s'active.
- [ ] **Data Matrix** : part des boîtes qui en portent un dans la pharmacie (cela dépend des laboratoires et de la réglementation).
- [ ] **Modem GSM** : code PIN, opérateur, longueur réelle des SMS.
- [ ] **Compte SMTP** : serveur, port et mode TLS du fournisseur de la pharmacie.
- [ ] **Matériel** : scanner, caméra, tiroir-caisse, imprimantes ; essai sur iPhone ; SQL Server jamais essayé.
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

- [ ] Chifa : confirmer le sens du suffixe du n° d'assuré (« /00 », « /80 »…) en comparant les cartes Chifa d'un assuré et d'un membre de sa famille (ou numéro vert CNAS 3010) ; rien n'en est déduit aujourd'hui (voir docs/chifa.md).
