# Quels scripts servent à la mise en production

> Réponse du 2026-11-26, d'après les en-têtes des scripts. Manuel pas à pas : [manuel-mep.md](manuel-mep.md). Règles des scripts de
> base : [scripts-base-de-donnees.md](scripts-base-de-donnees.md).

**Tous les scripts du dépôt ne servent pas à la mise en production.** Il y a quatre sortes de scripts : installation, mise à jour, incidents,
développement.

## 1. Installation neuve d'une officine (parcours A)

**Les scripts numérotés `database\migration\001` à `174` ne sont PAS lancés un par un** : `database\init\schema.sql` en est l'équivalent
exact, tenu à jour jusqu'au dernier script (le script de création refuse de démarrer si l'en-tête « 001 à NNN » est en retard).

| Script | Utilisé ? | Rôle |
|---|---|---|
| `scripts\mep\creer_base_officine.ps1` | **Oui, une fois** | Lance à lui seul, dans l'ordre, les six fichiers ci-dessous, puis vérifie (rôles, comptages, droits des journaux) |
| `database\init\schema.sql` | Oui (par le script) | Toute la structure, équivaut aux scripts 001 à 174 |
| `database\init\fuseau_horaire.sql` | Oui (par le script) | Fuseau Africa/Algiers de la base (absent d'un dump de schéma) |
| `database\init\seed_referentiel.sql` | Oui (par le script) | Référentiel de départ (wilayas, rôles, taux de TVA…) |
| `database\init\111b_roles_integrite.sql` | Oui (par le script) | Séparation des rôles, journaux protégés |
| `database\init\163b_droit_identifiant_base.sql` | Oui (par le script) | Droit de lire l'identifiant de la base (2e serveur) |
| `database\init\166b_droit_promotion_base.sql` | Oui (par le script) | Droit de promouvoir la base (2e serveur) |
| `scripts\mep\outils_mep.ps1` | Oui (chargé par les scripts de `scripts\mep`) | Fonctions communes ; **jamais lancé seul** |
| `scripts\mep\creer_livraison.ps1` | Oui, sur le **PC des sources** (installation et mise à jour) | Publie l'API et le poste dans le dossier de livraison, **obfusque nos sept assemblys** (.NET Reactor en version complète, `-licensed` ; correspondances rangées hors livraison ; refus si l'outil manque ou n'est pas activé ; `-SansObfuscation` pour un essai interne), y copie les **seuls scripts utiles en officine** (`<livraison>\scripts`, liste fermée, § 1 bis), puis lance `nettoyer_livraison.ps1` (2026-11-28) |
| `scripts\mep\nettoyer_livraison.ps1` | Oui (lancé par `creer_livraison.ps1` et `creer_paquet_essai.ps1`) | Nettoie la **copie livrée** : plus de `.pdb` ni de configuration de développement, scripts SQL sans commentaires (`NettoyageSql.cs`), scripts PowerShell sans commentaires (aide de tête et `#Requires` gardés ; refus si les instructions changent), contrôles (dont : aucun fichier de l'outil d'obfuscation, `.nrmap`, `.license`, `.v3lic`) ; refuse le dépôt des sources |
| `scripts\certificat_https.ps1` | **Oui** | HTTPS de l'API sur le réseau, une seule autorité pour toute la pharmacie |
| `scripts\sauvegarde_pharmacie.ps1` | Seulement en dépannage | Script planifié de secours ; la sauvegarde normale est faite par l'application |
| `database\init\clean.sql` | **Jamais** | Remise à vierge : supprime tout |
| `101b` (suppression de `num_facture_chifa`) | **Jamais** à l'installation | — |

## 1 bis. Scripts copiés en officine : le dossier `scripts` de la livraison (lot P1 bis, 2026-11-27)

En officine, on copie **`<dossier de livraison>\scripts`**, jamais le dossier `scripts` du dépôt. `creer_livraison.ps1` n'y met que
18 fichiers, sans leurs commentaires :

| Dossier de la livraison | Scripts |
|---|---|
| `scripts\` | `certificat_https.ps1`, `sauvegarde_pharmacie.ps1`, `restaurer_pharmacie.ps1`, `basculer-pc-secours.ps1` |
| `scripts\mep\` | `creer_base_officine.ps1`, `mettre_a_jour_base_officine.ps1`, `outils_mep.ps1`, `creer_dossier_journaux.ps1` (dossier commun des journaux `C:\Logs\PharmaOfficine` et ses droits, 2026-11-28) |
| `scripts\second-serveur\` | les dix scripts du 2e serveur (`commun.ps1` compris) |

**Jamais livrés** : `scripts\dev\*`, `scripts\paquet\*`, `initialiser_base_locale.ps1`, `creer_paquet_essai.ps1`, `creer_livraison.ps1`,
`nettoyer_livraison.ps1`, `obfusquer_livraison.ps1` (obfuscation de nos assemblys, appelé par `creer_livraison.ps1` et
`creer_paquet_essai.ps1`), `NettoyageSql.cs` (PC des sources) et `mep\creer_base_centrale.ps1` (serveur central, jamais en officine).

Tout nouveau fichier de `scripts\` doit être classé : livré (liste `$scriptsLivres` de `creer_livraison.ps1`) ou non livré (test
`ScriptsLivraisonTests`, qui échoue tant que le fichier n'est pas classé). `creer_base_officine.ps1` et `mettre_a_jour_base_officine.ps1`
se lancent avec `-DossierDepot` et `-DossierMigration` pointant sur le dossier publié de l'API (manuel de MEP, A17 et B).

## 2. Mise à jour d'une officine en service (parcours B)

- `scripts\mep\mettre_a_jour_base_officine.ps1` : sauvegarde obligatoire (vérifiée), puis **tous les scripts numérotés de
  `database\migration` rejoués dans l'ordre depuis le 001** (ils sont rejouables). Avec `-SeparerRoles`, 111b est lancé juste après le
  script 111 s'il n'est pas déjà fait. Ne lance jamais `schema.sql` ni `clean.sql`.
- **Ne jamais lancer un script numéroté seul à la main** : il ramènerait une ancienne version d'une fonction ; toujours toute la suite.
- `101b` : seulement à la mise à jour d'une officine existante, après décision du titulaire sur les conflits de n° de facture Chifa.

## 3. Jamais à la mise en production

| Script | Pourquoi |
|---|---|
| `database\migration\retour\*` | Retours arrière : seulement sur décision, après un incident |
| `database\tests\*`, `database\cloud\tests\*` | Tests, sur une base d'essai |
| `scripts\initialiser_base_locale.ps1` | Bases de développement et d'essai (scripts numérotés puis données de démonstration) |
| `scripts\dev\*` | Développement (serveur central d'essai) |
| `scripts\creer_paquet_essai.ps1`, `scripts\paquet\*` | Paquet d'essai remis à un pharmacien qui veut essayer le logiciel |
| `src\Pharma.Editeur` | Outil de l'éditeur (clés et codes d'activation) : sur le PC de l'éditeur, **jamais chez une officine** ([procedure-cle-editeur.md](procedure-cle-editeur.md)) |

## 4. Selon le cas

| Cas | Scripts |
|---|---|
| **2e serveur** (parcours C) | `scripts\second-serveur\` : `preparer-principal.ps1`, `ouvrir-port-base.ps1`, `initialiser-second-serveur.ps1`, `partager-ged.ps1`, `autoriser-lecture-copie-ged.ps1`, `copier-ged.ps1`, `verifier-second-serveur.ps1` (`commun.ps1` : fonctions communes) ; `basculer-second-serveur.ps1` seulement pour la bascule **manuelle** (F3) ; `remettre-en-second.ps1` au retour du PC réparé (F4). Voir [procedure-deux-serveurs.md](procedure-deux-serveurs.md) et [promotion-serveurs.md](promotion-serveurs.md) |
| **PC de secours** (parcours D) | `scripts\basculer-pc-secours.ps1`, seulement en cas de panne (F2) |
| **Restauration** (F1) | `scripts\restaurer_pharmacie.ps1` |
| **Serveur central** (parcours E) | `scripts\mep\creer_base_centrale.ps1` et `database\cloud\001` au dernier (044 à ce jour) |

## 5. Inventaire complet des scripts de la branche `develop`

Généré le 2026-11-26 depuis `git ls-files` (364 fichiers ; hors code source `src/` et `tests/`). Numéros 113 et 119
non attribués (voulu : [base-de-donnees.md](base-de-donnees.md)).

| Fichier | Usage |
|---|---|
| `database/cloud/001_base_centrale.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/002_tables_miroir.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/003_chargement_complet_officine.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/004_reglage_familles_envoyees.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/005_voie_descendante_elements.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/006_debit_chargement.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/007_familles_banque_clients.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/008_famille_banque_elargie.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/009_central_restaure_quota_chargement.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/010_mutualisation_reglages.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/011_descendant_ajouts_locaux_motifs_remises.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/012_verification_integrite_central.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/013_ordres_vers_officines.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/014_ordre_prix_produit.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/015_nomenclature_vers_officines.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/016_commande_brouillon_vers_officines.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/017_referentiels_vers_officines.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/018_referentiels_prestations_indications_dci.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/019_miroir_associations_produits.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/020_restauration_officine_archive.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/021_depart_officine.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/022_integrite_restauration_tables.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/023_integrite_verification_branches.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/024_dernier_ticket_officine.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/025_integrite_tickets_reattribues.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/026_archive_sans_tables_recalculees.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/027_depart_effacement_elements_techniques.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/028_effacer_famille_lignes_archivees.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/029_role_lecture_etat.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/030_comptes_distants.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/031_ecrans_editeur.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/032_ecrans_titulaire.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/033_code_rattachement_poste.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/034_titulaire_multi_officines_ordres.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/035_reglages_communs_titulaire.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/036_lignes_absentes_ecrans.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/037_envoi_codes_reglage.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/038_remise_mot_de_passe_retrait_message.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/039_creation_compte_ecran.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/040_import_comptes_duree_provisoire.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/041_delai_provisoire_comptes_anterieurs.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/042_deconnexion_motif_inactivite.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/043_regles_rattachement_ordres_central.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/044_ordre_activation_offre.sql` | Serveur central seulement (lancé par `creer_base_centrale.ps1`, ou suite rejouée à sa mise à jour) |
| `database/cloud/tests/001_base_centrale_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/003_chargement_complet_officine_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/004_reglage_familles_envoyees_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/005_voie_descendante_elements_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/006_debit_chargement_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/007_familles_banque_clients_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/008_famille_banque_elargie_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/009_central_restaure_quota_chargement_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/010_mutualisation_reglages_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/011_descendant_ajouts_locaux_motifs_remises_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/012_verification_integrite_central_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/013_ordres_vers_officines_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/014_ordre_prix_produit_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/015_nomenclature_vers_officines_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/016_commande_brouillon_vers_officines_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/017_referentiels_vers_officines_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/018_referentiels_prestations_indications_dci_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/019_miroir_associations_produits_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/020_restauration_officine_archive_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/021_depart_officine_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/022_integrite_restauration_tables_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/023_integrite_verification_branches_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/024_dernier_ticket_officine_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/025_integrite_tickets_reattribues_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/026_archive_sans_tables_recalculees_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/027_depart_effacement_elements_techniques_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/028_effacer_famille_lignes_archivees_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/029_role_lecture_etat_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/030_comptes_distants_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/031_ecrans_editeur_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/032_ecrans_titulaire_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/033_code_rattachement_poste_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/034_titulaire_multi_officines_ordres_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/035_reglages_communs_titulaire_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/036_lignes_absentes_ecrans_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/037_envoi_codes_reglage_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/038_remise_mot_de_passe_retrait_message_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/039_creation_compte_ecran_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/040_import_comptes_duree_provisoire_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/041_delai_provisoire_comptes_anterieurs_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/042_deconnexion_motif_inactivite_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/043_regles_rattachement_ordres_central_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/cloud/tests/044_ordre_activation_offre_tests.sql` | Jamais en production : test du serveur central, sur une base d'essai |
| `database/data/900_donnees_demo.sql` | Jamais en production : données fictives de démonstration |
| `database/data/901_complement_sintrom.sql` | Jamais en production : données fictives de démonstration |
| `database/data/902_emplacements_demo.sql` | Jamais en production : données fictives de démonstration |
| `database/data/903_factures_demo.sql` | Jamais en production : données fictives de démonstration |
| `database/data/904_coordonnees_demo.sql` | Jamais en production : données fictives de démonstration |
| `database/data/905_jeu_de_test.sql` | Jamais en production : données fictives de démonstration |
| `database/init/111b_roles_integrite.sql` | Installation neuve (lancé par `creer_base_officine.ps1`) |
| `database/init/163b_droit_identifiant_base.sql` | Installation neuve (lancé par `creer_base_officine.ps1`) |
| `database/init/166b_droit_promotion_base.sql` | Installation neuve (lancé par `creer_base_officine.ps1`) |
| `database/init/clean.sql` | Jamais en production : remise à vierge, supprime tout |
| `database/init/fuseau_horaire.sql` | Installation neuve (lancé par `creer_base_officine.ps1`) |
| `database/init/schema.sql` | Installation neuve (lancé par `creer_base_officine.ps1`) |
| `database/init/seed_referentiel.sql` | Installation neuve (lancé par `creer_base_officine.ps1`) |
| `database/migration/001_schema.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/002_alertes_stock.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/003_provenance.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/004_codes_barres_internes.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/005_seuils_peremption.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/006_droits_utilisateurs.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/007_tracabilite_boite.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/008_reprise_donnees.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/009_caisse.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/010_identite_pharmacie_seuils_titulaire.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/011_ordonnance_scannee.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/012_achats.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/013_notifications.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/014_sauvegardes.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/015_catalogue.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/016_emplacements.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/017_inventaire.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/018_lot_unique.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/019_scan_reception_mobile.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/020_seuils_stock.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/021_prix_boite.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/022_caisse_partagee.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/023_nomenclature.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/024_registre_psychotropes.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/025_affichage_clair_sombre.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/026_ventes_attente.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/027_annulation_vente.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/028_retour_client.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/029_reglements.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/030_tresorerie.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/031_facture_client.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/032_inventaire_complet.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/033_index_performance.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/034_nomenclature_absents.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/035_mot_de_passe_a_changer.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/036_solde_tresorerie_echeance.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/037_produit_a_verifier.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/038_releve_credit.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/039_suivi_dettes.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/040_facture_echeance.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/041_contacts.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/042_email_tiers.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/043_historique_factures.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/044_reception_detaillee.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/045_prix_lot_ug_categories.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/046_blocage_produit.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/047_numeros_recus_autorisation_code.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/048_reprise_classe_prix_achat_lots.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/049_vente_sans_ordonnance.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/050_ged.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/051_rappel_lot_deja_bloque.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/052_reprise_super_admin.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/053_lot_delaisse.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/054_surveillance_caisse.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/055_surveillance_stock.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/056_droit_don.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/057_reception_aveugle.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/058_surveillance_comptes.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/059_surveillance_fantomes_remises_horaires.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/060_volet_chifa.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/061_tva_sorties.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/062_vues_alertes_colonnes_nommees.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/063_revocation_connexions.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/064_caisse_cloturee.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/065_stock_vendable_hors_bloque.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/066_alertes_peremption_claires.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/067_controle_interne.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/068_verification_destinataire.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/069_receptions_attente.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/070_comptage_intermediaire.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/071_don_remise_par_personne.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/072_remises_automatiques.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/073_cle_encaissement.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/074_tresorerie_traites.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/075_rappel_lot_delaisse_a_verifier.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/076_journaux_non_vidables.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/077_alertes_non_envoyees_doublons.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/078_financements.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/079_preteur_libre_alertes_ecarts.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/080_classe_confirmee_titulaire.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/081_conditions_pret_assurance.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/082_medecin_coordonnees.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/083_fuseau_horaire.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/084_avoir_dette_patient.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/085_droit_cloture_caisse.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/086_seuil_ecart_chifa.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/087_imputation_credit.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/088_libelle_encaissement_dette.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/089_canal_organisme_assure_unique.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/090_lien_patient_mdn.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/091_vente_remis_a_pour.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/092_import_patients_doublons.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/093_droit_doublons_import.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/094_motifs_reglables.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/095_rapprochement_volet.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/096_sorties_tiroir.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/097_rapprochement_decisions.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/098_annulations_regularisation_reglement.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/099_remboursement_regularisation.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/100_volet_chifa_centres_payeurs_exclusion.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/101_fusion_facture_chifa.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/101b_suppression_num_facture_chifa.sql` | Mise à jour d'une officine existante seulement, après décision du titulaire sur les conflits Chifa ; jamais à l'installation |
| `database/migration/102_rang_fin_droit.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/103_stock_shp_correction_facture.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/104_indications_motif_retour.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/105_prestations.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/106_avoir_patient.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/107_controle_fin_droits.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/108_prestations_remboursables.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/109_retour_avoir.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/110_bordereaux_chifa.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/111_integrite_hachage.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/112_journal_ventes.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/114_profils_reprise.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/115_multi_grossistes.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/116_ruptures_substitution.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/117_registre_renforce.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/118_wilaya_duree_traitement.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/120_serialisation_file.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/121_option_reprise_chifa.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/122_second_serveur.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/123_formulaire_accord_sms.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/124_profils_reprise_noms_neutres.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/125_wilayas_59_69.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/126_synchronisation_sortie.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/127_synchronisation_textes_medecins.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/128_synchronisation_envoi.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/129_synchronisation_chargement.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/130_synchronisation_familles_nominatives.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/131_synchronisation_descendant.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/132_contenance_depuis_nomenclature.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/133_synchronisation_familles_banque_clients.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/134_contenance_sachets_ampoules.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/135_synchronisation_famille_banque_elargie.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/136_synchronisation_colonnes_figees_descendant.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/137_contenance_unite_taille_unitaire.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/138_synchronisation_noms_personnel_textes.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/139_contenance_flacons_seringues_stylos.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/140_synchronisation_supervision_purge.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/141_mutualisation_reglages.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/142_mutualisation_reglages_modification_locale.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/143_copie_ged_second_serveur_frequence.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/144_synchronisation_verification_integrite_centrale.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/145_synchronisation_ordres_central.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/146_synchronisation_commande_brouillon_central.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/147_synchronisation_associations_produits_montantes.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/148_sauvegarde_alerte_sans_disque_externe.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/149_synchronisation_retour_arriere_nouvelle_epoque.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/150_index_recherche_suggestions.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/151_integrite_restauration_officine.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/152_controle_demarrage_encaissement.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/153_alertes_psychotropes_traitement_en_cours.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/154_parapharmacie_catalogue.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/155_retour_partiel_tiers_payant.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/156_reglage_taches_laissees_poste.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/157_rapprochement_volet_apres_retour.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/158_retour_regularisation_dettes_patient.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/159_retour_regularisation_rendu_patient.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/160_message_central_postes.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/161_index_cles_etrangeres_manquantes.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/162_deconnexion_inactivite_defaut_15_minutes.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/163_serveur_ecarte_lecture_seule.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/164_bascule_saut_compteurs_promotion.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/165_presence_postes.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/166_bascule_retrait_decision.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/167_postes_autorises.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/168_controle_reception_abandonnee.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/169_contenance_contenant_seul_pilulier.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/170_pret_paiement_partiel_remboursement_anticipe.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/171_taux_tva_liste.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/172_activation_offre.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/173_remise_nulle_hors_controle.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/174_postes_preuve_exigee.sql` | Mise à jour : rejoué par `mettre_a_jour_base_officine.ps1` (pas à l'installation : inclus dans `schema.sql`) |
| `database/migration/retour/162_deconnexion_inactivite_defaut_15_minutes_retour.sql` | Jamais en production : retour arrière, seulement sur décision après un incident |
| `database/migration/retour/163_serveur_ecarte_lecture_seule_retour.sql` | Jamais en production : retour arrière, seulement sur décision après un incident |
| `database/migration/retour/163b_droit_identifiant_base_retour.sql` | Jamais en production : retour arrière, seulement sur décision après un incident |
| `database/migration/retour/164_bascule_saut_compteurs_promotion_retour.sql` | Jamais en production : retour arrière, seulement sur décision après un incident |
| `database/migration/retour/165_presence_postes_retour.sql` | Jamais en production : retour arrière, seulement sur décision après un incident |
| `database/migration/retour/166_bascule_retrait_decision_retour.sql` | Jamais en production : retour arrière, seulement sur décision après un incident |
| `database/migration/retour/166b_droit_promotion_base_retour.sql` | Jamais en production : retour arrière, seulement sur décision après un incident |
| `database/migration/retour/167_postes_autorises_retour.sql` | Jamais en production : retour arrière, seulement sur décision après un incident |
| `database/migration/retour/168_controle_reception_abandonnee_retour.sql` | Jamais en production : retour arrière, seulement sur décision après un incident |
| `database/migration/retour/169_contenance_contenant_seul_pilulier_retour.sql` | Jamais en production : retour arrière, seulement sur décision après un incident |
| `database/migration/retour/170_pret_paiement_partiel_remboursement_anticipe_retour.sql` | Jamais en production : retour arrière, seulement sur décision après un incident |
| `database/migration/retour/171_taux_tva_liste_retour.sql` | Jamais en production : retour arrière, seulement sur décision après un incident |
| `database/migration/retour/172_activation_offre_retour.sql` | Jamais en production : retour arrière, seulement sur décision après un incident |
| `database/migration/retour/173_remise_nulle_hors_controle_retour.sql` | Jamais en production : retour arrière, seulement sur décision après un incident |
| `database/migration/retour/174_postes_preuve_exigee_retour.sql` | Jamais en production : retour arrière, seulement sur décision après un incident |
| `database/tests/101b_suppression_num_facture_chifa_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/111_integrite_hachage_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/117_registre_renforce_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/126_synchronisation_sortie_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/127_synchronisation_textes_medecins_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/128_synchronisation_envoi_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/129_synchronisation_chargement_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/130_synchronisation_familles_nominatives_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/131_synchronisation_descendant_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/132_contenance_depuis_nomenclature_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/133_synchronisation_familles_banque_clients_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/134_contenance_sachets_ampoules_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/135_synchronisation_famille_banque_elargie_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/136_synchronisation_colonnes_figees_descendant_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/137_contenance_unite_taille_unitaire_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/138_synchronisation_noms_personnel_textes_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/139_contenance_flacons_seringues_stylos_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/140_synchronisation_supervision_purge_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/141_mutualisation_reglages_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/142_mutualisation_reglages_modification_locale_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/143_copie_ged_second_serveur_frequence_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/144_synchronisation_verification_integrite_centrale_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/145_synchronisation_ordres_central_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/146_synchronisation_commande_brouillon_central_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/147_synchronisation_associations_produits_montantes_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/148_sauvegarde_alerte_sans_disque_externe_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/149_synchronisation_retour_arriere_nouvelle_epoque_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/150_index_recherche_suggestions_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/151_integrite_restauration_officine_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/152_controle_demarrage_encaissement_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/153_alertes_psychotropes_traitement_en_cours_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/154_parapharmacie_catalogue_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/155_retour_partiel_tiers_payant_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/156_reglage_taches_laissees_poste_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/157_rapprochement_volet_apres_retour_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/158_retour_regularisation_dettes_patient_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/159_retour_regularisation_rendu_patient_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/160_message_central_postes_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/161_index_cles_etrangeres_manquantes_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/162_deconnexion_inactivite_defaut_15_minutes_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/163_serveur_ecarte_lecture_seule_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/163b_droit_identifiant_base_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/164_bascule_saut_compteurs_promotion_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/165_presence_postes_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/166_bascule_retrait_decision_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/166b_droit_promotion_base_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/167_postes_autorises_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/168_controle_reception_abandonnee_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/169_contenance_contenant_seul_pilulier_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/170_pret_paiement_partiel_remboursement_anticipe_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/171_taux_tva_liste_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/172_activation_offre_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/173_remise_nulle_hors_controle_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/174_postes_preuve_exigee_tests.sql` | Jamais en production : test, sur une base d'essai |
| `database/tests/rejeu_complet.ps1` | Jamais en production : test, sur une base d'essai |
| `deploy/demo/docker-compose.yml` | Jamais en production : démonstration cloud (Docker) |
| `deploy/demo/init/10_base_officine.sh` | Jamais en production : démonstration cloud (Docker) |
| `deploy/demo/reinit/remise_a_zero.sh` | Jamais en production : démonstration cloud (Docker) |
| `scripts/basculer-pc-secours.ps1` | Incident F2 : panne du PC serveur, bascule sur le PC de secours |
| `scripts/certificat_https.ps1` | Installation : HTTPS de l'API (aussi sur le 2e serveur et le PC de secours) |
| `scripts/creer_paquet_essai.ps1` | Jamais en production : fabrique le paquet d'essai remis à un pharmacien |
| `scripts/dev/essai_cloud_officine.ps1` | Jamais en production : développement |
| `scripts/dev/preparer_central_vs.ps1` | Jamais en production : développement |
| `scripts/initialiser_base_locale.ps1` | Jamais en production : bases de développement et d'essai |
| `scripts/mep/creer_base_centrale.ps1` | Serveur central seulement : crée sa base |
| `scripts/mep/creer_base_officine.ps1` | Installation neuve : **une fois**, crée la base (schema, fuseau, référentiel, 111b, 163b, 166b) |
| `scripts/mep/mettre_a_jour_base_officine.ps1` | Mise à jour d'une officine en service : sauvegarde puis toute la suite depuis 001 |
| `scripts/mep/outils_mep.ps1` | Chargé par les trois scripts de `scripts\mep` ; jamais lancé seul |
| `scripts/mep/NettoyageSql.cs` | Chargé par `nettoyer_livraison.ps1` (et compilé dans les tests d'architecture) ; jamais lancé seul |
| `scripts/paquet/demarrer.cmd` | Jamais en production : paquet d'essai |
| `scripts/paquet/installer.ps1` | Jamais en production : paquet d'essai |
| `scripts/restaurer_pharmacie.ps1` | Incident F1 : restauration de la base ou de la GED |
| `scripts/sauvegarde_pharmacie.ps1` | Dépannage seulement : sauvegarde planifiée de secours (la sauvegarde normale est faite par l'application) |
| `scripts/second-serveur/autoriser-lecture-copie-ged.ps1` | 2e serveur : installation (lecture de la copie de la GED) |
| `scripts/second-serveur/basculer-second-serveur.ps1` | 2e serveur : incident F3, bascule **manuelle** seulement (la promotion automatique n'en a pas besoin) |
| `scripts/second-serveur/commun.ps1` | 2e serveur : fonctions communes, jamais lancé seul |
| `scripts/second-serveur/copier-ged.ps1` | 2e serveur : installation (tâche de copie de la GED) ; `-Desinstaller` après une bascule |
| `scripts/second-serveur/initialiser-second-serveur.ps1` | 2e serveur : installation (copie de la base) |
| `scripts/second-serveur/ouvrir-port-base.ps1` | 2e serveur : installation et retour F4 |
| `scripts/second-serveur/partager-ged.ps1` | 2e serveur : installation et retour F4 (partage de la GED) |
| `scripts/second-serveur/preparer-principal.ps1` | 2e serveur : installation (sur le principal) et retour F4 |
| `scripts/second-serveur/remettre-en-second.ps1` | 2e serveur : incident F4, retour du PC réparé comme 2e serveur |
| `scripts/second-serveur/verifier-second-serveur.ps1` | 2e serveur : contrôle après installation et après chaque intervention |
