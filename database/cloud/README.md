# Base centrale (serveur central de synchronisation)

Scripts de la base **centrale** commune à toutes les officines (lot S2, 2026-11-16, [conception](../../docs/conception-synchronisation.md) § 12).
Ils ne se passent **jamais** sur la base d'une officine (celle-ci a ses scripts dans `database/migration`).

| Script | Contenu |
|---|---|
| `001_base_centrale.sql` | Rôle applicatif `pharma_cloud_api` (créé **sans connexion**, non propriétaire), tables d'administration (`officine`, `certificat`, `code_activation`, `journal_securite`), `officine_epoque` (dernier lot par officine et époque), `lot_recu` (lots bruts gardés pour preuve, **partitionné par mois**), catalogue `cloud_table_miroir`, fonctions de l'API centrale (`cloud_activer`, `cloud_certificat`, `cloud_appliquer_lot`…), Row Level Security. |
| `002_tables_miroir.sql` | **Généré** : une table miroir par table envoyée par les officines, mêmes colonnes que la table locale (dont `officine_id`), clé `(officine_id, clé locale)`, colonnes `sync_epoque`, `sync_numero`, `sync_recu_le`, RLS forcée. Ne pas modifier à la main. |
| `tests/001_base_centrale_tests.sql` | Tests en transaction annulée : cloisonnement (l'officine B ne lit ni n'écrit les lignes de A, même sans filtre), idempotence, trou, empreinte différente, suppression, état final, codes d'activation, journal en ajout seul, absence de nom de patient. |

## Règles

- Scripts **idempotents**, numérotés, passés dans l'ordre par le **propriétaire** de la base (jamais par `pharma_cloud_api`).
- **Cloisonnement** : chaque table de données a `ENABLE` + `FORCE ROW LEVEL SECURITY` et la politique `cloisonnement`
  (`officine_id = cloud_officine_courante()`). L'API centrale pose l'officine du **certificat** par `set_config('app.officine_id', …, true)`
  (équivalent de `SET LOCAL`) au début de chaque transaction ; sans officine posée, aucune ligne n'est visible.
- **Alignement avec la base locale** : après tout changement d'une table envoyée (script officine), régénérer `002` :
  `Pharma.Cloud.Api.exe --generer-miroir <racine du dépôt>` (lit `database/init/schema.sql` et le classement `sync_table`).
  Le test `tests/Pharma.Cloud.Tests/SchemaMiroirTests.cs` échoue si le fichier n'est pas à jour. Un changement de **type** de colonne
  demande un script central à part (le générateur ajoute les colonnes, il ne les transforme pas).
- **Partitions** de `lot_recu` : créées pour le mois précédent à M+2 par `001` ; ensuite chaque mois par
  `Pharma.Cloud.Api.exe --partitions` (tâche planifiée). Une ligne hors partition tombe dans `lot_recu_defaut`.
- **Aucun nom de patient** : les patients et porteurs n'arrivent que sous code (`P-…`, `R-…`), les colonnes exclues par l'officine
  n'existent pas ici, aucune recherche par nom.
- Le rôle `pharma_cloud_api` reçoit `LOGIN` et son mot de passe **à l'installation** (jamais dans un script ni dans le dépôt) :
  voir [mise-en-production.md](../../docs/mise-en-production.md), « Serveur central de synchronisation ».

## Essai local (poste de développement)

```powershell
$env:PGPASSWORD = "<mot de passe de pharma_app>"
& "C:\Program Files\PostgreSQL\18\bin\psql.exe" -U pharma_app -h localhost -d postgres -c "CREATE DATABASE pharmacie_cloud_dev"
foreach ($f in "001_base_centrale.sql", "002_tables_miroir.sql") {
    & "C:\Program Files\PostgreSQL\18\bin\psql.exe" -U pharma_app -h localhost -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f "database\cloud\$f" }
& "C:\Program Files\PostgreSQL\18\bin\psql.exe" -U pharma_app -h localhost -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database\cloud\tests\001_base_centrale_tests.sql
```
