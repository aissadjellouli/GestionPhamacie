# Points restants

Liste de travail tenue à jour à chaque lot. Cocher un point quand il est livré et vérifié dans le poste client.

Dernière mise à jour : 2026-11-16 (bilan de la session du 13 au 16/11/2026).

## Livré et commité le 2026-11-16 (à pousser par le titulaire)

Commits `5db185c` (écrans), `4a0bfaa` (contenance), `97340dc` (synchronisation), `db955f4` (ce fichier).

- [x] **Synchronisation** : réglages descendants (scripts 130-131, central 004-005, table `reglage_central`) ; limite pendant un chargement et activation par groupes (central 006) ; familles nominatives (130), Pharmacie et Clients facturés (133, central 007), Banque élargie (135, central 008) ; conservation réglable au central (004) ; corrections de la revue globale et de l'audit de sécurité (136, central 009). Build et 1 918 tests verts, scripts rejoués deux fois sans effet.
- [x] **Contenance** : enregistrement depuis la nomenclature et sachets / ampoules (scripts 132 à 134, motif corrigé au 136, étendu au 137, puis aux flacons, seringues, stylos, cartouches, poches et tubes au 139 : 565 produits remplis sur dev et test ; 3 253 restent vides, dont 1 176 à plusieurs nombres ou conditionnements, 1 494 à contenant en tête sans « B/ », 96 flacons de poudre, 165 à texte en plus, 23 masses seules, 243 formes libres, 45 sans ligne de nomenclature, 11 désaccords ou vides).
- [x] **Écrans du super admin** : Reprise, Restauration, accès aux Profils de reprise.

## HTTPS sur le réseau de l'officine (décidé le 2026-11-16 : oui)

Option retenue : HTTPS seul sur le réseau, HTTP limité au PC serveur (`localhost:5080`). Voir `docs/mise-en-production.md`, parcours A, étapes 4.6 et 7.
- [x] `scripts/certificat_https.ps1` : HTTP sur `localhost:5080`, HTTPS sur `0.0.0.0:5443`, pare-feu 5443 en réseau privé, 5080 jamais ouvert (règles existantes signalées), export du `.crt` et de l'empreinte dans un dossier choisi, `-WhatIf`, idempotent. Essai en simulation seulement ; jamais exécuté en réel sur ce poste.
- [x] Distribution du certificat : clé USB pour les postes, hors réseau HTTP pour les téléphones, empreinte SHA-256 en texte (pas de QR code : aucune librairie dans la solution). L'API ne publie plus le `.crt` ; l'ancien chemin n'est servi qu'à 127.0.0.1 / ::1.
- [x] Noms et adresses supplémentaires (2e serveur de bascule, adresse VPN) : `-NomsSupplementaires` / `-AdressesSupplementaires`, à renseigner par le titulaire ; rien n'est supposé. Documenté dans `mise-en-production.md` (parcours A, étape 4.6) et `acces-distant.md`.
- [x] Mise à jour de `docs/mise-en-production.md` (installation du certificat sur chaque poste, `PHARMA_API=https://<serveur>:5443/`, vérifications), `scanner-mobile.md`, `acces-distant.md`.
- [x] Poste : exemple d'adresse `https://192.168.1.20:5443/` (écran Imprimantes), avertissement pour `http://` hors localhost (non bloquant : choix confirmé par le titulaire le 2026-11-16), bandeau à trois messages (injoignable, certificat refusé, pas encore basculé) ; journal local `liaison.log`.
- [ ] Essai réel en HTTPS : connexion, vente, impression, scanner mobile ; API en production en HTTPS jamais vérifiée.
- [ ] Port 5432 de la base fermé aux autres postes : aucune vérification tracée.

Vue d'ensemble courte (point d'avancement) : [reste-a-faire-synthese.md](reste-a-faire-synthese.md).

## Décisions en attente du titulaire

- [ ] **GED : protection des documents** (sujet à reprendre, 2026-11-17) : GED consultable au central ou non, sauvegarde chiffrée hors de l'officine (après le lot 9), rotation de deux disques pour une officine à un seul serveur, alerte « pas de sauvegarde sur disque externe ». Détail et recommandation : [ged-protection-a-decider.md](ged-protection-a-decider.md).

- [ ] **Écrans de vente** :
  - seuils d'alerte des psychotropes (valeurs de départ proposées : 2 fois en 30 jours, 1 fois par ordonnance et par an, plus de 3 boîtes ou plus de 28 jours, prescripteur à moins d'une délivrance par an) ;
  - liste des 214 DCI d'antibiotiques ;
  - format de l'export pour les impôts et taux de TVA.
- [ ] **Majorations Chifa** : quels types en plus de « Garde » ? (question posée le 15/11, sans réponse ; voir `docs/chifa.md`).
- [ ] **Démo** : « page démo », « démo interactive » ou « préparation démo cloud » (Dockerfile, docker-compose local, mode démo).
- [ ] **Script 111b** (séparation des rôles PostgreSQL) : à passer par le titulaire avec un superutilisateur ; sans lui, une falsification est détectée mais pas empêchée (voir `docs/securite-et-droits.md`).
- [x] **Réglages communs aux officines** : tranché le 2026-11-16 : « imposé » ou « proposé » est décidé par l'administrateur du serveur central, rien n'est figé dans le logiciel ; précisé le même jour : choix par table, changement tracé au central et dans l'officine, valeur imposée qui remplace la valeur locale (ancienne valeur au journal, titulaire prévenu). Reste à réaliser (fin du lot S3, `docs/conception-synchronisation.md` § 16).
- [ ] **Ancienne colonne `num_facture_chifa`** : le script `101b` est **écrit et testé sur copies jetables** (`database/migration/101b_suppression_num_facture_chifa.sql`) ; la **décision d'exécution reste en attente** du titulaire (destructif, sauvegarde obligatoire, procédure : `docs/mise-en-production.md`, étape 8). Le jour de l'exécution : retirer la colonne de `database/init/schema.sql` (colonne et son commentaire dans `vente`) et régénérer `database/cloud/002_tables_miroir.sql`.
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

- [x] Synchronisation S3, supervision et purge (2026-11-16, script 140) : purge de `sync_sortie` après accusé (30 jours, puis chargement complet), bandeau des postes, alertes au titulaire, écran « Supervision et purge » (`docs/conception-synchronisation.md` § 20).
- [ ] Synchronisation S3, suite : à confirmer avec le titulaire : valeurs de départ (bandeau 60 min, retard 24 h, erreur 60 min, conservation 30 jours), abonnement automatique des destinataires « sauvegarde » au sujet « synchronisation », SMS suivant l'option « SMS sauvegarde » ; à vérifier : durée des sauvegardes du serveur central (conservation), détection du quota par un 429 avec motif (essai avec le serveur central réel, champ explicite plus sûr).
- [x] Synchronisation S3, mutualisation des réglages : conception détaillée faite (`docs/conception-synchronisation.md` § 16 bis), décisions D1 à D14 acceptées par le titulaire le 2026-11-16 (recommandations), lots M1 à M9 réalisés. **Lots M2 (script officine 141) et M3 (script central 010, miroir régénéré) réalisés le 2026-11-16** (§ 16 bis.12) : tables et garde en place, rien imposé ni publié ; **Lot M6 réalisé (2026-11-16)** : outil d'administration du central (`--reglages-mutualises`, `--publier-reglage`, `--mode-reglage`, `--modele-depuis-officine`, `--liberer-reglages`, `--retablir-reglages`, `--journal-reglages` ; § 16 bis.12, manuel parcours E étape 9), essai réel sur base jetable réussi ; **à décider** : un mode « local » par table n'existe pas dans le script 010 (refusé par l'outil, libération par officine seulement) ; **Lot M4 réalisé le 2026-11-16** (application des modèles, Adopter, garde 409 dans les 11 cas d'usage, journal avant / après, message `reglages_centraux`, marqueur `PGOPTIONS` des outils de rejeu, preuve sur copie jetable : § 16 bis.12). **Lots M5 (routes de l'API locale) et M7 (écrans du poste : bandeau « Fixé / Proposé par le serveur central » dans les 9 écrans, Réglages fiscaux, Traçabilité, Synchronisation › Réglages mutualisés) réalisés le 2026-11-16** (§ 16 bis.12 ; essai à l'écran sur deux copies jetables, captures et mesures ; écarts à valider consignés dans le § 16 bis.12). **Décision D-M8-2 réalisée le 2026-11-17 (script officine 142)** : à la première réception d'un modèle proposé, une table jamais modifiée sur place depuis l'installation reçoit la valeur du central toute seule ; une table modifiée (avant ou après 141, d'après le journal d'audit) garde la sienne et se propose par « Adopter » ; limite : une modification faite en SQL direct n'a pas de trace (§ 16 bis.12). **M8 clos le 2026-11-17** (13 scénarios réussis sur un essai neuf ; défaut D-M8-3, libération et rétablissement, corrigé) **et M9 clos le 2026-11-17** (documentation finale, sans le chiffrement, qui reste le lot 9 sur « go chiffrement » ; § 16 bis.13). Constat hors lot : `database/init/seed_referentiel.sql` ne sème qu'une des 5 règles d'alerte des psychotropes (les 4 autres viennent des scripts 024 et suivants) ; le lot M4 crée les règles absentes quand il applique un modèle. Ordres du central vers l'officine (prix, brouillons de commande, employés) : lot S5 ; S4 (contrôle d'intégrité au central), S5 (diffusion des référentiels), S7 (écrans du central). Voir `docs/conception-synchronisation.md`.
- [ ] Découpage par officine des grandes tables du central, avant d'accueillir beaucoup d'officines.
- [ ] Multi-officines (lot 12) et partie cloud du lot 5 : après le choix de l'hébergeur.
- [ ] Restauration de la base d'une officine depuis le central (nouvelle époque + chargement) : non réalisée (`docs/conception-synchronisation.md` § 13.6).
- [ ] Compatibilité des versions du format d'échange : à coder à l'arrivée d'une version 2.
- [ ] Envoi des produits vers CHIFA-Officine et import de son fichier : selon la réponse de la CNAS.
- [ ] Effacement automatique au central : désactivé ; à activer après vérification des durées réglementaires.
- [ ] Nettoyage des données d'essai de `pharmacie_test` : sur une liste validée par le titulaire.
- [ ] **Lot 9, chiffrement** (colonnes sensibles, données au repos, sauvegardes) : en dernier, uniquement sur « go chiffrement ».

## À vérifier (aucune trace de vérification)

- [ ] Bases `pharmacie_recette_*` et `pharmacie_essai_restauration*` : mises à niveau le 2026-11-16 (scripts 060 à 136, sauf 101b et 111b) pour `pharmacie_recette_1` à `_4`, `pharmacie_recette_ui` et `pharmacie_essai_restauration` ; elles étaient en réalité à la version 060-069, pas 128. Tests 117 et 126 à 136 réussis (sauf le test 126 sur `pharmacie_recette_4`, faux positif lié aux données : le patient s'appelle « Patient »). `pharmacie_essai_restauration_avant_restauration_20261111_1909` : plus ancienne que 054, non mise à niveau (échec au script 067, remise en l'état d'origine). Rôles 111b non séparés, synchronisation désactivée. Reste à décider avec le titulaire : garder ou supprimer chaque base.
- [ ] Fichiers de l'autorité de certification d'essai restés dans un dossier temporaire de travail : à effacer.
- [ ] 1 à 3 échecs de connexion sécurisée (TLS) sous forte charge : cause non examinée.
- [x] Contenance, les deux produits non remplis (2026-11-16) : cause trouvée, ce n'étaient pas des erreurs du motif. « ENERMAG » (« B/20AMP.AUTOCASSABLES », unité suivie d'un complément) et « GEOLAX » (« B/10 SACH. ET B/20 SACH. », deux nombres) sont exclus par la règle du script 134, C# et SQL donnent le même résultat. Les 4 019 cas douteux sont classés dans `docs/regles-metier.md` (règle « script 137 ») : le script 137 en remplit 246 (unité de délivrance suivie de sa taille, « B/5 AMP. DE 2ML »), 3 773 restent vides, aucune autre extension n'est sûre. Script 137 validé par le titulaire le 2026-11-16 (« garder 137 »).
- [ ] Contenance : valeur proposée en grisé sur la fiche produit à vérifier à l'écran (non fait) ; décider si les conditionnements « B/N FL », « B/N SERINGUE » (854 produits) doivent donner N, et si une saisie en lot (écran « Contenance manquante ») est utile pour les 3 773 restants.
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
- [x] Remise négative acceptée sans message (ramenée à 0) ; coupure inconnue ignorée à la clôture (caisse fermée avec 0 compté) ; saisie énorme au comptage. (Corrigé le 2026-11-14 pour la clôture : coupure inconnue refusée ; au plus 100 000 billets ou pièces par coupure et 10 000 000 DA en « autres » ; clôture ou relève à 0 DA compté sur confirmation explicite. Corrigé le 2026-11-16 : remise négative refusée avec un message au poste et au serveur, voir docs/regles-metier.md. Rien ne reste.)
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
- [x] Scanner mobile (lot 3) : contrôle en rayon, dates à vérifier, rangement, inventaire, scans de réception importés sur le poste ; HTTPS local (scripts/certificat_https.ps1).
- [x] Saisie assistée du produit (liste sous le champ, flèches et Entrée) dans Commandes et dans Réception.
- [x] Lot unique par produit, n° de lot et grossiste ; étiquette interne créée et imprimée pour chaque boîte sans Data Matrix à la réception.
- [x] Inventaire tournant (lot 2), emplacements (lot 1), notifications au titulaire et sauvegardes.

- [ ] Chifa : confirmer le sens du suffixe du n° d'assuré (« /00 », « /80 »…) en comparant les cartes Chifa d'un assuré et d'un membre de sa famille (ou numéro vert CNAS 3010) ; rien n'en est déduit aujourd'hui (voir docs/chifa.md).

## Mise en production : à décider (2026-11-16)

Points que `docs/mise-en-production.md` ne peut pas trancher sans le titulaire ou le développeur (marqués **[À CONFIRMER]** dans le manuel) :

- [ ] Sauvegardes : un seul mécanisme, sauvegarde de l'application ou script planifié `scripts/sauvegarde_pharmacie.ps1` (son en-tête dit « pas les deux »). Régler aussi ses variables `D:\` et `E:\`.
- [ ] Script `101b` (suppression de `num_facture_chifa`) : écrit et testé ; reste la décision d'exécution du titulaire (procédure dans `docs/mise-en-production.md`, étape 8).
- [ ] Conflits de n° de facture Chifa (script 101) : bloquent-ils la remise en service ?
- [x] HTTPS sur le 2e serveur et le PC de secours : une seule autorité par pharmacie (PFX protégé au coffre), certificats des autres PC signés par elle (`-CertificatPourAutrePc`, `-Importer`, `-RestaurerAutorite`), procédures dans `docs/mise-en-production.md` (parcours A 4.6, C 8, D 6, F2 bis, F6, F7). Essai de chaîne en mémoire (`CertificatsHttpsTests`) ; script jamais exécuté en réel (à essayer sur le terrain).
- [x] `AdresseApiSecours` des postes : `https://<nom ou IP>:5443/` documentée (manuel, `basculer-pc-secours.ps1`). Corrigé (2026-11-16) : l'exemple affiché dans l'écran Administration › Imprimantes (`http://192.168.1.20:5080/`, `src/Pharma.Desktop/Composants/Pages/AdminImprimantes.razor`), le message de refus de `ReglagesPoste.ControlerAdresse` (avertissement non bloquant pour `http://` hors localhost) et le bandeau (injoignable, certificat refusé, pas encore basculé : trois messages distincts).
- [x] Super admin : création sans modem GSM (sans code SMS) possible ou non ? **Terminé le 2026-11-16.** Décision : au moins un canal vérifié (SMS si un modem fonctionne, e-mail si le serveur d'e-mail est réglé) ; téléphone et e-mail ne sont plus obligatoires tous les deux ; création refusée si aucun canal ne fonctionne (message : régler le modem GSM ou le serveur e-mail dans Administration › Notifications). Cela remplace l'ancienne règle « sans modem, création possible après confirmation ». Un canal non vérifié n'est pas enregistré ; le dernier canal vérifié ne peut pas être retiré ; le code part sur tous les canaux vérifiés du compte.
- [ ] Livraison officielle : aucun paquet de production dans le dépôt (`scripts/paquet` = essai). Définir la publication et le dossier de destination de l'API, des postes et des `scripts` sur chaque PC.
- [ ] Partage de la copie de secours : compte du service de l'API (compte système par défaut) et droits sur le partage.
- [x] Port 5432 vers le 2e serveur : `scripts/second-serveur/ouvrir-port-base.ps1` crée la règle (entrée TCP, profil Privé, adresse du 2e serveur seulement, `-WhatIf`, avertissement si une autre règle ouvre 5432). Reste : intégrer l'étape au parcours C du manuel (remplace le [À CONFIRMER]) et l'essayer sur le vrai PC serveur.
- [x] Scripts de mise en production (2026-11-16) : `scripts/mep/creer_base_officine.ps1` (base neuve), `mettre_a_jour_base_officine.ps1` (officine en service), `creer_base_centrale.ps1` (serveur central), documentés dans `docs/scripts-base-de-donnees.md` ; manuel mis à jour (parcours A étape 3, B étapes 3 à 10, E étape 2). Essayés sur des bases jetables (neuve : schéma identique à `pharmacie_dev` ; mise à jour : copie de `pharmacie_recette_1`, arrêt sur conflit Chifa vérifié ; central : scripts 001 à 009 et tests réussis), bases supprimées ensuite. Reste : essai de `111b` et de `SET ROLE` avec `postgres` (ci-dessus), ajout des fichiers à `PharmaOfficine.sln` / `.slnx`.
- [x] Scripts `007`, `018`, `097`, `098` non rejouables sur une base à jour : corrigés par une garde sur le catalogue (vue recréée seulement si elle n'a pas déjà les colonnes de la version finale) ; `024` (colonnes nommées au lieu de `r.*`), `126` et `129` (fonction `sync_enregistrer_lot` non ramenée à une ancienne version) corrigés aussi. `mettre_a_jour_base_officine.ps1` rejoue toujours depuis 001 (départ automatique à 101 supprimé, `-ScriptDepart` gardé pour les incidents). Garde-fous : `ScriptsRejouablesTests` et `database/tests/rejeu_complet.ps1`. Observé une fois à l'essai de restauration, sans cause établie : échec transitoire de l'échange des bases (erreur de droit au renommage, `RestaurationPostgres.RenommerAsync`), réussi au second essai ; à surveiller.
- [x] GED du 2e serveur (2026-11-17) : la GED ne va jamais au central ; copie automatique du principal vers le 2e serveur, sans suppression, à la fréquence réglée dans l'application (voir la décision du 2026-11-17 plus bas dans cette ligne) (`scripts/second-serveur/copier-ged.ps1`, `partager-ged.ps1`, tests `CopieGedSecondServeurTests`), manuel : parcours C étapes 12 bis à 12 quater, F3 (2 bis, 3 bis), F4 (2, 3 bis, 3 ter, 4). Essayé en local entre deux dossiers (nouveaux, modifiés, suppression gardée, plus récent non écrasé, relance sans recopie) ; `-Installer`, `-Desinstaller` et le partage seulement en `-Essai`. Reste à faire sur les vrais serveurs : **[À CONFIRMER]** SMB entre deux PC hors domaine avec un compte local de même nom et même mot de passe, droit « Ouvrir une session en tant que tâche » du compte `PharmaGedCopie`, durée de la première copie ; ajout des deux scripts et du fichier de tests à `PharmaOfficine.sln` / `.slnx` si besoin. Les N dernières minutes avant une panne peuvent manquer (le poste ne garde pas un document déjà reçu par le principal). **Décision du titulaire (2026-11-17) : la fréquence de la copie est RÉGLABLE dans l'application (Administration › 2e serveur › Copie de la GED, 1 à 60 minutes, 5 par défaut, titulaire seul, journal avant/après)** ; fait : script SQL 143 (`surveillance_replication.ged_copie_minutes`), route `PUT /api/admin/second-serveur/copie-ged`, écran, `copier-ged.ps1` (tâche chaque minute, copie seulement si N minutes sont passées, lecture du réglage dans la base locale du 2e serveur en lecture seule, 5 par défaut si illisible), `autoriser-lecture-copie-ged.ps1` (rôle de lecture sans mot de passe) ; reste à faire sur les vrais serveurs : lancer `autoriser-lecture-copie-ged.ps1` sur le principal puis sur le 2e serveur (parcours C, étape 12 ter bis), **décidé le 2026-11-17 : authentification Windows (SSPI)** au lieu d'une règle `trust` : seul le compte Windows de la tâche de copie, sur ce PC, est accepté comme `pharma_ged_lecture` (`pg_ident.conf` et `pg_hba.conf`, boucle locale seulement) ; **[À VÉRIFIER]** sur le premier vrai serveur : nom présenté par Windows pour le compte local (`PharmaGedCopie@<NOM DU PC>`) ; alternatives écartées : `trust` (tout programme du PC) et un mot de passe en clair dans le profil du compte de la tâche.
- [ ] `Jwt__Cle` identique sur le PC de secours et le 2e serveur, ou reconnexion acceptée après bascule ?
- [ ] `pharma_proprietaire` : méthode d'exécution des scripts après 111b (`postgres` avec `SET ROLE`, intégrée à `scripts/mep/mettre_a_jour_base_officine.ps1`) **à essayer sur une copie avec le compte `postgres`** : non testée le 2026-11-16 (pas de mot de passe superutilisateur ni de `pgpass.conf` sur le PC de développement ; le script ne s'est exécuté qu'en `-WhatIf` pour cette partie). Compte d'administration à retenir.
- [x] Remise à vide après 111b : le manuel ne passe plus par `clean.sql` ; le script `creer_base_officine.ps1` exige une base absente, la suppression d'une base neuve se fait à la main par `postgres` (parcours A, « Recommencer depuis zéro »).
- [ ] Restauration du PC de secours : rôles `pharma_proprietaire` et `pharma_app` requis avant `basculer-pc-secours.ps1` ?
- [ ] `--importer-cle` quand une clé existe déjà ; `Sauvegardes__PgDump` vide.
- [ ] Serveur central : hébergement, adresse d'écoute (`Cloud:Adresse` vaut `127.0.0.1` par défaut), mode de lancement et compte de service (pas de service Windows dans le code), point de contrôle de santé, nom de la base et compte propriétaire de production, tests sur la production ou sur une copie.
- [ ] CHIFA-Officine, lecteur de carte et Token : procédure d'installation et PC concerné (hors dépôt).
