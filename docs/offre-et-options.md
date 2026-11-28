# Offre de base et options vendues à la demande

> Décision du titulaire du 2026-11-26. **Mécanisme d'activation et fermeture des packs codés le 2026-11-26** (lots O1 à O6, voir « Mise en œuvre »).

## Le besoin

Certaines fonctionnalités de l'application deviendront des **options vendues à part**, activées à la demande d'une pharmacie :

- une **offre de base** (« basic ») est livrée à toutes les pharmacies, avec les fonctions de base ;
- les **autres fonctionnalités** sont livrées à la demande et **facturées à part** ;
- certaines limites sont **fixées par l'éditeur**. Premier cas : le **nombre maximum de postes autorisés**.

> Vocabulaire (2026-11-26) : dans ce document, celui qui signe les offres et fixe les limites est **l'éditeur**. Le « super admin » est
> seulement le compte de reprise de données et de restauration créé sur le PC serveur (rôle `super_admin`, voir
> [mise-en-production.md](mise-en-production.md), parcours A, étape 10) : il ne signe rien et ne fixe aucune limite.

Le partage entre offre de base et options **n'est pas encore décidé**. Il sera fait lors d'un tour complet des fonctionnalités, à partir de
[fonctionnalites.md](fonctionnalites.md), qui liste toutes les fonctionnalités faites et en attente.

## Exigence : rien de modifiable dans la base

Une option activée, ou une limite comme le maximum de postes, **ne doit pas pouvoir être changée en modifiant la base de données**.

Constat (2026-11-26) : toute valeur rangée en clair dans la base peut être changée par quelqu'un qui a l'accès à PostgreSQL, et c'est le cas
chez la pharmacie. Avant le lot O2 (voir « Mise en œuvre »), le maximum de postes est dans `parametre_postes.maximum_postes` (script 167) et le titulaire le règle à
l'écran (Administration › Postes autorisés).

Piste technique, **non validée** : les options et les limites d'une pharmacie sont **signées** avec une clé privée qui n'est jamais chez la
pharmacie. L'API vérifie cette signature avec la clé publique intégrée à son programme, et refuse toute valeur modifiée à la main.

## Questions ouvertes

> Réponses du titulaire du 2026-11-26 : voir « Décisions » en fin de document (reste ouvert : durée de la transition, Q8).

1. **Qui signe les activations** (question posée alors sous le nom « super admin ») : l'éditeur du logiciel ou quelqu'un de la pharmacie ?
   Si la clé privée est chez la pharmacie, la signature ne protège plus rien.
2. **Comment l'activation arrive** :
   - (a) un code signé, généré par un outil gardé chez l'éditeur et saisi sur place ;
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
| **H. Comptoir assisté** | Afficheur client, lecture d'ordonnance par OCR, lecture assistée des ordonnances manuscrites | Comptoir à fort passage |

### Quotas et services

- **Postes** : 2 inclus, puis par poste supplémentaire (fixé par l'éditeur, non modifiable dans la base : premier cas d'usage).
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
3. Plan technique (mécanisme d'activation signé, outil de l'éditeur, lecture seule pour le titulaire, contrôle par le serveur), validé
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
| G | activation, envoi, rechargement, réglages (sauf désactiver), supervision, accès à distance, nouvelle époque de `/admin/synchronisation` | statut, ordres reçus et leurs résultats, désactivation de la synchronisation (2026-11-27) |
| H | `/ordonnances/analyser`, `/ordonnances/scans` (OCR) | lecture manuelle de l'ordonnance |

**Limites connues** : le quota de **scanners mobiles** n'est pas compté (aucun registre des téléphones : seul le pack B ouvre `/mobile`) ;
pas de bandeau dans l'onglet multi-grossistes des Achats ni sur l'ordonnance scannée de la caisse (le refus du serveur s'affiche) ; les
boutons d'action ne sont pas grisés : le bandeau prévient, le serveur refuse.

**Rappels de fin d'offre** (codés le 2026-11-26, `src/Pharma.Application/Offre/Offre.cs`, classe `RappelsOffre`, lancée par la tâche
des notifications de l'API) : pour une offre ou une période de transition qui a une date de fin, le titulaire reçoit une alerte par SMS
ou par e-mail (mêmes destinataires que les alertes techniques) **90, 60, 30, 15, 7 et 1 jour(s)** avant la fin (« go rappels » du
2026-11-27), puis au **début du délai de grâce de 15 jours**. Chaque étape n'est envoyée **qu'une fois** pour une date de fin donnée (journal des actions, action `offre_rappel`) ; si
plusieurs étapes sont dues le même jour, seule la plus proche de la fin est envoyée. L'écran Mon offre garde son message dès 90 jours. La vérification
se fait une fois par jour ; l'alerte part avant d'être notée (au pire un rappel en double, jamais un rappel perdu) et l'étape n'est notée que si
l'alerte a vraiment été mise en file, au moins un message (2026-11-27 : sinon, sans destinataire ou file en panne, nouvel essai le lendemain ;
`IAlertesTitulaire.OffreAsync` rend le nombre de messages mis en file). Une nouvelle date de fin
(nouveau code) relance les sept étapes.

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
3. **À chaque mise en place ou mise à jour d'une pharmacie** : relever son code d'officine, son NIF et l'**empreinte de chaque serveur**
   (Administration › Mon offre, ou `Pharma.Api.exe --empreinte-serveur` sur chaque serveur), signer un code de transition
   `signer --cle <cle-editeur.pem> --officine <code> --nif <NIF> --postes <n> --serveur <empreinte> [--serveur <empreinte> …] --transition --fin AAAA-MM-JJ` (durée fixée
   à ce moment-là), le saisir dans Mon offre avec le compte du titulaire (par le titulaire, ou par l'éditeur à ses côtés : la saisie est
   réservée au titulaire) ou l'envoyer par le central :
   `Pharma.Cloud.Api.exe --ordre-activation --officine <code> --code <code> --auteur <nom>`.
4. **Vente d'une option** : signer un nouveau code (packs `--packs A,D`, postes, scanners, `--fin` si abonnement, toujours `--serveur`) ; le plus récent l'emporte.

## Liaison renforcée de l'offre (point 5 de la revue de code, décision du titulaire du 2026-11-27 : « horloge + matériel »)

La revue de code de la branche (2026-11-27) a relevé que la liaison de l'offre reposait sur des valeurs que la pharmacie contrôle (code de
l'officine, NIF, horloge du serveur). Deux contournements, deux parades :

| | Contournement | Qui peut le faire | Parade décidée |
|---|---|---|---|
| **a** | **Copier l'activation d'une autre pharmacie** : recopier dans sa base le code de l'officine, le NIF et le code `PO1.…` d'une autre | Quelqu'un qui a accès à PostgreSQL et à la base d'une autre pharmacie | **Liaison au matériel** : chaque serveur (principal, 2e serveur, PC de secours) a une clé non exportable protégée par son TPM ; le code d'activation (version 2) contient les **empreintes des serveurs autorisés** (1 à 3) ; l'API prouve à chaque contrôle qu'elle détient l'une de ces clés. Une base copiée sur un autre PC ne vaut plus rien. |
| **b** | **Reculer l'horloge du serveur** pour qu'une offre échue paraisse encore valable | L'administrateur Windows du serveur | **Date plancher** : la plus grande de la dernière date du journal fiscal des ventes (chaîné et scellé) et d'une date mémorisée dans un fichier du serveur, hors de la base ; l'offre (échéance, grâce, rappels) se calcule avec la plus grande de la date du serveur et de cette date plancher : reculer l'horloge ne prolonge rien. Horloge en retard de plus d'une heure : alerte au titulaire une fois, journal `offre_horloge_reculee` ; la caisse n'est jamais bloquée. |

**Conséquences en exploitation (matériel)** :
- À l'installation, l'administrateur crée la clé de chaque serveur (`Pharma.Api.exe --creer-cle-serveur`) et lit son empreinte
  (`Pharma.Api.exe --empreinte-serveur`, aussi affichée dans Administration › Mon offre) ; il la transmet à l'éditeur avec le code de
  l'officine et le NIF ; l'éditeur signe avec `--serveur <empreinte>` (répétable, 1 à 3).
- Tout **remplacement de serveur** (PC neuf, Windows réinstallé, restauration sur un autre PC, nouveau 2e serveur ou PC de secours)
  demande un **nouveau code** ; en attendant, le délai de grâce de 15 jours s'applique. Le 2e serveur et le PC de secours sont à déclarer
  **à l'avance** dans le code, sinon une bascule ferait perdre les options pendant la grâce.
- Codes déjà signés (version 1, sans empreinte, ex. bases de l'éditeur) : toujours acceptés, avec « Code non lié au serveur : demandez un
  nouveau code » ; l'outil ne signe plus que des codes version 2.

**Hors de portée** (non traité, à savoir) : modifier le programme lui-même (par exemple remplacer la clé publique dans la `.dll`) ; seule une
signature du programme par un certificat de code protégerait contre cela.

### Mise en œuvre (2026-11-27)

**Matériel (a)** :
- **Clé de chaque serveur** : clé CNG de **machine** nommée `PharmaOfficine-Serveur`, ECDSA P-256, **non exportable**, usage signature ;
  fournisseur TPM « Microsoft Platform Crypto Provider » essayé d'abord, sinon « Microsoft Software Key Storage Provider » (clé logicielle, non
  exportable, **avertissement** affiché par la commande ; rien n'est bloqué). Droits posés à la création : SYSTEM (compte du service
  « PharmaOfficine API », LocalSystem par défaut) et Administrateurs, personne d'autre. Hors Windows : clé logicielle dans
  `<racine des données>/Serveur/cle-serveur.pem` (droits 600). Code : `src/Pharma.Infrastructure/Offre/CleServeur.cs`.
- **Commandes** (le serveur ne démarre pas ; traitées avant la base, la clé des jetons et le réseau : marchent aussi sur un 2e serveur en
  veille ou un PC de secours) : `Pharma.Api.exe --creer-cle-serveur` (administrateur ; **idempotente** : une clé existante n'est jamais
  remplacée, son empreinte est réaffichée) et `Pharma.Api.exe --empreinte-serveur`. L'API ne fait qu'ouvrir la clé.
- **Empreinte** : 16 premiers octets du SHA-256 de la clé publique (SubjectPublicKeyInfo), en hexadécimal majuscule, affichée par 8 groupes
  de 4 (`1A2B-3C4D-…`) ; comparée sans tirets ni casse (`EmpreinteServeur`).
- **Code version 2** : même format `PO1.<contenu>.<signature>`, contenu `"v":2` avec `"serveurs":[…]` (1 à 3 empreintes distinctes). Un code
  version 1 garde exactement son contenu d'avant (pas de clé `serveurs`).
- **Preuve** : à chaque évaluation de l'offre (cache existant d'une minute) et à chaque saisie, l'API vérifie que l'empreinte de sa clé est
  dans la liste, puis fait signer par la clé un défi aléatoire de 32 octets (préfixé d'un domaine propre à l'offre) et vérifie la signature
  avec cette clé publique (`PreuveCleServeur`) : une empreinte ou une clé publique recopiée sans la clé privée ne passe pas.
- **Serveur non déclaré ou clé absente** : à la **saisie** (titulaire ou central), le code est refusé avec « Ce serveur n'est pas déclaré dans
  le code de l'offre : demandez un nouveau code à l'éditeur avec l'empreinte … » ; pour un code **déjà rangé** (base restaurée ou copiée sur
  un autre PC), la première constatation est notée (`refusee_le`, journal) et l'offre reste ouverte en **délai de grâce 15 jours depuis cette
  constatation**, puis le code suit le chemin d'une activation refusée (activation précédente valable, sinon offre de base).
- **Codes version 1** : acceptés ; Mon offre affiche « Code non lié au serveur : demandez un nouveau code à l'éditeur ». L'outil de l'éditeur
  exige `--serveur` (1 à 3) et `lire` affiche les serveurs autorisés.
- **Mon offre** : empreinte de CE serveur avec un bouton « Copier », à côté du code de l'officine et du NIF (« clé absente » sinon ; **clé présente mais illisible** par le compte de l'API, par exemple
  API lancée sans droits d'administrateur : « Clé de ce serveur présente mais illisible par le compte de l'API : lancez l'API en service ou
  en administrateur (ne recréez pas la clé) », même message à la saisie et dans `--empreinte-serveur` ; Windows répond alors « Accès refusé »,
  `0x80090010`, au lieu de « jeu de clés inexistant », `0x80090016` ; correction du 2026-11-27 après l'essai réel).

**Horloge (b)** :
- **Date plancher** = la plus grande de : la dernière date du journal fiscal (`max(survenu_le)` et `enregistre_le` de la dernière ligne de
  `journal_vente`, deux dates posées par la base) et la date du fichier `C:\ProgramData\PharmaOfficine\Offre\date-plancher.json` (racine des
  données `Donnees:Racine`). Le fichier est écrit à côté puis remplacé, **au plus une fois par jour** (seulement quand le jour avance, jamais
  vers le passé), droits restreints à SYSTEM, Administrateurs et au compte de l'API ; illisible ou absent : seule la date du journal compte.
- **Date effective** = max(date du serveur, date plancher) ; l'échéance, le délai de grâce, les rappels (90 j … 1 j, grâce) et le refus d'un
  code échu à la saisie se calculent avec le **jour effectif**.
- **Horloge en retard de plus d'une heure** sur la date plancher : une ligne `offre_horloge_reculee` au journal des actions (une seule par date
  plancher ; rien n'est noté si la base est en lecture seule) et un message dans Mon offre tant que le retard dure. Rien d'autre ne bloque.
- **Date du serveur central** : voir les compléments du 2026-11-27 ci-dessous (elle fait foi depuis).
- Le fichier peut être effacé par un administrateur, mais la date du journal fiscal reste.

**Aucun changement de schéma** (journal des actions et `activation_offre` existants). Tests : `OffreTests` (date effective, recul d'horloge,
rappels et saisie au jour effectif, codes v1/v2, serveur non déclaré, clé absente, défi signé, outil) et `CleServeurOffreTests`
(commande idempotente, fichier de la date plancher, commandes avant la base).

### Limites et corrections du 2026-11-27 (décisions du titulaire, points 3 et 4)

**(a) Nouveau délai de grâce après un nouveau refus** — *limite* : la grâce d'un code refusé partait de `activation_offre.refusee_le`, posée
à la première constatation et jamais remise à zéro ; un code redevenu valable (retour sur le serveur déclaré, NIF corrigé) puis refusé de
nouveau n'avait plus de grâce. *Correction* : à chaque évaluation, un code rangé qui **repasse le contrôle** (signature, officine, NIF, non
échu, serveur déclaré) et porte une marque de refus la perd (`refusee_le` et `motif_refus` remis à vide, une instruction unique) avec une
ligne `offre_activation_refus_leve` au journal des actions (sans utilisateur ; ancienne date et ancien motif dans les détails). Un nouveau
refus est une nouvelle constatation : nouvelle date, nouvelle ligne `offre_activation_refusee`, **nouveau délai de grâce de 15 jours**. Un
code échu n'est pas « redevenu valable » : sa marque reste. Base en lecture seule : rien n'est levé (aucune erreur).

**(b) Horloge mise par erreur loin dans le futur** — *limite* : la date du serveur en avance était écrite dans `date-plancher.json` et
bloquait ensuite la date effective dans le futur. *Règle retenue (la plus simple et sûre)* : la date plancher (fichier, journal fiscal, date
du central) sert de référence ; **une date du serveur plus de 2 jours au-delà de la date plancher n'est pas écrite** dans le fichier et
déclenche l'alerte « horloge du serveur en avance » (ligne `offre_horloge_avance` au journal, une seule par date plancher ; message dans
Mon offre tant que l'écart dure). Justification : en marche normale le fichier avance d'un jour à la fois (écrit une fois par jour), donc
jamais de plus de 2 jours ; une seule comparaison, sans état de plus ; on n'écrit jamais une date douteuse (refuser coûte au pire un
plancher un peu ancien, que la date du serveur dépasse de toute façon) ; 2 jours couvrent un week-end prolongé serveur éteint sans fausse
alerte. Un serveur resté arrêté plus de 2 jours sans vente ni échange avec le central donne l'alerte jusqu'à la première vente (le message
le dit). Première mise en service (aucune date plancher) : la date du serveur est écrite. L'offre se calcule quand même avec la date du
serveur en avance : cela ne peut que rapprocher l'échéance, jamais la prolonger.

**Limite restante (documentée, non corrigeable)** : une **vente enregistrée sous une horloge fausse reste dans le journal fiscal** (chaîné,
non modifiable) ; sa date devient la date plancher. *Comment le titulaire s'en aperçoit* : l'alerte « horloge en avance » apparaît dès la
première évaluation de l'offre (connexion, au plus une minute), **avant** l'encaissement, et dit de vérifier la date avant d'encaisser ; si
une vente a quand même été faite puis l'horloge corrigée, l'alerte « l'horloge de ce serveur retarde » apparaît (journal
`offre_horloge_reculee`) et reste jusqu'à ce que la date réelle rattrape la date de cette vente. *Ce que cela change pour l'offre* : elle se
calcule à cette date future jusque-là : échéance, délai de grâce et rappels arrivent **plus tôt** (jamais plus tard) ; si une offre datée
paraît échue à tort, l'éditeur signe un nouveau code (fin repoussée). Aucune donnée n'est effacée, la caisse n'est pas bloquée.

**Date du serveur central (fait)** : chaque réponse de la voie descendante (échange léger à chaque passage, et accusé d'un lot) porte le
champ facultatif `DateCentrale` (date du central, `ChangementsDescendantsDto`) ; une officine antérieure l'ignore, un central antérieur ne
l'envoie pas (rien ne change). L'officine la mémorise dans le même fichier `date-plancher.json` (champ `Centrale`, **au plus une fois par
heure, jamais vers le passé** : un central restauré ne fait rien reculer) ; elle **fait foi** et entre dans la date plancher (recul comme
avance de l'horloge). Sans central : rien ne change. La voie réduite sans le pack G (échange léger seul) la transmet aussi.

**Avertissement « clé logicielle » (fait)** : si la clé de CE serveur existe mais n'est pas protégée par le TPM (fournisseur logiciel, ou
clé en fichier hors Windows), Mon offre l'écrit à côté de l'empreinte : « clé logicielle, sans TPM : moins bien protégée contre la copie du
serveur (rien n'est bloqué) » (champ `AlerteCle` de `OffreDto`, calculé par l'API).

Tests : `OffreTests` (marque levée puis nouvelle grâce, code échu non levé, saut de plus de 2 jours non écrit, horloge future puis corrigée,
date du central comme plancher, mémorisation une fois par heure, clé logicielle), `EnvoiSynchronisationTests` (date du central mémorisée par
l'échange et l'accusé, jamais en arrière), `DescendantsBornesTests` et `CleServeurOffreTests` (lecture du champ, fichier ancien relu, clé
en fichier logicielle), `DescendantsBornesCentralTests` (réponse datée, lue par une officine antérieure) et `ActivationsOffreSqlTests` sur
copie jetable (`DerniereDateFiscaleAsync`, alertes d'horloge une fois par date plancher et en lecture seule, marque levée avec journal).
**Aucun changement de schéma**, aucun script.

## Décisions

| Date | Décision |
|---|---|
| 2026-11-26 | Des fonctionnalités deviendront des options vendues à part, activées à la demande ; une offre de base est livrée à toutes les pharmacies. |
| 2026-11-26 | Le maximum de postes autorisés est fixé par l'éditeur (appelé alors « super admin »), sans possibilité de le modifier dans la base. |
| 2026-11-26 | Q1 : celui qui signe les activations (appelé « super admin » dans la question) est **l'éditeur du logiciel** ; il est distinct du compte super admin de reprise et de restauration ; la clé privée qui signe les activations reste chez l'éditeur, jamais chez la pharmacie. |
| 2026-11-26 | Q2 : pharmacie reliée au **serveur central** : c'est le central qui gère et transmet l'activation signée ; sinon, **code signé** saisi sur place (produit par un outil gardé chez l'éditeur). |
| 2026-11-26 | Q3 : l'activation est **liée à une pharmacie précise** (son identifiant dans l'activation signée) : une activation recopiée ailleurs est refusée. |
| 2026-11-26 | Q4 : signature absente ou fausse : **retour à l'offre de base** (options fermées, maximum de postes ramené à 2), titulaire prévenu, inscrit au journal ; une vente en cours n'est jamais interrompue. |
| 2026-11-26 | Q5 : activation **définitive ou limitée dans le temps** (date de fin facultative dans l'activation) ; à l'échéance : titulaire prévenu 30 jours avant, 7 jours avant (étendu le 2026-11-27 : 90, 60, 30, 15, 7 et 1 jour(s) avant), puis au début du délai de grâce de 15 jours, puis option fermée (écran masqué ou en lecture seule), **aucune donnée effacée**. |
| 2026-11-26 | Q6 : écran **« Mon offre »** en lecture seule pour le titulaire (packs, quotas, dates de fin), champ de saisie d'un code d'activation, coordonnées de l'éditeur pour demander une option. |
| 2026-11-26 | Q7 : l'activation est rangée dans la base répliquée (valable sur le 2e serveur après une bascule) ; la signature est contrôlée sur chaque serveur ; le central **transmet** une activation signée par l'éditeur, il ne peut pas en créer. |
| 2026-11-26 | Q8 : pharmacies déjà installées : **tout reste ouvert pendant une période de transition** (code « tout compris » limité dans le temps), puis passage à leur offre réelle. **Durée de la transition : à définir.** |
| 2026-11-26 | Liaison (Q3 précisée) : l'activation contient le **code de l'officine** (`officine_id`) **et son NIF** ; le NIF doit être saisi avant l'activation. |
| 2026-11-26 | Clé de l'éditeur : **fichier chiffré par un mot de passe**, hors de tout dépôt, sur le PC de l'éditeur, avec une copie sur une clé USB rangée à part ; l'outil demande le mot de passe à chaque signature. |
| 2026-11-26 | Transition (Q8 précisée) : **code de transition** « tout compris » signé par l'éditeur, saisi dans Mon offre avec le compte du titulaire (par le titulaire, ou par l'éditeur à ses côtés) lors de la mise en place ou de la mise à jour, avec une date de fin choisie à ce moment-là. **Sans aucune activation valable : offre de base, 2 postes** ; une activation effacée ramène à l'offre de base, jamais à tout ouvert. |
| 2026-11-26 | **Tour des packs : recommandations acceptées** (« prends tes recommandations ») : packs A à H confirmés, avec ces précisions. A : la **réception à l'aveugle avec double saisie** entre dans le pack A (la base garde la réception simple). B : l'**inventaire complet annuel** et le **blocage d'un lot rappelé** restent dans la base ; la liste des patients à contacter et son export vont dans B. C : bons de commande, réception, facture d'achat et retours restent dans la base. D : la **facture à un patient** reste dans la base ; sans D, la clôture de caisse ne verse rien en trésorerie ; les rapports simples restent dans la base. E : les **alertes de sécurité** par SMS ou e-mail (sauvegarde absente, rupture d'intégrité, poste ou serveur isolé) restent dans la base. F : sauvegarde quotidienne, rotation des disques et test de restauration restent dans la base. Quotas : 2 postes et **0 scanner mobile** dans la base. **Fermeture d'un pack** : packs avec données (A à D) en **lecture seule** (actions refusées « Option non incluse — voir Mon offre », rien d'effacé) ; packs sans données propres (E à H) **arrêtés** ; tâches automatiques du pack arrêtées ; le serveur refuse lui-même les routes d'un pack fermé. |
| 2026-11-26 | Lot O6 (« 1 reco, 2 reco, 3 reco, go O6 ») : (1) sans le pack A, les anomalies sont **enregistrées en silence** et deviennent visibles, avec leur historique, dès l'achat du pack ; (2) pack F échu alors que les deux serveurs tournent : la paire **continue de fonctionner** (réplication, bascule), le titulaire est prévenu, aucune nouvelle installation, écrans de réglage du 2e serveur en lecture seule ; (3) pack G échu : la synchronisation s'arrête, mais les **résultats des ordres déjà reçus** sont encore envoyés. |
| 2026-11-27 | (1) **Postes : preuve toujours exigée** : « Chaque poste doit être autorisé avec son code, sinon pas de connexion, et un poste non autorisé n'entre pas dans le total / maximum autorisé. » Modes « transition » et « inactif » supprimés (script 174, `parametre_postes.mode` plus lu), choix du mode retiré de l'écran, écran de connexion d'un PC non autorisé réduit à la saisie du code ; exemptions gardées : routes ouvertes, clients sans poste (page mobile), super admin (règles métier, « Postes autorisés »). |
| 2026-11-27 | Postes, précisions (« 1 garder, 2 garder ») : (1) un poste **créé mais pas encore autorisé** (code généré, pas encore saisi sur le PC) **compte dans le maximum**, sa place est réservée ; (2) **exception du super admin** gardée : restauration et reprise de données possibles depuis un PC non autorisé (routes protégées par son compte et par un code reçu sur son téléphone pour chaque action). |
| 2026-11-27 | **Le titulaire peut accéder à partir d'un poste non autorisé : Administration seulement** : depuis un PC non autorisé, seul le titulaire se connecte (tout autre employé refusé), et n'a accès qu'à l'Administration (Postes autorisés, Mon offre, utilisateurs, réglages…), jamais à la caisse, la vente, le stock ni les finances. But : générer les codes des postes et saisir l'offre depuis n'importe quel PC (installation, PC neuf), sans contourner la limite : ce PC n'est pas un poste et n'entre pas dans le maximum. Contrôle au serveur (jeton marqué, liste fermée de routes) ; voir règles métier, « Postes autorisés ». |
| 2026-11-27 | **Point 5, liaison renforcée : « horloge + matériel »** : (a) contre la copie d'une activation, liaison aux clés TPM des serveurs de l'officine (empreintes dans le code version 2) ; (b) contre le recul de l'horloge, date plancher tirée du journal fiscal et d'un fichier du serveur. Voir « Liaison renforcée de l'offre ». |
| 2026-11-27 | (2) **Aucun blocage sans le central ni sans le pack G** : « une officine sans le central, si le titulaire ne veut pas le central, ne doit jamais se retrouver bloquée, pour tous les cas d'usage du logiciel ». Sans G : pas d'attente du contrôle de démarrage pour l'encaissement, réglages imposés rendus à l'officine (réimposés si G revient), ni bandeau, ni alerte de retard ou d'erreur d'envoi, rien sur le central dans la barre d'état ; la synchronisation peut être désactivée sans G. |
| 2026-11-27 | (3) **Code d'offre transmis par le central sans G** : la synchronisation active sans G relève encore les ordres (échange léger, sans lot de données) et n'applique que `activation_offre` ; les autres ordres restent en attente (ni appliqués, ni refusés, ni alertés ; confirmation refusée « Option non incluse ») ; seuls les résultats d'ordres remontent. |
| 2026-11-27 | (4) **Rappels de fin d'offre** : une étape n'est notée que si l'alerte a vraiment été mise en file. |
| 2026-11-27 | (5) Limite restante : **liaison renforcée** de l'activation à la pharmacie (au-delà du code de l'officine et du NIF) : **plan à venir**, rien de codé. |
| 2026-11-28 | Revue de code (« go all points ») : (a) **alertes de sécurité toujours envoyées** sans le pack A (journal rompu, numérotation des tickets, poste révoqué, clé d'un poste, codes faux répétés : `SecuriteAsync`) ; classement des deux alertes de codes faux en « sécurité » **confirmé le 2026-11-28**. (b) **Date du central bornée** : un saut de plus de 2 jours n'est retenu qu'après confirmation par le central (deuxième réponse, une heure plus tard à son horloge). (c) **Grâce « serveur non déclaré » accordée une seule fois**, jamais depuis une constatation datée dans le futur. (d) **Décision du 2026-11-28 : un code version 1 plus ancien qu'un code version 2 n'est plus pris** (retour à l'offre de base, plus de réouverture de l'ancien code sur une base restaurée ailleurs). |
| 2026-11-28 | **Horloge reculée sans serveur central** (« go horloge code ») : pendant un recul, la date plancher avance du temps réellement passé (compteur indépendant de l'horloge pendant que l'API tourne ; dernière heure vue dans `date-plancher.json` après un redémarrage, écart négatif ou de plus de 2 jours non compté) ; fichier écrit au plus une fois par heure pendant un recul. Choix : aucun forfait pour le temps d'arrêt de l'API. Limites : temps d'arrêt avec un nouveau recul au redémarrage, fichier effacé. |
| 2026-11-28 | **Sans le pack G, le certificat de synchronisation est renouvelé** (60 jours avant l'échéance, comme avec G) et son échéance est signalée au titulaire : la voie réduite qui transmet les codes d'offre ne s'arrête plus au bout de 3 ans. |
| 2026-11-28 | **Nom du pack H** (« garde le nom actuel pour tous sauf H ») : H devient **Comptoir assisté** (était Confort du comptoir) ; les autres packs gardent leur nom. Lettre inchangée : les codes d'activation déjà signés restent valables ; seul le libellé affiché change (Mon offre, messages « option non incluse »). |
