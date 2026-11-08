# ADR 0004 – Clean Architecture et règles métier dans le domaine

**Statut** : accepté

**Contexte** : la couche Application ne contenait que des interfaces ; les points d’accès appelaient directement les requêtes SQL de l’Infrastructure. Les règles métier existaient à la fois en C# et en SQL et avaient commencé à diverger (le FEFO C# écartait les lots bloqués, la fonction `lot_fefo` non). L’Application dépendait aussi des DTO du contrat HTTP (`Pharma.Shared`).

**Décision** :

- Les règles métier sont écrites et testées dans `Pharma.Domain`. Les déclencheurs SQL restent un filet de sécurité.
- `Pharma.Application` contient les cas d’usage, organisés par fonctionnalité, et les ports qu’ils utilisent. Elle ne dépend que du domaine.
- Cas d’usage écrits à la main (une classe, une méthode `ExecuterAsync`), sans bibliothèque de médiation.
- L’Application renvoie ses propres modèles ; l’API les convertit en DTO de `Pharma.Shared`.
- Les lectures restent en SQL direct (Npgsql), les écritures passent par le domaine et EF Core (ADR 0002 inchangé).
- `Pharma.Architecture.Tests` fait échouer le build de tests si une dépendance va dans le mauvais sens.

**Conséquences** : un peu plus de code de conversion ; chaque nouvelle fonctionnalité suit le même chemin (cas d’usage → port → adaptateur → point d’accès). Quand une règle existe en C# et en SQL, les tests d’intégration sur `pharmacie_test` (à venir) devront vérifier que les deux sont d’accord.
