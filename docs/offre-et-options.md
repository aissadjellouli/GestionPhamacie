# Offre de base et options vendues à la demande

> Décision du titulaire du 2026-11-26. **Mécanisme d'activation et fermeture des packs codés le 2026-11-26** (lots O1 à O6, voir « Mise en œuvre »).

## Le besoin

Certaines fonctionnalités de l'application deviendront des **options vendues à part**, activées à la demande d'une pharmacie :

- une **offre de base** (« basic ») est livrée à toutes les pharmacies, avec les fonctions de base ;
- les **autres fonctionnalités** sont livrées à la demande et **facturées à part** ;
- certaines limites sont **fixées par le super admin**. Premier cas : le **nombre maximum de postes autorisés**.

Le partage entre offre de base et options **n'est pas encore décidé**. Il sera fait lors d'un tour complet des fonctionnalités, à partir de
[fonctionnalites.md](fonctionnalites.md), qui liste toutes les fonctionnalités faites et en attente.

## Exigence : rien de modifiable dans la base

Une option activée, ou une limite comme le maximum de postes, **ne doit pas pouvoir être changée en modifiant la base de données**.

Constat (2026-11-26) : toute valeur rangée en clair dans la base peut être changée par quelqu'un qui a l'accès à PostgreSQL, et c'est le cas
chez la pharmacie. Aujourd'hui, le maximum de postes est dans `parametre_postes.maximum_postes` (script 167) et le titulaire le règle à
l'écran (Administration › Postes autorisés).

Piste technique, **non validée** : les options et les limites d'une pharmacie sont **signées** avec une clé privée qui n'est jamais chez la
pharmacie. L'API vérifie cette signature avec la clé publique intégrée à son programme, et refuse toute valeur modifiée à la main.

## Questions ouvertes

> Réponses du titulaire du 2026-11-26 : voir « Décisions » en fin de document (reste ouvert : durée de la transition, Q8).

1. **Qui est le super admin** : l'éditeur du logiciel ou quelqu'un de la pharmacie ? Si la clé privée est chez la pharmacie, la signature
   ne protège plus rien.
2. **Comment l'activation arrive** :
   - (a) un code signé, généré par un outil gardé chez l'éditeur et saisi par le super admin ;
   - (b) une activation signée envoyée par le serveur central ;
   - (c) autre chose.
3. **Liée à une pharmacie précise ?** Sinon, une même activation pourrait être recopiée d'une pharmacie à l'autre.
4. **Si la signature est absente ou fausse** : quel comportement (option fermée, limite minimale, valeur par défaut) ?
5. **Activation limitée dans le temps** (abonnement) ou définitive ? Que se passe-t-il à l'échéance : prévenir avant, puis fermer l'option ?
   Quelles données restent consultables ?
6. **Ce que voit le titulaire** : la liste de ses options et limites, en lecture seule, et la marche à suivre pour en demander une.
7. **2e serveur et serveur central** : les activations doivent valoir sur le 2e serveur après une bascule. Que fait le central ?
8. **Pharmacies déjà installées** : qu'est-ce qui reste ouvert pour elles lors du passage à l'offre de base ?

## Proposition de découpage (recommandation du 2026-11-26, à valider pack par pack)

Faite à partir des 275 fonctionnalités de [fonctionnalites.md](fonctionnalites.md). Principes retenus :

1. **Ce que la loi ou la sécurité exige reste dans la base**, jamais vendu à part : journal fiscal et scellements, registre des
   psychotropes, journal des actions, intégrité, sauvegardes, droits. Faire payer une obligation exposerait le titulaire et l'éditeur.
2. **Ce qu'une pharmacie fait chaque jour reste dans la base** : vendre, encaisser, tiers payant Chifa, recevoir, commander, gérer le
   stock. Sans Chifa, le logiciel n'est pas utilisable en Algérie.
3. **Les options sont regroupées en packs** (6 à 8) plutôt que vendues une par une : plus simples à vendre, à activer et à tester.
4. **Les quantités sont des quotas** (postes, scanners mobiles), séparés des packs.
5. **Option fermée = données gardées** : rien n'est effacé ; l'écran disparaît ou passe en lecture seule. Réactiver rend tout.

### Offre de base (« Essentiel »)

| Domaine | Inclus |
|---|---|
| Caisse et ventes | Toute la caisse : recherche, FEFO, panier, encaissement (espèces, carte, chèque), tiers payant, crédit, remises, prestations, avoirs, retours, ventes en attente, ticket imprimé, suggestions |
| Contrôle de caisse | Ouverture, clôture à l'aveugle, caisse partagée, annulation, état de la caisse, versement au coffre, tiroir, passation |
| Stock | Consultation, emplacements, sorties (périmé, casse, don), correction d'entrée, blocage, seuils d'alerte, **inventaire complet annuel** |
| Achats | Bons de commande, réception (Data Matrix compris), facture d'achat, réceptions en attente, retours au grossiste, payer un grossiste, traites |
| Catalogue | Nomenclature nationale, création de produit, catégories, indications, contenance, substitution |
| Psychotropes et antibiotiques | Registre complet, alertes à la délivrance, suivi des antibiotiques (**obligations**) |
| Patients et crédit | Fichier des patients, assuré principal, crédit, encaisser une dette, fiche contact |
| Chifa | Volet scanné, bordereaux, rejets, paiements, MDN, centres payeurs, droits expirés |
| Alertes | Péremption, rupture, stock critique, rapport quotidien à l'écran |
| Rapports | Rapports jour / semaine / mois, tableau de bord d'accueil |
| Fiscal et intégrité | Journal chaîné, scellements, clôture mensuelle, TVA, export d'audit, journal des actions (**obligations**) |
| Sauvegardes | Sauvegarde quotidienne sur disque externe, rotation des disques, test de restauration, restauration par le super admin, GED locale |
| Sécurité et administration | Utilisateurs, droits, code comptoir, HTTPS, postes autorisés, réglages, imprimantes, thème, navigation |
| Quota | **2 postes inclus** (à confirmer) |

### Packs en option

| Pack | Contenu | Pour qui |
|---|---|---|
| **A. Contrôle et anti-fraude** | Contrôle interne (anomalies par employé), pertes et vigilance par employé, réception à l'aveugle avec double saisie, ancien lot délaissé et alertes au titulaire | Pharmacie avec plusieurs employés : c'est le pack qui fait gagner de l'argent au titulaire |
| **B. Inventaire et traçabilité** | Inventaire tournant, identité par boîte et frise, étiquettes EAN et imprimante thermique, rappel de lot avec patients à contacter, scanner mobile | Pharmacie qui veut un stock juste au jour près |
| **C. Achats avancés** | Multi-grossistes (import des catalogues, comparaison des prix), historique des ruptures, commandes intelligentes (V2), prévenir un patient quand un produit revient | Pharmacie à gros volume d'achats |
| **D. Gestion financière** | Trésorerie (coffre, banques, dépenses), dettes et créances avec échéancier, financements et prêts, tableau de bord financier, marge par produit ou laboratoire, factures aux entreprises et organismes | Titulaire qui gère sa comptabilité dans le logiciel |
| **E. Pilotage et notifications** | Pilotage mobile du titulaire, rapport quotidien par e-mail, SMS d'urgence (modem GSM) | Titulaire souvent absent du comptoir |
| **F. Continuité de service** | 2e serveur, réplication synchrone, bascule automatique, relais, copie de la GED, PC de secours | Pharmacie qui ne peut pas s'arrêter (garde, gros débit) |
| **G. Multi-officines et accès à distance** | Synchronisation avec le central, accès à distance, vue d'ensemble de plusieurs officines, réglages communs, ordres et messages à distance, sauvegarde chiffrée hors de l'officine (lot 9) | Titulaire de plusieurs officines, ou qui veut voir sa pharmacie de chez lui |
| **H. Confort du comptoir** | Afficheur client, lecture d'ordonnance par OCR, lecture assistée des ordonnances manuscrites | Comptoir à fort passage |

### Quotas et services

- **Postes** : 2 inclus, puis par poste supplémentaire (fixé par le super admin, non modifiable dans la base : premier cas d'usage).
- **Scanners mobiles** : nombre d'appareils (si le pack B est pris).
- **Services facturés à part, hors logiciel** : reprise des données d'un ancien logiciel, installation, formation, installation du 2e serveur.

### Points d'attention

- **Sérialisation nationale** et **répertoire national des ordonnances** : à mettre dans la base dès qu'ils deviennent obligatoires.
- **Rapports** : les rapports simples restent dans la base, seul le tableau de bord financier est payant.
- **Pack F** : vendu avec une installation faite par l'éditeur (essai de bascule réel obligatoire, procedure-deux-serveurs.md).
- **Pharmacies déjà équipées** : à décider (tout garder ouvert, ou période de transition).

## Prochaines étapes

1. Tour des fonctionnalités avec le titulaire : classer chaque fonctionnalité en **base** ou **option**.
2. Réponses aux questions ouvertes.
3. Plan technique (mécanisme d'activation signé, écran du super admin, lecture seule pour le titulaire, contrôle par le serveur), validé
   avant tout code. Premier cas d'usage : le maximum de postes autorisés.

## Mise en œuvre (2026-11-26, « go all lot »)

| Lot | Contenu | État |
|---|---|---|
| O1 | Activation signée : code `PO1.<contenu>.<signature>` (ECDSA P-256, SHA-256), contrôlé par l'API avec la clé publique intégrée au programme (`CleEditeur`) ; lié au code de l'officine et au NIF ; table `activation_offre` (script 172) ; offre en vigueur = la plus récente activation valable, sinon offre de base (2 postes) | Fait |
| O2 | Maximum de postes lu dans l'offre (plus réglable par le titulaire ; `parametre_postes.maximum_postes` n'est plus lu) | Fait |
| O3 | Outil de l'éditeur `src/Pharma.Editeur` : `generer-cle`, `cle-publique`, `signer`, `lire` (jamais livré aux pharmacies) | Fait |
| O4 | Écran Administration › Mon offre (titulaire) : offre en lecture seule, packs, postes, échéance, saisie d'un code, codes enregistrés | Fait (mesuré à 0 px, clair et sombre) |
| O5 | Voie du central : ordre `activation_offre` (script central 044, console `--ordre-activation`) ; le central transmet, l'officine contrôle | Fait (console ; pas d'écran de l'éditeur au central) |
| O6 | Fermer les packs non inclus : filtre `ExigerPack` sur les routes (refus 403 « Option non incluse — voir Mon offre » ; lecture permise pour B, C, D et les réglages du 2e serveur F ; A fermé entièrement, anomalies enregistrées en silence), comportements (réception à l'aveugle sans A ; aucun versement au coffre à la clôture sans D ; facture à une entreprise ou un organisme : D ; rapport quotidien : E ; afficheur client : H), alertes au titulaire selon les packs (sécurité toujours), synchronisation sans G (seuls les résultats d'ordres déjà reçus remontent), bandeau « Option non incluse » sur les écrans des packs | Fait (essayé en réel, bandeaux mesurés à 0 px, clair et sombre) |

### Rattachement des routes (lot O6, figé par `PacksOffreRoutesTests`)

| Pack | Routes fermées | Restent dans la base |
|---|---|---|
| A | `/surveillance` (liste, traitement, réglages, produits sensibles, risques) | tiroir, intégrité, journal fiscal |
| B | traçabilité par boîte, étiquetage, consultation d'un rappel, sérialisation, `/mobile`, démarrage d'un inventaire tournant et ses réglages | rappel (blocage) d'un lot, comptage (sert à l'inventaire complet), inventaire complet |
| C | multi-grossistes (catalogues, comparaison, répartition), `/achats/ruptures`, `/caisse/demandes-patients` | bons de commande et leur envoi par e-mail, réception, facture d'achat, retours |
| D | `/tresorerie`, `/suivi-dettes`, `/financements`, `/pilotage/financier` ; facture à une entreprise ou un organisme | facture à un patient, rapports |
| E | `/pilotage`, rapport quotidien envoyé tout de suite | alertes de sécurité |
| F | réglages `/admin/second-serveur` (consultation permise) | vigie et bascule de la paire déjà installée |
| G | activation, envoi, rechargement, réglages, supervision, accès à distance, nouvelle époque de `/admin/synchronisation` | statut, ordres déjà reçus et leurs résultats |
| H | `/ordonnances/analyser`, `/ordonnances/scans` (OCR) | lecture manuelle de l'ordonnance |

**Limites connues** : le quota de **scanners mobiles** n'est pas compté (aucun registre des téléphones : seul le pack B ouvre `/mobile`) ;
pas de bandeau dans l'onglet multi-grossistes des Achats ni sur l'ordonnance scannée de la caisse (le refus du serveur s'affiche) ; les
boutons d'action ne sont pas grisés : le bandeau prévient, le serveur refuse.

### Mise en service (à faire une fois par l'éditeur)

> Procédure détaillée, pas à pas : [procedure-cle-editeur.md](procedure-cle-editeur.md). **Clés créées et intégrées le 2026-11-26.**

1. **Générer DEUX clés** (« go clé de secours », 2026-11-26) sur le PC de l'éditeur, **jamais sur le serveur d'une officine**, hors de
   tout dépôt, dans deux dossiers différents et avec **deux mots de passe différents** (12 caractères au moins chacun) :
   `dotnet run --project src\Pharma.Editeur -- generer-cle --dossier <dossier de la clé principale>` puis la même commande avec
   `--dossier <dossier de la clé de secours>`. Ranger chaque fichier `cle-editeur.pem` sur sa propre clé USB, dans deux lieux différents,
   et noter les deux mots de passe à part. La clé principale signe ; la clé de secours ne sert que si la principale est perdue ou volée.
   Une clé ne se régénère jamais à l'identique.
2. **Intégrer les deux clés publiques** (contenu de chaque `cle-editeur-publique.txt`) dans `src/Pharma.Application/Offre/ActivationOffre.cs` :
   `CleEditeur.PrincipaleBase64` et `CleEditeur.SecoursBase64`, puis livrer cette version. **Tant que les deux sont vides, aucun code n'est
   accepté : toutes les pharmacies sont à l'offre de base (2 postes, aucun pack : écrans des packs A à H fermés)** ; les postes déjà
   autorisés restent, seules les nouvelles autorisations sont refusées. Bases de développement et d'essai comprises : leur saisir un code de
   transition. **Clé principale perdue** : signer avec la clé de secours (aucune nouvelle version, aucun retour à l'offre de base), puis
   générer une nouvelle clé de secours à la version suivante. **Clé principale volée** : signer avec la clé de secours et retirer la
   principale de la liste à la version suivante (ses codes ne valent plus : nouveaux codes à signer pour les pharmacies concernées).
3. **À chaque mise en place ou mise à jour d'une pharmacie** : relever son code d'officine et son NIF (Administration › Mon offre), signer un
   code de transition `signer --cle <cle-editeur.pem> --officine <code> --nif <NIF> --postes <n> --transition --fin AAAA-MM-JJ` (durée fixée
   à ce moment-là), le saisir dans Mon offre (connecté en titulaire) ou l'envoyer par le central :
   `Pharma.Cloud.Api.exe --ordre-activation --officine <code> --code <code> --auteur <nom>`.
4. **Vente d'une option** : signer un nouveau code (packs `--packs A,D`, postes, scanners, `--fin` si abonnement) ; le plus récent l'emporte.

## Décisions

| Date | Décision |
|---|---|
| 2026-11-26 | Des fonctionnalités deviendront des options vendues à part, activées à la demande ; une offre de base est livrée à toutes les pharmacies. |
| 2026-11-26 | Le maximum de postes autorisés est fixé par le super admin, sans possibilité de le modifier dans la base. |
| 2026-11-26 | Q1 : le super admin est **l'éditeur du logiciel** ; la clé privée qui signe les activations reste chez l'éditeur, jamais chez la pharmacie. |
| 2026-11-26 | Q2 : pharmacie reliée au **serveur central** : c'est le central qui gère et transmet l'activation signée ; sinon, **code signé** saisi sur place (produit par un outil gardé chez l'éditeur). |
| 2026-11-26 | Q3 : l'activation est **liée à une pharmacie précise** (son identifiant dans l'activation signée) : une activation recopiée ailleurs est refusée. |
| 2026-11-26 | Q4 : signature absente ou fausse : **retour à l'offre de base** (options fermées, maximum de postes ramené à 2), titulaire prévenu, inscrit au journal ; une vente en cours n'est jamais interrompue. |
| 2026-11-26 | Q5 : activation **définitive ou limitée dans le temps** (date de fin facultative dans l'activation) ; à l'échéance : titulaire prévenu 30 puis 7 jours avant, délai de grâce de 15 jours, puis option fermée (écran masqué ou en lecture seule), **aucune donnée effacée**. |
| 2026-11-26 | Q6 : écran **« Mon offre »** en lecture seule pour le titulaire (packs, quotas, dates de fin), champ de saisie d'un code d'activation, coordonnées de l'éditeur pour demander une option. |
| 2026-11-26 | Q7 : l'activation est rangée dans la base répliquée (valable sur le 2e serveur après une bascule) ; la signature est contrôlée sur chaque serveur ; le central **transmet** une activation signée par l'éditeur, il ne peut pas en créer. |
| 2026-11-26 | Q8 : pharmacies déjà installées : **tout reste ouvert pendant une période de transition** (code « tout compris » limité dans le temps), puis passage à leur offre réelle. **Durée de la transition : à définir.** |
| 2026-11-26 | Liaison (Q3 précisée) : l'activation contient le **code de l'officine** (`officine_id`) **et son NIF** ; le NIF doit être saisi avant l'activation. |
| 2026-11-26 | Clé de l'éditeur : **fichier chiffré par un mot de passe**, hors de tout dépôt, sur le PC de l'éditeur, avec une copie sur une clé USB rangée à part ; l'outil demande le mot de passe à chaque signature. |
| 2026-11-26 | Transition (Q8 précisée) : **code de transition** « tout compris » signé, saisi par l'éditeur lors de la mise en place ou de la mise à jour, avec une date de fin choisie à ce moment-là. **Sans aucune activation valable : offre de base, 2 postes** ; une activation effacée ramène à l'offre de base, jamais à tout ouvert. |
| 2026-11-26 | **Tour des packs : recommandations acceptées** (« prends tes recommandations ») : packs A à H confirmés, avec ces précisions. A : la **réception à l'aveugle avec double saisie** entre dans le pack A (la base garde la réception simple). B : l'**inventaire complet annuel** et le **blocage d'un lot rappelé** restent dans la base ; la liste des patients à contacter et son export vont dans B. C : bons de commande, réception, facture d'achat et retours restent dans la base. D : la **facture à un patient** reste dans la base ; sans D, la clôture de caisse ne verse rien en trésorerie ; les rapports simples restent dans la base. E : les **alertes de sécurité** par SMS ou e-mail (sauvegarde absente, rupture d'intégrité, poste ou serveur isolé) restent dans la base. F : sauvegarde quotidienne, rotation des disques et test de restauration restent dans la base. Quotas : 2 postes et **0 scanner mobile** dans la base. **Fermeture d'un pack** : packs avec données (A à D) en **lecture seule** (actions refusées « Option non incluse — voir Mon offre », rien d'effacé) ; packs sans données propres (E à H) **arrêtés** ; tâches automatiques du pack arrêtées ; le serveur refuse lui-même les routes d'un pack fermé. |
| 2026-11-26 | Lot O6 (« 1 reco, 2 reco, 3 reco, go O6 ») : (1) sans le pack A, les anomalies sont **enregistrées en silence** et deviennent visibles, avec leur historique, dès l'achat du pack ; (2) pack F échu alors que les deux serveurs tournent : la paire **continue de fonctionner** (réplication, bascule), le titulaire est prévenu, aucune nouvelle installation, écrans de réglage du 2e serveur en lecture seule ; (3) pack G échu : la synchronisation s'arrête, mais les **résultats des ordres déjà reçus** sont encore envoyés. |
