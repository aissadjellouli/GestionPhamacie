# Base de données

PostgreSQL 17+. Le schéma est défini par les scripts du dossier `database/`, exécutés dans l’ordre des numéros. EF Core lit et écrit les données mais ne crée pas le schéma.

## Ordre des scripts

| Script | Contenu |
| --- | --- |
| `001_schema.sql` | Tables de base : utilisateurs, catalogue, fournisseurs, lots, ventes, patients, bordereaux, registre des psychotropes, journal |
| `002_alertes_stock.sql` | Paramètres, consommation, risque de péremption, état du stock, alternatives, FEFO, blocage du périmé, table et génération des alertes |
| `003_provenance.sql` | Réceptions, grossiste de chaque lot, boîtes sérialisées, retours fournisseurs |
| `004_codes_barres_internes.sql` | Codes EAN-13 internes (préfixe 20), recherche au scan |
| `005_seuils_peremption.sql` | Seuils de péremption par pharmacie, grossiste et produit |
| `006_droits_utilisateurs.sql` | Rôles, permissions, journal non modifiable |
| `007_tracabilite_boite.sql` | Identité de chaque boîte, historique, vente boîte par boîte, rappel de lot |
| `008_reprise_donnees.sql` | Schéma `import` pour la reprise de l’ancien logiciel |
| `demo/900_donnees_demo.sql` | Données fictives (développement et tests seulement) |

Pour une nouvelle évolution : ajouter un script `009_...sql`, ne jamais modifier un script déjà appliqué en production.

## Base locale de développement

```powershell
.\scripts\initialiser_base_locale.ps1 -Recreer
```

Le script crée le rôle `pharma_app` et deux bases, `pharmacie_dev` (pour lancer l’API) et `pharmacie_test` (pour les futurs tests d’intégration), applique tous les scripts puis les données fictives. Options : `-PgBin` (dossier de `psql`), `-SansDemo`, `-Bases`.

Les données fictives reproduisent les cas des maquettes : lot d’amoxicilline périmé, insuline proche de la péremption, oméprazole à risque d’écoulement, metformine en rupture avec une alternative, amlodipine en stock critique, vitamine D3 dormante, alprazolam sous registre. Les dates sont calculées à partir du jour d’exécution.

## Conventions

- Noms en minuscules avec soulignés (`ligne_vente`, `date_peremption`). Le `PharmaDbContext` convertit automatiquement les noms C# (`LigneVente`, `DatePeremption`).
- Clés `bigint GENERATED ALWAYS AS IDENTITY`.
- Montants `numeric(12,2)` en dinars ; dates d’horodatage `timestamptz`.
- Les valeurs fermées (statuts, types) sont des `text` avec `CHECK`, en minuscules.
- Tables d’historique non modifiables : `registre_psychotrope`, `journal_audit`, `boite_evenement`.

## Sauvegarde

`scripts/sauvegarde_pharmacie.ps1` : `pg_dump` chaque soir, vérification de lisibilité, copie sur disque externe, 30 jours d’historique. Détails dans [mise-en-production.md](mise-en-production.md).
