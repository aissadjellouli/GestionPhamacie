# Scripts de la base de données : quel script lancer, quand, avec quel compte

Ce document est la **référence unique** pour créer ou mettre à jour une base. Le détail de chaque étape de la mise en production reste dans [mise-en-production.md](mise-en-production.md) ; la description de chaque script SQL est dans [base-de-donnees.md](base-de-donnees.md).

Les trois scripts PowerShell sont dans `scripts\mep\`. Ils enchaînent tout dans le bon ordre, avec des contrôles, et s'arrêtent à la première erreur (code de sortie 1). Ils acceptent `-WhatIf` (affiche ce qui serait fait, ne change rien). Aucun mot de passe n'est passé en paramètre : il est saisi à l'écran, reste en mémoire le temps du script et n'est jamais écrit sur disque (ou bien un fichier `pgpass.conf` existant est utilisé).

## 1. Quelle situation, quel script

| Situation | Script | PC | Compte PostgreSQL |
| --- | --- | --- | --- |
| Installation neuve d'une officine (base qui n'existe pas) | `creer_base_officine.ps1` | PC serveur de l'officine | `postgres` (superutilisateur) pour créer le rôle et la base ; `pharma_app` pour la structure |
| Mise à jour d'une officine en service | `mettre_a_jour_base_officine.ps1` | PC serveur de l'officine | `pharma_app` ; après la séparation des rôles : `postgres` avec `SET ROLE pharma_proprietaire` |
| Serveur central de synchronisation neuf | `creer_base_centrale.ps1` | serveur central | `postgres` pour créer la base ; le compte propriétaire pour les scripts |

Toutes les commandes se lancent **depuis le dossier du dépôt** (ou de la livraison), dans une fenêtre PowerShell en administrateur. Si PowerShell refuse le script : `powershell -ExecutionPolicy Bypass -File .\scripts\mep\<script>.ps1 <paramètres>`.

Options communes : `-Hote` (défaut `localhost`), `-Port` (défaut 5432), `-PgBin` (dossier `bin` de PostgreSQL ; défaut : la version la plus récente de `C:\Program Files\PostgreSQL`).

### 1.1 Base neuve d'officine : `creer_base_officine.ps1`

```powershell
.\scripts\mep\creer_base_officine.ps1 -Base pharmacie
```

| Option | Rôle |
| --- | --- |
| `-Base <nom>` (obligatoire) | Nom de la base à créer (minuscules, chiffres, `_`) |
| `-CompteAdmin <compte>` | Compte qui crée le rôle et la base (défaut `postgres`) |
| `-SansSeparationRoles` | Ne lance pas `111b`. **Les journaux ne sont alors pas protégés** : à réserver aux essais |
| `-WhatIf` | Simulation |

**Ce que fait le script, dans l'ordre**

1. Vérifie, sans toucher à la base : `psql`, `pg_dump`, `pg_restore` trouvés ; les en-têtes de `database\init\schema.sql` et `seed_referentiel.sql` disent « 001 à NNN », NNN étant le dernier script numéroté de `database\migration`. Sinon : **refus avant de commencer** (l'init est en retard, voir le développeur).
2. Vérifie que PostgreSQL répond, que la base **n'existe pas** (si elle existe : refus, rien n'est supprimé), que le compte d'administration a les droits nécessaires.
3. Crée le rôle `pharma_app` s'il manque (avec connexion et le droit `CREATEDB`, nécessaire à la restauration depuis l'application) ; demande alors le mot de passe à lui donner (12 caractères au moins, propre à cette pharmacie, noté au coffre). Ajoute `CREATEDB` s'il manque.
4. Crée la base (propriétaire `pharma_app`, UTF8).
5. Passe `init\schema.sql`, puis `083_fuseau_horaire.sql` (le fuseau horaire n'est pas repris dans `schema.sql`), puis `init\seed_referentiel.sql`.
6. Sauf `-SansSeparationRoles` : passe `111b_roles_integrite.sql` avec le superutilisateur (voir 3.1).
7. Vérifie : fuseau `Africa/Algiers` ; comptages de référence (wilayas, rôles, utilisateurs, aucune vente) ; `pharma_app` lit et écrit (écriture d'essai annulée) ; avec `111b` : `objets_encore_a_pharma_app = 0`, propriétaire de la base `pharma_proprietaire`, `pharma_app` ne peut plus modifier `journal_audit`.

**Résultat attendu** : la ligne « Terminé », le compte de départ `titulaire` (mot de passe provisoire à changer à la première connexion), code de sortie 0.

**En cas d'échec** : lire le message. Si la base a été créée à moitié (échec après l'étape 4), elle contient des données partielles : le script ne la supprime jamais. Une base **sans aucune donnée réelle** se remet à vide à la main (parcours A, « Recommencer depuis zéro », avec sauvegarde) ; sinon voir « Retour arrière » plus bas.

### 1.2 Mise à jour d'une officine en service : `mettre_a_jour_base_officine.ps1`

```powershell
.\scripts\mep\mettre_a_jour_base_officine.ps1 -Base pharmacie -DossierSauvegarde "E:\Sauvegardes\Pharmacie"
```

| Option | Rôle |
| --- | --- |
| `-Base <nom>` (obligatoire) | Base de l'officine |
| `-DossierSauvegarde <dossier>` (obligatoire) | Où écrire la sauvegarde `pg_dump -Fc` (créé si besoin, de préférence disque externe). Le fichier `<base>_avant_maj_<date>_<heure>.dump` n'est jamais écrasé |
| `-SeparerRoles` | Si `111b` n'est pas fait, le passe juste après le script `111` |
| `-ContinuerMalgreConflitsChifa` | Reprend après un arrêt sur n° de facture Chifa, une fois la décision du titulaire prise |
| `-ScriptDepart <NNN>` | Premier script à rejouer. Défaut : **001, toujours**. Réservé à la reprise après incident (voir ci-dessous) |
| `-CompteSuperUtilisateur <compte>` | Défaut `postgres` ; demandé seulement si les rôles sont ou vont être séparés |
| `-DossierMigration <dossier>` | Dossier des scripts (défaut : `database\migration` du dépôt). Pour une livraison : `<livraison>\Api\database\migration` |
| `-Script111b <fichier>` | Chemin de `111b_roles_integrite.sql` (il n'est pas dans le dossier publié) |
| `-NomService <nom>` | Service de l'API (défaut `PharmaOfficine API`) |
| `-SansControleService` | Ne contrôle pas le service. **Essais sur une copie seulement** |
| `-WhatIf` | Simulation |

**Ce que fait le script, dans l'ordre**

1. Contrôles sans connexion : outils, scripts trouvés, **service « PharmaOfficine API » arrêté** (sinon refus).
2. Détection de l'état des rôles : séparés (`111b` déjà fait : rôle `pharma_proprietaire` présent, propriétaire de la base et des tables) ou non. État partiel : refus.
3. **Sauvegarde `pg_dump -Fc` obligatoire**, relue par `pg_restore --list`. Sans sauvegarde valide, rien n'est modifié.
4. Rejeu des scripts numérotés (`NNN_*.sql`, 001 à 999, **jamais les scripts « b »**), un par un, dans l'ordre, chacun avec `ON_ERROR_STOP`. Les scripts sont idempotents : il n'y a pas de table de suivi. Chaque script est lancé seul, **hors transaction** (le `033` contient `CREATE INDEX CONCURRENTLY`, interdit dans une transaction ; c'est le seul script dans ce cas).
5. **Avant le script `101`** (si `101` n'est pas encore appliqué) : les 3 requêtes de contrôle des n° de facture Chifa, en lecture seule. S'il y a des lignes : **arrêt** avec la liste. **Ne pas continuer : voir le titulaire.** Après sa décision, relancer avec `-ContinuerMalgreConflitsChifa`.
6. Avec `-SeparerRoles` et rôles non séparés : `111b` est passé (superutilisateur) juste après le script `111`.
7. Contrôles finaux et résumé : scripts joués, durée, état des rôles, chemin de la sauvegarde.

**Point de départ du rejeu (`-ScriptDepart`)**. Toujours le script **001** : toute la suite se rejoue sur une base déjà à jour, sans départ automatique (l'ancien départ à 101, qui contournait les échecs des scripts `007`, `018`, `097` et `098`, est supprimé : ces scripts, ainsi que `024`, `126` et `129`, sont corrigés, voir la règle des vues en 4). Le paramètre `-ScriptDepart <NNN>` reste, pour un **incident seulement** (reprise au script en échec) ; une fois la cause corrigée, relancer sans ce paramètre pour que toute la suite soit rejouée. La restauration d'une sauvegarde depuis l'application rejoue aussi toute la suite (001 à 032 en transaction, 033 seul, 034 et suivants en transaction).

**Comptes**
- Avant `111b` : `pharma_app`.
- Après `111b` : le superutilisateur, avec `SET ROLE pharma_proprietaire` dans la même session `psql` (`-c "SET ROLE pharma_proprietaire" -f script`). **[À VÉRIFIER]** Cette méthode n'a pas encore été essayée avec le compte `postgres` (aucun mot de passe superutilisateur disponible pour l'essai du 2026-11-16) : l'essayer sur une **copie** de la base avant la production. Le script contrôle en début de course que `SET ROLE pharma_proprietaire` donne bien le rôle attendu et s'arrête sinon.
- `111b` : toujours le superutilisateur.

**Le script `101b` n'est jamais lancé par ce script** (voir 3.2).

**En cas d'échec** : le message donne le script en cause. Corriger la cause, relancer le même script, sans `-ScriptDepart` (toute la suite est rejouable ; `-ScriptDepart <numéro du script en échec>` n'est qu'un secours en cas d'incident). Si on ne s'en sort pas : retour arrière ci-dessous. Le service de l'API reste arrêté tant que la mise à jour n'est pas réussie.

### 1.3 Serveur central neuf : `creer_base_centrale.ps1`

```powershell
.\scripts\mep\creer_base_centrale.ps1 -Base pharmacie_central -Proprietaire <compte propriétaire> -AvecTests
```

| Option | Rôle |
| --- | --- |
| `-Base <nom>` (obligatoire) | Nom de la base centrale |
| `-Proprietaire <compte>` (obligatoire) | Compte propriétaire de la base, distinct de `pharma_cloud_api`. Il doit exister (parcours E, étape 1.3) |
| `-CompteAdmin <compte>` | Compte qui crée la base (défaut `postgres`) |
| `-AvecTests` | Passe aussi les tests de `database\cloud\tests` (transaction annulée) |
| `-WhatIf` | Simulation |

**Ce que fait le script** : contrôle la suite `database\cloud\001` à `028` (sans trou) ; refuse si la base contient déjà des tables centrales ; crée la base si elle n'existe pas ; passe `001` à `028` dans l'ordre avec le compte propriétaire (il n'y a pas de `schema.sql` central : un seul endroit à tenir à jour) ; vérifie les tables, la sécurité au niveau des lignes sur les tables miroir, le rôle `pharma_cloud_api` (créé **sans connexion** par `001`, propriétaire d'aucune table) ; avec `-AvecTests`, chaque fichier de test doit finir par « tous réussis ».

**Après le script** : le mot de passe de `pharma_cloud_api` se règle à part, par le superutilisateur (parcours E, étape 2.4) : `ALTER ROLE pharma_cloud_api LOGIN;` puis `\password pharma_cloud_api`. Ne jamais lancer les tests sur la base de production du central (parcours E, étape 2.2, **[À CONFIRMER]**).

### 1.4 Retour arrière (restauration de la sauvegarde)

Pour une mise à jour qui a échoué ou qu'on abandonne : la sauvegarde `.dump` faite par le script est la seule voie de retour (les scripts n'ont pas de « Down »). L'API reste arrêtée, puis restauration comme au parcours F1 de [mise-en-production.md](mise-en-production.md) (base d'abord restaurée sous un autre nom, contrôlée, puis renommée). Les ventes saisies depuis la sauvegarde sont perdues : décider vite, avant la reprise de l'activité. Les scripts déjà appliqués avant un échec sont sans perte de données (idempotents), le retour arrière n'est donc nécessaire que si la base reste inutilisable.

## 2. Les dossiers et leur rôle

| Dossier | Rôle |
| --- | --- |
| `database\init\` | **Base neuve** d'officine : `schema.sql` (toute la structure) et `seed_referentiel.sql` (référentiel de départ) équivalent aux scripts 001 à NNN. À tenir à jour **à chaque nouveau script** : leur en-tête cite « 001 à NNN » et `creer_base_officine.ps1` refuse de créer une base si NNN n'est pas le dernier script. `clean.sql` remet une base à vierge (jamais en service) |
| `database\migration\` | **Mise à jour** d'une base existante : scripts `NNN_nom.sql` rejoués dans l'ordre, idempotents. Les scripts « b » (`101b`, `111b`) sont à part (3.1, 3.2) |
| `database\cloud\` | Base **centrale** : scripts `001` à `028`, jamais sur une base d'officine |
| `database\tests\` | Tests SQL des scripts de `migration` (transaction annulée) ; `rejeu_complet.ps1` : rejeu de toute la suite sur une copie jetable (pas un test SQL) |
| `database\cloud\tests\` | Tests SQL des scripts du central (lancés par `-AvecTests`) |
| `database\data\` | Données **fictives** de démonstration (`9xx_*.sql`) : développement et recette seulement, **jamais en production** |

## 3. Scripts à part et cas particuliers

### 3.1 `111b` : séparation des rôles (journaux inaltérables)

`database\migration\111b_roles_integrite.sql`, une fois par base, avec le **superutilisateur**. Il crée le rôle sans connexion `pharma_proprietaire`, lui donne la base et les objets, et retire à `pharma_app` la modification et la suppression des journaux. Sans lui, `pharma_app` peut désactiver les déclencheurs de protection : la vérification nocturne détecte la falsification mais ne l'empêche pas.
- Base neuve : lancé par `creer_base_officine.ps1` (sauf `-SansSeparationRoles`).
- Mise à jour : option `-SeparerRoles`, ou à la main avec `psql` et le compte `postgres` : `psql -h localhost -U postgres -d <base> -v ON_ERROR_STOP=1 -f database\migration\111b_roles_integrite.sql` (résultat attendu : `objets_encore_a_pharma_app` = 0).
- Ensuite, **tous** les scripts de mise à jour passent par le superutilisateur avec `SET ROLE pharma_proprietaire`, plus jamais par `pharma_app`.

### 3.2 `101b` : jamais automatique

`database\migration\101b_suppression_num_facture_chifa.sql` supprime la colonne obsolète `vente.num_facture_chifa` : **irréversible** (sauf restauration). Aucun script `mep` ne le lance. Il se passe seulement après la **décision explicite du titulaire**, les conflits de n° de facture Chifa tranchés dans l'application, une sauvegarde vérifiée et l'API arrêtée. Procédure : parcours B, étape 8 de [mise-en-production.md](mise-en-production.md).

### 3.3 `033` hors transaction

`033_index_performance.sql` contient `CREATE INDEX CONCURRENTLY`. Il ne peut pas être joué dans une transaction : les scripts `mep` lancent chaque fichier seul (pas de `BEGIN` autour, pas de `--single-transaction`). Un test d'architecture vérifie que `033` reste le seul script concerné.

### 3.4 Ne jamais rejouer un script isolé

Un ancien script rejoué seul peut ramener une **fonction ou une vue à une ancienne version** (exemple : rejouer `136` ou `137` après `139` remet l'ancienne définition). Toujours rejouer **toute la suite** depuis le script 001, c'est ce que fait `mettre_a_jour_base_officine.ps1`. Chaque script est écrit pour que ce rejeu ne change rien sur une base à jour (règle 2 ci-dessous).

## 4. Règles pour écrire un nouveau script

1. **Nom parlant** : `NNN_ce_que_fait_le_script.sql` (le numéro suivant le dernier ; pas de nom du genre `corrections` ou `complement`).
2. **Idempotent et rejouable sur une base plus récente que lui** : rejouable sans erreur et sans effet sur une base à jour (tableau dans [base-de-donnees.md](base-de-donnees.md)), car la restauration d'une sauvegarde et la mise à jour d'une officine rejouent toute la suite 001 à NNN. **Règle des vues** : `CREATE OR REPLACE VIEW` ne peut ni renommer ni supprimer de colonne, et ne doit jamais ajouter de colonne à une vue définie par un script antérieur (créer une autre vue). Si un script plus récent change quand même les colonnes d'une vue (renommage, ajout), l'ancienne définition est protégée par une garde sur le catalogue : un bloc `DO $garde$ BEGIN IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = '<vue>' AND column_name = '<colonne de la version finale>') THEN EXECUTE $vue$ CREATE OR REPLACE VIEW … $vue$; END IF; END $garde$;` (exemples : `007`, `018`, `097`, `098`). Nommer les colonnes une à une (jamais `alias.*`, figé à la création : exemple corrigé `024`). Jamais `DROP VIEW … CASCADE` (il détruirait les objets dépendants). Une fonction redéfinie par un script plus récent (autre signature qui l'appelle, autre langage) n'est pas recréée par l'ancien script quand la version récente existe (exemples : `126` et `129`, gardées sur l'existence de la version à 5 arguments de `sync_enregistrer_lot`). Une contrainte CHECK à liste de valeurs se garde de même (base-de-donnees.md). Contrôles : `ScriptsRejouablesTests` (échoue si un script rejoué sur une base à jour échoue ou change la structure, si une vue utilise `alias.*` ou `DROP VIEW … CASCADE`) et `database/tests/rejeu_complet.ps1` sur une copie.
3. **Exécuté sur `pharmacie_dev` ET `pharmacie_test`**, après une sauvegarde `pg_dump -Fc`, deux fois de suite (le second passage ne change rien). **Rejeu complet de la suite vérifié sur une copie jetable** : `database/tests/rejeu_complet.ps1 -Base pharmacie_jetable_rejeu_<nom>` (copie de la base de développement obtenue par `pg_dump -Fc` puis `pg_restore` ; rejoue 001 à NNN comme la restauration, deux fois, et compare structure, données et comptages avant/après).
4. **`init` mis à jour dans la même modification** : `schema.sql` régénéré (`pg_dump --schema-only --no-owner --no-privileges --no-comments` d'une base où tout est rejoué), `seed_referentiel.sql` complété si le script ajoute des données de référence, en-têtes « 001 à NNN » à jour. Le test `ScriptsInitTests` échoue sinon.
5. **Ajouté dans `PharmaOfficine.sln` et `PharmaOfficine.slnx`** (dossier `database`).
6. **Test SQL** dans `database\tests\NNN_nom_tests.sql` (transaction annulée), ajouté aussi aux solutions.
7. Si le script touche une table envoyée au serveur central : régénérer `database\cloud\002_tables_miroir.sql` (voir `database\cloud\README.md`).
8. Si le script contient une instruction interdite dans une transaction (`CREATE INDEX CONCURRENTLY`, `VACUUM`…) : le signaler ici et dans `ScriptsMepTests`.
9. Mettre à jour [base-de-donnees.md](base-de-donnees.md) (une ligne dans le tableau) et, pour une règle métier, `regles-metier.md`.
10. **Écriture dans un réglage mutualisable** (script 141 : `parametre_fiscal`, `regle_alerte_psychotrope`, `parametre_tracabilite`, `parametre_serialisation`, `parametre_substitution`, `motif_reglable`, `parametre_formulaire_sms`, `parametre_surveillance`, `parametre_alerte`, `parametre_inventaire`, `remise_automatique`) : la garde `zz_reglage_garde` refuse toute écriture sur une table **imposée par le serveur central** (synchronisation active et officine inscrite, mode `impose`). Un script qui écrit dans l'une de ces tables, ou une mise à jour qui rejoue toute la suite, doit poser le marqueur de session `SET LOCAL pharma.reglage_central = 'on'` (ou lancer `psql` avec `PGOPTIONS="-c pharma.reglage_central=on"`). Tant qu'aucune table n'est imposée (cas actuel : toutes locales, aucun modèle publié), la garde ne bloque rien. **Outils qui posent déjà ce marqueur (lot M4)** : la restauration depuis l'application (`RestaurationPostgres.OptionsScripts`), `scripts/mep/mettre_a_jour_base_officine.ps1` (par `Invoke-OutilPg` de `outils_mep.ps1`), `database/tests/rejeu_complet.ps1`, `scripts/initialiser_base_locale.ps1` et `scripts/paquet/installer.ps1` ; le test `MarqueurRejeuReglagesTests` vérifie les fichiers et rejoue toute la suite sur une copie jetable dont les 11 tables sont imposées (échec sans le marqueur, réussite avec). Un nouvel outil qui lance `psql` sur la suite doit faire de même. Preuve manuelle : `rejeu_complet.ps1 -Base pharmacie_jetable_rejeu_… -SansMarqueurReglages` (option de preuve, doit échouer sur une copie dont une table est imposée) puis sans l'option (réussit). Voir [conception-synchronisation.md](conception-synchronisation.md) § 16 bis.

## 5. Renommages du 2026-11-16 (ancien nom, nouveau nom)

Pour qui cherche un ancien nom dans une note, un ticket ou une sauvegarde. Le contenu des scripts n'a pas changé (sauf mention).

| Ancien nom | Nouveau nom |
| --- | --- |
| `migration\010_administration.sql` | `migration\010_identite_pharmacie_seuils_titulaire.sql` |
| `migration\019_mobile.sql` | `migration\019_scan_reception_mobile.sql` |
| `migration\025_affichage.sql` | `migration\025_affichage_clair_sombre.sql` |
| `migration\047_recette_corrections.sql` | `migration\047_numeros_recus_autorisation_code.sql` |
| `migration\048_reprise_corrections.sql` | `migration\048_reprise_classe_prix_achat_lots.sql` |
| `migration\059_corrections_surveillance.sql` | `migration\059_surveillance_fantomes_remises_horaires.sql` |
| `migration\062_vues_parametres_nommes.sql` | `migration\062_vues_alertes_colonnes_nommees.sql` |
| `migration\100_volet_chifa_complements.sql` | `migration\100_volet_chifa_centres_payeurs_exclusion.sql` |
| `migration\136_synchronisation_corrections_revue.sql` | `migration\136_synchronisation_colonnes_figees_descendant.sql` |
| `migration\137_contenance_extension.sql` | `migration\137_contenance_unite_taille_unitaire.sql` |
| `cloud\003_chargement.sql` | `cloud\003_chargement_complet_officine.sql` |
| `cloud\004_reglages.sql` | `cloud\004_reglage_familles_envoyees.sql` |
| `cloud\005_descendant.sql` | `cloud\005_voie_descendante_elements.sql` |
| `cloud\009_corrections_revue.sql` | `cloud\009_central_restaure_quota_chargement.sql` |
| `tests\136_synchronisation_corrections_revue_tests.sql` | `tests\136_synchronisation_colonnes_figees_descendant_tests.sql` |
| `tests\137_contenance_extension_tests.sql` | `tests\137_contenance_unite_taille_unitaire_tests.sql` |
| `cloud\tests\003_chargement_tests.sql` | `cloud\tests\003_chargement_complet_officine_tests.sql` |
| `cloud\tests\004_reglages_tests.sql` | `cloud\tests\004_reglage_familles_envoyees_tests.sql` |
| `cloud\tests\005_descendant_tests.sql` | `cloud\tests\005_voie_descendante_elements_tests.sql` |
| `cloud\tests\009_corrections_revue_tests.sql` | `cloud\tests\009_central_restaure_quota_chargement_tests.sql` |

## 6. Outil de développement : `scripts\initialiser_base_locale.ps1`

Crée ou met à jour les bases **de développement** (`pharmacie_dev`, `pharmacie_test`) : rôle `pharma_app` de démonstration, scripts numérotés **001 à 999** (jamais les « b »), puis données fictives de `database\data`. **Jamais en production** : son mot de passe de démonstration est écrit dans le script, et `-Recreer` supprime des bases. Toute la suite se rejoue depuis 001, y compris sur une base de développement déjà à jour (scripts rejouables, règle 2 du chapitre 4).

## 7. Outil de développement : `scripts\dev\essai_cloud_officine.ps1`

Lance sur le PC de développement une officine d'essai et un serveur central d'essai reliés entre eux (base centrale `pharmacie_cloud_essai` créée par `creer_base_centrale.ps1`, copie jetable `pharmacie_essai_officine` de `pharmacie_dev`). **Jamais en production.** `-Demarrer`, `-Etat`, `-Arreter`, `-WhatIf` ; `pharmacie_dev` n'est lue que par `pg_dump`. Guide : [essai-cloud-officine.md](essai-cloud-officine.md).

## En cas d'échec : première aide

| Message | Cause probable |
| --- | --- |
| « L'en-tête de schema.sql ne cite pas 001 à NNN » | L'init n'a pas été régénéré après le dernier script : voir le développeur, rien n'a été fait |
| « La base existe déjà » | Normal : ces scripts ne créent que des bases neuves. Mise à jour : `mettre_a_jour_base_officine.ps1` |
| « Le service … est Running » | Arrêter l'API : `Stop-Service "PharmaOfficine API"` |
| « ARRÊT avant le script 101 » | N° de facture Chifa à examiner avec le titulaire (1.2, point 5) |
| « ne peut pas modifier le nom de la colonne d'une vue » / « ne peut pas supprimer les colonnes d'une vue » | Un script redéfinit une vue dont un script plus récent a changé les colonnes, sans garde : corriger le script (règle des vues, 4). Ne plus utiliser de départ à 101 |
| « à exécuter par un superutilisateur » | `111b` lancé avec un autre compte que `postgres` |
| « SET ROLE pharma_proprietaire n'a pas donné le rôle attendu » | Méthode `SET ROLE` à vérifier (1.2) : ne pas continuer, voir le technicien |
