# ADR 0002 – Schéma piloté par des scripts SQL

**Statut** : accepté (complété : scripts idempotents)

**Contexte** : les règles critiques (péremption, registres non modifiables, droits, alertes) reposent sur des vues, fonctions et déclencheurs PostgreSQL, mal couverts par les migrations EF Core.

**Décision** :

- Le dossier `database/` est la source de vérité, en scripts numérotés dont le sens ne change plus après mise en production. EF Core sert à lire et écrire, sans migrations. Les requêtes complexes passent par `NpgsqlDataSource`.
- **Chaque script est idempotent** : le rejouer sur une base à jour ne produit ni erreur ni changement. Mettre à jour une base = rejouer tous les scripts dans l’ordre. Les techniques sont détaillées dans [base-de-donnees.md](../base-de-donnees.md#scripts-idempotents).
- Les réglages et droits choisis par la pharmacie ne sont jamais écrasés par une réexécution.

**Conséquences** : chaque évolution = un nouveau script, lui aussi idempotent ; le mapping EF doit suivre les noms en base (convention automatique en minuscules avec soulignés). Il n’y a pas de table de suivi des versions appliquées : c’est l’idempotence qui garantit qu’une base partielle ou à jour se met à niveau correctement.
