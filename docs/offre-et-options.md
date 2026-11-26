# Offre de base et options vendues à la demande

> Décision du titulaire du 2026-11-26. **À l'étude, rien n'est codé.** Ce document sert de point de départ au tour des fonctionnalités à
> faire ensemble ; il sera complété au fil des décisions.

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

## Décisions

| Date | Décision |
|---|---|
| 2026-11-26 | Des fonctionnalités deviendront des options vendues à part, activées à la demande ; une offre de base est livrée à toutes les pharmacies. |
| 2026-11-26 | Le maximum de postes autorisés est fixé par le super admin, sans possibilité de le modifier dans la base. |
