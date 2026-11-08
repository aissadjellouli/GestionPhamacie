# ADR 0002 – Schéma piloté par des scripts SQL

**Statut** : accepté

**Contexte** : les règles critiques (péremption, registres non modifiables, droits, alertes) reposent sur des vues, fonctions et déclencheurs PostgreSQL, mal couverts par les migrations EF Core.

**Décision** : le dossier `database/` est la source de vérité, en scripts numérotés jamais modifiés après mise en production. EF Core sert à lire et écrire, sans migrations. Les requêtes complexes passent par `NpgsqlDataSource`.

**Conséquences** : chaque évolution = un nouveau script ; le mapping EF doit suivre les noms en base (convention automatique en minuscules avec soulignés).
