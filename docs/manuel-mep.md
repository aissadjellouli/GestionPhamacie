# Manuel de mise en production (MEP) d'une officine

Ce manuel met **dans l'ordre chronologique** tout ce qu'il faut faire pour mettre une officine en service, de J-15 au lendemain de
l'ouverture. Il condense [mise-en-production.md](mise-en-production.md) (la référence détaillée), [procedure-deux-serveurs.md](procedure-deux-serveurs.md),
[promotion-serveurs.md](promotion-serveurs.md), [procedure-cle-editeur.md](procedure-cle-editeur.md) et [offre-et-options.md](offre-et-options.md).
Chaque étape renvoie à sa section source (« détail : … »). Les commandes sont recopiées **à l'identique** des sources.

Rédigé le 2026-11-26, à partir des sources à cette date. Écarts entre les sources corrigés le 2026-11-26 (annexe A8).

**Mode d'emploi**

- Suivre les étapes **de haut en bas**, sans en sauter. Cocher chaque case `- [ ]` au fur et à mesure.
- Les valeurs entre `<` et `>` sont **à remplacer** (exemple : `<serveur>`, `<base>`, `<mot de passe>`).
- Les marqueurs **[À CONFIRMER]** et **[À VÉRIFIER]** viennent des sources : la réponse n'est pas connue. Ne pas la deviner :
  demander au titulaire ou au développeur.
- Les chapitres marqués **« facultatif / selon le cas »** ne se font que si l'officine en a besoin.

---

## Sommaire

- [0. Avant de commencer](#0-avant-de-commencer)
  - [0.1 Règles absolues](#01-règles-absolues)
  - [0.2 Rôles](#02-rôles)
  - [0.3 Les PC et les lieux](#03-les-pc-et-les-lieux)
  - [0.4 Prérequis matériel et Windows](#04-prérequis-matériel-et-windows)
  - [0.5 Comptes](#05-comptes)
  - [0.6 Ports](#06-ports)
  - [0.7 Services Windows](#07-services-windows)
  - [0.8 Variables d'environnement](#08-variables-denvironnement)
  - [0.9 Fichiers et dossiers](#09-fichiers-et-dossiers)
  - [0.10 À avoir sous la main le jour J](#010-à-avoir-sous-la-main-le-jour-j)
  - [0.11 Conventions de commandes](#011-conventions-de-commandes)
  - [0.12 Décisions déjà prises](#012-décisions-déjà-prises)
  - [0.13 Calendrier et critère de retour arrière](#013-calendrier-et-critère-de-retour-arrière)
- [Partie A. Installation neuve d'une officine (cas principal)](#partie-a-installation-neuve-dune-officine-cas-principal)
- [Chapitre C. 2e serveur (facultatif / selon le cas)](#chapitre-c-2e-serveur-facultatif--selon-le-cas)
- [Chapitre D. PC de secours (facultatif / selon le cas)](#chapitre-d-pc-de-secours-facultatif--selon-le-cas)
- [Chapitre E. Serveur central de synchronisation (facultatif / selon le cas)](#chapitre-e-serveur-central-de-synchronisation-facultatif--selon-le-cas)
- [Chapitre R. Reprise des données de l'ancien logiciel (facultatif / selon le cas)](#chapitre-r-reprise-des-données-de-lancien-logiciel-facultatif--selon-le-cas)
- [Chapitre B. Mise à jour d'une officine existante](#chapitre-b-mise-à-jour-dune-officine-existante)
- [Chapitre F. Procédures d'incident (F1 à F8)](#chapitre-f-procédures-dincident-f1-à-f8)
- [Annexes](#annexes)
- [Checklist finale avant ouverture](#checklist-finale-avant-ouverture)
- [Checklist après chaque mise à jour](#checklist-après-chaque-mise-à-jour)
- [Table de correspondance](#table-de-correspondance)

---

## 0. Avant de commencer

### 0.1 Règles absolues

Toutes ces règles viennent des sources (référence entre parenthèses).

| # | Règle | Source |
| --- | --- | --- |
| R1 | **Ne jamais ouvrir l'API sur internet.** Accès du titulaire à distance : par VPN seulement. | mise-en-production.md, A 13.1 |
| R2 | **HTTPS seul sur le réseau de l'officine.** HTTP seulement sur le PC serveur lui-même (`http://localhost:5080`). Ne jamais régler `AutoriserHttpReseau`. | « Décisions déjà prises », A 4.5 |
| R3 | **Le port 5432 (PostgreSQL) n'est jamais ouvert au réseau**, sauf au 2e serveur (et à lui seul). Le port 445 (copie de la GED) seulement à l'IP de l'autre serveur. | « Ports », C 1, C 4 bis |
| R4 | **Aucun mot de passe ni secret** dans un fichier du dépôt, dans `appsettings*.json`, dans une commande, dans un script ou dans un courriel. Les mots de passe sont saisis à l'écran et notés au coffre. | « Les PC et les lieux », A 4.4, E 3.2 |
| R5 | **Jamais de certificat ni de secret par le réseau** : transport par clé USB. Le certificat de l'autorité ne passe jamais par un lien HTTP ni par courriel. | « Les PC et les lieux », A 5.3, A 6.1 |
| R6 | **Le PFX de l'autorité** (clé privée) reste au coffre, hors ligne : jamais sur un poste, un téléphone, la clé USB des postes `E:\`, ni par le réseau ou courriel. | A 4.6 (point 4) |
| R7 | **La clé privée de l'éditeur** (`cle-editeur.pem`) reste chez l'éditeur : générée et utilisée sur le **PC de l'éditeur**, jamais sur le serveur d'une officine. L'outil `Pharma.Editeur` n'est **jamais livré** aux pharmacies. | procedure-cle-editeur.md, « Règles de sécurité » ; offre-et-options.md, « Décisions » |
| R8 | Ne jamais lancer les fichiers `database\data\9xx_*.sql` (données fictives) ; ne jamais utiliser le paquet `scripts/paquet/` pour une officine réelle. | A 3.1, A 12.7, « Décisions déjà prises » |
| R9 | Ne jamais relancer `schema.sql` ni `clean.sql` sur une base en service. | Parcours B (intro) |
| R10 | Le script `101b` n'est **jamais** passé à l'installation (une base créée depuis `database\init` n'a jamais la colonne). Sur une base existante, seulement après le « go » du titulaire. | A 3.1, B 8 |
| R11 | Après la séparation des rôles (`111b`), ne plus jamais lancer les scripts de la base avec `pharma_app`. | A 3.1, B 6 |
| R12 | Les scripts `163b` et `166b` se passent avec `postgres` **sur la base de la pharmacie** (`-d <base>`), **jamais sur la base système `postgres`**. | promotion-serveurs.md § 3 |
| R13 | **Une seule base en écriture**, toujours. L'ancien principal ne redevient jamais actif sans la procédure F4. | Parcours C (intro), F3 étape 6 |
| R14 | **Un seul mécanisme de sauvegarde** : la sauvegarde de l'application. Le script planifié n'est qu'un dépannage, jamais en même temps. | A 11.1 |
| R15 | Ne jamais copier le dossier `Synchronisation` d'un serveur à l'autre. | Parcours E (intro), annexe A4 |
| R16 | Toute **suppression de données** (base, famille nominative, officine partie) : décision humaine, validation du titulaire obligatoire. | A 3 « Recommencer depuis zéro », E 7.1, F8 étape 4 |
| R17 | Ne pas démarrer le service de l'API (A 4.7) avant le certificat HTTPS (A 4.6) : sinon l'API refuse de démarrer. | Parcours A (intro) |
| R18 | Tout script du 2e serveur : lancer d'abord avec `-Essai` (ou `-WhatIf`), lire, puis sans. | procedure-deux-serveurs.md § 1 ; parcours C (intro) |
| R19 | Ne jamais rediriger vers un fichier la sortie de `--depart-officine` (le mot de passe de l'archive s'y affiche). | F8 étape 1 |
| R20 | Ne pas publier de réglage mutualisé en production avant la livraison du lot M4. | E 9 |
| R21 | Avant toute mise en service avec deux serveurs : **essai de bascule réel obligatoire**. | procedure-deux-serveurs.md § 7 bis ; promotion-serveurs.md § 11 |

### 0.2 Rôles

| Rôle | Qui | Ce qu'il fait |
| --- | --- | --- |
| **Technicien** | personne qui installe | Commandes PowerShell, services, pare-feu, base |
| **Titulaire** | pharmacien titulaire | Réglages dans l'application, décisions (sauvegardes, Chifa, synchronisation, suppression de données) |
| **Éditeur** | éditeur du logiciel | Signe le code d'activation de l'offre (sur **son** PC) ; administre le serveur central |
| **Super admin** | compte créé sur le PC serveur (A 10.1) | Reprise de données et restauration depuis l'application (rôle `super_admin`). Il ne signe aucune offre et ne fixe aucune limite : c'est l'**éditeur** (écart n° 9 de l'annexe A8 : corrigé) |
| **Administrateur Windows** | compte qui a les droits d'administrateur sur le PC | Installe, change les variables système, lance les scripts |
| **Superutilisateur PostgreSQL** | compte `postgres` (mot de passe noté à l'installation) | Crée les rôles, lance `111b`, `163b`, `166b`, les procédures de rôles |
| **Compte `pharma_app`** | compte de l'application, propriétaire de la base avant `111b` | Compte de connexion de l'API ; lance les scripts de la base tant que les rôles ne sont pas séparés |

Détail : mise-en-production.md, « Rôles » ; procedure-cle-editeur.md ; offre-et-options.md, « Décisions ».

### 0.3 Les PC et les lieux

| Nom | Ce que c'est |
| --- | --- |
| **PC serveur** | PC principal de l'officine : PostgreSQL et API. Toujours allumé, onduleur obligatoire |
| **Poste** | poste de caisse (un par caisse), qui ouvre Tracodex Officine |
| **PC des sources** | PC qui a le code du dépôt et le SDK .NET 10 : c'est là qu'on compile (`dotnet publish`) |
| **PC de l'éditeur** | PC où la clé privée de l'éditeur signe les codes d'activation (jamais celui de l'officine) |
| **2e serveur** | 2e PC de l'officine, réplique de la base (chapitre C, facultatif) |
| **PC de secours** | PC de l'officine avec PostgreSQL et l'API, prêt à prendre le relais (chapitre D, facultatif) |
| **Serveur central** | serveur de synchronisation hors officine (chapitre E, facultatif) : hébergement **[À VÉRIFIER : hébergeur agréé]** |
| **Téléphone** | scanner mobile (Android ou iPhone) |
| **Clé USB** | support de transport (exemple : `E:\` pour les postes, `F:\` pour le coffre). Jamais de certificat ni de secret par le réseau |

Détail : mise-en-production.md, « Les PC et les lieux » ; procedure-cle-editeur.md.

### 0.4 Prérequis matériel et Windows

Concerne le **PC serveur** et le **2e serveur** (même configuration : en bascule, il porte toute la charge).

**Version minimale de Windows pour le chiffrement résistant au quantique** : ce chiffrement relève du **lot 9, pas encore codé**
(chiffrement-data.md, « Statut » et § 11 : « rien n'est lancé »). Le tableau ci-dessous est donc le minimum **exigé le jour où le lot 9
sera mis en service** ; pour faire tourner le logiciel aujourd'hui, le minimum est celui de A02 (Windows 10 ou Windows Server 2016).
Choisir dès maintenant un PC serveur qui passe en Windows 11 24H2 évite de le changer au lot 9.

| Fonction | Windows 11 Pro | Windows Server |
| --- | --- | --- |
| ML-KEM et ML-DSA (.NET 10, chiffrement et signature) | **24H2** ou 25H2, mise à jour de novembre 2025 (KB5068861, build 26100.7171) ou plus | **2025**, même mise à jour |
| Échange de clé hybride en HTTPS (Schannel, TLS 1.3, `X25519_MLKEM768`) | 24H2 et 25H2 : KB5089573 (builds 26100.8524 et 26200.8524) ; 26H1 : KB5095091 (build 28000.2340) | **2025** : KB5099536 (build 26100.33158, 14 juillet 2026) |

- Windows 10, Windows 11 23H2 et avant, Windows Server 2022 et avant : non cités par Microsoft, donc considérés comme non pris en charge **pour le
  chiffrement du lot 9** (pas pour le fonctionnement actuel, voir A02).
- Listes complétées par Microsoft au fil des mises à jour : **à revérifier avant la mise en service**.
- Postes : minimum (application du poste et Chifa_Officine) non mesuré **[À VÉRIFIER]**. Un poste sous Windows 10 n'a plus de mises à jour
  de sécurité depuis octobre 2025 : sujet à traiter à part.

**Plancher matériel de Windows 11** : processeur 64 bits de 2 cœurs à 1 GHz de la liste compatible (Intel Core 8e génération ou plus,
AMD Ryzen 2000 ou plus), 4 Go de mémoire, 64 Go de disque, UEFI avec Secure Boot, puce TPM 2.0.

**Configuration conseillée du PC serveur** (estimation, non mesurée **[À VÉRIFIER : essai de charge]**) :

| Élément | Conseillé |
| --- | --- |
| Processeur | 4 cœurs : Intel Core i3 12e génération ou plus, ou AMD Ryzen 3 5000 ou plus |
| Mémoire | **16 Go** (8 Go au strict minimum) |
| Disque | SSD NVMe de 512 Go (place de la GED **[À VÉRIFIER : volume réel]**) |
| Sauvegarde | Deux disques externes en rotation, dont un toujours hors de l'officine (**obligatoire** sans 2e serveur) |
| Réseau | Ethernet gigabit, pas de Wi-Fi |
| Onduleur | **Obligatoire** |
| Windows | Windows 11 Pro 24H2 ou plus, à jour (exigé pour le lot 9, conseillé dès maintenant) |

**Licence Windows** : Windows 11 Pro recommandé (≈ 200 $, **[À VÉRIFIER]**). Les clés vendues 1 000 à 5 000 DA sur les petites annonces
sont déconseillées : exiger la facture d'un revendeur agréé. Chiffres exacts **[À VÉRIFIER]** (nombre d'officines, prix d'un revendeur
agréé en Algérie ou taux de change).

**Heure des PC** : à moins d'une minute entre les deux serveurs, à moins de 2 minutes entre chaque poste et le serveur.

Détail : mise-en-production.md, « Matériel et Windows du PC serveur » et A 1.2 ; procedure-deux-serveurs.md § 1.7 ; « Postes autorisés ».

### 0.5 Comptes

| Compte | Où | Créé à | Mot de passe |
| --- | --- | --- | --- |
| Administrateur Windows | chaque PC | avant | connu du technicien |
| `postgres` (superutilisateur PostgreSQL) | PC serveur, 2e serveur, PC de secours | A10 (installation PostgreSQL) | noté au coffre |
| `pharma_app` (compte de l'application) | PC serveur | A15 (`creer_base_officine.ps1`) | 12 caractères minimum, propre à la pharmacie, noté au coffre |
| `pharma_proprietaire` (sans connexion) | base de la pharmacie | A15 (`111b`) | aucun (pas de connexion) |
| `titulaire` (application) | base | A15 (référentiel) | provisoire `Provisoire-2026`, changé à la première connexion (8 caractères minimum) |
| Super admin (application) | PC serveur (`super-admin.dat`) | A59 | 12 caractères au moins, + téléphone et/ou e-mail vérifié |
| Comptes des employés | application | A47 | personnels |
| Rôle de réplication `pharma_replication` | PC serveur | C (`preparer-principal.ps1`) | 12 caractères au moins, au coffre |
| `PharmaGedCopie` (compte Windows local) | les deux serveurs | C (`partager-ged.ps1`) | 14 caractères au moins, au coffre, identique sur les deux |
| `pharma_cloud_api`, `<propriétaire>` | serveur central | E | tiré au sort, au coffre |

### 0.6 Ports

| Port | Où | Qui peut s'y connecter | Rôle |
| --- | --- | --- | --- |
| 5432 | PC serveur | **Personne sur le réseau**, sauf le 2e serveur (chapitre C) | PostgreSQL |
| 5080 | PC serveur (et 2e serveur, PC de secours) | `localhost` seulement (jamais le réseau) | API en HTTP, pour le PC lui-même |
| 5443 | PC serveur (et 2e serveur, PC de secours) | Postes, téléphones (réseau privé) | API en HTTPS |
| 445 | serveur principal | seulement l'IP de l'autre serveur (chapitre C) | Partage SMB de la GED |
| 5090 | Serveur central | `127.0.0.1` par défaut **[À VÉRIFIER : exposition à l'hébergement]** | Serveur central (HTTPS) |

Détail : mise-en-production.md, « Ports » ; parcours C, étape 1.

### 0.7 Services Windows

| Service | Où | Démarrage | Remarque |
| --- | --- | --- | --- |
| `postgresql-x64-18` | PC serveur, 2e serveur, PC de secours | Automatique | Ne pas arrêter pendant la journée |
| `PharmaOfficine API` | PC serveur | Automatique | Créé à l'étape A24 |
| `PharmaOfficine API` | 2e serveur | **Automatique**, avec relance en cas d'arrêt | Tourne **en veille** ; ne pas démarrer avant d'avoir réglé `SecondServeur:AutreServeur` |
| `PharmaOfficine API` | PC de secours | **Manuel** (arrêté) | Démarré seulement en cas de bascule |
| Serveur central | Hébergement | **[À CONFIRMER : pas de service Windows dans le code, voir parcours E, étapes 3.2 et 5.2]** | |

Détail : mise-en-production.md, « Services Windows ».

### 0.8 Variables d'environnement

**API (système, scope `Machine`)** — PC serveur, 2e serveur, PC de secours :

| Variable | Obligatoire | Exemple (à remplacer) | Rôle |
| --- | --- | --- | --- |
| `ConnectionStrings__Pharmacie` | oui | `Host=localhost;Port=5432;Database=<base>;Username=pharma_app;Password=<mot de passe>` | Connexion à la base. Pas de caractère « ; » dans le mot de passe |
| `Jwt__Cle` | oui | 64 caractères minimum, aléatoire | Signature des connexions. Même valeur sur les deux serveurs (chapitre C) |
| `Chifa__DossierExport` | oui | `<dossier d'export de CHIFA-Officine sur ce PC>` | Par défaut `D:\CHIFA_OFFICINE\CNAS_SIGNED` : **à remplacer** |
| `Ged__Dossier` | non | `C:\ProgramData\PharmaOfficine\GED` (défaut) | Dossier de tous les documents |
| `Ged__RetentionSauvegardesJours` | non | `7` (défaut) | Conservation (en jours) des copies de base dans `GED\Sauvegardes`, si le titulaire n'a pas fixé la sienne (Administration, Sauvegardes). Distinct du nombre de sauvegardes gardées sur le disque externe (14 par défaut) |
| `Ordonnances__Dossier` | non | défaut : `GED\Ordonnances` | Scans d'ordonnances |
| `Journaux__Dossier`, `Journaux__Prefixe`, `Journaux__TailleMaxMo` (50), `Journaux__RetentionMois` (3), `Journaux__Niveau` | non | | Journal du serveur |
| `LimiteConnexions__ParMinute` | non | `20` (défaut) | Essais de connexion par minute et par adresse |
| `Sauvegardes__PgDump` | non | chemin de `pg_dump.exe` | Si l'API ne le trouve pas. **[À CONFIRMER : comportement de l'API quand la valeur est vide.]** |
| `AutoriserHttpReseau` | **non, à ne pas régler** | | Dérogation provisoire : HTTP en clair sur le réseau |
| `SecondServeur__AutreServeur` | seulement avec un 2e serveur | IP de l'autre serveur | Évite que deux API écrivent |
| `SecondServeur__Bascule__PromotionAutomatique` | non | vrai par défaut | `false` = bascule toujours manuelle |
| `SuperAdmin:Fichier` | non | `C:\ProgramData\PharmaOfficine\super-admin.dat` (défaut) | Compte super admin |
| `Integrite:FichierCle` | non | `C:\ProgramData\PharmaOfficine\cle-integrite.dat` (défaut) | Clé de scellement |

**Postes** (chaque poste, scope `Machine`) :

| Variable | Valeur |
| --- | --- |
| `PHARMA_API` | `https://<serveur>:5443/` (HTTPS, obligatoire sauf sur le PC serveur) |
| `PHARMA_POSTE` | un nom **différent** par poste, ex. `Caisse 1`, `Caisse 2` (c'est le tiroir de la caisse) |

**Serveur central** (session PowerShell de l'outil d'administration) : `PHARMA_CLOUD_DOSSIER`, `PHARMA_CLOUD_MOT_DE_PASSE`,
`PHARMA_CLOUD_CONNEXION`, `PHARMA_CLOUD_ADMIN_CONNEXION`, et pour l'envoi d'e-mail `PHARMA_CLOUD_SMTP_HOTE`, `PHARMA_CLOUD_SMTP_PORT`,
`PHARMA_CLOUD_SMTP_UTILISATEUR`, `PHARMA_CLOUD_SMTP_MOT_DE_PASSE`, `PHARMA_CLOUD_SMTP_EXPEDITEUR` (voir E-09).

Détail : mise-en-production.md, « Variables d'environnement de l'API », A 4.5, E 3.2 ; procedure-deux-serveurs.md § 7.7.

### 0.9 Fichiers et dossiers

| Chemin | Où | Contenu | À sauvegarder |
| --- | --- | --- | --- |
| `C:\Program Files\PharmaOfficine\Api` | PC serveur | Programme de l'API, `appsettings.Https.json`, `database\` (scripts numérotés) et `database\init\` (tous les scripts de MEP : schéma, référentiel, fuseau, `111b`, `163b`, `166b`) | Non (republié à chaque mise à jour) |
| `C:\ProgramData\PharmaOfficine\super-admin.dat` | PC serveur | Compte super admin, chiffré pour ce PC | **Oui**, sur le disque externe |
| `C:\ProgramData\PharmaOfficine\cle-integrite.dat` | PC serveur | Clé de scellement des journaux, chiffrée pour ce PC | **Oui**, sur le disque externe |
| `C:\ProgramData\PharmaOfficine\archives-fiscales\AAAA\MM` | PC serveur | Rapports scellés, à conserver 6 ans au moins | **Oui** |
| `C:\ProgramData\PharmaOfficine\GED` | PC serveur | Documents (ordonnances, Chifa, factures, rapports…) | Oui (sous-dossier `GED` du disque externe) |
| `C:\ProgramData\PharmaOfficine\Journaux` | PC serveur | Journal du serveur (`pharma-api-AAAAMMJJ.log`) | Non |
| `C:\ProgramData\PharmaOfficine\Journaux\Central` | serveur central | Journaux `central-api-AAAAMMJJ.log` et `central-ecrans-AAAAMMJJ.log` | Non |
| `C:\ProgramData\PharmaOfficine\poste.json` | chaque poste | Réglages du poste (imprimantes, adresse de secours) | Non |
| `C:\ProgramData\PharmaOfficine\copie-secours.json` | PC serveur | Réglages et état de la copie de secours | Non |
| `C:\ProgramData\PharmaOfficine\Synchronisation` | PC serveur | Clé privée de synchronisation (DPAPI, ce PC seulement) | **Ne jamais copier sur un autre serveur** |

**Contenu de `database\init`** (livré avec l'API, `<dossier de livraison>\Api\database\init`) :

| Fichier | Rôle | Passé par |
| --- | --- | --- |
| `schema.sql` | Structure complète : équivaut aux scripts `001` à `174` (en-tête du fichier), sans la colonne supprimée par `101b` | `creer_base_officine.ps1` (compte `pharma_app`) |
| `fuseau_horaire.sql` | Fuseau de la base `Africa/Algiers` (copie du script `083`) | `creer_base_officine.ps1` |
| `seed_referentiel.sql` | Référentiel de départ (rôles, droits, organismes, taux de TVA 0 % et 19 %, compte `titulaire`…) | `creer_base_officine.ps1` |
| `111b_roles_integrite.sql` | Séparation des rôles (journaux inaltérables) | `creer_base_officine.ps1`, compte `postgres` |
| `163b_droit_identifiant_base.sql` | Droit de lire l'identifiant de la base (bascule du 2e serveur) | `creer_base_officine.ps1`, compte `postgres` |
| `166b_droit_promotion_base.sql` | Droit de promouvoir la base (promotion automatique du 2e serveur) | `creer_base_officine.ps1`, compte `postgres` |
| `clean.sql` | Remise à vierge d'une base | **Ne sert plus** à l'installation ; jamais livré dans le dossier publié |

`101b` n'est **jamais** dans `database\init` ni passé à l'installation. `163b` et `166b` ne servent qu'avec un 2e serveur, mais sont sans
effet sinon.

Détail : mise-en-production.md, « Fichiers et dossiers à connaître », A 4.1 ; en-têtes de `database\init\*.sql` et de `scripts\mep\creer_base_officine.ps1`.

### 0.10 À avoir sous la main le jour J

**Au coffre** (jamais dans un fichier du dépôt, jamais par courriel) :

- [ ] Mot de passe du superutilisateur `postgres`.
- [ ] Mot de passe de `pharma_app` (propre à cette pharmacie).
- [ ] Clé de signature `Jwt__Cle` (72 caractères générés à A21).
- [ ] Phrase de passe du PFX de l'autorité (papier, **à part** de la clé USB du coffre).
- [ ] Empreinte SHA-256 de l'autorité (aussi dans `E:\empreinte-autorite-pharmacie.txt`).
- [ ] Mot de passe du super admin, et son téléphone / e-mail vérifiés.
- [ ] Mot de passe de la clé de scellement exportée (`.pocle`), si 2e serveur ou PC de secours.
- [ ] Mot de passe du rôle de réplication et du compte `PharmaGedCopie`, phrase de passe du PFX du 2e serveur (si 2e serveur).

**Supports** :

- [ ] Clé USB **des postes** (`E:\`) : `autorite-pharmacie.crt`, `empreinte-autorite-pharmacie.txt`, livraison (`Api`, `Poste`), dossier `scripts`.
- [ ] Clé USB **du coffre** (`F:\`) : `autorite-pharmacie.pfx` ; une **seconde copie** du PFX sur une autre clé, rangée ailleurs.
- [ ] **Deux disques externes** de sauvegarde (même lettre de lecteur, même dossier), dont un toujours hors de l'officine.
- [ ] Fichier de la nomenclature nationale (`database\data\NOMENCLATURE NATIONALE - VERSION JUIN 2026.xlsx` ou plus récent).

**Informations** :

- [ ] Fiche de la pharmacie (A04) : nom, adresse, téléphone, RC, NIF, NIS, AI, n° d'officine Chifa, employés et rôles, banques,
      organismes conventionnés, SMTP et/ou modem GSM.
- [ ] Adresse IP fixe du PC serveur (et du 2e serveur, du PC de secours).
- [ ] **Code d'activation** de l'offre : code de transition (ou de l'offre vendue) signé par l'éditeur pour **ce** code d'officine et **ce**
      NIF (A45). Sans code : offre de base, 2 postes, écrans des packs fermés.
- [ ] Taux de TVA des catégories validés par le comptable (A54).

Détail : mise-en-production.md A 1.1, A 1.4, A 4.4, A 4.6, A 10.1, A 11.2 bis ; procedure-deux-serveurs.md § 10 ; offre-et-options.md, « Mise en service ».

### 0.11 Conventions de commandes

- Une fenêtre **PowerShell « en administrateur »** : clic droit sur PowerShell, « Exécuter en tant qu'administrateur ».
- Dans chaque fenêtre PowerShell du PC serveur, définir une fois (adapter le numéro si la version de PostgreSQL diffère) :
  ```powershell
  $psql = "C:\Program Files\PostgreSQL\18\bin\psql.exe"
  ```
- psql demande le mot de passe à chaque connexion. PostgreSQL accepte un fichier `pgpass.conf`. **[À CONFIRMER : le technicien le crée-t-il ?]**
- Si PowerShell refuse un script (stratégie d'exécution) : `powershell -ExecutionPolicy Bypass -File .\scripts\<script>.ps1 <paramètres>`.
- Les scripts se lancent **depuis le dossier du dépôt** (ou de la livraison), là où se trouve le dossier `scripts`.

Détail : mise-en-production.md, « Conventions de commandes ».

### 0.12 Décisions déjà prises

- **HTTPS seul sur le réseau de l'officine** (2026-11-16). Hors développement, l'API **refuse de démarrer** tant qu'elle écoute en HTTP sur le réseau.
- **Synchronisation avec le serveur central désactivée par défaut.** Rien ne part tant que le titulaire ne l'active pas (chapitre E).
- **Clé de scellement des journaux** : exportée par le titulaire dans un fichier protégé par mot de passe (annexe A2).
- **Le paquet `scripts/paquet/` est pour un essai** (données fictives). Jamais pour une officine réelle.
- **Sauvegarde** : un seul mécanisme, celui de l'application (2026-11-26).
- **Offre** : sans activation valable, offre de base, 2 postes ; le maximum de postes vient de l'offre (plus réglable par le titulaire).
- **Deux serveurs** : essai de bascule réel obligatoire avant la mise en service (2026-11-25).

Détail : mise-en-production.md, « Décisions déjà prises », A 11.1, C 16 ; offre-et-options.md, « Décisions ».

### 0.13 Calendrier et critère de retour arrière

| Jour | Étapes de ce manuel |
| --- | --- |
| J-15 | Répétition générale complète sur une copie (A01 à A26, puis A41 à A67 et chapitre R si reprise de données) |
| J-15 à J-7 | Installation réelle : A01 à A40 ; chapitres D et C s'ils sont prévus |
| J-7 | Formation, paramétrage et sauvegardes (A41 à A70) |
| J-1 au soir | Clôture de caisse de l'ancien logiciel, sauvegarde, export, reprise (chapitre R), contrôle des totaux, vérifications (A72 à A77) |
| J | Ouverture, accompagnement sur place (A78) |
| J+1 à J+30 | Ancien logiciel en lecture seule ; étiquetage progressif du stock (A79) |

**Critère de retour arrière**, fixé à l'avance : si les totaux ne concordent pas la veille au soir, on rouvre l'ancien logiciel et on reporte.

Détail : mise-en-production.md, parcours A, « Calendrier type ». (La ligne « J-15 à J-7 » regroupe les étapes 1 à 7 de la source, que le
calendrier de la source ne place pas explicitement.)

---

## Partie A. Installation neuve d'une officine (cas principal)

Format de chaque étape : **Qui** · **Où** (PC, compte) · **Action** · commande · **Attendu** · **Si échec** · renvoi à la source.
« MEP » = [mise-en-production.md](mise-en-production.md).

### Phase A1 — Préparer (J-15 à J-7)

- [ ] **A01 — Matériel.** **Qui** : technicien. **Où** : sur place, aucun compte.
  - PC serveur toujours allumé, **onduleur obligatoire**. Désactiver la mise en veille du PC serveur (Paramètres, Système, Alimentation).
  - Un poste par caisse, chacun avec une imprimante de tickets (pilote Windows) et une douchette si utilisée.
  - Imprimante d'étiquettes réseau (facultative).
  - Deux disques externes pour les sauvegardes, une clé USB pour les certificats et la clé de scellement.
  - **Attendu** : tout le matériel est là et branché.
  - Détail : MEP § A 1.1.

- [ ] **A02 — Versions de Windows.** **Qui** : technicien. **Où** : chaque PC, aucun compte.

  | Machine | Minimum technique | Recommandé |
  | --- | --- | --- |
  | Poste de caisse | Windows 10 version 2004 (build 19041) | Windows 11 (22H2 ou plus), ou Windows 10 IoT Enterprise LTSC |
  | PC serveur | Windows 10 ou Windows Server 2016 (fonctionnement actuel, sans le lot 9) | Windows 11 Pro 24H2 ou plus, ou Windows Server 2025 : **exigé** le jour où le chiffrement du lot 9 sera mis en service (§ 0.4) |

  - Le minimum « Windows 10 ou Windows Server 2016 » vaut pour le logiciel d'aujourd'hui. Le chiffrement résistant au quantique (lot 9,
    pas encore codé) exigera Windows 11 Pro 24H2 ou Windows Server 2025 sur le PC serveur et le 2e serveur (§ 0.4) : un PC sous
    Windows 10 ou Server 2016 à 2022 devra alors être mis à niveau ou remplacé. Écart n° 2 de l'annexe A8 : corrigé.
  - Le logiciel n'a été essayé que sous Windows 11 : **faire un essai sur le système réel avant le jour J**. Les téléphones n'ont besoin que
    d'un navigateur récent.
  - Détail : MEP § A 1.2 et « Matériel et Windows du PC serveur ».

- [ ] **A03 — Réseau.** **Qui** : technicien. **Où** : PC serveur et box, administrateur Windows.
  - Donner au PC serveur une **adresse IP fixe** (box ou PC). Noter l'adresse.
  - Vérifier : `ipconfig` affiche l'adresse retenue. Wi-Fi seulement si le scanner mobile est utilisé.
  - Détail : MEP § A 1.3.

- [ ] **A04 — Informations à demander à la pharmacie.** **Qui** : technicien, auprès du titulaire. **Où** : papier.
  - Nom, adresse, téléphone ; **RC, NIF, NIS, AI** ; n° d'officine Chifa ; employés et rôles (assistant, vendeur, stagiaire) ; banques ;
    organismes conventionnés (militaires…) avec adresse et NIF ; compte e-mail (SMTP) et/ou modem GSM pour les alertes.
  - CHIFA-Officine installé sur le PC serveur et son dossier d'export connu **[À CONFIRMER : procédure d'installation fournie par la CNAS,
    hors du dépôt]**. Lecteur de carte et Token : **[À CONFIRMER : à installer sur quel PC]**.
  - Détail : MEP § A 1.4.

- [ ] **A05 — Nomenclature nationale.** **Qui** : technicien. **Où** : PC des sources ou PC serveur.
  - Fichier livré : `database\data\NOMENCLATURE NATIONALE - VERSION JUIN 2026.xlsx`. Si le ministère a publié plus récent (miph.gov.dz),
    le garder pour A52.
  - Détail : MEP § A 1.5.

- [ ] **A06 — Outils du PC des sources.** **Qui** : technicien. **Où** : PC des sources, administrateur Windows.
  - Installer le **SDK .NET 10** **[À CONFIRMER : qui compile les livraisons de production ?]**.
    ```powershell
    dotnet --version
    ```
  - **Attendu** : une version 10.x. **Si échec** : installer le SDK puis rouvrir PowerShell.
  - Détail : MEP § A 1.6.

- [ ] **A07 — Répétition.** **Qui** : technicien. **Où** : copie de test. Pour une reprise de données : répétition générale complète sur une
  **copie** de l'ancienne base (chapitre R). L'ancienne base n'est jamais modifiée.
  - Détail : MEP § A 1.7.

- [ ] **A08 — Décider si un PC de secours est prévu.** **Qui** : titulaire. Si oui, faire le **chapitre D** après A26 (il dépend seulement du
  PC serveur installé). Décider aussi du 2e serveur (chapitre C, après l'installation) et du serveur central (chapitre E).
  - Détail : MEP § A 1.8, A 13.2, A 13.3.

### Phase A2 — PC serveur : PostgreSQL et Windows

- [ ] **A09 — Ouvrir PowerShell et définir `$psql`.** **Qui** : technicien. **Où** : PC serveur, PowerShell en administrateur. Voir § 0.11.
  - Détail : MEP, « Conventions de commandes ».

- [ ] **A10 — Installer PostgreSQL 18** (avec `psql` et `pg_dump`, inclus). **Qui** : technicien. **Où** : PC serveur, administrateur Windows.
  - Noter le mot de passe de `postgres` (au coffre). Garder le port `5432`. Prérequis : Windows 10 ou plus (runtime C « UCRT »).
    ```powershell
    & "C:\Program Files\PostgreSQL\18\bin\psql.exe" --version
    Get-Service postgresql* | Select-Object Name, Status, StartType
    ```
  - **Attendu** : version `psql (PostgreSQL) 18…` ; service `postgresql-x64-18` `Running` et `Automatic`.
  - **Si échec** : relancer l'installeur ; vérifier qu'aucune autre version de PostgreSQL n'occupe le port 5432.
  - Détail : MEP § A 2.1.

- [ ] **A11 — Compte `pharma_app` : rien à faire ici.** Le script de A15 crée `pharma_app` (avec `CREATEDB`) **et** la base, et demande son mot
  de passe (propre à la pharmacie, 12 caractères minimum, au coffre). **Ne pas créer la base à la main** : le script refuse une base existante.
  - Création à la main seulement pour le PC de secours (chapitre D, D-03).
  - Détail : MEP § A 2.2.

- [ ] **A12 — Fuseau horaire de Windows** (Algérie, UTC+1 toute l'année). **Qui** : technicien. **Où** : PC serveur, administrateur Windows.
  ```powershell
  Set-TimeZone -Id "W. Central Africa Standard Time"
  Get-TimeZone
  ```
  - **Attendu** : « (UTC+01:00) Afrique centrale - Ouest ». Sinon, l'API écrit un avertissement et les dates des ventes sont décalées.
  - **Si échec** : Paramètres, Heure et langue, Date et heure, Fuseau horaire.
  - Détail : MEP § A 2.3.

- [ ] **A13 — PostgreSQL fermé au réseau.** **Qui** : technicien. **Où** : PC serveur, administrateur Windows.
  ```powershell
  Get-NetTCPConnection -LocalPort 5432 -State Listen | Select-Object LocalAddress
  ```
  - **Attendu** : seulement `127.0.0.1` et `::1`. Aucune règle de pare-feu n'ouvre 5432 aux postes.
  - **Si échec** : remettre `listen_addresses` sur `localhost` dans `postgresql.conf`.
  - Détail : MEP § A 2.4.

- [ ] **A14 — Échange de clé hybride en HTTPS** (désactivé par défaut). **Qui** : technicien. **Où** : chaque serveur, PowerShell administrateur.
  ```powershell
  Enable-TlsEccCurve -Name "X25519_MLKEM768" -Position 0
  ```
  - **Ou** par stratégie de groupe (pas les deux : la stratégie l'emporte). Sans effet si l'autre côté ne le gère pas (connexion classique,
    sans erreur). Central sous Linux : OpenSSL 3.5 ou plus **[À VÉRIFIER avec l'hébergeur]**.
  - Détail : MEP, « Matériel et Windows du PC serveur ».

### Phase A3 — Base de données

- [ ] **A15 — Créer la base complète** (structure, fuseau, référentiel, séparation des rôles, droits `163b` et `166b`).
  **Prérequis** : faire **d'abord A18 (publication), A19 (copie de l'API, qui apporte `database\init` et les scripts numérotés) et A20
  (copie du dossier `scripts`)**, puis revenir ici ; A21 et la suite se font ensuite, dans l'ordre.
  **Qui** : technicien. **Où** : PC serveur, PowerShell ouvert dans le dossier parent du dossier `scripts` copié à A20. **Compte** :
  `postgres` (création du compte et de la base, `111b`, `163b`, `166b`), puis `pharma_app` (structure). Mots de passe saisis à l'écran,
  jamais dans la commande.
  ```powershell
  .\scripts\mep\creer_base_officine.ps1 -Base <base> -DossierDepot "C:\Program Files\PharmaOfficine\Api"
  ```
  - `<base>` : exemple `pharmacie`. Pour voir le plan sans rien créer : ajouter `-WhatIf`. Depuis le dépôt (PC des sources, essais),
    `-DossierDepot` est inutile.
  - Le script, dans l'ordre : vérifie que l'init est à jour (« 001 à NNN » = dernier script, aujourd'hui `174`) ; vérifie que PostgreSQL
    répond et que la base **n'existe pas** ; crée `pharma_app` s'il manque (`LOGIN CREATEDB`) ; crée la base ; passe `schema.sql`,
    `fuseau_horaire.sql` (copie du `083`), `seed_referentiel.sql` ; puis, avec `postgres`, `111b` (journaux inaltérables), `163b` (lire
    l'identifiant de la base), `166b` (droit de promouvoir) ; enfin ses vérifications (`objets_encore_a_pharma_app = 0`, comptages de
    référence, lecture et écriture par `pharma_app`, droits sur les journaux). `101b` n'est **jamais** lancé.
  - **Attendu** : « Terminé », code de sortie 0 (`$LASTEXITCODE`), message « Référentiel chargé. Connexion : titulaire / Provisoire-2026 (à
    changer à la première connexion). »
  - **Interdit** : `database\data\9xx_*.sql`. Option `-SansSeparationRoles` : **essais seulement** (journaux non protégés).
  - Ensuite, les **futurs** scripts passent par `mettre_a_jour_base_officine.ps1` (chapitre B), avec `postgres` et `SET ROLE pharma_proprietaire`.
    Ne plus jamais les lancer avec `pharma_app`.
  - **Si échec** : le script s'arrête à la première erreur. « La base existe déjà » : normal, voir « Recommencer depuis zéro » (annexe A7).
    « L'en-tête de schema.sql ne cite pas 001 à NNN » : init en retard, rien n'a été fait, voir le développeur. `à exécuter par un
    superutilisateur` : le compte d'administration n'est pas `postgres`.
  - Le script lit `<DossierDepot>\database\init` et `<DossierDepot>\database\migration`, ou à défaut les scripts numérotés à plat dans
    `<DossierDepot>\database` (cas du dossier publié de l'API) ; sans `-DossierDepot`, il prend le dossier parent de `scripts`
    (`creer_base_officine.ps1`, paramètre `DossierDepot` et lignes 99 à 102). D'où `-DossierDepot` vers le dossier de l'API sur le PC
    serveur (écart n° 15 : corrigé).
  - Détail : MEP § A 3.1 ; [scripts-base-de-donnees.md](scripts-base-de-donnees.md) ; en-tête de `scripts\mep\creer_base_officine.ps1`.

- [ ] **A16 — Contrôler la base** (contrôle indépendant, facultatif). **Qui** : technicien. **Où** : PC serveur, compte `pharma_app`.
  ```powershell
  & $psql -h localhost -U pharma_app -d <base> -tAc "show timezone"
  & $psql -h localhost -U pharma_app -d <base> -tAc "SELECT count(*) FROM utilisateur"
  & $psql -h localhost -U pharma_app -d <base> -tAc "SELECT count(*) FROM vente"
  ```
  - **Attendu** : `Africa/Algiers` ; au moins `1` utilisateur ; `0` vente.
  - **Si échec** : `relation does not exist` : structure non créée, relancer A15 sur une base neuve.
  - Détail : MEP § A 3.2.

- [ ] **A17 — Contrôler les droits `163b` et `166b`** (utiles seulement avec un 2e serveur, sans effet sinon). **Qui** : technicien.
  **Où** : PC serveur, compte `postgres`, **sur la base de la pharmacie** (`-d <base>`), **jamais sur la base système `postgres`** (le droit
  s'enregistre base par base).
  ```powershell
  & 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -h localhost -U postgres -d <base de la pharmacie> -Atc "SELECT has_function_privilege('pharma_app', 'pg_promote(boolean, integer)', 'EXECUTE')"
  ```
  - **Attendu** : `t`.
  - **Si `f`** : passer `166b` sur la base de la pharmacie (commande de promotion-serveurs.md § 3, recopiée telle quelle ; le chemin y est
    celui du dépôt ; sur le PC serveur, le fichier est dans `C:\Program Files\PharmaOfficine\Api\database\init`, comme le précise
    désormais promotion-serveurs.md § 3, écart n° 11) :
    ```powershell
    & 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -h localhost -p 5432 -U postgres -d <base de la pharmacie> -v ON_ERROR_STOP=1 `
        -f C:\MesAtlassian\pharmaofficine\database\init\166b_droit_promotion_base.sql
    ```
    **Attendu** : la dernière ligne `pharma_app_peut_promouvoir` vaut `t`.
  - `163b`, mode d'emploi de son en-tête : `psql -U postgres -d <base> -v ON_ERROR_STOP=1 -f 163b_droit_identifiant_base.sql`. Aucune
    requête de contrôle dans les sources ; contrôle indirect : pas de `lignee_illisible` au journal du 2e serveur.
  - À refaire après une restauration sur une base neuve (`163b` et `166b`).
  - Détail : promotion-serveurs.md § 3 ; en-têtes de `163b` et `166b` ; MEP parcours C (intro).

### Phase A4 — PC serveur : API, configuration, HTTPS, service

- [ ] **A18 — Publier l'API.** **Qui** : technicien. **Où** : PC des sources, administrateur Windows, depuis le dossier du dépôt.
  ```powershell
  dotnet publish src\Pharma.Api -c Release -r win-x64 --self-contained true -o "<dossier de livraison>\Api"
  ```
  - `--self-contained true` : aucun runtime .NET à installer sur le PC serveur. Contrepartie : les correctifs de sécurité .NET n'arrivent
    qu'avec une nouvelle publication (chapitre B).
  - Le dossier publié contient `Pharma.Api.exe`, `database\` (scripts numérotés, à plat) et `database\init\` (`schema.sql`, `seed_referentiel.sql`, `fuseau_horaire.sql`,
    `111b`, `163b`, `166b` ; jamais `clean.sql` ni `101b`).
  - **Attendu** : `Pharma.Api.exe` et `database` présents dans `<dossier de livraison>\Api`.
  - **Si échec** : SDK absent (A06) ou version différente de .NET 10.
  - Détail : MEP § A 4.1.

- [ ] **A19 — Copier l'API sur le PC serveur.** **Qui** : technicien. **Où** : PC serveur (depuis la clé USB), administrateur Windows.
  ```powershell
  New-Item -ItemType Directory -Force "C:\Program Files\PharmaOfficine" | Out-Null
  Copy-Item -Recurse -Force "E:\<dossier de livraison>\Api" "C:\Program Files\PharmaOfficine\"
  Test-Path "C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe"
  ```
  - **Attendu** : `True`.
  - Détail : MEP § A 4.2.

- [ ] **A20 — Copier le dossier `scripts`** du dépôt (ou de la livraison) sur le PC serveur. **Qui** : technicien. **Où** : PC serveur,
  administrateur Windows. Il contient `certificat_https.ps1`, `sauvegarde_pharmacie.ps1`, `restaurer_pharmacie.ps1`, `basculer-pc-secours.ps1`
  et `second-serveur\`. Ces scripts **ne sont pas** dans le dossier publié de l'API.
  **[À CONFIRMER : dossier de destination officiel sur le PC serveur, par exemple `C:\Program Files\PharmaOfficine\scripts`.]**
  - Détail : MEP § A 4.3.

- [ ] **A21 — Variables d'environnement de l'API.** **Qui** : technicien. **Où** : PC serveur, **nouvelle** fenêtre PowerShell en
  administrateur. Remplacer les `<...>` :
  ```powershell
  [Environment]::SetEnvironmentVariable("ConnectionStrings__Pharmacie", "Host=localhost;Port=5432;Database=<base>;Username=pharma_app;Password=<mot de passe pharma_app>", "Machine")
  [Environment]::SetEnvironmentVariable("Chifa__DossierExport", "<dossier d'export de CHIFA-Officine>", "Machine")
  ```
  Générer la clé de signature (64 caractères minimum, 72 ici), la noter au coffre, puis la coller dans la commande suivante :
  ```powershell
  $alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789"
  $octets = New-Object byte[] 72
  [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($octets)
  -join ($octets | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
  ```
  ```powershell
  [Environment]::SetEnvironmentVariable("Jwt__Cle", "<clé générée>", "Machine")
  ```
  - **Ne jamais** mettre la clé ou le mot de passe dans `appsettings.json`, dans le dépôt, ni dans un courriel.
  - Vérifier sans afficher les secrets :
    ```powershell
    [Environment]::GetEnvironmentVariable("Jwt__Cle", "Machine").Length    # doit être 64 ou plus
    [Environment]::GetEnvironmentVariable("ConnectionStrings__Pharmacie", "Machine") -ne $null
    ```
  - **Attendu** : un nombre ≥ 64, et `True`.
  - **Si échec** : l'API ne voit les variables qu'après son (re)démarrage (A24).
  - Détail : MEP § A 4.4.

- [ ] **A22 — Réglages facultatifs** (seulement si besoin). **Qui** : technicien. **Où** : PC serveur, administrateur Windows. Mêmes commandes
  que A21 (`SetEnvironmentVariable`, scope `Machine`) : `Ged__Dossier`, `Ged__RetentionSauvegardesJours`, `Ordonnances__Dossier`,
  `Journaux__Dossier`, `Journaux__TailleMaxMo`, `Journaux__RetentionMois`, `Journaux__Niveau`, `LimiteConnexions__ParMinute`,
  `Sauvegardes__PgDump` **[À CONFIRMER : comportement de l'API quand la valeur est vide.]**.
  - **Ne pas** régler `AutoriserHttpReseau`. Ne pas modifier `Urls` dans `appsettings.json` : `appsettings.Https.json` (A23) prend le relais.
  - Détail : MEP § A 4.5.

- [ ] **A23 — HTTPS : une seule autorité pour toute la pharmacie.** **Qui** : technicien. **Où** : PC serveur, administrateur Windows. Deux
  clés USB : **postes** (`E:\`, certificat public) et **coffre** (`F:\`, clé privée de l'autorité).
  - Le script crée l'autorité (une fois, 10 ans), la sauvegarde **une fois** en PFX protégé par une phrase de passe, crée le certificat du
    serveur (825 jours), écrit `appsettings.Https.json`, ouvre 5443 en réseau **privé**, exporte le certificat **public** et son empreinte
    sur `E:\`. La clé de l'autorité est **non exportable** dans Windows : le PFX du coffre est le **seul exemplaire**, impossible à refaire.
  - [ ] **A23.0 — Se placer dans le dossier qui contient `scripts`** (celui de A20) :
    ```powershell
    Set-Location "<dossier qui contient scripts>"
    Test-Path .\scripts\certificat_https.ps1
    ```
    **Attendu** : `True`.
  - [ ] **A23.1 — Essai à blanc** (rien n'est écrit, aucune phrase demandée) :
    ```powershell
    .\scripts\certificat_https.ps1 -DossierApi "C:\Program Files\PharmaOfficine\Api" -DossierExport E:\ -FichierSauvegardeAutorite F:\autorite-pharmacie.pfx -PareFeu -WhatIf
    ```
    **Attendu** : chaque action annoncée `[SIMULATION]`, fin « rien n'a été modifié ».
  - [ ] **A23.2 — Lancement réel** (clé du coffre `F:\` branchée) :
    ```powershell
    .\scripts\certificat_https.ps1 -DossierApi "C:\Program Files\PharmaOfficine\Api" -DossierExport E:\ -FichierSauvegardeAutorite F:\autorite-pharmacie.pfx -PareFeu
    ```
    - Saisir la **phrase de passe** (12 caractères au moins), deux fois. **La noter sur papier, au coffre, à part de la clé USB.**
    - Le script affiche les noms et adresses du certificat (par défaut : `localhost`, `127.0.0.1`, le nom du PC, ses IPv4 privées) et
      l'empreinte SHA-256 de l'autorité. **Noter l'empreinte** (aussi dans `E:\empreinte-autorite-pharmacie.txt`).
  - [ ] **A23.3 — Noms et adresses supplémentaires** (seulement ce qui sert, rien n'est deviné), par exemple l'adresse VPN :
    `-AdressesSupplementaires <adresse VPN>`, `-NomsSupplementaires <nom>`. Relancer la commande avec ces paramètres (l'autorité est
    conservée). Ne **pas** mettre ici les noms du 2e serveur ni du PC de secours (C-11, D-06).
  - [ ] **A23.4 — Ranger le PFX au coffre** : retirer `F:\`, la ranger hors ligne. Faire une **seconde copie** du PFX sur une autre clé
    (même phrase), conservée ailleurs. Jamais sur un poste, un téléphone, `E:\`, ni par le réseau ou courriel.
  - [ ] **A23.5 — Vérifier les fichiers créés** :
    ```powershell
    Get-Content "C:\Program Files\PharmaOfficine\Api\appsettings.Https.json"
    Get-ChildItem E:\, F:\ | Select-Object FullName
    Get-NetFirewallRule -DisplayName "PharmaOfficine API (https 5443)" | Select-Object DisplayName, Enabled
    Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -like "*PharmaOfficine*" } | Select-Object Subject, NotAfter, HasPrivateKey
    ```
    **Attendu** : `"Url": "http://localhost:5080"` et `"Url": "https://0.0.0.0:5443"` ; `E:\` contient `autorite-pharmacie.crt` et
    `empreinte-autorite-pharmacie.txt` (**pas** de `.pfx`) ; `F:\` contient `autorite-pharmacie.pfx` ; règle `Enabled : True` ; deux
    certificats `CN=PharmaOfficine Autorite pharmacie` (10 ans) et `CN=PharmaOfficine Mobile` (825 jours), `HasPrivateKey : True`.
  - [ ] **A23.6 — Test de relecture du PFX** (conseillé) : `certutil -dump F:\autorite-pharmacie.pfx` ; une phrase juste affiche le
    certificat, une fausse est refusée.
  - **Décision** : renouveler le certificat du serveur avant 825 jours (F6).
  - **Si échec** : « Dossier de l'API introuvable » : A19 non faite. « Adresse IP supplémentaire invalide » : syntaxe. « Première exécution :
    … Préciser -FichierSauvegardeAutorite » : ajouter le paramètre. « Dossier introuvable pour -FichierSauvegardeAutorite » : clé du coffre non
    branchée. « Le fichier existe déjà » : le PFX n'est jamais écrasé (voir F2 bis). « Les deux saisies sont différentes » ou « 12 caractères
    au moins » : recommencer. « ne doit pas être rangé avec le certificat public » : autre dossier que celui des postes. « Ancienne autorité …
    trouvée » : procédure F7.
  - Détail : MEP § A 4.6.

- [ ] **A24 — Installer l'API comme service Windows** (après A23). **Qui** : technicien. **Où** : PC serveur, administrateur Windows.
  ```powershell
  New-Service -Name "PharmaOfficine API" -BinaryPathName '"C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe"' -DisplayName "PharmaOfficine API" -StartupType Automatic
  Start-Service "PharmaOfficine API"
  Get-Service "PharmaOfficine API"
  ```
  - **Attendu** : `Status : Running`. Les guillemets autour du chemin sont **obligatoires**.
  - **Si échec** : le service se ferme aussitôt : lire le journal (annexe A1). « Démarrage refusé : l'API écoute en HTTP (non chiffré) » =
    A23 non faite. Message sur `Jwt:Cle` = A21.
  - Détail : MEP § A 4.7.

- [ ] **A24 bis — Créer la clé de ce serveur pour l'offre** et relever son **empreinte** (point 5 du 2026-11-27). **Qui** : technicien.
  **Où** : PC serveur, invite de commandes **en administrateur**.
  ```powershell
  & "C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe" --creer-cle-serveur
  & "C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe" --empreinte-serveur
  ```
  - **Attendu** : « Clé de ce serveur créée. » (ou « existe déjà » : jamais remplacée), fournisseur « Microsoft Platform Crypto Provider »
    (TPM), empreinte en 8 groupes de 4 caractères : la **noter** pour l'éditeur (A45). La base n'est pas lue (marche aussi en veille).
  - « ATTENTION : clé LOGICIELLE » : pas de TPM utilisable, rien n'est bloqué ; vérifier le TPM dans le BIOS/UEFI **avant** de transmettre
    l'empreinte. « Droits d'administrateur nécessaires » : relancer l'invite en administrateur.
  - Détail : MEP § A 4.7 bis.

- [ ] **A25 — Vérifier l'API.** **Qui** : technicien. **Où** : PC serveur, administrateur Windows.
  ```powershell
  (Invoke-WebRequest http://localhost:5080/api/healthcheck -UseBasicParsing).StatusCode
  ```
  - **Attendu** : `200`. **Si échec** : `Get-Service`, puis le journal.
  - Détail : MEP § A 4.8.

- [ ] **A26 — Sauvegarder la clé de scellement** créée au premier démarrage. **Qui** : technicien. **Où** : PC serveur, administrateur Windows.
  - `Test-Path "C:\ProgramData\PharmaOfficine\cle-integrite.dat"` → `True`.
  - Copier `cle-integrite.dat` sur le **disque externe**, dans `E:\Securite\`. Lisible seulement sur **ce** PC ; sans lui, les sceaux déjà
    posés ne peuvent plus être vérifiés.
  - **Attendu** : copie faite, taille identique.
  - Détail : MEP § A 4.9.

> **PC de secours prévu (A08) ?** Faire maintenant le [chapitre D](#chapitre-d-pc-de-secours-facultatif--selon-le-cas).

### Phase A5 — Postes de caisse (sur chaque poste, une caisse après l'autre)

- [ ] **A27 — Navigateur intégré WebView2.** **Qui** : technicien. **Où** : chaque poste, administrateur Windows. Paramètres, Applications,
  Applications installées : « Microsoft Edge WebView2 Runtime ». S'il manque, l'installer (installeur gratuit de Microsoft).
  **Attendu** : présent. Détail : MEP § A 5.1.

- [ ] **A28 — Pilotes des imprimantes** (tickets, étiquettes) et douchette. **Qui** : technicien. **Où** : chaque poste, administrateur Windows.
  Pilotes du fabricant ; imprimer une page de test depuis Windows. Détail : MEP § A 5.2.

- [ ] **A29 — Copier et vérifier l'autorité.** **Qui** : technicien. **Où** : chaque poste, administrateur Windows.
  - Copier `autorite-pharmacie.crt` de la clé USB vers `C:\Temp\`. **Jamais** par le réseau HTTP.
    ```powershell
    $calculee = (Get-FileHash "C:\Temp\autorite-pharmacie.crt" -Algorithm SHA256).Hash
    $attendue = ((Get-Content "E:\empreinte-autorite-pharmacie.txt")[2]) -replace '[^0-9A-Fa-f]', ''
    "Calculée : $calculee"; "Attendue : $attendue"; $calculee -eq $attendue
    ```
  - **Attendu** : `True`. **Si échec** : `False` = ne pas installer, refaire la copie depuis la clé USB.
  - Détail : MEP § A 5.3.

- [ ] **A30 — Installer l'autorité** dans les racines de confiance de l'ordinateur. **Qui** : technicien. **Où** : chaque poste, administrateur Windows.
  ```powershell
  certutil -addstore -f Root "C:\Temp\autorite-pharmacie.crt"
  Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.Subject -eq "CN=PharmaOfficine Autorite pharmacie" } | Select-Object Subject, NotAfter
  ```
  - Alternative graphique : double-clic, « Installer le certificat… », « Ordinateur local », **Autorités de certification racines de confiance**.
  - **Attendu** : une ligne. Supprimer ensuite le fichier du dossier temporaire.
  - Détail : MEP § A 5.4.

- [ ] **A31 — Installer le programme du poste.** **Qui** : technicien. **Où** : PC des sources, puis chaque poste (administrateur Windows).
  ```powershell
  dotnet publish src\Pharma.Desktop -c Release -r win-x64 --self-contained true -o "<dossier de livraison>\Poste"
  ```
  - Copier le dossier sur chaque poste, par exemple `C:\Program Files\PharmaOfficine\Poste` ; raccourci sur le bureau vers `Pharma.Desktop.exe`.
  - Aucun runtime .NET à installer. Pas encore d'installateur ni de mise à jour automatique des postes.
  - Détail : MEP § A 5.5.

- [ ] **A32 — Variables du poste.** **Qui** : technicien. **Où** : chaque poste, administrateur Windows.
  ```powershell
  [Environment]::SetEnvironmentVariable("PHARMA_API", "https://<serveur>:5443/", "Machine")
  [Environment]::SetEnvironmentVariable("PHARMA_POSTE", "Caisse 1", "Machine")
  ```
  - `<serveur>` : nom ou IP **présent dans le certificat** (A23). `PHARMA_POSTE` : **un nom différent par poste** (sinon même caisse).
  - Sur le PC serveur lui-même, `http://localhost:5080/` reste permis. **Attendu** : fermer et rouvrir le poste.
  - Détail : MEP § A 5.6.

- [ ] **A33 — Ouvrir Tracodex Officine.** **Qui** : employé. **Où** : chaque poste.
  - **Attendu** : écran de connexion sans avertissement de certificat.
  - **Si échec** : avertissement de certificat = autorité non installée (A30) ou nom absent du certificat (A23.3). « Serveur injoignable » =
    API arrêtée ou pare-feu 5443.
  - Détail : MEP § A 5.7 ; « Postes autorisés ».

### Phase A6 — Téléphones (scanner mobile, facultatif)

- [ ] **A34 — Transférer l'autorité** sur le téléphone **hors réseau HTTP** : câble USB ou clé USB OTG. Jamais par lien HTTP ni courriel.
  Détail : MEP § A 6.1.
- [ ] **A35 — Vérifier l'empreinte avant d'installer** : identique, caractère pour caractère, à `empreinte-autorite-pharmacie.txt`. Sinon, ne
  pas installer. **Android** : Paramètres, Sécurité, Chiffrement et identifiants, Installer un certificat, **Certificat CA**. **iPhone** :
  installer le profil, puis Réglages, Général, Informations, **Réglages des certificats** (activer la confiance). Pas de QR code.
  Détail : MEP § A 6.2.
- [ ] **A36 — Ouvrir le scanner** : `https://<serveur>:5443/mobile/`. **Attendu** : sans avertissement. Mode d'emploi :
  [scanner-mobile.md](scanner-mobile.md). Le scanner mobile relève du pack B de l'offre (A45). Détail : MEP § A 6.3 ; offre-et-options.md (lot O6).

### Phase A7 — Fermer le réseau HTTP et vérifier le HTTPS (après les postes et téléphones)

- [ ] **A37 — Aucune règle de pare-feu sur 5080.** **Qui** : technicien. **Où** : PC serveur, administrateur Windows.
  ```powershell
  Get-NetFirewallRule -Direction Inbound -Enabled True | Where-Object { ($_ | Get-NetFirewallPortFilter).LocalPort -contains '5080' } | Select-Object DisplayName
  ```
  - **Attendu** : aucune ligne. Sinon : `Disable-NetFirewallRule -DisplayName '<nom de la règle>'`.
  - Détail : MEP § A 7.1.

- [ ] **A38 — Contrôler les écoutes.** **Qui** : technicien. **Où** : PC serveur, administrateur Windows.
  ```powershell
  netstat -ano | findstr :5080
  netstat -ano | findstr :5443
  ```
  - **Attendu** : 5080 seulement sur `127.0.0.1` et `[::1]` ; 5443 sur `0.0.0.0`.
  - Détail : MEP § A 7.2.

- [ ] **A39 — Contrôler depuis un poste.** **Qui** : technicien. **Où** : un poste, compte employé.
  ```powershell
  (Invoke-WebRequest https://<serveur>:5443/api/healthcheck -UseBasicParsing).StatusCode
  (Invoke-WebRequest https://<serveur>:5443/api/healthcheck -UseBasicParsing).Headers["Strict-Transport-Security"]
  Invoke-WebRequest http://<serveur>:5080/ -UseBasicParsing
  Invoke-WebRequest https://<serveur>:5443/mobile/autorite-pharmacie.crt -UseBasicParsing
  ```
  - **Attendu** : `200` sans erreur de certificat ; en-tête `Strict-Transport-Security` présent ; la commande `http://…:5080` **échoue** ; la
    commande du `.crt` **échoue** en 404.
  - Détail : MEP § A 7.3.

- [ ] **A40 — Essai réel sur un vrai poste**, avant le jour J : connexion, vente, impression, scanner mobile en HTTPS. **Qui** : technicien,
  employé. **Où** : un poste et un téléphone. Détail : MEP § A 7.4.

### Phase A8 — Premier démarrage et sécurité (J-7)

- [ ] **A41 — Se connecter avec le compte de départ** `titulaire` / `Provisoire-2026`. **Qui** : titulaire. **Où** : un poste. Détail : MEP § A 8.1.
- [ ] **A42 — Choisir le mot de passe personnel** (8 caractères minimum). Tant que ce n'est pas fait, le serveur refuse toute autre action.
  Changer un mot de passe, désactiver un compte ou effacer un code comptoir ferme aussitôt les connexions de ce compte. Détail : MEP § A 8.2.

### Phase A9 — Paramétrage (titulaire, dans l'application, dans l'ordre)

- [ ] **A43 — Identité de la pharmacie.** **Qui** : titulaire. Administration › Langue et pharmacie (onglet « Pharmacie et langue ») : nom, titulaire, adresse, téléphone,
  **RC, NIF, NIS, AI**, n° d'officine Chifa (imprimés sur tickets, factures, rapports). **Le NIF doit être saisi avant A45** (le code
  d'activation est lié au NIF). Détail : MEP § A 9.1 ; procedure-cle-editeur.md § 3.

- [ ] **A44 — Organismes payeurs.** Même page : organismes conventionnés (militaires…) avec adresse et NIF. Détail : MEP § A 9.2.

- [ ] **A45 — Offre et code d'activation.** **Qui** : titulaire (relevé et saisie), **éditeur** (signature, sur **son** PC).
  - [ ] **A45.1** Titulaire, dans **Administration › Mon offre** : relever le **code de l'officine**, le **NIF** (NIF saisi d'abord, A43,
    Administration › Langue et pharmacie) et l'**empreinte de ce serveur** (boutons « Copier » ; clé créée à A24 bis), plus les empreintes
    du 2e serveur (C-14 bis) et du PC de secours (D-08 bis) s'il y en a. Les transmettre à l'éditeur.
  - [ ] **A45.2** Éditeur, **sur le PC de l'éditeur** (jamais sur le serveur de l'officine), dans sa propre fenêtre PowerShell, signer un
    **code de transition** (durée fixée ce jour-là) ou le code de l'offre vendue. Mot de passe de la clé demandé :
    ```powershell
    cd C:\MesAtlassian\pharmaofficine
    dotnet run --project src\Pharma.Editeur -- signer --cle C:\MesAtlassian\cle-editeur\cle-editeur.pem `
        --officine <code de l'officine> --nif <NIF> --postes <n> --serveur <empreinte> [--serveur <empreinte> …] `
        [--packs A,D | --packs tous] [--scanners <n>] [--fin AAAA-MM-JJ] [--transition]
    ```
    Forme courte d'un code de transition (offre-et-options.md, « Mise en service ») :
    `signer --cle <cle-editeur.pem> --officine <code> --nif <NIF> --postes <n> --serveur <empreinte> --transition --fin AAAA-MM-JJ`.
    - `--serveur <empreinte>` obligatoire, 1 à 3 fois (un par serveur de l'officine) ; `--postes <n>` obligatoire (1 à 200) ; `--transition` = les 8 packs, `--fin` obligatoire ; `--fin` absent (hors transition) = définitive.
    - **Attendu** : une ligne de résumé (pharmacie, NIF, postes, packs, fin), les serveurs autorisés, puis le code `PO1.…` (non secret : il
      ne vaut que pour cette pharmacie et sur les serveurs déclarés).
  - [ ] **A45.3** Éditeur : relire le code avant de l'envoyer :
    ```powershell
    dotnet run --project src\Pharma.Editeur -- lire --code <PO1.…> --cle-publique (Get-Content C:\MesAtlassian\cle-editeur\cle-editeur-publique.txt)
    ```
  - [ ] **A45.4** Avec le **compte du titulaire** (par le titulaire, ou par l'éditeur à ses côtés : la saisie est réservée au titulaire,
    `.ExigerTitulaire()` sur `/offre/activation`) : **Administration › Mon offre**, coller le code dans « Code d'activation », **Activer**. (Pharmacie synchronisée :
    possible aussi par le central, `Pharma.Cloud.Api.exe --ordre-activation --officine <code de l'officine> --code <PO1.…> --auteur <nom>`.)
  - **Attendu** : « Code accepté : Offre activée (ou Période de transition), N postes » ; les écrans des packs se rouvrent (au plus 2 minutes
    sur les autres postes).
  - **Sans code** : offre de base, **2 postes**, écrans des packs A à H fermés.
  - **Refus « Ce serveur n'est pas déclaré dans le code de l'offre … »** : l'empreinte de ce serveur manque au code ; re-signer avec
    l'empreinte affichée. **Remplacement d'un serveur** (PC neuf, Windows réinstallé, restauration sur un autre PC) : nouveau code ; en
    attendant, 15 jours de grâce. **Horloge** : la reculer ne prolonge pas l'offre (message dans Mon offre, journal `offre_horloge_reculee`).
  - **Offre à date de fin** : l'écran Mon offre prévient dès 90 jours ; alerte au titulaire par SMS ou e-mail (mêmes destinataires que les
    alertes techniques) 90, 60, 30, 15, 7 et 1 jour(s) avant la fin, puis au début du délai de grâce de 15 jours, une seule fois chacune pour une date de fin
    donnée (journal `offre_rappel`), seule l'étape la plus proche étant envoyée (`RappelsOffre`, `src\Pharma.Application\Offre\Offre.cs`).
  - **Si échec** (refus, rien ne change) : autre pharmacie (code de l'officine), autre NIF, signature fausse, code échu, code plus ancien que
    celui en place, code déjà enregistré.
  - Détail : MEP § A 13.0 ; [procedure-cle-editeur.md](procedure-cle-editeur.md) § 3 et § 4 ; offre-et-options.md, « Mise en service ».

- [ ] **A46 — Autoriser chaque poste.** **Qui** : titulaire, puis technicien sur chaque poste. Le **maximum de postes vient de l'offre** (A45).
  - **Preuve toujours exigée** (décision du titulaire du 2026-11-27, script 174) : un PC non autorisé ne se connecte pas et ne compte pas dans
    le maximum ; plus de mode « transition » ni « inactif », plus de choix du mode à l'écran. Sur un PC non autorisé, l'écran de connexion ne
    montre que « Code du poste » et « Autoriser » (le super admin garde son lien « Connexion super admin »).
  - **Base neuve** : `seed_referentiel.sql` pose la ligne `parametre_postes` avec les défauts du schéma (`INSERT INTO parametre_postes
    DEFAULT VALUES`) : mode `exige` (script 174, plus lu par l'API), code valable 15 minutes ; liste `poste_autorise`
    **vide** : aucun PC ne se connecte avant le premier poste, autorisé **obligatoirement** par `Pharma.Api.exe --autoriser-poste` (A46.1). Maximum : celui de l'offre ; sans code d'activation, offre de base, **2 postes** (`OffreCourante.PostesBase = 2`) ; la colonne
    `parametre_postes.maximum_postes` (20 par défaut) n'est plus lue depuis le script 172. Saisir le code (A45) avant d'autoriser plus de
    2 postes.
  - **Mise à jour d'une officine en service** (version avec le script 174) : dès le redémarrage de l'API, aucun PC non autorisé ne se connecte.
    Générer les codes de **tous** les postes **le jour même** (A46.1 puis A46.2 sur chaque PC), avec le titulaire, de préférence avant
    l'ouverture.
  - [ ] **A46.1** Premier poste : sur le PC lui-même, « Connexion du titulaire (Administration seulement) » sous la saisie du code (décision du
    2026-11-27 : le titulaire seul, Administration seulement, ni caisse ni vente), Postes autorisés › « Autoriser un poste » affiche le code ;
    se déconnecter, taper le code (A46.2), se reconnecter. `Pharma.Api.exe --autoriser-poste "CAISSE-1"` sur le PC serveur reste possible.
    Le code de l'offre (A45) se saisit aussi ainsi, depuis n'importe quel PC.
  - [ ] **A46.2** Sur le poste, avec le compte Windows habituel : écran de connexion, « Autoriser ce poste », taper le code. Sur un compte
    standard, Windows demande **une fois** l'accord d'un administrateur du PC. Refus : « Autorisation annulée », recommencer. **[À VÉRIFIER]**
    au premier poste réel : la connexion avec le compte standard fonctionne ensuite (preuve acceptée).
  - [ ] **A46.3** Contrôle : chaque PC se connecte après la saisie de son code ; Administration › Postes autorisés les liste « Autorisé »
    (plus de réglage « Preuve des postes » depuis le script 174).
  - Nom du poste : celui saisi à l'autorisation (« Nom du poste »), changeable par « Renommer ». PC remplacé : « Remplacer le PC » ; PC volé
    ou perdu : « Révoquer ». L'heure de chaque PC à moins de 2 minutes de celle du serveur.
  - La source décrit cette section pour une officine « après la mise à jour » ; procedure-deux-serveurs.md § 6.1 la place dans la mise en
    place. Sans poste autorisé, pas de promotion automatique (chapitre C).
  - Détail : MEP, « Postes autorisés » ; procedure-deux-serveurs.md § 6.1 et 6.1 bis.

- [ ] **A47 — Comptes des employés.** Administration, Utilisateurs et droits : **un compte par employé**, identifiant et mot de passe
  personnels, bon rôle (assistant, vendeur, stagiaire). Détail : MEP § A 9.3.
- [ ] **A48 — Droits de chaque rôle.** Réservés par défaut au titulaire : trésorerie, factures, inventaire complet, organismes. Détail : MEP § A 9.4.
- [ ] **A49 — Code comptoir.** Chaque employé définit son **code comptoir** (bandeau, « Mon code comptoir ») et peut changer son mot de passe
  (bandeau, « Mot de passe »). Détail : MEP § A 9.5.
- [ ] **A50 — Caisse et contrôle.** Administration, Caisse et contrôle : fond de caisse, seuil d'écart, comptage aveugle, déconnexion
  automatique, versement au coffre (manuel ou automatique), seuil de validation des sorties par le tiroir (défaut 2 000 DA).
  Administration, Motifs et catégories : motifs du tiroir et leur effet. Administration, Contrôle interne : alerte au-delà de N sorties
  par employé et par jour (défaut 5). Détail : MEP § A 9.6.
- [ ] **A51 — Alertes, stocks, emplacements.** Administration, Alertes et seuils, puis Seuils de stock ; Administration, Emplacements (rayons,
  réfrigérateur, armoire des psychotropes, réserves). Détail : MEP § A 9.7.
- [ ] **A52 — Nomenclature nationale.** Administration, Nomenclature nationale : « Importer le fichier Excel… » (fichier de A05). Contrôler
  les compteurs (en vigueur, non renouvelés, retirés). **Avant** toute reprise de données. Détail : MEP § A 9.8.
- [ ] **A53 — Contenances des produits.** Si la nomenclature ne donne pas la contenance d'un produit : **Administration › Contenances des
  produits** (même écran que Stock › Contenances, droit `catalogue.creer`). « À compléter » (par défaut) : produits actifs sans contenance,
  les plus vendus d'abord ; saisie d'un entier de 1 à 10000 ; import / export par fichier Excel ou CSV possible.
  Détail : regles-metier.md (« Contenances des produits », 2026-11-26) ; fonctionnalites.md.
- [ ] **A54 — Taux de TVA.** **Administration › Taux de TVA**. Au départ : liste **0 %** (« Exonéré ») et **19 %** (« Taux normal ») ; taux par
  défaut **Médicament 0 %** et **Parapharmacie 19 %**. **Taux des autres catégories : à valider par le comptable**, puis à régler ici.
  Détail : a-faire.md (« TVA », 2026-11-26) ; base-de-donnees.md (script 171) ; en-tête de `seed_referentiel.sql`.
- [ ] **A55 — Trésorerie.** Date et solde de départ du coffre, puis les banques (une seule reçoit les paiements par carte). Détail : MEP § A 9.9.
  (La trésorerie relève du pack D de l'offre : écran fermé sans ce pack, offre-et-options.md lot O6.)
- [ ] **A56 — Notifications au titulaire.** Administration, Notifications au titulaire : SMTP et/ou modem GSM, destinataires, **message
  d'essai**. Le modem GSM sert aussi aux codes SMS du super admin (A59). Détail : MEP § A 9.10.
- [ ] **A57 — Imprimantes, sur chaque poste.** Administration, Imprimantes et étiquettes : imprimante de tickets, largeur du rouleau, ticket
  automatique, imprimante d'étiquettes, afficheur client. Essai d'impression et de l'afficheur. Détail : MEP § A 9.11.
- [ ] **A58 — Adresse de secours, sur chaque poste** (seulement si le chapitre D est fait) : Administration, Imprimantes et étiquettes,
  Serveur et PC de secours (voir D-13). Sinon, passer. Détail : MEP § A 9.12.

### Phase A10 — Compte super admin

- [ ] **A59 — Créer le compte.** **Qui** : technicien (titulaire présent pour les codes). **Où** : PC serveur, PowerShell en administrateur,
  **après** A21.
  ```powershell
  & "C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe" --super-admin
  ```
  - Saisir : identifiant, mot de passe (12 caractères au moins), puis un **téléphone mobile** (SMS), une **adresse e-mail**, ou les deux.
    **Au moins un canal vérifié est obligatoire** ; les codes reçus se saisissent dans la console.
  - **Si aucun canal ne fonctionne, le compte n'est pas créé** : régler le modem GSM ou le serveur e-mail (A56), puis relancer.
  - Le compte est chiffré pour ce PC (`super-admin.dat`) : il fonctionne même si la base est détruite, illisible sur un autre PC.
  - **Attendu** : compte créé, l'invite revient.
  - Détail : MEP § A 10.1.
- [ ] **A60 — Sauvegarder `super-admin.dat`** sur le disque externe (`E:\Securite\`), comme `cle-integrite.dat` (A26). Détail : MEP § A 10.2.
- [ ] **A61 — (Plus tard, si besoin) Changer mot de passe, téléphone ou e-mail** : relancer la même commande. Mot de passe actuel + code reçu
  sur un canal vérifié ; nouveau numéro ou nouvelle adresse : code reçu sur le nouveau canal. Laisser vide garde la valeur, « - » retire le
  canal ; **le dernier canal vérifié ne peut pas être retiré**. Détail : MEP § A 10.3.
- [ ] **A62 — (Plus tard, si besoin) Téléphone et e-mail perdus** : un administrateur du PC serveur supprime `super-admin.dat`, puis recrée le
  compte (A59). Ne jamais supprimer le compte sans cette procédure. Détail : MEP § A 10.4.

### Phase A11 — Sauvegardes (avant toute vraie donnée)

- [ ] **A63 — Un seul mécanisme : la sauvegarde de l'application** (option a, décision du 2026-11-26). Elle copie la base sur le disque
  externe (les **14 dernières** gardées par défaut, réglage « conservation », 1 à 365) et dans `GED\Sauvegardes` (gardée **7 jours** par
  défaut : réglage du titulaire, sinon `Ged__RetentionSauvegardesJours`), et les documents de la GED dans le sous-dossier `GED` du disque. Le script planifié
  `scripts\sauvegarde_pharmacie.ps1` (option b) n'est qu'un **dépannage** (A66), jamais en même temps. Détail : MEP § A 11.1.
- [ ] **A64 — Régler et lancer la première sauvegarde.** **Qui** : titulaire. **Où** : un poste ou le PC serveur. Administration, Sauvegardes :
  dossier (disque externe), rétention, puis **première sauvegarde**.
  - **Attendu** : un fichier `pharmacie_AAAAMMJJ_HHMMSS.dump`. Vérifier sa lisibilité :
    ```powershell
    & "C:\Program Files\PostgreSQL\18\bin\pg_restore.exe" --list "E:\Sauvegardes\Pharmacie\<fichier>.dump" > $null
    $LASTEXITCODE
    ```
    Résultat : `0`.
  - Détail : MEP § A 11.2.
- [ ] **A65 — Sans 2e serveur : deux disques externes en rotation, obligatoire.** **Qui** : titulaire. **Où** : PC serveur.
  - Même lettre de lecteur (Gestion des disques) et même dossier. Un disque branché, **l'autre toujours hors de l'officine** ; échange au
    moins une fois par semaine. Après chaque échange : « Sauvegarder maintenant ».
  - Délai d'alerte : Administration › Sauvegardes, « Alerte si aucune sauvegarde sur le disque depuis (jours) », 7 par défaut (1 à 90).
  - Détail : MEP § A 11.2 bis ; [ged-protection-a-decider.md](ged-protection-a-decider.md).
- [ ] **A66 — (Dépannage seulement) Script planifié** : seulement si l'API est arrêtée durablement ; à retirer du planificateur dès que l'API
  repart. **Qui** : technicien. **Où** : PC serveur, administrateur Windows.
  1. Fichier `C:\ProgramData\PharmaOfficine\pgpass.conf`, une ligne : `localhost:5432:<base>:pharma_app:<mot de passe>`.
  2. Limiter l'accès :
     ```powershell
     icacls C:\ProgramData\PharmaOfficine\pgpass.conf /inheritance:r /grant:r "<compte>:R"
     ```
  3. Adapter `$DbName`, `$DossierLocal`, `$DossierExterne`, `$RetentionJours` en tête du script. **[À CONFIRMER : valeurs de `D:\` et `E:\` à remplacer.]**
  4. Planificateur de tâches : chaque soir, programme `powershell.exe`, arguments `-ExecutionPolicy Bypass -File "C:\...\scripts\sauvegarde_pharmacie.ps1"`.
  - **Attendu** : `C:\ProgramData\PharmaOfficine\sauvegarde.log` créé après la première exécution.
  - Détail : MEP § A 11.3.
- [ ] **A67 — Tester une restauration dans une base d'essai** (avant la mise en service, puis chaque mois). **Qui** : technicien. **Où** : PC
  serveur, administrateur Windows.
  ```powershell
  .\scripts\restaurer_pharmacie.ps1 -Sauvegardes "E:\Sauvegardes\Pharmacie" -Base pharmacie_essai -SansGed
  ```
  - **Attendu** : nombre de ventes et date de la dernière. La base en service n'est pas touchée.
  - Supprimer ensuite la base d'essai : `& $psql -h localhost -U postgres -d postgres -c "DROP DATABASE pharmacie_essai"`.
  - Détail : MEP § A 11.4 ; procédure F1.

### Phase A12 — Options

- [ ] **A68 — Accès du titulaire hors de la pharmacie, par VPN** : voir [acces-distant.md](acces-distant.md). **Jamais** l'API sur internet.
  Avec une adresse VPN : relancer A23 avec `-AdressesSupplementaires <adresse VPN>`. Détail : MEP § A 13.1.
- [ ] **A69 — 2e serveur** (facultatif, fortement conseillé) : [chapitre C](#chapitre-c-2e-serveur-facultatif--selon-le-cas), **après** la
  présente installation, **y compris l'essai de bascule réel obligatoire (C-27) avant l'ouverture**. Détail : MEP § A 13.2.
- [ ] **A70 — Synchronisation** : chapitre E. Elle reste **désactivée** tant que le serveur central n'est pas prêt et que le titulaire ne l'a pas
  activée. Ne pas la toucher avant le jour J. Détail : MEP § A 13.3.

### Phase A13 — Veille de l'ouverture (J-1 au soir)

- [ ] **A71 — Reprise des données de l'ancien logiciel** (si besoin) : [chapitre R](#chapitre-r-reprise-des-données-de-lancien-logiciel-facultatif--selon-le-cas).
  Clôture de caisse de l'ancien logiciel, sauvegarde, export, reprise, contrôle des totaux. Totaux non concordants : **retour arrière** (§ 0.13).
  Détail : MEP § A 12 ; « Calendrier type ».
- [ ] **A72 — Vente test.** Ouvrir une caisse, vente test en espèces, l'annuler, clôturer : écart nul. Détail : MEP § A 14.1.
- [ ] **A73 — Impressions.** Un ticket, une étiquette, un rapport de clôture. Détail : MEP § A 14.2.
- [ ] **A74 — Droits.** Un vendeur ne voit pas ce qui ne lui est pas permis (trésorerie, administration). Détail : MEP § A 14.3.
- [ ] **A75 — Intégrité.** Administration, Contrôle interne, Intégrité des journaux : « Vérifier maintenant ». Sans anomalie. Détail : MEP § A 14.4.
- [ ] **A76 — Sauvegarde complète** juste avant l'ouverture (A64). Détail : MEP § A 14.5.
- [ ] **A77 — Checklist** : compléter la [checklist finale avant ouverture](#checklist-finale-avant-ouverture). Détail : MEP § A 14.6.

### Phase A14 — Jour J et suite

- [ ] **A78 — Ouverture** sur le nouveau logiciel, avec accompagnement sur place. Détail : MEP § A 15.1.
- [ ] **A79 — J+1 à J+30** : ancien logiciel en **lecture seule**. Étiqueter le stock progressivement (Achats, Étiquettes internes, ou à la fin
  de chaque inventaire tournant). Détail : MEP § A 15.2.

---

## Chapitre C. 2e serveur (facultatif / selon le cas)

Option facultative, **fortement conseillée**. À faire **après** la partie A. Le 2e serveur reçoit **en continu** la base du PC serveur
(réplication PostgreSQL en streaming, avec slot). Son API tourne **en permanence, en veille** (santé : `role = veille`).

- **Bascule automatique** : si plus de la moitié des postes connus ne joignent plus le PC serveur et que le 2e serveur ne le joint plus non plus
  (cas 1), il se promeut seul (environ 1 min 30 à 2 min) ; s'il joint encore le PC serveur mais aucun poste (cas 2), il le met d'abord hors jeu
  puis se promeut ; si quelques postes seulement le perdent (cas 3), il relaie leurs requêtes. Les postes basculent seuls ; une vente
  interrompue est renvoyée automatiquement (jamais deux fois).
- **Bascule manuelle (F3)** si : égalité, base du PC serveur vivante mais API muette, droit `166b` absent, promotion automatique désactivée.
- Toujours **une seule base en écriture**.
- Ordre de référence : [procedure-deux-serveurs.md](procedure-deux-serveurs.md) (en cas d'écart, mise-en-production.md fait foi).
  Fonctionnement de la promotion : [promotion-serveurs.md](promotion-serveurs.md).
- **Règle** : chaque script a un mode `-Essai` (ou `-WhatIf`) : **toujours le lancer d'abord**.

| Script (`scripts\second-serveur\`, avec `commun.ps1`) | Où | Rôle |
| --- | --- | --- |
| `preparer-principal.ps1` | PC serveur | Rôle de réplication, `postgresql.conf`, `pg_hba.conf`, slot `second_serveur`, `pg_monitor` pour `pharma_app` |
| `ouvrir-port-base.ps1` | serveur principal | Port 5432 ouvert à la seule IP de l'autre serveur |
| `initialiser-second-serveur.ps1` | 2e serveur | `pg_basebackup` avec slot et `-R`, démarrage, contrôle « lecture seule », import de la clé de scellement, contrôle de la clé des jetons |
| `verifier-second-serveur.ps1` | l'un ou l'autre | Rôle, réplication, version de l'API, service, empreinte de la clé, dossiers ; relevé inscrit sur le principal |
| `basculer-second-serveur.ps1` | 2e serveur | **Bascule manuelle** (F3) |
| `remettre-en-second.ps1` | ancien principal | **Retour** (F4) |
| `partager-ged.ps1` | serveur principal | Partage de la GED en lecture seule, au seul compte de copie ; pare-feu 445 limité |
| `copier-ged.ps1` | serveur qui reçoit | Copie de la GED sans rien supprimer, à la fréquence réglée dans l'application |
| `autoriser-lecture-copie-ged.ps1` | principal, puis 2e serveur | Rôle de lecture sans mot de passe pour lire la fréquence de copie |

### Phase C1 — Préparer

- [ ] **C-01 — Matériel, versions, pare-feu.** **Qui** : technicien. **Où** : les deux serveurs, administrateur Windows.
  - 2e serveur : PC de l'officine **allumé en permanence**, même réseau local (sans Internet), **IP fixe**.
  - **Même version majeure de PostgreSQL** (A10) et **même version de l'API** (A18), dans `C:\Program Files\PharmaOfficine\Api`.
    **[À CONFIRMER : livraison officielle de l'API sur le 2e serveur.]**
  - Pare-feu du 2e serveur : 5443 ouvert aux postes (par `certificat_https.ps1 -Importer -PareFeu`), 5080 jamais. 5432 jamais, sauf à l'autre
    serveur (`ouvrir-port-base.ps1`). 445 jamais au réseau, seulement à l'IP de l'autre serveur (`partager-ged.ps1`).
  - Prérequis : partie A complète sur le PC serveur ; comptes : administrateur Windows des deux PC, mot de passe `postgres`, titulaire ;
    **clé USB du coffre** ; heure à moins d'une minute entre les deux serveurs, à moins de 2 minutes entre poste et serveur.
  - Détail : MEP § C 1 ; procedure-deux-serveurs.md § 1.

- [ ] **C-02 — Copier `scripts\second-serveur`** (avec `commun.ps1`) sur les **deux** serveurs. **Où** : les deux, administrateur Windows.
  **[À CONFIRMER : dossier de destination.]** Détail : MEP § C 2.

### Phase C2 — PC serveur (principal)

- [ ] **C-03 — Droits `163b` et `166b` sur la base de la pharmacie.** **Qui** : technicien. **Où** : PC serveur, `postgres`, `-d <base>`.
  Passés par `creer_base_officine.ps1` (A15) ou `mettre_a_jour_base_officine.ps1` ; **propres à chaque base** : contrôler comme à A17
  (`has_function_privilege` → `t`). Sans `163b`, aucun serveur n'est jamais écarté ; sans `166b`, pas de promotion automatique.
  Détail : procedure-deux-serveurs.md § 2.1 ; promotion-serveurs.md § 3 ; MEP parcours C (intro).

- [ ] **C-04 — Préparer le principal, essai.** **Qui** : technicien. **Où** : PC serveur, administrateur Windows (+ mot de passe `postgres`).
  ```powershell
  cd "<dossier>\scripts\second-serveur"
  .\preparer-principal.ps1 -AdresseSecond <IP du 2e> -AdressePrincipal <IP du principal> -Essai
  ```
  **Attendu** : actions prévues affichées, rien n'est modifié. Détail : MEP § C 3.

- [ ] **C-05 — Préparer le principal, réel.** **Où** : PC serveur, administrateur Windows.
  ```powershell
  .\preparer-principal.ps1 -AdresseSecond <IP du 2e> -AdressePrincipal <IP du principal>
  ```
  - Noter le **mot de passe du rôle de réplication** (12 caractères au moins, au coffre). Si un redémarrage de PostgreSQL est demandé :
    ajouter `-Redemarrer` à un moment calme.
  - **Attendu** : fin sans erreur ; `pg_hba.conf` limité à `<IP>/32`, `listen_addresses` réglé.
  - Détail : MEP § C 4.

- [ ] **C-06 — Ouvrir 5432 au seul 2e serveur.** **Où** : PC serveur, administrateur Windows.
  ```powershell
  cd "<dossier>\scripts\second-serveur"
  .\ouvrir-port-base.ps1 -AdresseSecond <IP du 2e> -WhatIf
  .\ouvrir-port-base.ps1 -AdresseSecond <IP du 2e>
  Get-NetFirewallRule -DisplayName "PharmaOfficine PostgreSQL 5432 (2e serveur)" | Get-NetFirewallAddressFilter | Select-Object RemoteAddress
  ```
  - **Attendu** : « Pare-feu : port 5432 ouvert en entrée, profil Privé, au seul 2e serveur (<IP>). » puis « Contrôle : aucune autre règle
    active n'ouvre le port 5432. » ; `RemoteAddress` = l'IP du 2e serveur (jamais `Any`).
  - **Si échec** : « À lancer dans PowerShell en administrateur » ; « Adresse IPv4 invalide » (une seule IP) ; « Une autre règle ouvre le port
    5432 » : la désactiver (`Disable-NetFirewallRule -DisplayName '<nom affiché>'`) et relancer ; **ne pas continuer** tant que l'avertissement demeure.
  - Changement d'IP du 2e serveur : relancer ce script et `preparer-principal.ps1` avec la nouvelle IP, supprimer à la main les anciennes
    lignes `/32` de `pg_hba.conf`, puis `SELECT pg_reload_conf();`. Plus de 2e serveur : `.\ouvrir-port-base.ps1 -AdresseSecond <IP> -Retirer`.
  - Détail : MEP § C 4 bis.

- [ ] **C-07 — Déclarer le 2e serveur à l'API.** **Où** : PC serveur, administrateur Windows.
  ```powershell
  [Environment]::SetEnvironmentVariable("SecondServeur__AutreServeur", "<IP du 2e>", "Machine")
  Restart-Service "PharmaOfficine API"
  ```
  Équivaut à `"SecondServeur": { "AutreServeur": "<IP du 2e>" }` dans la configuration : ne pas écrire la clé à deux endroits. Détail : MEP § C 5.

- [ ] **C-08 — Relever l'empreinte de la clé des jetons** : `Pharma.Api.exe --empreinte-jeton` (16 caractères, jamais la clé). **Où** : PC
  serveur. À noter pour C-13. Détail : procedure-deux-serveurs.md § 2.5 ; MEP § C 7.

- [ ] **C-09 — Exporter la clé de scellement.** **Qui** : titulaire, dans l'application. Administration, 2e serveur, **Clé de scellement** :
  mot de passe de 12 caractères au moins (deux fois), dossier de la clé USB, « Exporter la clé ». Fichier `cle-scellement-AAAAMMJJ-<empreinte>.pocle`.
  Clé USB **au coffre**, mot de passe **à part**. Détail : MEP § C 6.

### Phase C3 — 2e serveur

- [ ] **C-10 — Variables du 2e serveur.** **Où** : 2e serveur, administrateur Windows.
  - `ConnectionStrings__Pharmacie` : `Host=localhost`, même compte `pharma_app`, même mot de passe.
  - `Jwt__Cle` : **la même valeur** que le principal (transmise au coffre, jamais par courriel) ; posée et contrôlée par C-13
    (`-EmpreinteJetonPrincipal`), contrôle possible ensuite avec `verifier-second-serveur.ps1`.
  - `Chifa__DossierExport` : propre à ce PC. **Ne pas** régler `AutoriserHttpReseau`. Longueur de `Jwt__Cle` ≥ 64 (comme A21).
  - Détail : MEP § C 7 ; procedure-deux-serveurs.md § 3.1.

- [ ] **C-11 — Certificat du 2e serveur, sur le PC serveur** (signé par la même autorité : rien à réinstaller sur les postes). **Où** : PC
  serveur, administrateur Windows, clé USB `E:\`. Prérequis : A23 fait.
  ```powershell
  Set-Location "<dossier qui contient scripts>"
  .\scripts\certificat_https.ps1 -CertificatPourAutrePc -NomsAutrePc <nom du 2e> -AdressesAutrePc <IP du 2e> -FichierCertificat E:\certificat-second.pfx -WhatIf
  .\scripts\certificat_https.ps1 -CertificatPourAutrePc -NomsAutrePc <nom du 2e> -AdressesAutrePc <IP du 2e> -FichierCertificat E:\certificat-second.pfx
  ```
  - Saisir **exactement** le nom et l'IP que les postes utiliseront (C-23). Plusieurs valeurs : `-NomsAutrePc SERVEUR2,SERVEUR2.local`,
    `-AdressesAutrePc 192.168.1.30,10.8.0.30`. Phrase de passe (12 caractères au moins, deux fois), notée au coffre.
  - **Attendu** : `Certificat de l'autre PC écrit : E:\certificat-second.pfx (valable jusqu'au …)` et la liste des noms et adresses.
  - **Si échec** : « Aucune autorité … dans Cert:\LocalMachine\My » = ce n'est pas le PC serveur (ou F2 bis). « Le fichier existe déjà » :
    autre nom. « Les deux saisies sont différentes » : recommencer.
  - Détail : MEP § C 8a.

- [ ] **C-12 — Importer le certificat sur le 2e serveur.** **Où** : 2e serveur, administrateur Windows, clé USB.
  ```powershell
  .\scripts\certificat_https.ps1 -Importer -FichierCertificat E:\certificat-second.pfx -DossierApi "C:\Program Files\PharmaOfficine\Api" -PareFeu -WhatIf
  .\scripts\certificat_https.ps1 -Importer -FichierCertificat E:\certificat-second.pfx -DossierApi "C:\Program Files\PharmaOfficine\Api" -PareFeu
  ```
  - Vérifier : `Get-Content "C:\Program Files\PharmaOfficine\Api\appsettings.Https.json"` et
    `Get-NetFirewallRule -DisplayName "PharmaOfficine API (https 5443)" | Select-Object DisplayName, Enabled`.
  - **Attendu** : `Certificat importé dans Cert:\LocalMachine\My …`, configuration identique au PC serveur, règle `Enabled : True`. Service de
    l'API **arrêté** jusqu'à C-14.
  - **Si échec** : « phrase de passe incorrecte » ; « Ce fichier est une autorité » = mauvais fichier (F2 bis). Certificat valable 825 jours (F6).
  - Supprimer ensuite le PFX de la clé USB des postes ; le garder au coffre avec sa phrase, à part.
  - Détail : MEP § C 8b.

- [ ] **C-13 — Initialiser la réplique.** **Où** : 2e serveur, administrateur Windows (+ mots de passe `postgres` et de réplication ; le
  titulaire tape celui de la clé de scellement).
  ```powershell
  cd "<dossier>\scripts\second-serveur"
  .\initialiser-second-serveur.ps1 -Principal <IP du principal> -EmpreinteJetonPrincipal <empreinte du principal> -FichierCle E:\cle-scellement-<...>.pocle -Essai
  .\initialiser-second-serveur.ps1 -Principal <IP du principal> -EmpreinteJetonPrincipal <empreinte du principal> -FichierCle E:\cle-scellement-<...>.pocle
  ```
  - Taper `OUI`. Le script contrôle la clé des jetons (saisie masquée, arrêt si l'empreinte diffère), règle le service en démarrage automatique
    avec relance (arrêté pour l'instant), garde l'ancien dossier PostgreSQL, copie la base, ajoute `application_name=second_serveur`, importe
    la clé de scellement.
  - **Attendu** : contrôle « lecture seule » réussi ; service **arrêté** jusqu'à C-14.
  - **Si échec** : relire le message ; ne pas relancer sans comprendre la cause (l'ancien dossier est gardé).
  - Détail : MEP § C 9 ; procedure-deux-serveurs.md § 3.3.

- [ ] **C-14 — Déclarer le principal, démarrer l'API en veille.** **Où** : 2e serveur, administrateur Windows.
  ```powershell
  [Environment]::SetEnvironmentVariable("SecondServeur__AutreServeur", "<IP du principal>", "Machine")
  Start-Service "PharmaOfficine API"
  Get-Service "PharmaOfficine API" | Select-Object Status, StartType
  Invoke-RestMethod https://<IP du 2e>:5443/api/healthcheck
  ```
  - **Attendu** : `Running`, `Automatic` ; santé `role = veille`.
  - **Si échec** : `role` absent ou « inconnu » = ancienne API. Service en boucle = `SecondServeur__AutreServeur` absent ou faux.
  - Détail : MEP § C 10.

- [ ] **C-14 bis — Créer la clé de ce serveur pour l'offre** et relever son **empreinte** (point 5 du 2026-11-27). **Qui** : technicien.
  **Où** : 2e serveur, invite de commandes **en administrateur**.
  ```powershell
  & "C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe" --creer-cle-serveur
  & "C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe" --empreinte-serveur
  ```
  - **Attendu** : « Clé de ce serveur créée. » (ou « existe déjà » : jamais remplacée), fournisseur « Microsoft Platform Crypto Provider »
    (TPM), empreinte en 8 groupes de 4 caractères : la **noter** pour l'éditeur (A45). La base n'est pas lue (marche aussi en veille).
  - « ATTENTION : clé LOGICIELLE » : pas de TPM utilisable, rien n'est bloqué ; vérifier le TPM dans le BIOS/UEFI **avant** de transmettre
    l'empreinte. « Droits d'administrateur nécessaires » : relancer l'invite en administrateur.
  - Transmettre l'empreinte du 2e serveur à l'éditeur : **nouveau code** avec les deux `--serveur` (principal et 2e), saisi dans Mon offre
    sur le principal ; sinon, après une bascule, 15 jours de grâce puis offre de base.
  - Détail : MEP § C 10 bis.

- [ ] **C-15 — Réglages par serveur.** **Qui** : titulaire, sur le PC serveur. Administration, 2e serveur, **Réglages par serveur** : dossiers
  **CHIFA-Officine** et **logiciel MDN** de chaque serveur ; cases « CHIFA-Officine installé », « Lecteur de carte et Token disponibles »,
  « Logiciel MDN installé », « Douchette branchée et testée ». Répliqués (table `serveur_officine`). Détail : MEP § C 11.

- [ ] **C-16 — Contrôler la réplique.** **Où** : 2e serveur, administrateur Windows (+ mot de passe de `pharma_app`).
  ```powershell
  cd "<dossier>\scripts\second-serveur"
  .\verifier-second-serveur.ps1 -Principal <IP du principal> -DossierChifa "<dossier CHIFA du 2e>" -DossierMdn "<dossier MDN du 2e>"
  ```
  - Puis titulaire : Administration, 2e serveur, **État de la réplication** : « 2e serveur connecté », retard, slot actif ; versions
    identiques, clé présente et **identique**, dossiers joignables.
  - **Attendu** : tout vert (écart de version en rouge). Détail : MEP § C 12.

### Phase C4 — Copie de la GED

- [ ] **C-17 — Partager la GED, sur le principal.** **Où** : PC serveur, administrateur Windows.
  ```powershell
  cd "<dossier>\scripts\second-serveur"
  .\partager-ged.ps1 -AdresseSecond <IP du 2e> -Essai
  .\partager-ged.ps1 -AdresseSecond <IP du 2e>
  Get-SmbShareAccess -Name 'PharmaGED$'
  Get-NetFirewallRule -DisplayName "PharmaOfficine SMB 445 GED (2e serveur)" | Get-NetFirewallAddressFilter | Select-Object RemoteAddress
  ```
  - Crée le compte local `PharmaGedCopie` (mot de passe de 14 caractères au moins, **au coffre**, ressaisi à l'identique sur le 2e serveur),
    le droit NTFS de lecture, le partage masqué `PharmaGED$` en lecture seule pour ce compte seul, la règle 445 pour l'IP du 2e seule.
    Autre dossier : `-DossierGed` ; ordonnances hors GED : `-DossierOrdonnances <dossier>`.
  - **Attendu** : « Partage "PharmaGED$" créé : lecture seule, PharmaGedCopie seulement » ; une seule ligne `PharmaGedCopie`, `Read` ;
    `RemoteAddress` = l'IP du 2e ; « Contrôle : aucune autre règle active n'ouvre le port 445 ».
  - **Si échec** : « Dossier de la GED introuvable » = `-DossierGed`. « donne un droit à … » = retirer ce droit. « Une autre règle ouvre le port
    445 » : la désactiver ; **ne pas continuer** tant qu'elle ouvre 445 à tout le réseau. Plus de 2e serveur : `.\partager-ged.ps1 -AdresseSecond <IP> -Retirer`.
  - Détail : MEP § C 12 bis.

- [ ] **C-18 — Essai de copie.** **Où** : 2e serveur, administrateur Windows.
  ```powershell
  cd "<dossier>\scripts\second-serveur"
  .\copier-ged.ps1 -Source \\<IP du principal>\PharmaGED$ -Essai
  ```
  - **Attendu** : « [ESSAI : rien n'est modifié] GED : fichiers à copier (simulation) », puis « Simulation terminée : rien n'a été écrit ».
  - **Si échec** : « REFUS : source injoignable … » (code 1) = C-17, principal allumé, IP.
  - Détail : MEP § C 12 ter.

- [ ] **C-19 — Lecture de la fréquence de copie** (facultatif : sinon toutes les 5 minutes). **Où** : d'abord le **principal**, puis le **2e
  serveur**, administrateur Windows + `postgres`. Le script 143 doit être passé.
  ```powershell
  cd "<dossier>\scripts\second-serveur"
  .\autoriser-lecture-copie-ged.ps1 -Essai      # sur le principal puis sur le 2e serveur : affiche ce qui serait fait
  .\autoriser-lecture-copie-ged.ps1             # sur le principal d'abord, puis sur le 2e serveur (après la réplication)
  ```
  - Autre compte que `PharmaGedCopie` : `.\autoriser-lecture-copie-ged.ps1 -CompteWindows <compte>`.
  - **Attendu** : « Rôle pharma_ged_lecture : existe », « Droit de lecture de la seule colonne ged_copie_minutes donné », « pg_ident.conf
    complété : PharmaGedCopie@<NOM DU PC> -> pharma_ged_lecture », « pg_hba.conf complété ». Après C-20 : journal « Réglage de l'application :
    toutes les N minute(s) ».
  - **Si refus** (« Réglage de la fréquence illisible ») : le journal de PostgreSQL du 2e serveur donne le nom de compte reçu ; relancer avec
    ce nom ou signaler l'écart.
  - Détail : MEP § C 12 ter bis.

- [ ] **C-20 — Installer la copie automatique.** **Où** : 2e serveur, administrateur Windows (mot de passe `PharmaGedCopie` = celui du principal).
  ```powershell
  .\copier-ged.ps1 -Source \\<IP du principal>\PharmaGED$ -Installer -Essai
  .\copier-ged.ps1 -Source \\<IP du principal>\PharmaGED$ -Installer
  Start-ScheduledTask -TaskName "PharmaOfficine copie de la GED (2e serveur)"
  Get-ScheduledTaskInfo -TaskName "PharmaOfficine copie de la GED (2e serveur)" | Select-Object LastRunTime, LastTaskResult
  Get-Content "C:\ProgramData\PharmaOfficine\Journaux\Copie-GED\copie-ged-$(Get-Date -Format yyyyMM).log" -Tail 20
  ```
  - Tâche déclenchée chaque minute, copie toutes les N minutes (Administration › 2e serveur › Copie de la GED, 1 à 60, **5 par défaut**).
    Copie immédiate : `.\copier-ged.ps1 -Source \\<IP du principal>\PharmaGED$ -Maintenant`. Première copie longue.
  - **Attendu** : `LastTaskResult` = `0` ; journal « rien à copier » ou « fichiers copiés » ; le 2e serveur a **au moins** autant de fichiers.
  - **Si échec** : `1` = « REFUS : source injoignable » ou « paramètre invalide » ; `2` = fichiers non copiés (relancer) ; `0x80070569` ou
    `0x8007052E` = mot de passe différent (relancer `-Installer`) ou droit « Ouvrir une session en tant que tâche ». **[À CONFIRMER : à essayer
    sur les deux vrais serveurs (SMB entre PC hors domaine avec un compte local de même nom et même mot de passe).]**
  - **Limite** : retard de N minutes au plus ; les documents des dernières minutes avant une panne peuvent manquer. Les sauvegardes de la GED
    (disque externe) restent la garantie.
  - Retrait : `.\copier-ged.ps1 -Source \\<IP du principal>\PharmaGED$ -Desinstaller` (2e serveur), `.\partager-ged.ps1 -AdresseSecond <IP> -Retirer`
    (principal) ; `Remove-LocalUser PharmaGedCopie` pour supprimer le compte.
  - Détail : MEP § C 12 quater.

### Phase C5 — Réplication synchrone (recommandé : aucune vente perdue)

- [ ] **C-21 — Réplication synchrone des ventes, factures et registre.** **Où** : les deux serveurs, administrateur Windows + `postgres`.
  - **a. Nom de la copie** : `application_name=second_serveur` dans `primary_conninfo` (`postgresql.auto.conf` du 2e serveur), ajouté par
    `initialiser-second-serveur.ps1` et `remettre-en-second.ps1`. Copie faite avec une ancienne version : l'ajouter à la main (dans les
    apostrophes, fichier **sans BOM**), redémarrer PostgreSQL du 2e serveur.
  - **b. Sur le principal** (`psql -U postgres`), **les trois ensemble** :
    ```sql
    ALTER SYSTEM SET synchronous_standby_names = 'second_serveur';
    ALTER SYSTEM SET synchronous_commit = 'local';   -- obligatoire : sinon TOUTES les transactions attendraient le 2e serveur, et bloqueraient s'il est arrêté
    ALTER SYSTEM SET wal_sender_timeout = '10s';     -- réseau figé : copie écartée au bout de 10 s au lieu de 60
    SELECT pg_reload_conf();
    ```
    Aucun redémarrage. **Non répliqués** : à reposer sur le nouveau principal après une bascule (F4, étape 2).
  - **Attendu** : `SELECT application_name, sync_state FROM pg_stat_replication;` → `second_serveur | sync` ; `SHOW synchronous_commit;` → `local`.
  - **Retour arrière** : `ALTER SYSTEM RESET synchronous_standby_names; SELECT pg_reload_conf();`.
  - Détail : MEP § C 15 ; procedure-deux-serveurs.md § 5.

### Phase C6 — Postes

- [ ] **C-22 — Postes autorisés et prouvés** (indispensable à la bascule automatique : seuls les postes autorisés votent). Voir A46.
  Détail : procedure-deux-serveurs.md § 6.1 et 6.1 bis.
- [ ] **C-23 — Adresse de secours, sur chaque poste.** **Qui** : titulaire. Administration, Imprimantes et étiquettes, **Serveur et PC de
  secours** : « PC de secours » = `https://<nom ou IP du 2e>:5443/` (présent dans le certificat de C-11 ; jamais `http://…:5080/`). Délai avant
  le bandeau : 60 s par défaut. Détail : MEP § C 13.
- [ ] **C-24 — Case « Basculer automatiquement sur le 2e serveur (ce poste) »** (même écran), cochée par défaut. Décochée : le poste propose
  seulement le bouton de bascule. Détail : procedure-deux-serveurs.md § 6.3.

### Phase C7 — Alertes et contrôles de fin d'installation

- [ ] **C-25 — Alertes.** **Qui** : titulaire. Prévenu (SMS ou e-mail, au plus une fois par heure) si le 2e serveur décroche ou si le retard
  dépasse les seuils (300 s et 64 Mo par défaut) : vérifier les seuils dans l'écran « 2e serveur ». La réplication reprend seule au
  redémarrage. Au-delà de `max_slot_wal_keep_size` (20 Go par défaut), réinitialiser le 2e serveur (C-13). Détail : MEP § C 14.

- [ ] **C-26 — Contrôles de fin d'installation.**

  | # | Contrôle | Attendu |
  | --- | --- | --- |
  | 1 | Administration › 2e serveur › État de la réplication | « 2e serveur connecté », retard faible, slot actif |
  | 2 | Santé des deux API | Principal : `role = principal` ; 2e serveur : `role = veille` |
  | 3 | Réplication synchrone (si C-21) | `second_serveur \| sync` |
  | 4 | Alertes (seuils, SMS ou e-mail du titulaire) | C-25 |
  | 5 | Barre d'état d'un poste | Rien concernant le 2e serveur (n'apparaît qu'en anomalie) |
  | 6 | Postes autorisés | Tous listés comme prouvés |
  | 7 | Promotion automatique | Active par défaut (`SecondServeur__Bascule__PromotionAutomatique`) ; `false` = toujours manuelle |

  Détail : procedure-deux-serveurs.md § 7.

### Phase C8 — Essai de bascule réel (OBLIGATOIRE avant la mise en service)

- [ ] **C-27 — Essai de bascule réel.** Décision du titulaire du 2026-11-25. **Qui** : technicien, titulaire. **Où** : PC serveur, 2e serveur,
  deux postes au moins. **Quand** : après C-01 à C-26, **avant l'ouverture**, sans client, prévoir une heure.
  1. **Préparer** : écran « 2e serveur » tout vert, `role = veille`, `second_serveur | sync` si C-21. **Au moins deux postes autorisés**, un
     employé connecté sur chacun **depuis plus de 3 minutes**, case « Basculer automatiquement » cochée. Sur un poste : produit au panier
     **sans encaisser**.
  2. **Provoquer la panne** : **débrancher le câble réseau du PC serveur** (ou arrêter ensemble l'API **et** PostgreSQL ; jamais l'API seule).
     Noter l'heure.
  3. **Attendu** : bandeau « Le serveur principal ne répond plus. Vérification avec le 2e serveur en cours » en 15 à 30 s ; 2e serveur promu
     en **1 min 30 à 2 min** (`role = principal`) ; postes basculés **seuls** (« Ce poste est passé sur le 2e serveur, devenu serveur
     principal… », « Connecté · 2e serveur », session et panier gardés) ; alerte de bascule au titulaire ; le 2e serveur est « Principal ».
  4. **Rebrancher l'ancien PC serveur** : il passe seul en lecture seule (`role = ecarte`). Ne rien y lancer d'autre.
  5. **Remettre en place** : procédure **F4** complète ; puis garder cette répartition ou revenir à l'origine (F4, étape 4).
  - La promotion **saute les compteurs** des tickets, factures, avoirs et bons de commande (trou voulu). **[À VÉRIFIER avec le comptable :
    trou de numéros dû à l'essai avant la mise en service.]**
  - **Pas de promotion au bout de 3 minutes** : lire l'alerte et le journal du 2e serveur (`Vigie : verdict …`). Causes fréquentes : postes non
    autorisés, vus depuis moins de 3 minutes, API arrêtée seule, `166b` absent. Faire la **bascule manuelle F3**, corriger, refaire l'essai.
  - **Noter** : heure de la panne, de la promotion, de la bascule de chaque poste, tout écart.
  - Détail : MEP § C 16 ; procedure-deux-serveurs.md § 7 bis ; promotion-serveurs.md § 4 et § 8.

**Non encore essayé en réel** (à surveiller à la première installation) : `initialiser-second-serveur.ps1` et `remettre-en-second.ps1` sur deux
vrais PC ; copie de la GED entre deux vrais serveurs ; isolement du serveur principal, égalité des postes ; connexion d'un poste sur un compte
Windows standard après l'autorisation **[À VÉRIFIER]** ; CHIFA-Officine, lecteur de carte et Token sur le 2e serveur (question posée à la CNAS).
Détail : procedure-deux-serveurs.md § 11.

---

## Chapitre D. PC de secours (facultatif / selon le cas)

Facultatif, **fortement conseillé**. À faire après A26 si décidé à A08. Un PC de l'officine reçoit une **copie de secours** de la base à
chaque sauvegarde. En cas de panne du PC serveur, la pharmacie repart dessus (F2). Les copies ne sont **pas chiffrées** : accès restreint.

- [ ] **D-01 — Choisir le PC de secours** (PC de l'officine, allumé et branché). Détail : MEP § D 1.
- [ ] **D-02 — PostgreSQL**, même version majeure que le PC serveur (A10). **Où** : PC de secours, administrateur Windows. Détail : MEP § D 2.
- [ ] **D-03 — Compte `pharma_app` avec `CREATEDB`, sans créer de base.** **Où** : PC de secours, `postgres`, en psql interactif
  (`& $psql -h localhost -U postgres -d postgres`) :
  ```sql
  CREATE ROLE pharma_app LOGIN CREATEDB;
  \password pharma_app
  ```
  - Créer aussi `pharma_proprietaire` : `CREATE ROLE pharma_proprietaire NOLOGIN;`. **[À CONFIRMER : la restauration rejoue les propriétaires
    des objets ; les rôles doivent exister avant la restauration.]**
  - Compte déjà créé sans `CREATEDB` : `ALTER ROLE pharma_app CREATEDB;`. **Attendu** : `CREATE ROLE`, `\password` demande deux fois.
    **Si échec** : `already exists` : vérifier avec `\du pharma_app`.
  - Détail : MEP § D 3 et § A 2.2.
- [ ] **D-04 — API à la même version** que le PC serveur, dans `C:\Program Files\PharmaOfficine\Api` (A18, A19). Détail : MEP § D 4.
- [ ] **D-05 — Variables de l'API** (A21) : `ConnectionStrings__Pharmacie` (base `<base>`, même compte, même mot de passe), `Jwt__Cle`
  **[À CONFIRMER : même valeur que le PC serveur ? Sinon, reconnexion obligatoire après bascule.]**, `Chifa__DossierExport` propre à ce PC.
  Détail : MEP § D 5.
- [ ] **D-06 — Certificat HTTPS** (même autorité) : **6a** sur le PC serveur (comme C-11), **6b** sur le PC de secours (comme C-12), en remplaçant
  « 2e serveur » par « PC de secours » ; fichier conseillé `E:\certificat-secours.pfx` ; noms et adresses : ceux que les postes saisiront (D-13).
  L'IP affichée par `basculer-pc-secours.ps1` doit figurer dans le certificat. Détail : MEP § D 6.
- [ ] **D-07 — Service de l'API en démarrage manuel, arrêté.** **Où** : PC de secours, administrateur Windows.
  ```powershell
  New-Service -Name "PharmaOfficine API" -BinaryPathName '"C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe"' -DisplayName "PharmaOfficine API" -StartupType Manual
  Get-Service "PharmaOfficine API"
  ```
  **Attendu** : `Stopped`. Détail : MEP § D 7.
- [ ] **D-08 — Importer la clé de scellement** (exportée à C-09, même procédure côté titulaire). **Où** : PC de secours, administrateur
  Windows ; le titulaire tape le mot de passe du fichier.
  ```powershell
  & "C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe" --importer-cle E:\cle-scellement-<...>.pocle
  & "C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe" --empreinte-cle
  ```
  - **Attendu** : la même empreinte que sur l'écran du titulaire. `--remplacer` seulement si le titulaire le demande. **[À CONFIRMER :
    comportement de `--importer-cle` si une clé existe déjà.]** L'API refuse de démarrer si la clé est illisible.
  - Détail : MEP § D 8.
- [ ] **D-08 bis — Créer la clé de ce serveur pour l'offre** et relever son **empreinte** (point 5 du 2026-11-27). **Qui** : technicien.
  **Où** : PC de secours, invite de commandes **en administrateur**.
  ```powershell
  & "C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe" --creer-cle-serveur
  & "C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe" --empreinte-serveur
  ```
  - **Attendu** : « Clé de ce serveur créée. » (ou « existe déjà » : jamais remplacée), fournisseur « Microsoft Platform Crypto Provider »
    (TPM), empreinte en 8 groupes de 4 caractères : la **noter** pour l'éditeur (A45). La base n'est pas lue (marche aussi en veille).
  - « ATTENTION : clé LOGICIELLE » : pas de TPM utilisable, rien n'est bloqué ; vérifier le TPM dans le BIOS/UEFI **avant** de transmettre
    l'empreinte. « Droits d'administrateur nécessaires » : relancer l'invite en administrateur.
  - Transmettre l'empreinte du PC de secours à l'éditeur (3 serveurs au plus dans un code) : nouveau code saisi dans Mon offre sur le PC
    serveur ; sinon, après la bascule, 15 jours de grâce puis offre de base.
  - Détail : MEP § D 8 bis.
- [ ] **D-09 — Dossier partagé de la copie.** **Où** : PC de secours, administrateur Windows.
  ```powershell
  New-Item -ItemType Directory -Force "D:\CopieSecours" | Out-Null
  New-SmbShare -Name CopieSecours -Path "D:\CopieSecours" -FullAccess "<compte autorisé>"
  ```
  - **[À CONFIRMER : le service de l'API du PC serveur tourne en compte système local par défaut ; il accède au partage avec le compte de
    l'ordinateur. Quel compte donner ? Tester.]** **Attendu** : `\\<PC de secours>\CopieSecours` existe.
  - Détail : MEP § D 9.
- [ ] **D-10 — Configurer la copie de secours.** **Qui** : titulaire, sur le PC serveur. Administration, Sauvegardes, **Copie de secours** :
  dossier `\\<PC de secours>\CopieSecours`, nombre de copies, délai d'alerte (26 h par défaut). Chaque copie est vérifiée (taille, SHA-256,
  `<copie>.sha256`). Détail : MEP § D 10.
- [ ] **D-11 — Vérifier la première copie.** **Où** : PC de secours.
  ```powershell
  Get-ChildItem "D:\CopieSecours" | Select-Object Name, Length
  ```
  **Attendu** : un `pharmacie_*.dump` et son `.sha256`. Détail : MEP § D 11.
- [ ] **D-12 — Test chaque mois.** Titulaire : Administration, Sauvegardes, **« Tester la restauration de la dernière copie »**. Simulation sur
  le PC de secours :
  ```powershell
  .\basculer-pc-secours.ps1 -Copies "D:\CopieSecours" -Essai
  ```
  **[À CONFIRMER : copie du script sur le PC de secours.]** Détail : MEP § D 12.
- [ ] **D-13 — Adresse de secours des postes** : `https://<IP du PC de secours>:5443/`, délai 60 s, comme C-23. Cette IP doit être dans le
  certificat du PC de secours (D-06). Détail : MEP § D 13 ; A 9.12.

---

## Chapitre E. Serveur central de synchronisation (facultatif / selon le cas)

Le serveur central (Tracodex Réseau) reçoit la copie des données des officines. **Il tourne pour l'instant sur le PC de développement** (base
`pharmacie_cloud_dev`, port 5090). Hébergement réel **[À VÉRIFIER : hébergeur agréé]**. Aucun secret dans un fichier du dépôt ni dans
`appsettings*.json` ; tout passe par des variables d'environnement. Les scripts de `database\cloud\` ne se passent **jamais** sur la base d'une
officine. **Ordre de mise à jour : serveur central d'abord, officines ensuite.**

### E1 — Hébergement et base centrale

- [ ] **E-01 — Hébergement agréé.** **[À VÉRIFIER : hébergeur, emplacement, exposition réseau, reverse proxy.]** Détail : MEP § E 1.1.
- [ ] **E-02 — PostgreSQL** (même version majeure que les officines, ou plus récente). **Où** : serveur central, administrateur. Détail : MEP § E 1.2.
- [ ] **E-03 — Compte propriétaire** de la base, distinct du rôle applicatif. **[À CONFIRMER : nom du compte propriétaire et nom de la base de
  production.]** Détail : MEP § E 1.3.
- [ ] **E-04 — Créer la base centrale.** **Où** : serveur central. **Compte** : `postgres` (création), puis `<propriétaire>` (scripts) ; mots de
  passe saisis à l'écran.
  ```powershell
  .\scripts\mep\creer_base_centrale.ps1 -Base <base centrale> -Proprietaire <propriétaire>
  ```
  - `-WhatIf` pour voir le plan ; `-Hote <hôte>` si distant. Le compte `<propriétaire>` doit exister (E-03). Passe tous les scripts numérotés
    de `database\cloud` dans l'ordre, de `001` au dernier (`044` à ce jour ; écart n° 5 : corrigé) ; `001` crée `pharma_cloud_api` sans connexion ; `029` crée
    `pharma_cloud_etat` sans connexion.
  - **Attendu** : « Terminé », code de sortie 0.
  - **Si échec** : le script nomme le script en erreur. « Le compte propriétaire n'existe pas » : E-03. « contient déjà des tables centrales » :
    base déjà installée (mise à jour : E-43 à E-46).
  - Détail : MEP § E 2.1 ; en-tête de `scripts\mep\creer_base_centrale.ps1`.
- [ ] **E-05 — Tests** : même commande que E-04 avec `-AvecTests` (chaque fichier de `database\cloud\tests` finit par « tous réussis »). Pas de
  fichier de test pour `002`. **[À CONFIRMER : faut-il lancer les tests sur la base de production, ou seulement sur une copie ?]** En attendant :
  sur une base d'essai. Détail : MEP § E 2.2.
- [ ] **E-06 — Contrôle indépendant** (facultatif). **Compte** : `<propriétaire>`.
  ```powershell
  & $psql -h <hôte> -U <propriétaire> -d <base centrale> -tAc "SELECT rolcanlogin FROM pg_roles WHERE rolname = 'pharma_cloud_api'"
  ```
  **Attendu** : `f`. Détail : MEP § E 2.3.
- [ ] **E-07 — Mot de passe du rôle applicatif.** **Compte** : `postgres`.
  ```sql
  ALTER ROLE pharma_cloud_api LOGIN;
  \password pharma_cloud_api
  ```
  Tiré au sort, au coffre, **jamais** dans un script. `pharma_cloud_api` n'est pas propriétaire des tables. Détail : MEP § E 2.4.

### E2 — Compte de service, variables, publication

- [ ] **E-08 — Dossier de l'autorité**, hors de tout dépôt. **Où** : serveur central, administrateur Windows.
  ```powershell
  New-Item -ItemType Directory -Force "D:\PharmaCentral\Autorite" | Out-Null
  icacls "D:\PharmaCentral\Autorite" /inheritance:r /grant:r "Administrateurs:(OI)(CI)F" "<compte du service>:(OI)(CI)M"
  ```
  Détail : MEP § E 3.1.
- [ ] **E-09 — Variables** (session PowerShell des commandes d'administration) :
  ```powershell
  $env:PHARMA_CLOUD_DOSSIER = "D:\PharmaCentral\Autorite"
  $env:PHARMA_CLOUD_MOT_DE_PASSE = "<mot de passe des clés, 16 caractères au moins>"
  $env:PHARMA_CLOUD_CONNEXION = "Host=<hôte>;Port=5432;Database=<base centrale>;Username=pharma_cloud_api;Password=<mot de passe>"
  $env:PHARMA_CLOUD_ADMIN_CONNEXION = "Host=<hôte>;Port=5432;Database=<base centrale>;Username=<propriétaire>;Password=<mot de passe>"
  ```
  - `PHARMA_CLOUD_MOT_DE_PASSE` : **sans lui, l'autorité est perdue** (au coffre). `PHARMA_CLOUD_ADMIN_CONNEXION` : outil d'administration seulement.
  - Compte SMTP de l'éditeur (F8), dans la session, juste avant `--depart-officine` ; valeurs **factices** :
    ```powershell
    $env:PHARMA_CLOUD_SMTP_HOTE = "smtp.exemple.invalid"
    $env:PHARMA_CLOUD_SMTP_PORT = "587"            # 465 = TLS direct ; tout autre port = STARTTLS exigé ; jamais d'envoi en clair
    $env:PHARMA_CLOUD_SMTP_UTILISATEUR = "<identifiant du compte d'envoi>"
    $env:PHARMA_CLOUD_SMTP_MOT_DE_PASSE = "<mot de passe du compte d'envoi>"
    $env:PHARMA_CLOUD_SMTP_EXPEDITEUR = "<adresse d'expédition>"
    ```
    **[À CONFIRMER : compte SMTP de l'éditeur à créer (fournisseur, adresse d'expédition) ; aucun envoi réel essayé le 2026-11-20.]**
  - Service en continu : variables au niveau machine ou du compte de service. **[À CONFIRMER : le code ne fournit pas de service Windows pour le
    serveur central ; mode de lancement et compte du service à définir.]**
  - Détail : MEP § E 3.2.
- [ ] **E-10 — Publier le serveur central.** **Où** : PC des sources. **[À CONFIRMER : même forme que l'API,
  `dotnet publish src\Pharma.Cloud.Api -c Release -r win-x64 --self-contained true -o "<dossier>\Cloud"`, à valider.]** Détail : MEP § E 3.3.
- [ ] **E-11 — Compte e-mail des codes de second facteur** (compte de l'**éditeur**). **Où** : serveur central, session de E-09, après les scripts
  centraux (037 compris). Gmail : validation en deux étapes, puis mot de passe d'application (16 lettres), **demandé au clavier**.
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --envoi-codes --serveur smtp.gmail.com --port 587 --tls starttls `
      --identifiant <adresse du compte d'envoi> --operateur "<nom>" --essai <votre adresse>
  ```
  - Autre dossier de clés des écrans (`Ecrans:DossierCles`) : ajouter `--dossier-cles "<ce même dossier>"` (aussi à `--depart-officine … --email`
    et `--creer-comptes … --envoyer-identifiants`).
  - **Attendu** : « E-mail d'essai envoyé à x***@… » puis « Envoi des codes enregistré… ». « identifiant ou mot de passe refusé » : rien n'est enregistré.
  - Compte administrateur (éditeur) :
    ```powershell
    & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --creer-compte-editeur <identifiant> --nom "<nom>" --email <adresse de l'éditeur>
    ```
  - Adresse à changer :
    ```powershell
    & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --contact-compte <identifiant> --email <nouvelle adresse> --auteur "<nom>"
    ```
  - Plusieurs comptes (CSV UTF-8, `role;identifiant;nom;email;telephone`, 200 au plus) :
    ```powershell
    & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --creer-comptes <fichier.csv> --auteur "<nom>" --envoyer-identifiants --adresse-ecrans https://<adresse des écrans>/
    ```
  - Après la mise en ligne des écrans : régler dans « Envoi des codes » la durée des mots de passe provisoires (7 jours) et la déconnexion
    (éditeur 15 min, titulaire 30 min) ; facultatif `Ecrans:AdressePublique`.
  - Détail : MEP § E 3.4.

### E3 — Autorité, certificat, démarrage, tâches

- [ ] **E-12 — Créer l'autorité** (une seule fois).
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --initialiser-autorite
  ```
  **Attendu** : empreinte SHA-256 affichée, **la noter**. Sauvegarder à part `autorite.crt`, `autorite.cle` (chiffrée) et le mot de passe.
  Détail : MEP § E 4.1.
- [ ] **E-13 — Certificat HTTPS du serveur** (2 ans).
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --certificat-serveur --noms <nom DNS>,<adresse IP>
  ```
  Détail : MEP § E 4.2.
- [ ] **E-14 — Adresse d'écoute** (`Cloud:Adresse`, `Cloud:Port`), défaut `127.0.0.1`, `5090`. **[À VÉRIFIER : adresse réelle et exposition par
  reverse proxy à l'hébergement. Ne pas mettre `0.0.0.0` sans avoir vérifié.]** Détail : MEP § E 5.1.
- [ ] **E-15 — Démarrer** : `& "<dossier>\Cloud\Pharma.Cloud.Api.exe"` (compte de service). Seul le port HTTPS est ouvert ; la base n'est jamais
  exposée. **[À CONFIRMER : point de contrôle de santé (aucune route de santé n'est prévue dans le code).]** Détail : MEP § E 5.2.
- [ ] **E-16 — Chaque mois** : `Pharma.Cloud.Api.exe --partitions`. Détail : MEP § E 6.1.
- [ ] **E-17 — Toutes les 15 minutes** : `Pharma.Cloud.Api.exe --verifier-integrite` (anomalies : `--anomalies-integrite`). Compte de service avec
  `PHARMA_CLOUD_ADMIN_CONNEXION`. Détail : MEP § E 6.3.
- [ ] **E-18 — Chaque semaine** : `Pharma.Cloud.Api.exe --verifier-integrite --complete`. Détail : MEP § E 6.4.
- [ ] **E-19 — Chaque heure** : `Pharma.Cloud.Api.exe --expirer-ordres`. Ordres disponibles : `--ordres`, `--ordre-essai`, `--ordre-employe-creer`,
  `--ordre-employe-modifier`, `--ordre-employe-bloquer`, `--ordre-employe-debloquer`, `--ordre-droits-role`, `--ordre-prix`,
  `--importer-nomenclature` puis `--publier-nomenclature`, `--ordre-commande`, `--ordre-classe-suivie`, `--ordre-organismes`,
  `--ordre-prestations`, `--ordre-indications`, `--ordre-dci`, `--annuler-ordre` (auteur obligatoire). Détail : MEP § E 6.5.
- [ ] **E-20 — Chaque jour (facultatif)** : `Pharma.Cloud.Api.exe --purger-lots` (sans effet tant que l'effacement n'est pas activé, E-23).
  Détail : MEP § E 6.2.

### E4 — Réglages du serveur central (`--reglages` affiche tout ; chaque réglage est inscrit au journal de sécurité)

- [ ] **E-21 — Familles nominatives.** Défauts : `employes`, `banque`, `clients_factures` **non** ; `pharmacie`, `fournisseurs`,
  `courriel_commandes` **oui**. `Pharma.Cloud.Api.exe --regler-famille <famille> oui|non` ; `--regler-famille <famille> oui|non --officine <code>` ;
  `--regler-famille <famille> global --officine <code>`. Effacer ce qui a été reçu : `--effacer-famille <famille> [--officine <code>]`
  (simulation), puis `--confirmer <famille>` : **suppression de données, validation du titulaire obligatoire**. Détail : MEP § E 7.1.
- [ ] **E-22 — Conservation des lots bruts** : 6 ans par défaut, **6 ans minimum**. `--conservation <ans>`, `--conservation <ans> --officine <code>`,
  `--conservation global --officine <code>`. Détail : MEP § E 7.2.
- [ ] **E-23 — Effacement des lots trop anciens** : désactivé par défaut. `--purger-lots --simulation` ; `--purge-lots activer` (ou `desactiver`).
  **[À VÉRIFIER avant d'activer : durées réglementaires (6 ans fiscaux ; registre des psychotropes : durée à confirmer).]** Détail : MEP § E 7.3.
- [ ] **E-24 — Plafonds** : `quota_octets_jour` 512 Mio par jour par officine (`UPDATE cloud_parametre SET quota_octets_jour = <octets>`) ;
  `quota_chargement_octets` 8 Gio (`UPDATE cloud_parametre SET quota_chargement_octets = <octets>`) ; `chargements_max_24h` et
  `quota_chargements_octets_24h` (script `009`) ; 60 lots par minute, 600 pendant un chargement (`Cloud:LotsParMinute`,
  `Cloud:LotsParMinuteChargement`). Détail : MEP § E 7.4.
- [ ] **E-25 — Renouvellement des certificats d'officine** : seulement dans les 60 derniers jours, une fois par jour au plus. Détail : MEP § E 7.5.
- [ ] **E-26 — Révocation** : procédure F5. Détail : MEP § E 7.6.
- [ ] **E-27 — Proxy inverse** : limites par adresse = adresse du proxy derrière un proxy. **[À VÉRIFIER à l'hébergement.]**
  `Cloud:LimiteParAdresseMinute` (600). Détail : MEP § E 7.7.
- [ ] **E-28 — Conservation des données d'une officine partie** : 6 ans par défaut, **6 ans minimum**, depuis la date de départ.
  `--conservation-depart <ans>`, `--conservation-depart <ans> --officine <code>`, `--conservation-depart global --officine <code>`.
  **[À VÉRIFIER : avis du juriste, loi 18-07 et durée fiscale.]** Détail : MEP § E 7.8.

### E5 — Activer une officine (pour chaque officine)

- [ ] **E-29 — Créer l'officine.** **Où** : serveur central, compte d'administration.
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --creer-officine "<nom commercial>"
  ```
  Affiche le code provisoire et un **code d'activation** (usage unique, 24 h). 2e serveur de la même officine :
  `Pharma.Cloud.Api.exe --code-activation <code officine> --serveur second`. Détail : MEP § E 8.1.
- [ ] **E-30 — Transmettre le code au titulaire par un canal sûr**, jamais dans le même message que le nom de l'officine. Détail : MEP § E 8.2.
- [ ] **E-31 — Régler la synchronisation dans l'officine.** **Qui** : titulaire (droit `admin.parametres`), PC serveur. Administration,
  Synchronisation, **Réglages** : adresse du central (`https://…`), « Synchronisation activée », Enregistrer. Détail : MEP § E 8.3.
- [ ] **E-32 — Activer.** Même écran, **Activation** : coller le code, Activer. La clé privée est créée sur le PC serveur
  (`C:\ProgramData\PharmaOfficine\Synchronisation`, DPAPI) et ne le quitte jamais. Détail : MEP § E 8.4.
- [ ] **E-33 — Vérifier dans État** : « activé (certificat) », dernier envoi et dernier accusé à jour en une à deux minutes. Détail : MEP § E 8.5.
- [ ] **E-34 — Groupes progressifs** (ex. 10, puis 100, puis le reste) ; attendre la fin du chargement complet de chaque groupe. Détail : MEP § E 8.6.

**À savoir** (MEP, « Procédure d'activation des officines », « Chargement complet », « Supervision et purge de la file », « Lots manquants ») :
à la première activation, le central adopte le code de l'officine, les familles nominatives descendent, un chargement complet part (600 lots
par minute), puis seules les modifications partent chaque minute. Volume : environ 10 000 lignes par minute **[À VÉRIFIER par mesure.]**.
Supervision (Administration, Synchronisation, **Supervision et purge**) : bandeau après 60 minutes, alerte de retard après 24 h, alerte d'erreur
après 60 minutes ; droit `sync.suivre` ; au moins un destinataire abonné au sujet « Synchronisation ». Lots accusés gardés 30 jours. Lots
manquants : l'officine les renvoie ou demande d'elle-même un chargement complet ; **ne rien supprimer dans l'officine** ; message « reprise
impossible automatiquement … procédure » : **appeler le développeur**.

### E6 — Base de l'officine revenue en arrière (restauration, bascule ; script 149)

Après F1, F2 ou F3, l'envoi se suspend (« Base revenue en arrière… »), alerte au titulaire. Script central `020` avant le script `149` des officines.

- [ ] **E-35 — Titulaire : « Repartir sur une nouvelle époque ».** Une fois le service redémarré et les postes reconnectés : Administration,
  Synchronisation, État, bandeau « Envoi suspendu… », **« Repartir sur une nouvelle époque »**, saisir **son mot de passe**, **Confirmer**. Le
  central doit répondre (sinon refus, recommencer plus tard). **Attendu** : bandeau disparu, « Lots constitués » à l'époque suivante,
  chargement « en cours (demande) » puis terminé. Détail : MEP, « Base de l'officine revenue en arrière ».
- [ ] **E-36 — Titulaire, après le chargement complet** : Administration, Synchronisation, Intégrité au central, « Restaurations constatées » :
  **« Prendre acte »** et saisir la cause. **Attendu** : prise d'acte « Faite », puis « Aucune anomalie ». Classe « À justifier auprès de
  l'éditeur » : prévenir l'éditeur. **Ne rien supprimer** ni dans l'officine ni au central. Détail : idem.

### E7 — Réglages mutualisés (facultatif)

Prérequis : scripts centraux `010` et `011`, variable `PHARMA_CLOUD_ADMIN_CONNEXION` (jamais `PHARMA_CLOUD_CONNEXION`). Rien n'est publié par
défaut. **[À VÉRIFIER]** l'application d'un modèle dans l'officine (lot M4) : **ne pas publier en production avant ce lot.**

- [ ] **E-37 — État des 11 tables** :
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --reglages-mutualises
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --reglages-mutualises parametre_inventaire
  ```
  Détail : MEP § E 9.1.
- [ ] **E-38 — Premier modèle** (ne publie rien) :
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --modele-depuis-officine <code officine> parametre_inventaire --fichier "D:\PharmaCentral\Modeles\inventaire.json" [--mode propose|impose] [--ecraser]
  ```
  Relire les motifs (motifs locaux de l'officine). « L'officine n'a encore rien envoyé pour cette table » : attendre son chargement. Détail : MEP § E 9.2.
- [ ] **E-39 — Simuler, puis publier** :
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --publier-reglage <table> --fichier "<modèle.json>" --auteur "<nom>" --simulation
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --publier-reglage <table> --fichier "<modèle.json>" --auteur "<nom>" [--motif "<texte>"]
  ```
  Une seule erreur : rien n'est publié (code 1). Mode imposé : confirmation `oui` (ou `--oui`). Détail : MEP § E 9.3.
- [ ] **E-40 — Changer le mode** (décision du titulaire : l'imposé remplace les valeurs locales) :
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --mode-reglage <table> impose|propose --auteur "<nom>" [--motif "<texte>"] [--oui]
  ```
  Détail : MEP § E 9.4.
- [ ] **E-41 — Départ d'une officine** : libérer ou rétablir.
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --liberer-reglages <code officine> --auteur "<nom>" [--motif "<texte>"]
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --retablir-reglages <code officine> --auteur "<nom>" [--motif "<texte>"]
  ```
  Détail : MEP § E 9.5.
- [ ] **E-42 — Journal** :
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --journal-reglages [<table>] [--depuis AAAA-MM-JJ] [--limite <n>]
  ```
  Codes de sortie : 0 si tout est bon, 1 en cas d'erreur. Détail : MEP § E 9.6.

### E8 — Mise à jour du serveur central (avant toute mise à jour des officines)

- [ ] **E-43 — Sauvegarder la base centrale** (compte propriétaire). Détail : MEP § E 10.1.
- [ ] **E-44 — Repasser `002`, puis `003` au dernier, dans l'ordre**, un par un, avec le compte propriétaire (`creer_base_centrale.ps1` refuse
  une base déjà installée) :
  ```powershell
  & $psql -h <hôte> -U <propriétaire> -d <base centrale> -v ON_ERROR_STOP=1 -q -f "database\cloud\<script>.sql"
  ```
  Contraintes d'ordre avec les scripts des officines : central `019` avant officines `147` ; `020` avant `149` ; `022` à `024` avant `151` ;
  `025` avant `152` ; `002` régénéré avant `148` ; `027` et `028` demandent `022` ; `020` et `021` demandent PostgreSQL 18 ; `036` à repasser
  après un `002` régénéré. Scripts idempotents. Détail : MEP § E 10.2.
- [ ] **E-45 — Relancer les tests** de l'étape E 2.2 (`creer_base_centrale.ps1 -AvecTests`, sur une base d'essai ou une copie) (écart n° 6 :
  corrigé dans la source). Détail : MEP § E 10.3.
- [ ] **E-46 — Redémarrer le serveur central**, **avant** toute mise à jour des officines. Détail : MEP § E 10.4.

---

## Chapitre R. Reprise des données de l'ancien logiciel (facultatif / selon le cas)

Le J-1 au soir (répétition complète sur une copie à J-15). L'ancienne base n'est **jamais** modifiée : on travaille sur une copie.
**Prérequis** : A52 (nomenclature) et A59 (super admin) faits.

- [ ] **R-01 — Connexion super admin.** Écran de connexion, « Connexion super admin ». Ce compte ne voit que la reprise de données, déconnecté
  après 10 minutes sans activité. **Extraction**, **chargement** et **abandon** demandent chacun : avertissement, mot de passe ressaisi, puis le
  **code à 6 chiffres** (le même par SMS et par e-mail, un canal suffit), valable 5 minutes, à usage unique ; 3 codes faux bloquent 15 minutes.
  Détail : MEP § A 12.1.
- [ ] **R-02 — Transit.** Administration, Reprise de données : lecture seule de l'ancienne base. Sources : CSV ou Excel (encodage UTF-8 ou ANSI
  détecté ; accents faux : « Encodage des fichiers »), base par pilote ODBC, ou connexion saisie champ par champ. Correspondance des tables et
  colonnes à confirmer ; copie dans le schéma `import`. Les mots de passe ne sont ni extraits ni enregistrés. Détail : MEP § A 12.2.
- [ ] **R-03 — Rapprochement des produits** : code-barres, n° d'enregistrement, puis nom approché à confirmer. Produit absent de la nomenclature :
  créé « à vérifier », refusé en caisse jusqu'à la confirmation de sa classe par le titulaire. Détail : MEP § A 12.3.
- [ ] **R-04 — Contrôles** : toute anomalie bloquante empêche le chargement. Détail : MEP § A 12.4.
- [ ] **R-05 — Chargement**, en une transaction. Lot sans date de péremption : « date à vérifier ». Prix d'achat du lot repris s'il est dans
  l'export, sinon celui du produit. Données attribuées au **pharmacien titulaire** ; le journal garde l'identifiant du super admin et le canal
  (masqué). Détail : MEP § A 12.5.
- [ ] **R-06 — Vérification des totaux** (unités, valeur du stock au prix d'achat, crédits clients, dettes grossistes, nombre de clients), rapport
  **signé par le pharmacien**. Totaux non concordants la veille au soir : **retour arrière** (§ 0.13). Détail : MEP § A 12.6.
- [ ] **R-07 — Ne jamais** lancer `database\data\9xx_*.sql` pendant la reprise. Détail : MEP § A 12.7.

---

## Chapitre B. Mise à jour d'une officine existante

Pour une officine **déjà en service**. Ordre : B-01 à B-16. **Ne jamais relancer `schema.sql` ni `clean.sql`** sur une base en service.
Les scripts `database\migration\NNN_*.sql` sont réexécutables sans dégât ; `mettre_a_jour_base_officine.ps1` les rejoue **toujours depuis
`001`**, un par un, hors transaction, après une sauvegarde obligatoire. Les publications étant autonomes, Windows Update ne corrige **pas**
.NET : à chaque correctif de sécurité .NET important, republier l'API et les postes avec le SDK à jour.

- [ ] **B-01 — Préparer.** **Qui** : technicien. Lire la note de version ; choisir un créneau **sans caisse ouverte** ; prévenir les employés.
  Avant le créneau, **sur le PC des sources** (administrateur Windows) :
  ```powershell
  dotnet publish src\Pharma.Api -c Release -r win-x64 --self-contained true -o "<dossier de livraison>\Api"
  dotnet publish src\Pharma.Desktop -c Release -r win-x64 --self-contained true -o "<dossier de livraison>\Poste"
  ```
  - Copier `<dossier de livraison>` (scripts numérotés **à plat** dans `Api\database\`, init dans `Api\database\init\` :
    `Pharma.Api.csproj`) et le dossier `scripts` sur la clé USB. `111b`, `163b`, `166b` sont dans `Api\database\init\` : rien à copier à part.
  - **Attendu** : `Api\Pharma.Api.exe`, le **dernier script numéroté** dans `Api\database\` (`174_postes_preuve_exigee.sql` à ce jour : le
    numéro le plus haut de `database\migration` du dépôt), `Api\database\init\111b_roles_integrite.sql` et `Poste\Pharma.Desktop.exe` sur la
    clé (écart n° 18 : corrigé).
  - Détail : MEP § B 1.
- [ ] **B-02 — Officine synchronisée** : **mettre le serveur central à jour d'abord** (E-43 à E-46). Détail : MEP § B 2.
- [ ] **B-03 — Arrêter le service.** **Où** : PC serveur, administrateur Windows.
  ```powershell
  Stop-Service "PharmaOfficine API"
  Get-Service "PharmaOfficine API"
  ```
  **Attendu** : `Status : Stopped` (le script de B-04 refuse si le service tourne). Détail : MEP § B 3.
- [ ] **B-04 — Mettre la base à jour** (sauvegarde, détection des rôles, contrôle Chifa, rejeu). **Où** : PC serveur. **Compte** : `pharma_app`
  (et `postgres` si les rôles sont séparés ou à séparer), mots de passe saisis à l'écran.
  ```powershell
  .\scripts\mep\mettre_a_jour_base_officine.ps1 -Base <base> -DossierSauvegarde "E:\Sauvegardes\Pharmacie" -DossierMigration "E:\<dossier de livraison>\Api\database"
  ```
  - `-DossierMigration` : le dossier `Api\database` de la livraison (scripts numérotés à plat). Le script y prend `111b`, `163b` et `166b`
    dans le sous-dossier `init` (valeurs par défaut de `-Script111b`, `-Script163b`, `-Script166b`) : `-Script111b` est inutile (écart
    n° 3 : corrigé). Depuis le dépôt, ces options sont inutiles. `111b` est lancé **par ce script** avec `-SeparerRoles`, `163b` et `166b`
    en fin de rejeu ; seul `101b` se lance à part (B-08) (écart n° 4 : corrigé). `-SeparerRoles` si les
    rôles ne sont pas encore séparés et qu'on les sépare maintenant (B-06). `-WhatIf` pour voir le plan. `-ScriptDepart` : incidents seulement.
  - Sauvegarde `pg_dump -Fc` (`<base>_avant_maj_<date>_<heure>.dump`) relue par `pg_restore --list` (sans sauvegarde valide, rien n'est modifié).
  - Copier aussi `super-admin.dat`, `cle-integrite.dat` et le `.dump` hors du PC serveur (disque externe ; vérifier qu'ils sont à jour).
  - **Attendu** : « Terminé », code 0, la sauvegarde existe, « N scripts rejoués sans erreur ».
  - **Si échec** : le message donne le script en cause ; corriger, relancer sans `-ScriptDepart`. Retour arrière : B-10.
  - Détail : MEP § B 4 ; [scripts-base-de-donnees.md](scripts-base-de-donnees.md).
- [ ] **B-05 — Arrêt sur des n° de facture Chifa à examiner.** **Qui** : le titulaire décide. Trois cas contrôlés avant `101` (lecture seule) :
  n° non conformes (pas 8 chiffres), conflits, recopie impossible.
  - **Ne pas continuer** avant d'en parler au titulaire. Les conflits se tranchent dans Administration, Conflits de n° de facture Chifa.
  - Après la décision : relancer B-04 avec `-ContinuerMalgreConflitsChifa`.
  - **Attendu** : `SELECT count(*) FROM vente_chifa_facture_conflit WHERE tranche_le IS NULL;` → `0` une fois tranchés.
    **[À CONFIRMER : faut-il attendre ce `0` avant la remise en service ?]**
  - Détail : MEP § B 5.
- [ ] **B-06 — Séparation des rôles (`111b`)**, une fois par base. **Compte** : `postgres`. Fait si B-04 a tourné avec `-SeparerRoles` ; sinon,
  si l'état affiché est « pas encore faite » : relancer B-04 avec `-SeparerRoles`.
  - **Attendu** : `objets_encore_a_pharma_app` = **0**. Ensuite, tous les scripts passent par `postgres` avec `SET ROLE pharma_proprietaire`.
  - **Si échec** : `à exécuter par un superutilisateur` : le compte n'est pas `postgres`.
  - Détail : MEP § B 6.
- [ ] **B-07 — Rôles déjà séparés : méthode `SET ROLE`** (`-c "SET ROLE pharma_proprietaire" -f <script>`, même session). **[À VÉRIFIER : cette
  méthode n'a pas encore été validée par un essai avec le compte `postgres`. À essayer sur une COPIE de la base (restaurée sous un autre nom)
  avant la production.]** « SET ROLE pharma_proprietaire n'a pas donné le rôle attendu » : ne pas continuer. Détail : MEP § B 7.
- [ ] **B-08 — Script `101b`** (suppression de `num_facture_chifa`) : **jamais lancé par le script**. Décision du titulaire du 2026-11-26 : le
  passer sur toute officine installée **avant** cette date, une fois, **seulement après son « go »**. Base créée depuis `database\init` : rien à
  faire. Opération **destructive et irréversible** (sauf restauration). Fichier : `database/migration/101b_suppression_num_facture_chifa.sql`,
  pris dans le dépôt (il n'est **pas** dans le dossier publié : exclu par `Pharma.Api.csproj`, contrairement à `111b`, `163b`, `166b`).
  1. **Prérequis** : `101` exécuté, logiciel et API **arrêtés**, aucune autre session sur la base.
  2. **Sauvegarde complète obligatoire**, vérifiée lisible, copiée hors du PC serveur.
  3. **Contrôles en lecture seule** (psql interactif), tous à **0** :
     ```sql
     SELECT count(*) FROM vente_chifa_facture_conflit WHERE tranche_le IS NULL;                       -- conflits non tranchés
     SELECT count(*) FROM vente WHERE NULLIF(btrim(num_facture_chifa), '') IS NOT NULL
                                  AND NULLIF(btrim(chifa_facture), '') IS NULL;                        -- valeurs qui seraient perdues
     SELECT count(*) FROM vente v WHERE NULLIF(btrim(v.num_facture_chifa), '') IS NOT NULL AND NULLIF(btrim(v.chifa_facture), '') IS NOT NULL
                                    AND btrim(v.num_facture_chifa) <> btrim(v.chifa_facture)
                                    AND NOT EXISTS (SELECT 1 FROM vente_chifa_facture_conflit c WHERE c.vente_id = v.id AND c.tranche_le IS NOT NULL); -- écarts sans décision
     ```
  4. **Compte** : avant `111b`, `pharma_app` ; après `111b`, `postgres` avec `SET ROLE pharma_proprietaire`.
  5. **Commande** (avant `111b`) :
     ```powershell
     & $psql -h localhost -U pharma_app -d <base> -v ON_ERROR_STOP=1 -f "<dépôt>\database\migration\101b_suppression_num_facture_chifa.sql"
     ```
     Après `111b` : même commande avec `-U postgres` et `-c "SET ROLE pharma_proprietaire"` placé **avant** `-f`.
  6. **Attendu** : `101b : colonne vente.num_facture_chifa supprimée (… vente(s), dont … avec une valeur, toutes déjà portées par chifa_facture)`.
     Relancé : « n'existe déjà plus, rien à faire ». Refus (`101b REFUSÉ`) : rien n'a été modifié ; corriger, reprendre à B-03.
  7. **Vérification** : `SELECT count(*) FROM information_schema.columns WHERE table_name = 'vente' AND column_name = 'num_facture_chifa';` → `0` ;
     redémarrer l'API et le logiciel, ouvrir une vente et la liste des ventes Chifa.
  8. **Retour arrière** : restauration de la sauvegarde du point 2 (aucun `Down`) ; les ventes saisies depuis sont perdues.
  9. **Après la suppression** (développeur, dans le dépôt) : `schema.sql`, `database/cloud/002_tables_miroir.sql`, `docs/a-faire.md`.
  - Détail : MEP § B 8.
- [ ] **B-09 — Vérifier l'état de la base** (lecture seule, `pharma_app`) :
  ```powershell
  & $psql -h localhost -U pharma_app -d <base> -c "SELECT tableowner, count(*) FROM pg_tables WHERE schemaname = 'public' GROUP BY tableowner"
  ```
  **Attendu** : rôles séparés : toutes les tables à `pharma_proprietaire` ; sinon à `pharma_app` (journaux non protégés). Détail : MEP § B 9.
- [ ] **B-10 — Retour arrière** (seulement si la base reste inutilisable) : service **arrêté** ; restaurer la sauvegarde `.dump` de B-04 (F1, en
  base neuve puis renommage). Les ventes saisies depuis sont perdues : décider vite. Détail : MEP § B 10.
- [ ] **B-11 — Remplacer les fichiers de l'API.** **Où** : PC serveur, administrateur Windows. Renommer `C:\Program Files\PharmaOfficine\Api` en
  `Api_ancien`, copier le nouveau dossier `Api` (clé USB). `appsettings.Https.json` n'est pas publié : **le recopier** depuis `Api_ancien` si
  absent. Les variables restent. Détail : MEP § B 11.
- [ ] **B-12 — Démarrer et vérifier.**
  ```powershell
  Start-Service "PharmaOfficine API"
  Get-Service "PharmaOfficine API"
  (Invoke-WebRequest http://localhost:5080/api/healthcheck -UseBasicParsing).StatusCode
  ```
  **Attendu** : `Running`, `200`. **Si échec** : journal ; revenir en arrière : arrêter, remettre `Api_ancien`, redémarrer. Détail : MEP § B 12.
- [ ] **B-13 — Mettre à jour les postes.** Fermer Tracodex Officine, remplacer le dossier du poste par `Poste` (clé USB). `PHARMA_API` et
  `PHARMA_POSTE` ne changent pas. Nomenclature mise à jour : Administration, Nomenclature, « Importer le fichier Excel… ». Détail : MEP § B 13.
- [ ] **B-14 — Vérifications.** **Qui** : titulaire, sur un poste. Vente test, clôture ; Intégrité des journaux, « Vérifier maintenant » ; avec un
  2e serveur : `verifier-second-serveur.ps1` sur le 2e serveur (C-16), **même version** sur les deux. Détail : MEP § B 14.
- [ ] **B-15 — Code de transition de l'offre** (à chaque mise en place **ou mise à jour**) : relever code de l'officine et NIF (Administration ›
  Mon offre), l'éditeur signe sur **son** PC un code `--transition --fin AAAA-MM-JJ`, saisie dans Mon offre (ou par le central,
  `--ordre-activation`). Même procédure que A45. Pharmacies déjà installées : tout reste ouvert pendant une période de transition (durée **à
  définir**, offre-et-options.md Q8). Détail : offre-et-options.md, « Mise en service » (point 3) et « Décisions » ; procedure-cle-editeur.md § 3 et § 4.
- [ ] **B-16 — Postes autorisés** (script 174, décision du 2026-11-27 : preuve toujours exigée) : après la mise à jour, **aucun PC non
  autorisé ne se connecte**. Le jour même : générer le code de chaque poste et le taper sur chaque PC, comme A46.1 à A46.3 (premier poste par
  `Pharma.Api.exe --autoriser-poste` si aucun n'est encore autorisé). Détail : MEP, « Postes autorisés ».

Puis cocher la [checklist après chaque mise à jour](#checklist-après-chaque-mise-à-jour).

---

## Chapitre F. Procédures d'incident (F1 à F8)

Résumé actionnable. Chaque procédure se lit de haut en bas ; le détail est dans la section indiquée de mise-en-production.md.

### F1. Restauration (base ou GED détruite) — MEP § F1

Sauvegardes utiles : **disque externe** (`pharmacie_AAAAMMJJ_HHMMSS.dump`, GED dans son sous-dossier `GED`). La copie dans `GED\Sauvegardes`
disparaît avec le PC. Trois conservations **distinctes** : sur le disque externe, la sauvegarde de l'application garde les **N dernières**
(14 par défaut) ; la copie dans `GED\Sauvegardes` est gardée **7 jours** par défaut ; le script planifié (option b) supprime les `.dump`
de **plus de 30 jours** (`$RetentionJours`).

**Méthode 1 : depuis l'application** (API en marche ; super admin).
- [ ] F1-M1.1 Sur un poste, « Connexion super admin », écran **Restauration**.
- [ ] F1-M1.2 **Base** : choisir la sauvegarde, valider (avertissement, mot de passe, code SMS / e-mail).
- [ ] F1-M1.3 Le serveur met les postes en attente, fait une copie `GED\Sauvegardes\pharmacie_avant_restauration_….dump`, restaure en base neuve,
  la met à la version du logiciel, l'échange ; l'ancienne est gardée sous `<base>_avant_restauration_AAAAMMJJ_HHMM`. Droit `CREATEDB` de `pharma_app` requis.
- [ ] F1-M1.4 **GED** : « Recopier les documents de la GED » (rien n'est supprimé).
- [ ] F1-M1.5 Contrôler les totaux, ressaisir les opérations faites depuis la sauvegarde.
- [ ] F1-M1.6 Synchronisation activée : nouvelle époque par le titulaire (E-35).

**Méthode 2 : sans l'application** (API hors service, PC serveur remplacé).
- [ ] F1-M2.1 PC remplacé : installer PostgreSQL (A10 à A13), recréer `pharma_app` avec `CREATEDB` (D-03). **Ne pas** lancer `schema.sql`.
- [ ] F1-M2.2 `Stop-Service "PharmaOfficine API"` (s'il existe).
- [ ] F1-M2.3 Restaurer (mot de passe de `postgres` demandé) :
  ```powershell
  .\scripts\restaurer_pharmacie.ps1 -Sauvegardes "E:\Sauvegardes\Pharmacie"
  ```
  Sauvegarde illisible : `-Fichier "<fichier>.dump"`. Base existante : arrêt, ou `-Remplacer` (renommée). `-SansGed` / `-SansBase` ; aussi
  `-Base`, `-DossierGed`, `-PgBin`, `-HostPg`, `-Port`. **Attendu** : nombre de ventes et date de la dernière.
- [ ] F1-M2.4 Sauvegarde plus ancienne que le logiciel : `scripts\mep\mettre_a_jour_base_officine.ps1` (B-03 à B-10). Repasser `163b` et `166b`
  si la base est neuve (A17).
- [ ] F1-M2.5 `Start-Service "PharmaOfficine API"`, puis `http://localhost:5080/api/healthcheck`.
- [ ] F1-M2.6 Ressaisir les opérations ; nouveau PC : ressaisir le mot de passe des notifications.
- [ ] F1-M2.6 bis Synchronisation activée : nouvelle époque (E-35).
- [ ] F1-M2.7 PC serveur changé : refaire A26, **restaurer l'autorité HTTPS** depuis le PFX du coffre (F2 bis). Sans PFX : nouvelle autorité,
  réinstaller `autorite-pharmacie.crt` partout (A29, A30, A35).

Garder deux disques externes en rotation, dont un hors de l'officine (A65).

### F2. PC serveur en panne : bascule sur le PC de secours — MEP § F2

Prérequis : chapitre D. Les opérations faites depuis la dernière copie sont perdues.
- [ ] F2.1 Constater la panne ; **vérifier d'abord le PC serveur** (allumé, réseau, service). S'il répond, ne pas basculer.
- [ ] F2.2 Sur le PC de secours, PowerShell administrateur, dossier `scripts` : simulation
  ```powershell
  .\basculer-pc-secours.ps1 -Copies "D:\CopieSecours" -Essai
  ```
- [ ] F2.3 Bascule (mot de passe de `postgres` demandé) :
  ```powershell
  .\basculer-pc-secours.ps1 -Copies "D:\CopieSecours"
  ```
  **Attendu** : fin sans erreur, adresse du PC de secours affichée (le dernier script rejoué doit être le dernier de la version installée).
- [ ] F2.4 Chaque poste : bouton du bandeau **« Basculer sur le 2e serveur (<adresse de secours>) sur ce poste »**, ou rubrique Serveur et PC
  de secours ; fermer et rouvrir. Certificat refusé : autorité absente (A30) ou adresse absente du certificat (D-06). Journal du poste :
  `%LOCALAPPDATA%\PharmaOfficine\liaison.log`.
  - Le poste n'a qu'**une** adresse de secours : bouton et messages disent toujours « 2e serveur », même pour le PC de secours
    (`BarreEtat.razor` l. 37-39, `LiaisonServeur.cs` l. 40-48) ; les lire comme « PC de secours ». « … joignable mais n'est pas encore
    basculé… » renvoie à `basculer-second-serveur.ps1` : ici, c'est F2.3 qui n'est pas terminée. Le message de fin de `basculer-pc-secours.ps1`
    cite un bouton « Utiliser le PC de secours », absent du poste : c'est le bouton ci-dessus (écart n° 13 : corrigé).
- [ ] F2.5 GED : `.\scripts\restaurer_pharmacie.ps1 -SansBase` (paramètre `-DossierGed` du PC de secours).
- [ ] F2.6 Ressaisir les opérations et le mot de passe des notifications.
- [ ] F2.6 bis Synchronisation activée : nouvelle époque (E-35).
- [ ] F2.7 Retour sur le PC serveur réparé : F4.

### F2 bis. Restaurer l'autorité HTTPS (PC serveur détruit ou remplacé) — MEP § F2 bis

Prérequis : clé du coffre (`F:\autorite-pharmacie.pfx`) et sa phrase de passe.
- [ ] F2b.1 Nouveau PC principal, essai :
  ```powershell
  .\scripts\certificat_https.ps1 -RestaurerAutorite -FichierSauvegardeAutorite F:\autorite-pharmacie.pfx -WhatIf
  ```
- [ ] F2b.2 Restauration :
  ```powershell
  .\scripts\certificat_https.ps1 -RestaurerAutorite -FichierSauvegardeAutorite F:\autorite-pharmacie.pfx
  ```
  **Attendu** : `Autorité importée dans Cert:\LocalMachine\My …` ; empreinte **identique** à celle de A23.
- [ ] F2b.3 Certificat du nouveau PC :
  ```powershell
  .\scripts\certificat_https.ps1 -DossierApi "C:\Program Files\PharmaOfficine\Api" -DossierExport E:\ -PareFeu
  ```
  `-NomsSupplementaires` / `-AdressesSupplementaires` si nom ou IP changés (mettre à jour `PHARMA_API`, A32). Redémarrer le service.
- [ ] F2b.4 Un poste ouvre `https://<nouveau serveur>:5443/api/healthcheck` : 200 sans avertissement.
- [ ] F2b.5 PFX ou phrase perdus : nouvelle autorité (A23 avec un nouveau `-FichierSauvegardeAutorite`), réinstallation sur tous les postes et
  téléphones (A29, A30, A34, A35), nouveaux certificats du 2e serveur et du PC de secours (C-11, C-12, D-06).

### F3. Bascule du 2e serveur — MEP § F3 ; procedure-deux-serveurs.md § 8 ; promotion-serveurs.md § 8 et § 9

**D'abord : la bascule a-t-elle eu lieu toute seule ?** (postes « Ce poste est passé sur le 2e serveur… », « Connecté · 2e serveur », alerte au
titulaire). Si oui : **aucun script de bascule**, seulement F3.2, F3.2 bis, F3.3 bis, F3.5, F3.6, F3.7.
**Bascule manuelle** (décision du titulaire) : alerte « promotion manuelle nécessaire », égalité, `166b` absent, promotion désactivée, postes
restés sur « Serveur injoignable ». **Ne jamais basculer** si l'alerte dit « API du serveur 1 arrêtée » (`ApiServeur1Muette`) : relancer son service.

- [ ] F3.1 Constater la panne ; vérifier le PC principal. Le titulaire décide.
- [ ] F3.2 Sur le principal, si possible : arrêter l'API et PostgreSQL, ou **débrancher son câble réseau**.
- [ ] F3.2 bis GED, dernière copie si le principal est encore joignable (2e serveur, administrateur), après l'arrêt de son API :
  ```powershell
  .\copier-ged.ps1 -Source \\<IP du principal>\PharmaGED$
  ```
  « REFUS : source injoignable » : continuer, noter les documents manquants (F4.3 ter).
- [ ] F3.3 Sur le 2e serveur, dossier `scripts\second-serveur` :
  ```powershell
  .\basculer-second-serveur.ps1 -AncienPrincipal <IP du principal> -Schema https -PortApi 5443 -Essai
  .\basculer-second-serveur.ps1 -AncienPrincipal <IP du principal> -Schema https -PortApi 5443
  ```
  Refus si l'ancien répond encore. Taper `BASCULER`. **Attendu** : heure de la dernière transaction reçue (ce qui suit est à ressaisir).
- [ ] F3.3 bis Arrêter la copie de la GED sur le nouveau principal :
  ```powershell
  .\copier-ged.ps1 -Source \\<IP de l'ancien principal>\PharmaGED$ -Desinstaller -Essai
  .\copier-ged.ps1 -Source \\<IP de l'ancien principal>\PharmaGED$ -Desinstaller
  ```
  Vérifier que `Ged__Dossier` de ce serveur est le dossier où la copie a écrit. **Attendu** : « Tâche … supprimée » (ou « rien à supprimer »).
- [ ] F3.4 Postes : basculent seuls (case cochée) ; sinon bouton **« Basculer sur le 2e serveur »**. Vente interrompue renvoyée, jamais deux fois.
- [ ] F3.5 Titulaire : écran « 2e serveur » : « Principal » ; dossiers Chifa et MDN, clé identique. CHIFA-Officine, lecteur de carte et Token sur
  ce PC : à confirmer (question posée à la CNAS).
- [ ] F3.6 L'ancien principal ne redevient **jamais** actif sans F4.
- [ ] F3.7 Synchronisation : si l'envoi se suspend, nouvelle époque (E-35).

### F4. Retour du serveur réparé — MEP § F4 ; procedure-deux-serveurs.md § 9

- [ ] F4.1 **Ne pas redémarrer l'API de l'ancien principal.**
- [ ] F4.2 Sur le **nouveau** principal (administrateur) :
  ```powershell
  .\preparer-principal.ps1 -AdresseSecond <IP de l'ancien> -AdressePrincipal <IP du nouveau> -Essai
  .\preparer-principal.ps1 -AdresseSecond <IP de l'ancien> -AdressePrincipal <IP du nouveau>
  .\ouvrir-port-base.ps1 -AdresseSecond <IP de l'ancien> -WhatIf
  .\ouvrir-port-base.ps1 -AdresseSecond <IP de l'ancien>
  .\partager-ged.ps1 -AdresseSecond <IP de l'ancien> -Essai
  .\partager-ged.ps1 -AdresseSecond <IP de l'ancien>
  ```
  Puis, si réplication synchrone : `SHOW synchronous_standby_names;` et `SHOW synchronous_commit;` ; s'ils ne valent pas `second_serveur` et
  `local`, poser les trois réglages de C-21 b.
- [ ] F4.3 Sur l'**ancien** principal :
  ```powershell
  .\remettre-en-second.ps1 -NouveauPrincipal <IP du nouveau> -Essai
  .\remettre-en-second.ps1 -NouveauPrincipal <IP du nouveau>
  ```
  Ancienne base **gardée** (`<dossier>_ancien_principal_<date>`) : à examiner avant toute suppression. (procedure-deux-serveurs.md § 9 : puis
  `SecondServeur__AutreServeur = <IP du nouveau>` et démarrer l'API en veille.)
- [ ] F4.3 bis GED vers l'ancien principal, **avant** toute remise en service :
  ```powershell
  .\copier-ged.ps1 -Source \\<IP du nouveau principal>\PharmaGED$ -Essai
  .\copier-ged.ps1 -Source \\<IP du nouveau principal>\PharmaGED$
  .\copier-ged.ps1 -Source \\<IP du nouveau principal>\PharmaGED$ -Installer -Essai
  .\copier-ged.ps1 -Source \\<IP du nouveau principal>\PharmaGED$ -Installer
  ```
  **Attendu** : « fichiers copiés », puis `LastTaskResult` = `0`. Une seule tâche de copie, toujours sur le serveur second.
- [ ] F4.3 ter (Facultatif) Documents d'avant la panne : sur l'ancien, `.\partager-ged.ps1 -AdresseSecond <IP du nouveau> -Essai` puis sans
  `-Essai` ; sur le nouveau, `.\copier-ged.ps1 -Source \\<IP de l'ancien>\PharmaGED$ -Essai` puis sans `-Essai` (sans `-Installer`) ; sur
  l'ancien ensuite : `.\partager-ged.ps1 -AdresseSecond <IP du nouveau> -Retirer`.
- [ ] F4.4 (Facultatif) Rebascule à un moment calme : arrêter l'API et PostgreSQL du principal actuel, `basculer-second-serveur.ps1` sur l'autre,
  `remettre-en-second.ps1` sur celui qu'on vient d'arrêter, remettre l'adresse des postes. GED : (a) dernière copie (F3.2 bis), (b)
  `-Desinstaller` (F3.3 bis), (c) `partager-ged.ps1 -Retirer` puis `copier-ged.ps1 -Installer` sur le nouveau second.

### F5. Révocation d'un certificat (PC volé, officine résiliée) — MEP § F5

- [ ] F5.1 Lister (serveur central, compte d'administration) : `& "<dossier>\Cloud\Pharma.Cloud.Api.exe" --lister`
- [ ] F5.2 Révoquer : `& "<dossier>\Cloud\Pharma.Cloud.Api.exe" --revoquer <empreinte> --motif "<motif>"` ; tout un serveur d'officine :
  `--revoquer-serveur <code officine> <serveur> --motif "<motif>"`.
- [ ] F5.3 Effet au prochain appel. Réactivation par un nouveau code d'activation. Certificat de 3 ans, renouvelé seul 60 jours avant.

### F6. Renouveler le certificat HTTPS (825 jours) — MEP § F6

Dates : `Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -like "*PharmaOfficine*" } | Select-Object Subject, NotAfter`.
Renouveler **au moins 2 mois avant** l'échéance. L'autorité dure 10 ans (son renouvellement = réinstallation sur tous les postes).
- [ ] F6.1 PC serveur principal :
  ```powershell
  .\scripts\certificat_https.ps1 -DossierApi "C:\Program Files\PharmaOfficine\Api" -DossierExport E:\ -PareFeu
  ```
  Refait seulement à moins d'un mois de l'échéance ou si les noms changent ; plus tôt : retirer `CN=PharmaOfficine Mobile`
  (`Get-ChildItem Cert:\LocalMachine\My | Where-Object Subject -eq 'CN=PharmaOfficine Mobile' | Remove-Item`) et relancer avec la **même liste
  de noms et adresses**. Redémarrer le service.
- [ ] F6.2 2e serveur et PC de secours : C-11 (nouveau nom de fichier, ex. `E:\certificat-second-AAAAMMJJ.pfx`) puis C-12. Redémarrer l'API si elle tourne.
- [ ] F6.3 Vérifier `https://<serveur>:5443/api/healthcheck` sans avertissement.
- [ ] F6.4 Détruire les PFX temporaires de la clé des postes ; garder le PFX de l'autorité au coffre.

### F7. Ancienne autorité « une par PC » trouvée — MEP § F7

- [ ] F7.1 Constater : `Get-ChildItem Cert:\LocalMachine\My, Cert:\LocalMachine\Root | Where-Object { $_.Subject -eq "CN=PharmaOfficine Autorite locale" } | Select-Object PSParentPath, Thumbprint, NotAfter`.
- [ ] F7.2 Le script ne la supprime jamais ; les deux coexistent le temps de la transition.
- [ ] F7.3 Faire (ou refaire) A23, installer la **nouvelle** autorité sur les postes et téléphones (A29, A30, A34, A35).
- [ ] F7.4 Quand tous les postes fonctionnent en HTTPS, retirer l'ancienne à la main (décision du titulaire, garder l'empreinte) : PC serveur
  `Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq "CN=PharmaOfficine Autorite locale" } | Remove-Item` ; chaque poste
  `certutil -delstore Root "PharmaOfficine Autorite locale"`.

### F8. Départ d'une officine (fin de contrat) — MEP § F8

**Où** : serveur central, compte d'administration. Scripts `021` et `027` passés.
- [ ] F8.1 Départ et export :
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --depart-officine <code officine> --sortie "<dossier>\officine-<code>.zip" --operateur "<nom de l'opérateur>" --email "<adresse e-mail du titulaire>"
  ```
  `--email` facultatif. Certificats révoqués, synchronisation refusée ensuite. Archive zip chiffrée AES-256 ; mot de passe affiché **une fois**
  (ne pas rediriger la sortie) ; archive et mot de passe **jamais par le même canal** ; archive remise **en main propre sur clé USB** ; ouverture
  avec 7-Zip **[À VÉRIFIER : ouverture constatée avec 7-Zip sur un poste réel (non faite : 7-Zip absent du poste de développement le
  2026-11-20 ; format vérifié par les tests automatiques).]** ; **accusé de réception** signé (empreinte SHA-256 de l'archive, contrôle
  `Get-FileHash -Algorithm SHA256 <fichier>.zip`, jamais le mot de passe sur l'accusé).
- [ ] F8.2 Ne rien effacer : jamais automatique, refusé avant la fin de la conservation (E-28).
- [ ] F8.3 Après la conservation, simulation : `& "<dossier>\Cloud\Pharma.Cloud.Api.exe" --effacer-officine <code officine>`
- [ ] F8.4 Effacement réel, **validation du titulaire obligatoire**, sauvegarde du central juste avant :
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --effacer-officine <code officine> --confirmer <code officine> --operateur "<nom de l'opérateur>"
  ```
  Restent : fiche, certificats révoqués, code d'activation, réglages de familles, journaux. **[À VÉRIFIER : durée de conservation des sauvegardes du central.]**

### Clé de l'éditeur perdue ou volée — procedure-cle-editeur.md § 6

| Situation | Que faire |
| --- | --- |
| Clé principale perdue | Signer avec la **clé de secours** (`--cle C:\MesAtlassian\cle-secours\cle-editeur.pem`) ; à la version suivante, générer une nouvelle clé |
| Clé principale volée | Signer avec la clé de secours ; à la version suivante, retirer la principale de `CleEditeur`, nouveaux codes pour les pharmacies concernées |
| Clé de secours perdue ou volée | Rien ne change pour les pharmacies ; la remplacer à la version suivante |
| Les deux perdues | Deux nouvelles clés, nouvelle version, nouveau code pour **chaque** pharmacie |

---

## Annexes

### A1. Dépannage — MEP § A1

| Symptôme | Cause probable | Action |
| --- | --- | --- |
| Le service se ferme aussitôt | `Démarrage refusé : l'API écoute en HTTP (non chiffré) sur le réseau` | A23 non faite, ou `appsettings.Https.json` absent |
| Le service se ferme : message sur `Jwt:Cle` | Variable absente ou de moins de 64 caractères | A21 |
| Service `Running`, mais rien ne répond | Pare-feu ou mauvais port | `netstat -ano \| findstr :5443`, puis règle `PharmaOfficine API (https 5443)` |
| Avertissement de certificat sur un poste | Autorité non installée, ou nom du serveur absent du certificat | A30 ; relancer A23 avec `-NomsSupplementaires` / `-AdressesSupplementaires` ; 2e serveur ou PC de secours : C-11 |
| « Le certificat du 2e serveur est refusé par ce poste… » | Autorité absente, ou adresse absente du certificat du PC de secours | A30, ou corriger l'adresse ; F2.4 |
| « Le 2e serveur ne répond pas… » | 2e serveur éteint, service arrêté (sur le 2e serveur, l'API tourne **en veille en permanence** : un service arrêté est une anomalie ; sur un PC de secours, il ne démarre qu'après F2), 5443 fermé, adresse erronée | Vérifier le 2e serveur (service `Running`), le pare-feu, l'adresse (écart n° 12 : corrigé) |
| « Le 2e serveur est joignable mais n'est pas encore basculé… » | API de veille qui répond, base pas encore promue (promotion automatique pas faite ou impossible) | 2e serveur : bascule manuelle **F3** ; PC de secours : F2.3 (écart n° 12 : corrigé) |
| « Le certificat du serveur est refusé par ce poste… » (principal) | Autorité absente ou `PHARMA_API` absente du certificat | A30 ; vérifier l'adresse |
| `Première exécution : … Préciser -FichierSauvegardeAutorite` | Autorité absente sur ce PC | A23 (nouvelle pharmacie) ou F2 bis |
| Avertissement d'heure au démarrage | Fuseau Windows incorrect | A12 |
| « Serveur injoignable » sur le poste | API arrêtée, ou `PHARMA_API` erronée | Service, puis variable |
| « base en lecture seule » | Base d'un 2e serveur non basculé | Normal : écrire sur le principal |
| Règle de pare-feu sur 5080 | Règle héritée | `Disable-NetFirewallRule -DisplayName '<nom>'` |
| Deux postes partagent la même caisse | Même `PHARMA_POSTE` | Nom unique par poste |
| Pas de promotion automatique | Voir promotion-serveurs.md § 8 : `droit_pg_promote_absent` (166b), `lignee_illisible` (163b), `promotion_automatique_desactivee`, `Egalite`, `ApiServeur1Muette` (ne pas basculer), `retrait_sans_reponse_fiable`, `pg_promote_echec` | F3, puis corriger la cause |

Journaux : `C:\ProgramData\PharmaOfficine\Journaux\pharma-api-AAAAMMJJ.log` ; central : `C:\ProgramData\PharmaOfficine\Journaux\Central\central-api-AAAAMMJJ.log`
et `central-ecrans-AAAAMMJJ.log`. Qui est principal : `https://<serveur>:5443/api/healthcheck` (`role` = `principal`, `veille` ou `ecarte`) ;
copie en lecture seule : `SELECT pg_is_in_recovery();` (`t` avant promotion). Détail : promotion-serveurs.md § 10.

### A2. Clé de scellement des journaux — MEP § A2

Créée au premier démarrage (`cle-integrite.dat`, chiffrée pour ce PC) ; sauvegardée avec le PC serveur (A26) ; pour un autre PC : export par le
titulaire (C-09) puis import (`--importer-cle`, D-08 ou C-13) ; contrôle `Pharma.Api.exe --empreinte-cle`. Clé USB au coffre, mot de passe à part.

### A3. Archives fiscales — MEP § A3

`C:\ProgramData\PharmaOfficine\archives-fiscales\AAAA\MM` : sauvegardé avec le PC serveur, conservé **6 ans au moins**
(`parametre_fiscal.conservation_annees`). Ne **jamais** purger le journal ni les sceaux.

### A4. Synchronisation — MEP § A4

Ne **jamais** copier le dossier `Synchronisation` d'un serveur à l'autre (clé DPAPI, illisible ailleurs).

### A5. Réglages du poste (`poste.json`) — MEP § A5

`C:\ProgramData\PharmaOfficine\poste.json` : `AdresseApi` (prioritaire sur `PHARMA_API` quand rempli), `AdresseApiSecours`,
`DelaiServeurInjoignableSecondes` (60, entre 10 et 3 600). Régler dans l'application, pas à la main.

### A6. Développement (rappel) — MEP § A6

À chaque nouveau script `0xx`, régénérer `database/init/schema.sql` et compléter `seed_referentiel.sql`. `scripts\paquet\installer.ps1` : base
d'essai fictive, jamais pour une officine réelle. API sous Linux (démonstration) : [demo-cloud.md](demo-cloud.md), sans objet sur Windows.

### A7. Recommencer depuis zéro (avant la mise en service seulement) — MEP § A 3

1. Sauvegarde (A64) si la base contient quoi que ce soit à garder.
2. Le script refuse une base qui existe. La supprimer est une **décision humaine**, jamais un script : avec `postgres`, en psql interactif
   (`& $psql -h localhost -U postgres -d postgres`), taper la commande de suppression de la base `<base>` en toute connaissance de cause, puis `\q`.
3. Reprendre à A15. (`database\init\clean.sql` ne sert plus à ce parcours.)

### A8. Écarts relevés entre les sources (traités le 2026-11-26)

Les numéros de ligne de la colonne « Où » sont ceux du relevé, avant correction. Colonne « État » : « corrigé le 2026-11-26 » avec la
preuve tirée du code ou des scripts, ou « à confirmer » avec la raison.

| N° | Écart | Où | État |
| --- | --- | --- | --- |
| 1 | Date « Mise à jour : 2026-11-16 » alors que le contenu va jusqu'au 2026-11-26 | mise-en-production.md l. 5 | **Corrigé le 2026-11-26** : 2026-11-26 |
| 2 | Windows minimum du PC serveur : « Windows 10 ou Windows Server 2016 » (étape 1.2) contre Windows 11 24H2 / Server 2025 exigés pour le chiffrement, Windows 10 « non pris en charge » | mise-en-production.md l. 216 et l. 254, contre l. 56-63 et l. 87 | **Corrigé le 2026-11-26** (mise-en-production.md « Matériel et Windows du PC serveur » et 1.2 ; ici § 0.4 et A02) : Windows 10 / Server 2016 = minimum pour le logiciel d'aujourd'hui ; Windows 11 24H2 / Server 2025 = exigé pour le chiffrement du **lot 9, pas encore codé** (chiffrement-data.md, « Statut » : « aucun script, aucun code » ; § 11 : « rien n'est lancé »). Le minimum « Windows 10 / Server 2016 » lui-même reste celui de la source, non mesuré (le logiciel n'a été essayé que sous Windows 11) |
| 3 | `111b` : présent dans `Api\database\init` publié (« rien à copier à part ») contre « pas dans le dossier des scripts publiés (comme 111b) : le prendre dans le dépôt » et `-Script111b "E:\111b_roles_integrite.sql"` « copié à l'étape 1 » | mise-en-production.md l. 328, 665 contre l. 676-678, 696 | **Corrigé le 2026-11-26** (mise-en-production.md B 1, B 4, B 8 ; ici B-01, B-04, B-08 ; scripts-base-de-donnees.md) : `Pharma.Api.csproj` copie `database\init\*.sql` (sauf `clean.sql`) dans `Api\database\init` et les scripts numérotés **à plat** dans `Api\database` (`101b` exclu) ; `mettre_a_jour_base_officine.ps1` prend `111b`, `163b`, `166b` par défaut dans `<DossierMigration>\init` (l. 176-187). Commande : `-DossierMigration "…\Api\database"`, sans `-Script111b`. Le chemin `Api\database\migration` des sources n'existe pas dans la publication |
| 4 | « Les scripts b (101b, 111b) se lancent à part (étapes 6 et 8) » contre `111b` lancé par le script avec `-SeparerRoles` | mise-en-production.md l. 655 contre l. 687 | **Corrigé le 2026-11-26** (mise-en-production.md intro du parcours B ; ici B-04) : `111b` lancé par `mettre_a_jour_base_officine.ps1` avec `-SeparerRoles` (en-tête, étape 6), `163b` et `166b` en fin de rejeu, seul `101b` à part (en-tête l. 32) |
| 5 | Nombre de scripts centraux : « 042 à ce jour », « 001 à 028 », « 001 à 029 », « 036 à ce jour » ; en-tête du script : « 040 à ce jour » ; dossier `database\cloud` : dernier fichier `044_ordre_activation_offre.sql` | mise-en-production.md l. 1045, 1055, 1060, 1318 ; `scripts\mep\creer_base_centrale.ps1` l. 3 et l. 16 | **Corrigé le 2026-11-26** : « 001 au dernier (044 à ce jour) » dans mise-en-production.md (E intro, 2.1, 10.2), scripts-base-de-donnees.md et le **commentaire** d'en-tête de `creer_base_centrale.ps1` (l. 3, 16, 60). Le code du script n'a **aucun** dernier numéro codé en dur (suite lue dans le dossier, contrôlée sans trou, l. 96-101) ; il vérifie seulement des scripts précis (001, 029, 030, 037 à 040), et son message d'erreur PostgreSQL 18 cite « scripts 020 à 031 » (texte du code, non modifié) |
| 6 | E 10.3 « Relancer les tests de l'étape 2.3 » : les tests sont à l'étape 2.2 (2.3 = contrôle indépendant) | mise-en-production.md l. 1319 | **Corrigé le 2026-11-26** : « étape 2.2 » (mise-en-production.md E 10.3 ; ici E-45) |
| 7 | Étape 13.0 (offre) numérotée après 13.1 et 13.2 | mise-en-production.md l. 630-632 | **Corrigé le 2026-11-26** : 13.0 remise avant 13.1 et 13.2 (ce manuel la suivait déjà : A45 avant A68 et A69) |
| 8 | Rappel d'échéance de l'offre : « 30 puis 7 jours avant, délai de grâce de 15 jours » contre « 30 jours avant, puis 15 jours de grâce » | offre-et-options.md l. 170 contre procedure-cle-editeur.md l. 89 et l. 129 | **Corrigé le 2026-11-26** (offre-et-options.md, procedure-cle-editeur.md, regles-metier.md « Offre de base et options : activation signée par l'éditeur », ici A45) : codé dans `src\Pharma.Application\Offre\Offre.cs`, classe `RappelsOffre` (constantes l. 99 : 30, 7, 15) ; alerte SMS ou e-mail à 30 jours, à 7 jours, puis au début des 15 jours de grâce, une fois chacune par date de fin (journal `offre_rappel`), seule l'étape la plus proche ; Mon offre prévient dès 30 jours. **Étendu le 2026-11-27** (« go rappels ») : 90, 60, 30, 15, 7 et 1 jour(s) avant, Mon offre dès 90 jours |
| 9 | « Super admin » : l'éditeur du logiciel (fixe le maximum de postes) contre le compte super admin créé sur le PC serveur pour la reprise et la restauration | offre-et-options.md l. 165-166 contre mise-en-production.md l. 560-573 | **Corrigé le 2026-11-26** : « l'éditeur » dans offre-et-options.md (besoin, questions, quotas, étapes, décisions annotées « appelé alors super admin ») ; « super admin » = seulement le compte de reprise et de restauration (rôle `super_admin`), note de vocabulaire ajoutée ; ici, tableau des rôles |
| 10 | Conservation des sauvegardes : « les 14 dernières par défaut » (F1) contre `Ged__RetentionSauvegardesJours` 7 jours et `$RetentionJours = 30` du script planifié (réglages peut-être distincts) | mise-en-production.md l. 1332 contre l. 151, 578 ; `scripts\sauvegarde_pharmacie.ps1` (configuration) | **Corrigé le 2026-11-26** : trois réglages **distincts**, écrits dans mise-en-production.md (variables, 11.1, F1) et ici (variables, A63, F1). (1) Disque externe : les N dernières, `parametre_sauvegarde.conservation DEFAULT 14` (schema.sql), 1 à 365, `Sauvegardes.cs` l. 125. (2) `GED\Sauvegardes` : jours, `retention_ged_jours` du titulaire sinon `Ged:RetentionSauvegardesJours` (7, `Program.cs` l. 46), `Sauvegardes.cs` l. 167. (3) Script planifié : `.dump` de plus de 30 jours supprimés (`$RetentionJours = 30`, `Purger-Anciennes`) |
| 11 | Commande `166b` avec le chemin du dépôt de développement `C:\MesAtlassian\pharmaofficine\database\init\…` alors que sur le PC serveur le fichier est dans `C:\Program Files\PharmaOfficine\Api\database\init` | promotion-serveurs.md l. 40-41 contre mise-en-production.md l. 173 | **Vérifié le 2026-11-26** : promotion-serveurs.md § 3 donne bien le chemin sur le PC serveur (`C:\Program Files\PharmaOfficine\Api\database\init\166b_droit_promotion_base.sql`), sous la commande |
| 12 | Dépannage : 2e serveur « il ne démarre qu'après la bascule » contre API en veille permanente (vague 2) ; « Lancer la bascule … (procédure F2) » alors que la bascule du 2e serveur est F3 | mise-en-production.md l. 1583-1584 contre l. 139 et § C 10 | **Corrigé le 2026-11-26** (mise-en-production.md « Dépannage » ; ici annexe « Dépannage ») : API du 2e serveur en veille permanente (promotion-serveurs.md, rôles) ; renvoi **F3** pour le 2e serveur, F2 étape 3 pour le PC de secours. Le texte du bandeau du poste dit encore « il ne démarre qu'après la bascule » (`src\Pharma.Shared\LiaisonServeur.cs` l. 44) : vrai pour le PC de secours seulement ; **texte du code non modifié**, à revoir par le développeur |
| 13 | F2 (PC de secours) emploie le bouton et les messages « 2e serveur » | mise-en-production.md l. 1383-1384 | **Corrigé le 2026-11-26** (mise-en-production.md F2 étape 4 ; ici F2.4) : c'est bien ce que voit l'utilisateur. Le poste n'a qu'une adresse de secours ; bouton « Basculer sur le 2e serveur (<adresse>) sur ce poste » (`BarreEtat.razor` l. 39) et messages « 2e serveur » (`LiaisonServeur.cs` l. 40-48), aussi pour le PC de secours. Restent dans le code, **non modifiés** : le 3e message renvoie à `basculer-second-serveur.ps1`, et `basculer-pc-secours.ps1` (l. 209) cite un bouton « Utiliser le PC de secours » qui n'existe pas sur le poste |
| 14 | Section « Postes autorisés » écrite pour « après la mise à jour », rien sur une base neuve | mise-en-production.md l. 1666-1668 (procedure-deux-serveurs.md § 6.1 la place dans la mise en place) | **Corrigé le 2026-11-26** (mise-en-production.md « Postes autorisés » ; ici A46, marqueur retiré) : `seed_referentiel.sql` l. 54 `INSERT INTO parametre_postes DEFAULT VALUES` ; `schema.sql` `mode DEFAULT 'transition'`, `duree_code_minutes DEFAULT 15` ; aucun poste dans `poste_autorise` ; maximum lu dans l'offre (`PostesAutorises.cs` l. 299-300), offre de base 2 postes (`Offre.cs` l. 98, `PostesBase = 2`) ; `maximum_postes` plus lu (script 172, l. 10) |
| 15 | Étape 3.1 lancée « depuis le dossier du dépôt » sur le PC serveur, alors que `scripts` n'y est copié qu'à l'étape 4.3 et l'API (avec `database\init`) à l'étape 4.2 | mise-en-production.md l. 293-297 contre l. 333-342 | **Corrigé le 2026-11-26** (mise-en-production.md § 3 et 3.1 ; ici A15) : faire 4.1 à 4.3 (A18 à A20) avant 3.1 (A15), puis `creer_base_officine.ps1 -Base <base> -DossierDepot "C:\Program Files\PharmaOfficine\Api"` : le script lit `<DossierDepot>\database\init` et, sans `database\migration`, les scripts à plat de `<DossierDepot>\database` (l. 91, 99-102). Le dossier de destination de `scripts` sur le PC serveur reste **à confirmer** (4.3 / A20, marqueur de la source) |
| 16 | Nom du menu : « Administration, Langue et pharmacie » contre « Administration › Pharmacie » | mise-en-production.md l. 544 contre l. 632 et procedure-cle-editeur.md l. 75-76 | **Corrigé le 2026-11-26** : libellé réel « Langue et pharmacie » (`NavAdmin.razor` l. 130, route `admin/pharmacie` ; onglet « Pharmacie et langue », l. 147), partout : mise-en-production.md 9.1 et 13.0, procedure-cle-editeur.md § 3, fonctionnalites.md, ici A43 et A45.1 |
| 17 | Checklist après mise à jour : « Scripts 112 et suivants lancés avec le rôle propriétaire » contre parcours B : toute la suite depuis `001` rejouée avec `SET ROLE` après `111b` | mise-en-production.md l. 1657 contre l. 679 et l. 692 | **Corrigé le 2026-11-26** (les deux checklists) : toute la suite depuis `001` (`mettre_a_jour_base_officine.ps1`, en-tête l. 8 et 18) |
| 18 | Fichier attendu sur la clé `148_sauvegarde_alerte_sans_disque_externe.sql` : exemple daté, le dernier script est `174` | mise-en-production.md l. 666 ; en-tête de `schema.sql` | **Corrigé le 2026-11-26** (mise-en-production.md B 1 ; ici B-01) : « le dernier script numéroté, `172_activation_offre.sql` à ce jour », dans `Api\database\` (à plat) |
| 19 | Description du script de l'étape 3.1 sans `163b` ni `166b`, alors que l'en-tête de `creer_base_officine.ps1` (étapes 6 bis et 6 ter) et le parcours C les citent | mise-en-production.md l. 300 contre `scripts\mep\creer_base_officine.ps1` et l. 760 | **Corrigé le 2026-11-26** : `163b` et `166b` ajoutés à la description de 3.1, lancés avec `postgres` même avec `-SansSeparationRoles` (`creer_base_officine.ps1` l. 201-210) |
| 20 | Code de transition « saisi par l'éditeur » contre saisie « connecté en titulaire » dans Mon offre | offre-et-options.md l. 176 contre procedure-cle-editeur.md l. 113 et mise-en-production.md l. 632 | **Corrigé le 2026-11-26** : « saisi dans Mon offre avec le compte du titulaire (par le titulaire, ou par l'éditeur à ses côtés) » dans offre-et-options.md, procedure-cle-editeur.md, regles-metier.md, mise-en-production.md 13.0 et ici A45.4 ; preuve : `.ExigerTitulaire()` sur `POST /offre/activation` (`ReglagesMutualisesEndpoints.cs` l. 107-112) |
| 21 | En-tête de `basculer-second-serveur.ps1` : « Décidée par une personne, jamais automatique » (vrai pour ce script manuel, mais la promotion est automatique par ailleurs) | `scripts\second-serveur\basculer-second-serveur.ps1` l. 3 | **Corrigé le 2026-11-26** (commentaire d'en-tête seulement) : ce script est la bascule **manuelle** (F3) ; la promotion automatique existe par ailleurs (promotion-serveurs.md). Restent à revoir, hors de ce lot : l'écran `AdminSecondServeur.razor` l. 60 (« Bascule : jamais automatique », texte du code) et la règle datée du 2026-11-16 de regles-metier.md (« Bascule décidée par une personne, jamais automatique ») |

**Écarts nouveaux relevés pendant ces corrections (à confirmer, textes du code non modifiés)** :

- `mettre_a_jour_base_officine.ps1` : l'aide de `-DossierMigration` (l. 75) cite encore `<livraison>\Api\database\migration`, et les
  messages d'erreur l. 188-190 disent que `111b`, `163b`, `166b` « ne sont pas dans le dossier publié » : faux depuis que
  `Pharma.Api.csproj` les copie dans `Api\database\init`. À corriger par le développeur.
- `basculer-pc-secours.ps1` l. 209 : bouton « Utiliser le PC de secours » inexistant sur le poste (écart n° 13).
- `LiaisonServeur.cs` l. 44 et 48 : messages pensés pour le 2e serveur d'avant la veille permanente (écarts n° 12 et 13).

---

## Checklist finale avant ouverture

Cocher tout avant le jour J. Lignes de la source (mise-en-production.md, « Checklist avant ouverture »), puis lignes ajoutées depuis les
autres sources (marquées « ajout »).

- [ ] Onduleur branché, PC serveur allumé en permanence, mise en veille désactivée
- [ ] PostgreSQL : service démarré automatiquement ; 5432 écouté seulement sur `127.0.0.1` (A13)
- [ ] Base : `schema.sql`, `083`, `seed_referentiel.sql`, `111b` faits, `objets_encore_a_pharma_app = 0`
- [ ] Variables de l'API réglées (`ConnectionStrings__Pharmacie`, `Jwt__Cle` ≥ 64, `Chifa__DossierExport`)
- [ ] Certificat HTTPS créé ; `appsettings.Https.json` présent ; règle de pare-feu 5443 active
- [ ] PFX de l'autorité au coffre (hors ligne, seconde copie ailleurs), phrase de passe notée à part ; empreinte de l'autorité notée
- [ ] Service `PharmaOfficine API` en démarrage automatique et `Running`
- [ ] `http://localhost:5080/api/healthcheck` répond ; le réseau ne répond pas sur 5080
- [ ] Tous les postes : autorité installée (empreinte vérifiée), `PHARMA_API` en HTTPS, `PHARMA_POSTE` unique, écran de connexion sans avertissement
- [ ] Téléphones : autorité installée (empreinte vérifiée), scanner mobile testé
- [ ] `cle-integrite.dat` et `super-admin.dat` sauvegardés sur le disque externe, avec le mot de passe de la clé de scellement au coffre
- [ ] Compte titulaire : mot de passe personnel choisi ; comptes des employés créés ; droits vérifiés
- [ ] Administration : identité, RC, NIF, NIS, AI, n° Chifa, organismes, paramétrage (caisse, alertes, emplacements), imprimantes **sur chaque poste**
- [ ] Nomenclature nationale importée, compteurs contrôlés
- [ ] Notifications : message d'essai reçu
- [ ] Compte super admin créé ; au moins un canal vérifié (téléphone ou e-mail)
- [ ] Sauvegarde de l'application réglée, première sauvegarde faite, fichier lisible (ou script planifié, mais **un seul** des deux)
- [ ] Test de restauration dans une base d'essai fait, base d'essai supprimée
- [ ] Reprise de données (si besoin) : totaux contrôlés et rapport signé par le pharmacien
- [ ] Synchronisation : **désactivée** (sauf décision du titulaire)
- [ ] Vérifications avant ouverture (A72 à A77) faites
- [ ] Essai réel sur un vrai poste, en HTTPS (connexion, vente, impression, scanner mobile)
- [ ] (ajout) Droits `163b` et `166b` passés sur la base de la pharmacie ; `has_function_privilege` → `t` (A17 ; promotion-serveurs.md § 3)
- [ ] (ajout) Offre : code d'activation saisi dans Mon offre, « Offre activée » ou « Période de transition », nombre de postes affiché (A45)
- [ ] (ajout) Postes autorisés : premier poste par `--autoriser-poste`, chaque PC autorisé avec son code (A46)
- [ ] (ajout) Taux de TVA des catégories validés par le comptable et réglés (A54)
- [ ] (ajout) Contenances manquantes complétées si la nomenclature ne les donne pas (A53)
- [ ] (ajout) Sans 2e serveur : deux disques externes en rotation, un hors de l'officine (A65)
- [ ] (ajout) Avec 2e serveur : contrôles C-26 verts et **essai de bascule réel fait** (C-27), remise en place F4 faite
- [ ] (ajout) Avec PC de secours : première copie vérifiée (D-11), adresse de secours réglée sur chaque poste (D-13)

## Checklist après chaque mise à jour

Lignes de la source (mise-en-production.md, « Checklist après chaque mise à jour »), puis ajouts.

- [ ] Sauvegarde faite avant la mise à jour, et lisible (`pg_restore --list`)
- [ ] Serveur central mis à jour d'abord (si l'officine est synchronisée)
- [ ] Service arrêté puis redémarré, `http://localhost:5080/api/healthcheck` répond
- [ ] Scripts `database\migration` appliqués dans l'ordre, sans erreur ; pas de `schema.sql` ni de `clean.sql`
- [ ] Toute la suite des scripts, depuis `001`, rejouée par `mettre_a_jour_base_officine.ps1` ; après `111b`, avec `postgres` et `SET ROLE pharma_proprietaire`, jamais avec `pharma_app` (écart n° 17 : corrigé)
- [ ] API republiée et remplacée ; `appsettings.Https.json` toujours présent
- [ ] Postes republiés ; chaque poste ouvre l'écran de connexion
- [ ] Nomenclature réimportée si elle a changé
- [ ] Une vente test, une clôture, « Vérifier maintenant » de l'intégrité des journaux
- [ ] Si 2e serveur : `verifier-second-serveur.ps1` lancé sur le 2e serveur, versions identiques
- [ ] Si .NET a reçu un correctif de sécurité : republier l'API et les postes avec le SDK à jour
- [ ] Journal du serveur relu : aucune erreur au démarrage
- [ ] (ajout) `101b` : passé seulement après le « go » du titulaire, procédure B-08 suivie (ou rien à faire)
- [ ] (ajout) Code de transition de l'offre saisi (B-15)
- [ ] (ajout) Postes autorisés après la mise à jour : codes de tous les postes générés et saisis le jour même (B-16)
- [ ] (ajout) Avec 2e serveur : `163b` et `166b` présents (`has_function_privilege` → `t`)

---

## Table de correspondance

Toutes les étapes de [mise-en-production.md](mise-en-production.md) et leur place dans ce manuel.

### « Avant de commencer »

| Section source | Manuel |
| --- | --- |
| Rôles | § 0.2 |
| Les PC et les lieux | § 0.3 |
| Matériel et Windows du PC serveur (versions, plancher, configuration, licence) | § 0.4 ; `Enable-TlsEccCurve` : A14 |
| Conventions de commandes | § 0.11, A09 |
| Décisions déjà prises | § 0.12 |
| Ports | § 0.6 |
| Services Windows | § 0.7 |
| Variables d'environnement de l'API, côté postes | § 0.8 |
| Fichiers et dossiers à connaître | § 0.9 |

### Parcours A (installation neuve)

| Source | Manuel | Source | Manuel | Source | Manuel |
| --- | --- | --- | --- | --- | --- |
| Calendrier, critère | § 0.13 | 4.6 (0 à 6) | A23 (A23.0 à A23.6) | 9.8 | A52 |
| 1.1 | A01 | 4.7 | A24 | 9.9 | A55 |
| 1.2 | A02 | 4.8 | A25 | 9.10 | A56 |
| 1.3 | A03 | 4.9 | A26 | 9.11 | A57 |
| 1.4 | A04 | 5.1 | A27 | 9.12 | A58 |
| 1.5 | A05 | 5.2 | A28 | 10.1 | A59 |
| 1.6 | A06 | 5.3 | A29 | 10.2 | A60 |
| 1.7 | A07 | 5.4 | A30 | 10.3 | A61 |
| 1.8 | A08 | 5.5 | A31 | 10.4 | A62 |
| 2.1 | A10 | 5.6 | A32 | 11.1 | A63 |
| 2.2 | A11 (et D-03) | 5.7 | A33 | 11.2 | A64 |
| 2.3 | A12 | 6.1 | A34 | 11.2 bis | A65 |
| 2.4 | A13 | 6.2 | A35 | 11.3 (1 à 4) | A66 |
| 3.1 | A15 | 6.3 | A36 | 11.4 | A67 |
| 3.2 | A16 | 7.1 | A37 | 12 (intro) | A71, chapitre R |
| Recommencer depuis zéro | annexe A7 | 7.2 | A38 | 12.1 à 12.7 | R-01 à R-07 |
| 4.1 | A18 | 7.3 | A39 | 13.0 | A45 |
| 4.2 | A19 | 7.4 | A40 | 13.1 | A68 |
| 4.3 | A20 | 8.1 | A41 | 13.2 | A69, chapitre C |
| 4.4 | A21 | 8.2 | A42 | 13.3 | A70, chapitre E |
| 4.5 | A22 | 9.1 | A43 | 14.1 à 14.6 | A72 à A77 |
| | | 9.2 | A44 | 15.1 | A78 |
| | | 9.3 à 9.7 | A47 à A51 | 15.2 | A79 |

Ajouts d'autres sources dans la partie A : A17 (promotion-serveurs.md § 3, en-têtes `163b` / `166b`), A45 (procedure-cle-editeur.md,
offre-et-options.md), A46 (MEP « Postes autorisés »), A53 (regles-metier.md), A54 (a-faire.md, script 171).

### Parcours B (mises à jour)

| Source | Manuel |
| --- | --- |
| Intro | Chapitre B (intro) |
| 1 à 7 | B-01 à B-07 |
| 8 (procédure 101b, points 1 à 9) | B-08 (1 à 9) |
| 9 à 14 | B-09 à B-14 |
| Postes autorisés (1 à 4) | B-16 (et A46) |
| offre-et-options.md « Mise en service » point 3 | B-15 |

### Parcours C (2e serveur)

| Source | Manuel | Source | Manuel |
| --- | --- | --- | --- |
| Intro, droits 163b / 166b, tableau des scripts | Chapitre C (intro), C-03 | 9 | C-13 |
| 1 | C-01 | 10 | C-14 |
| 2 | C-02 | 11 | C-15 |
| 3 | C-04 | 12 | C-16 |
| 4 | C-05 | 12 bis | C-17 |
| 4 bis | C-06 | 12 ter | C-18 |
| 5 | C-07 | 12 ter bis | C-19 |
| 6 | C-09 | 12 quater | C-20 |
| 7 (dont empreinte de la clé des jetons) | C-10, C-08 | 13 | C-23 |
| 8a | C-11 | 14 | C-25 |
| 8b | C-12 | 15 (a, b) | C-21 |
| | | 16 | C-27 |

procedure-deux-serveurs.md : § 1 → C-01 ; § 2.1 → C-03 ; § 2.2 → C-04, C-05 ; § 2.3 → C-06 ; § 2.4 → C-07 ; § 2.5 → C-08 ; § 2.6 → C-09 ;
§ 3.1 à 3.6 → C-10 à C-16 ; § 4.1 à 4.4 → C-17 à C-20 ; § 5 → C-21 ; § 6.1, 6.1 bis → C-22, A46 ; § 6.2 → C-23 ; § 6.3 → C-24 ; § 7 → C-26 ;
§ 7 bis → C-27 ; § 8 → F3 ; § 9 → F4 ; § 10 → § 0.10 ; § 11 → fin du chapitre C.

### Parcours D (PC de secours)

| Source | Manuel |
| --- | --- |
| 1 à 13 | D-01 à D-13 (6a et 6b dans D-06) |

### Parcours E (serveur central)

| Source | Manuel | Source | Manuel |
| --- | --- | --- | --- |
| 1.1, 1.2, 1.3 | E-01, E-02, E-03 | 7.1 à 7.8 | E-21 à E-28 |
| 2.1, 2.2, 2.3, 2.4 | E-04, E-05, E-06, E-07 | 8.1 à 8.6 | E-29 à E-34 |
| 3.1, 3.2, 3.3, 3.4 | E-08, E-09, E-10, E-11 | Procédure d'activation, chargement complet, supervision et purge, lots manquants | E5 « À savoir » |
| 4.1, 4.2 | E-12, E-13 | Base revenue en arrière (deux actions du titulaire) | E-35, E-36 |
| 5.1, 5.2 | E-14, E-15 | 9.1 à 9.6 | E-37 à E-42 |
| 6.1, 6.3, 6.4, 6.5, 6.2 | E-16, E-17, E-18, E-19, E-20 | 10.1 à 10.4 | E-43 à E-46 |

### Procédures occasionnelles et annexes

| Source | Manuel |
| --- | --- |
| F1 méthode 1, étapes 1 à 6 | F1-M1.1 à F1-M1.6 |
| F1 méthode 2, étapes 1 à 7 (dont 6 bis) | F1-M2.1 à F1-M2.7 (dont 6 bis) |
| F2, étapes 1 à 7 (dont 6 bis) | F2.1 à F2.7 (dont 6 bis) |
| F2 bis, étapes 1 à 5 | F2b.1 à F2b.5 |
| F3, étapes 1 à 7 (dont 2 bis, 3 bis) | F3.1 à F3.7 (dont 2 bis, 3 bis) |
| F4, étapes 1 à 4 (dont 3 bis, 3 ter) | F4.1 à F4.4 (dont 3 bis, 3 ter) |
| F5, étapes 1 à 3 | F5.1 à F5.3 |
| F6, étapes 1 à 4 | F6.1 à F6.4 |
| F7, étapes 1 à 4 | F7.1 à F7.4 |
| F8, étapes 1 à 4 | F8.1 à F8.4 |
| Annexes A1 à A6 | Annexes A1 à A6 |
| Checklist avant ouverture (22 lignes) | Checklist finale avant ouverture (22 lignes de la source + ajouts) |
| Checklist après chaque mise à jour (12 lignes) | Checklist après chaque mise à jour (12 lignes de la source + ajouts) |
| Postes autorisés (1 à 4) | A46.1 à A46.3 et noms / remplacement / révocation dans A46 ; B-16 |
