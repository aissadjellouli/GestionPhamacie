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
