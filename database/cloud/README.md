# Base centrale (serveur central de synchronisation)

Scripts de la base **centrale** commune à toutes les officines (lot S2, 2026-11-16, [conception](../../docs/conception-synchronisation.md) § 12).
Ils ne se passent **jamais** sur la base d'une officine (celle-ci a ses scripts dans `database/migration`).

| Script | Contenu |
|---|---|
| `001_base_centrale.sql` | Rôle applicatif `pharma_cloud_api` (créé **sans connexion**, non propriétaire), tables d'administration (`officine`, `certificat`, `code_activation`, `journal_securite`), `officine_epoque` (dernier lot par officine et époque), `lot_recu` (lots bruts gardés pour preuve, **partitionné par mois**), catalogue `cloud_table_miroir`, fonctions de l'API centrale (`cloud_activer`, `cloud_certificat`, `cloud_appliquer_lot`…), Row Level Security. |
| `002_tables_miroir.sql` | **Généré** : une table miroir par table envoyée par les officines, mêmes colonnes que la table locale (dont `officine_id`), clé `(officine_id, clé locale)`, colonnes `sync_epoque`, `sync_numero`, `sync_recu_le`, RLS forcée. Ne pas modifier à la main. |
| `003_chargement_complet_officine.sql` | Lot S6 : chargement complet d'une officine (table `chargement`, `cloud_debut_chargement`, `cloud_fin_chargement`), `cloud_appliquer_lot` remplacée (lignes `instantane` marquées vues, lots perdus abandonnés au début, lignes absentes de l'officine marquées `sync_absente_le`, jamais effacées). À passer après `002`. |
| `004_reglage_familles_envoyees.sql` | Réglages du serveur central : familles de données nominatives (`cloud_famille`, `officine_famille`, `cloud_famille_colonne` ; défauts : employés non, pharmacie, fournisseurs, courriel des commandes oui) descendues dans la base de l'officine (script `005`) ; effacement à la réception des colonnes non autorisées (`cloud_appliquer_lot` remplacée, lot brut marqué `expurge_le`) ; `cloud_effacer_famille` (simulation par défaut) ; conservation des lots bruts (6 ans minimum, `cloud_purger_lots` désactivé par défaut, déclencheur `cloud_lot_recu_garde`). À passer après `003`. |
| `005_voie_descendante_elements.sql` | Voie descendante (contrat `desc-v1`) : `descendant` et `officine_descendant` (RLS, version monotone par officine, acquittement), `cloud_descendants` (publie le réglage effectif des familles nominatives puis renvoie les éléments plus récents) ; transport : accusé des lots (en-tête `X-Sync-Descendants`) et `POST /api/v1/echange`. À passer après `004`. |
| `006_debit_chargement.sql` | Débit pendant un chargement complet : `cloud_chargement_en_cours` (lu par l'API avant la limite de débit : 600 lots/min au lieu de 60, `Cloud:LotsParMinuteChargement`), plafond d'octets par chargement (`cloud_parametre.quota_chargement_octets`, 8 Gio) à la place du quota du jour, `chargement.octets_recus` ; `cloud_appliquer_lot` remplacée. À passer après `005`. |
| `007_familles_banque_clients.sql` | Familles nominatives complétées : pharmacie (+ nom, NIF, NIS, RC, article d'imposition), `banque` et `clients_factures` (défaut non). À passer après `006`. |
| `008_famille_banque_elargie.sql` | Famille `banque` élargie : banque du compte, banque et référence des règlements fournisseurs, références des effets, remises de chèques et prêts. À passer après `007`. |
| `tests/008_famille_banque_elargie_tests.sql` | Tests : 8 colonnes, défaut non, aucune dans une clé, effacement à la réception. |
| `009_central_restaure_quota_chargement.sql` | Central restauré (versions descendantes remontées), plafonds de chargement sur 24 h (nombre et octets, tous chargements). À passer après `008`. |
| `tests/009_central_restaure_quota_chargement_tests.sql` | Tests : version remontée, plafonds 24 h (remplacés compris), nécessité du positionnement de l'officine, aucun UPDATE/DELETE/TRUNCATE sur `lot_recu` et ses partitions. |
| `010_mutualisation_reglages.sql` | Mutualisation des réglages (lot M3, § 16 bis) : `cloud_reglage_table` (11 tables mutualisables, mode de départ), `cloud_reglage_modele` (un modèle par périmètre `tous` et par table : mode propose / impose, valeur mutu-v1, version, auteur ; **créé sans valeur pour les 11 tables, rien n'est publié**), `cloud_journal_reglage` (ajout seul), `officine.mutualisation_liberee_le` ; fonctions d'administration sans `EXECUTE` pour l'API (`cloud_publier_reglage`, `cloud_regler_mode_reglage`, `cloud_liberer_reglages`, `cloud_retablir_reglages`, `cloud_etat_reglages`) ; `cloud_descendants` remplacée (logique de `009` conservée, plus un élément `mutu_<table>` par table publiée) ; `pharma_cloud_api` ne fait que LIRE les modèles. À passer après `009` et après `002` régénéré (miroir `reglage_mutualise`). |
| `011_descendant_ajouts_locaux_motifs_remises.sql` | Correctif du défaut D-M8-1 (lot M8) : `cloud_descendants` redéfinie à l'identique, sauf `ajouts_locaux`, écrit seulement pour `motif_reglable` et `remise_automatique` (le format mutu-v1 l'interdit ailleurs ; sans ce script l'officine rejetait les 9 autres tables). Le script `010` a une garde : rejoué après `011`, il ne ramène pas l'ancienne fonction. Un élément déjà publié avec l'ancien format est republié une fois. |
| `tests/011_descendant_ajouts_locaux_motifs_remises_tests.sql` | Tests : pour les 11 tables, l'élément `mutu_<table>` ne contient `ajouts_locaux` que pour les motifs et les remises (aussi pour une officine libérée), droits et repère de la garde. Le test C# `DescendantsMutuV1Tests` fait valider chaque élément par le domaine. |
| `012_verification_integrite_central.sql` | Lot S4 (§ 22 de la conception) : référentiel des journaux chaînés (`cloud_chaine_reference`), **ajout seul** des journaux, sceaux, clôtures et vérifications reçus (modification ou suppression reçue non appliquée, anomalie notée), vérification incrémentale ou complète (`cloud_verifier_integrite` : rangs, chaînage, empreintes recalculées, ancrage des sceaux, continuité des tickets, vérification croisée), anomalies en ajout seul (`cloud_anomalie_integrite`), résumé publié vers l'officine (`reglage_central` / `integrite_centrale`). Délai d'attente `cloud_parametre.integrite_delai_heures` (24 h). `SET client_encoding = 'UTF8'`. À passer après `011`. |
| `tests/012_verification_integrite_central_tests.sql` | Tests : chaîne intacte, texte filtré non vérifiable, ajout seul (modification, suppression, renvoi identique, texte refiltré), trou et délai, sceaux, tickets, vérification croisée, altération directe trouvée par la vérification complète, cloisonnement, droits. |
| `013_ordres_vers_officines.sql` | Lots S5.1 et S5.5 (§ 24 de la conception) : `ordre_central` (essai, création, modification, blocage, déblocage d'un employé, droits d'un rôle ; auteur obligatoire, valeur attendue lue dans le miroir, expiration à 7 jours ; jamais le titulaire ni son rôle, jamais de mot de passe), `ordre_central_journal` (ajout seul), `cloud_creer_ordre`, `cloud_annuler_ordre`, `cloud_expirer_ordres`, publication par la voie descendante (élément `ordre_central`, format ordre-v1), retour du résultat de l'officine par déclencheur sur le miroir `sync_ordre_resultat`. À passer après `012` et `002` régénéré. |
| `tests/013_ordres_vers_officines_tests.sql` | Tests : création des six types et valeur attendue, refus (titulaire, rôle système, déjà bloqué, sans auteur, type ou employé inconnu), annulation, expiration, résultats reçus de l'officine, cloisonnement, journal en ajout seul, l'API ne crée jamais d'ordre. |
| `014_ordre_prix_produit.sql` | Lot S5.2 (§ 25) : type d'ordre `prix_produit` (prix public et/ou tarif de référence d'un produit, valeur attendue lue dans le miroir, montants > 0 à deux décimales) ; `cloud_creer_ordre` redéfinie ; contrainte des types élargie avec garde. À passer après `013`. |
| `tests/014_ordre_prix_produit_tests.sql` | Tests : attendu, tarif seul, refus (montant nul, trois décimales, texte, rien ne change, ordre vide, produit inconnu), autres types toujours acceptés. |
| `tests/010_mutualisation_reglages_tests.sql` | Tests : 11 modèles sans valeur, droits de l'API (lecture seule, aucune fonction d'administration), contrôles à la publication, journal avant / après en ajout seul, élément `mutu_` publié une fois et renouvelé seulement s'il change, libération et rétablissement d'une officine, central restauré, état des officines. |
| `tests/007_familles_banque_clients_tests.sql` | Tests : défauts, effacement à la réception, nouvelle version descendue une fois cochées. |
| `tests/006_debit_chargement_tests.sql` | Tests : quota du jour hors chargement, ignoré pendant, plafond du chargement, détection du chargement en cours. |
| `tests/005_voie_descendante_elements_tests.sql` | Tests : version 1 au premier échange, acquittement, nouvelle version après changement de réglage, cloisonnement, écriture directe refusée. |
| `tests/004_reglage_familles_envoyees_tests.sql` | Tests des réglages : défauts, surcharge, effacement à la réception et journal, effacement de famille (refus, simulation, confirmation), refus sous 6 ans, effacement inactif par défaut, journal, partitions vides. |
| `tests/003_chargement_complet_officine_tests.sql` | Tests du chargement : ordre instantané / modifications, saut des lots perdus, absentes signalées et gardées, idempotence, cloisonnement. |
| `tests/001_base_centrale_tests.sql` | Tests en transaction annulée : cloisonnement (l'officine B ne lit ni n'écrit les lignes de A, même sans filtre), idempotence, trou, empreinte différente, suppression, état final, codes d'activation, journal en ajout seul, absence de nom de patient. |

## Règles

- Scripts **idempotents**, numérotés, passés dans l'ordre par le **propriétaire** de la base (jamais par `pharma_cloud_api`).
- **Lots bruts** (`lot_recu`) : ajout seul. Deux exceptions contrôlées par le déclencheur `cloud_lot_recu_garde` : suppression d'un lot dont
  la durée de conservation de son officine est dépassée, effacement activé et opération posée par `cloud_purger_lots` ; retrait de colonnes
  nominatives par `cloud_effacer_famille` (contenu recontrôlé : seules les colonnes listées disparaissent). Les partitions étant communes
  à toutes les officines, une partition n'est supprimée qu'une fois vide.
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
foreach ($f in "001_base_centrale.sql", "002_tables_miroir.sql", "003_chargement_complet_officine.sql", "004_reglage_familles_envoyees.sql", "005_voie_descendante_elements.sql", "006_debit_chargement.sql", "007_familles_banque_clients.sql", "008_famille_banque_elargie.sql", "009_central_restaure_quota_chargement.sql", "010_mutualisation_reglages.sql", "011_descendant_ajouts_locaux_motifs_remises.sql", "012_verification_integrite_central.sql", "013_ordres_vers_officines.sql", "014_ordre_prix_produit.sql") {
    & "C:\Program Files\PostgreSQL\18\bin\psql.exe" -U pharma_app -h localhost -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f "database\cloud\$f" }
& "C:\Program Files\PostgreSQL\18\bin\psql.exe" -U pharma_app -h localhost -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database\cloud\tests\001_base_centrale_tests.sql
```
