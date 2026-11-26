# Offre de base et options vendues à la demande

> Décision du titulaire du 2026-11-26. **Mécanisme d'activation codé le 2026-11-26** (« go all lot » : lots O1 à O5, voir « Mise en œuvre ») ;
> le partage des fonctionnalités entre offre de base et packs (lot O6) attend le tour des packs A à H.

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
| O6 | Fermer les packs non inclus (écrans et routes masqués ou en lecture seule, données gardées) | **En attente** du tour des packs A à H |

### Mise en service (à faire une fois par l'éditeur)

1. **Générer la clé de l'éditeur** sur le PC de l'éditeur, hors de tout dépôt :
   `dotnet run --project src\Pharma.Editeur -- generer-cle --dossier <dossier protégé>` (mot de passe demandé deux fois, 12 caractères au
   moins). Copier `cle-editeur.pem` sur une clé USB rangée à part. **Sans ce fichier ou sans son mot de passe, plus aucun code ne peut être
   signé** (il faudrait une nouvelle clé et une nouvelle version du programme).
2. **Intégrer la clé publique** (contenu de `cle-editeur-publique.txt`) dans `src/Pharma.Application/Offre/ActivationOffre.cs`
   (`CleEditeur.PubliqueBase64`), puis livrer cette version. **Tant que cette constante est vide, aucun code n'est accepté : toutes les
   pharmacies sont à l'offre de base (2 postes)** ; les postes déjà autorisés restent, seules les nouvelles autorisations sont refusées.
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
