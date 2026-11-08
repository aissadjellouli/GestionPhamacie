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
| `009_caisse.sql` | Numéros de ticket, sessions de caisse, comptage de clôture, paramètres de caisse, remise |
| `010_administration.sql` | Identité de la pharmacie, délai de reprise des grossistes, contrôles des seuils, protection du rôle titulaire |
| `011_ordonnance_scannee.sql` | Image et texte lu de l'ordonnance scannée, date de création |
| `012_achats.sql` | Numéros de commande (C-000001) et de retour (R-000001), suivi des retours (envoi, avoir attendu et reçu), montant TTC des réceptions, recherche au scan par étiquette de boîte |
| `013_notifications.sql` | Réglages des notifications (SMTP, modem GSM, heure du rapport, seuils SMS), destinataires vérifiés, file des messages sortants avec filet contre les adresses non vérifiées |
| `014_sauvegardes.sql` | Réglages et journal non modifiable des sauvegardes automatiques |
| `015_catalogue.sql` | Droit « catalogue.creer » (titulaire et assistant) et unicité nom, dosage et forme des produits |
| `016_emplacements.sql` | Emplacements de rangement (rayon, étagère, tiroir ; zone de vente ou réserve), emplacement de vente et réserve de chaque produit |
| `017_inventaire.sql` | Inventaire tournant : seuils de correction automatique, inventaires par rayon et lignes par lot (comptage à l'aveugle, écart, décision), type de notification « écart d'inventaire » |
| `demo/900_donnees_demo.sql` | Données fictives (développement et tests seulement) |
| `demo/901_complement_sintrom.sql` | Complément fictif : Sintrom 4 mg (acénocoumarol) et un lot en stock, pour tester les ordonnances manuscrites ; ignoré sans données de démonstration |
| `demo/902_emplacements_demo.sql` | Emplacements fictifs (rayons A à E, réfrigérateur F-01, armoire des psychotropes P-01, réserve R-01 et R-02) et rangement des produits de démonstration , et un lot « date à vérifier » (AM55) pour tester l'inventaire |

Pour une nouvelle évolution : ajouter un script `018_...sql`, ne jamais modifier le sens d’un script déjà appliqué en production.

## Scripts idempotents

Chaque script peut être rejoué sans erreur et sans effet sur une base déjà à jour. C’est ce qui permet de mettre à jour une base existante en rejouant tous les scripts dans l’ordre.

| Élément | Écriture |
| --- | --- |
| Table, index, séquence, schéma, extension | `CREATE ... IF NOT EXISTS` |
| Colonne | `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` |
| Fonction, vue de même forme, déclencheur | `CREATE OR REPLACE` |
| Vue dont les colonnes changent d’un script à l’autre | `DROP VIEW IF EXISTS` puis `CREATE VIEW` |
| Déclencheur de contrainte (`CREATE CONSTRAINT TRIGGER`) | `DROP TRIGGER IF EXISTS` puis création (`OR REPLACE` n’existe pas pour eux) |
| Contrainte ajoutée à une table existante | bloc `DO` qui vérifie `pg_constraint` avant `ADD CONSTRAINT` |
| Données de référence (organismes, rôles) | `ON CONFLICT DO NOTHING` |
| Catalogue des permissions (fixé par le logiciel) | `ON CONFLICT DO UPDATE` des libellés |
| Paramètres à une ligne (`parametre_*`) | `INSERT ... ON CONFLICT (id) DO NOTHING` : les réglages de la pharmacie sont conservés |
| Droits par défaut des rôles | posés seulement si le rôle n’a encore aucun droit : les choix du titulaire ne sont jamais écrasés |
| Migration de données (ancienne colonne `utilisateur.role`) | bloc `DO` exécuté seulement si l’ancienne colonne existe encore |
| Données fictives (`demo/`) | le script s’arrête si elles sont déjà présentes (`\gset` et `\if` de psql) |

Contrôle effectué : sur une base neuve, deux passes complètes (001 à 009 puis démo) donnent une base identique ; sur une base existante, la réexécution ne change ni les lignes, ni les droits, ni les paramètres, ni les contraintes, déclencheurs, index et vues.

## Base locale de développement

```powershell
.\scripts\initialiser_base_locale.ps1            # crée les bases manquantes, met à jour les autres
.\scripts\initialiser_base_locale.ps1 -MiseAJour # met à jour sans le mot de passe postgres
.\scripts\initialiser_base_locale.ps1 -Recreer   # repart de bases vides
```

Le script crée le rôle `pharma_app` et deux bases, `pharmacie_dev` (pour lancer l’API) et `pharmacie_test` (pour les tests d’intégration), applique tous les scripts puis les données fictives. Options : `-PgBin` (dossier de `psql`, par défaut la dernière version installée), `-SansDemo`, `-Bases`.

Les données fictives reproduisent les cas des maquettes : lot d’amoxicilline périmé, insuline proche de la péremption, oméprazole à risque d’écoulement, metformine en rupture avec une alternative, amlodipine en stock critique, vitamine D3 dormante, alprazolam sous registre. Les dates sont calculées à partir du jour d’exécution.

## Conventions

- Noms en minuscules avec soulignés (`ligne_vente`, `date_peremption`). Le `PharmaDbContext` convertit automatiquement les noms C# (`LigneVente`, `DatePeremption`).
- Clés `bigint GENERATED ALWAYS AS IDENTITY`.
- Montants `numeric(12,2)` en dinars ; dates d’horodatage `timestamptz`.
- Les valeurs fermées (statuts, types) sont des `text` avec `CHECK`, en minuscules.
- Tables d’historique non modifiables : `registre_psychotrope`, `journal_audit`, `boite_evenement`, et les sessions de caisse une fois clôturées.

## Sauvegarde

`scripts/sauvegarde_pharmacie.ps1` : `pg_dump` chaque soir, vérification de lisibilité, copie sur disque externe, 30 jours d’historique. Détails dans [mise-en-production.md](mise-en-production.md).

L'API sait aussi sauvegarder elle-même, avec alerte SMS et e-mail au titulaire en cas d'échec ou de disque absent : voir [notifications.md](notifications.md). Choisir l'un ou l'autre.
