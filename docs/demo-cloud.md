# Démo cloud : API sous Linux

Décision du titulaire (2026-11-19, « go » reçu le 2026-11-20) : préparer une **démonstration cloud** de l'API (Dockerfile,
docker-compose local, mode démo). Ce document décrit l'**étape 0** : rendre l'API (`src/Pharma.Api`) utilisable sous Linux.
Le poste (`src/Pharma.Desktop`, WPF) reste **Windows seulement**.

**Sous Windows, rien ne change** sans réglage : mêmes dossiers (`C:\ProgramData\PharmaOfficine\…`), même chiffrement DPAPI machine
(mêmes fichiers, même entropie : les fichiers déjà protégés se relisent comme avant), mêmes polices (celles de Windows), même
recherche de `pg_dump.exe`, `pg_restore.exe` et `psql.exe`. Seule nouveauté visible : une ligne au journal du serveur au démarrage,
« Secrets locaux protégés par DPAPI machine ».

## État des étapes

| Étape | Contenu | État |
|---|---|---|
| 0 | API utilisable sous Linux (ce document) | **Faite le 2026-11-20** ; build et tests sous Windows ; **aucun essai réel sous Linux encore** |
| 1 | Dockerfile de l'API + docker-compose local (API + PostgreSQL + volume des données) | **Fichiers écrits le 2026-11-20** (tests de contenu) ; **jamais construit ni démarré** (Docker absent du poste) : voir [Étape 1](#étape-1--image-de-lapi-et-docker-compose-local) |
| 2 | Mode démo (données de démonstration, comptes d'essai, remise à zéro) | À faire |
| 3 | Tests (démarrage du conteneur, santé, PDF, sauvegarde/restauration dans le conteneur) | À faire |
| 4 | Documentation d'exploitation de la démo | À faire |

## Réglages ajoutés

Tous facultatifs. Sous Windows, sans réglage, les valeurs d'avant s'appliquent. Variables d'environnement : `Section__Cle`.

| Réglage | Défaut Windows | Défaut hors Windows | Rôle |
|---|---|---|---|
| `Donnees:Racine` | `C:\ProgramData\PharmaOfficine` | `/var/lib/pharmaofficine` | Racine des chemins par défaut ci-dessous |
| `Protection:Mode` | `auto` = DPAPI machine | `auto` = DataProtection | `auto`, `dpapi` (Windows seulement), `dataprotection` |
| `Protection:DossierCles` | — | `<racine>/cles-protection` | Clés DataProtection (**volume persistant**) |
| `Protection:CertificatFichier` | — | aucun | PFX qui chiffre les clés DataProtection au repos (recommandé) |
| `Protection:CertificatMotDePasse` | — | aucun | Mot de passe du PFX : **variable d'environnement ou coffre uniquement**, jamais dans un fichier du dépôt |
| `Fiscal:DossierArchives` | `<racine>\archives-fiscales` | `<racine>/archives-fiscales` | Rapports fiscaux scellés (à conserver 6 ans) |
| `CopieSecours:Fichier` | `<racine>\copie-secours.json` | `<racine>/copie-secours.json` | Réglages et état de la copie de secours |
| `ProxysConnus` | vide (désactivé) | vide (désactivé) | Proxys inverses dont les en-têtes `X-Forwarded-For` / `X-Forwarded-Proto` sont acceptés (IP ou réseau CIDR ; liste `ProxysConnus__0`, `ProxysConnus__1`… ou une valeur séparée par `;`) |

Chemins déjà réglables, dont le défaut suit maintenant la racine : `SuperAdmin:Fichier` (`super-admin.dat`), `Integrite:FichierCle`
(`cle-integrite.dat`), `Synchronisation:Dossier` (`Synchronisation`), `Ged:Dossier` (`GED`), `Journaux:Dossier` (`Journaux`).

**Hors Windows, un chemin Windows réglé est ignoré** (`C:\…`, `D:\…`, `\\serveur\…`) : le défaut sous la racine s'applique.
C'est le cas de `Ged:Dossier` et de `Chifa:DossierExport` d'`appsettings.json`. Concerne `Ged:Dossier`, `Ordonnances:Dossier`,
`Chifa:DossierExport` et `Journaux:Dossier`.

## Dossiers à prévoir sous Linux (volume)

Sous `/var/lib/pharmaofficine` (ou `Donnees:Racine`), accessible en écriture au compte qui exécute l'API :

| Chemin | Contenu | Perte |
|---|---|---|
| `cles-protection/` | Clés DataProtection | **Secrets illisibles** (clé de scellement, super admin, clé de synchronisation, mot de passe SMTP) |
| `cle-integrite.dat` | Clé de scellement des journaux | Anciens sceaux « faits avec une autre clé » |
| `super-admin.dat` | Compte super admin | À recréer (`--super-admin`) |
| `Synchronisation/` | Clé et certificat de synchronisation | Nouvelle activation |
| `archives-fiscales/` | Rapports scellés | Archives fiscales perdues |
| `GED/` | Documents | Documents perdus |
| `Journaux/` | Journal du serveur | Sans gravité |
| `copie-secours.json` | Réglages de la copie de secours | À ressaisir |

Droits posés par l'API hors Windows : fichiers de secrets en `600`, dossiers `Synchronisation` et `cles-protection` en `700`
(sauf si le dossier appartient à un autre compte, par exemple un volume monté : ses droits restent ceux du montage).

## Protection des secrets hors Windows

- ASP.NET Core DataProtection (fourni par le framework ASP.NET Core, **aucun paquet ajouté**), nom d'application `PharmaOfficine`,
  un usage par secret (mêmes libellés que l'entropie DPAPI : `PharmaOfficine.Integrite`, `PharmaOfficine.SuperAdmin`,
  `PharmaOfficine.Synchronisation`, `PharmaOfficine.Notifications.Smtp`).
- Sans certificat, les clés sont écrites **en clair** dans `cles-protection/` : seuls les droits du dossier les protègent.
  Pour une démo publique, fournir un certificat (`Protection:CertificatFichier` + mot de passe en variable d'environnement).
- Un secret protégé sous Windows (DPAPI) **n'est pas lisible** sous Linux, et inversement : une base restaurée d'un PC Windows
  demande de ressaisir le mot de passe SMTP (message déjà prévu) ; la clé de scellement, le super admin et la synchronisation
  se recréent sur le serveur Linux. Une clé illisible n'est **jamais remplacée en silence** (règle inchangée).
- Forcer `Protection:Mode=dataprotection` **sous Windows sur un serveur existant** rend ses fichiers DPAPI illisibles : à ne pas
  faire hors essai.
- Implémentation : `src/Pharma.Api/Securite/ProtectionDataProtection.cs` (l'Infrastructure n'a pas de dépendance à ASP.NET Core) ;
  abstraction et DPAPI : `src/Pharma.Infrastructure/Securite/ProtectionDonnees.cs`.

## Outils PostgreSQL

Sauvegarde (`pg_dump`), vérification et restauration (`pg_restore`, `psql`) :

- Windows (inchangé) : `nom.exe` à côté de `Sauvegardes:PgDump`, sinon `C:\Program Files\PostgreSQL\<version>\bin` (la plus récente).
- Hors Windows : `nom` sans `.exe` à côté de `Sauvegardes:PgDump`, sinon `/usr/lib/postgresql/<version>/bin` (la plus récente),
  sinon le `PATH`, `/usr/bin`, `/usr/local/bin`. L'image devra contenir le client PostgreSQL de la **même version majeure ou plus
  récente** que le serveur (à vérifier à l'étape 1).
- Code : `src/Pharma.Infrastructure/Sauvegardes/OutilsPostgres.cs`.

## Polices des PDF

- Windows (inchangé) : polices installées (Arial).
- Hors Windows : **Liberation Sans 2.1.5** embarquée dans l'assembly Infrastructure (normal, gras, italique, gras italique ;
  ~1,6 Mo), métriques identiques à Arial (mises en page inchangées). **Licence SIL Open Font License 1.1**
  (`src/Pharma.Infrastructure/Impression/Ressources/Polices/OFL-LiberationSans.txt`). Source : publication 2.1.5 du projet
  liberation-fonts (archive `liberation-fonts-ttf-2.1.5.tar.gz`, SHA-256
  `7191c669bf38899f73a2094ed00f7b800553364f90e2637010a69c0e268f25d0`).
- Limite : Liberation Sans n'a **pas l'arabe** ; un texte arabe dans un PDF (nom de pharmacie, produit) sortirait sans glyphes
  hors Windows. Non constaté, à vérifier avec des données réelles.
- Les polices Poppins/Inter du dépôt (OFL) ne sont qu'en WOFF2 (sous-ensembles web) : non utilisables par PDFsharp.
- Code : `src/Pharma.Infrastructure/Impression/PolicesPdf.cs`.

## Proxy inverse

`ProxysConnus` vide (défaut) : aucun en-tête de proxy n'est lu, comportement inchangé. Renseigné : `X-Forwarded-For` et
`X-Forwarded-Proto` acceptés **des seuls proxys déclarés** (les valeurs par défaut d'ASP.NET Core, boucle locale, sont retirées ;
un proxy sur la même machine se déclare `127.0.0.1`). L'adresse rétablie sert à la limite de débit des connexions et aux contrôles
« poste local ». Une valeur illisible arrête le démarrage. Derrière un proxy qui termine TLS, l'API écoute en HTTP : régler
`AutoriserHttpReseau=true` (refus de démarrage sinon, règle inchangée) ou limiter l'écoute au réseau interne du conteneur.

## Reste Windows seulement (constaté dans le code)

- Le **poste** (`Pharma.Desktop`, WPF).
- Exécution en **service Windows** (`UseWindowsService` : sans effet hors service Windows ; non essayé sous Linux).
- **Reprise** depuis une ancienne base : liste des sources ODBC lue dans le registre Windows (vide hors Windows) ; une connexion
  ODBC sous Linux demanderait unixODBC et un pilote (non essayé).
- **SMS par modem GSM** (port série) : `System.IO.Ports` existe sous Linux (`/dev/ttyUSB0`…), mais non essayé ; sans effet au
  démarrage (port ouvert seulement à l'envoi).
- Scripts PowerShell de `scripts/` (certificat HTTPS, sauvegardes planifiées, copie GED…).

## Points non vérifiés (à constater à l'étape 1)

- **Aucun démarrage réel sous Linux** n'a été fait : seuls le build et les tests sous Windows (avec les chemins Linux simulés).
- **Culture fr-FR** : l'API formate en `fr-FR` ; il faut ICU dans l'image (`libicu`), sinon .NET refuse de démarrer ou passe en
  culture invariante si `InvariantGlobalization` est activé (à ne pas activer). Sous Windows 10/11, .NET utilise aussi ICU :
  formats attendus identiques (non comparé).
- **Fuseau** : l'officine est en `Africa/Algiers` ; dans le conteneur, régler `TZ=Africa/Algiers` (données tz présentes dans
  l'image à vérifier). L'avertissement de fuseau au démarrage n'est qu'un avertissement ; sous Linux, `Africa/Algiers` a eu une
  heure d'été dans le passé : l'avertissement pourrait apparaître à tort (non vérifié).
- **Tâches de fond** lancées au démarrage (synchronisation, alertes, notifications, sauvegardes, surveillance, intégrité,
  scellement du registre, clôtures fiscales, sérialisation, 2e serveur) : toutes interceptent leurs erreurs et les inscrivent au
  journal ; aucune ne fait tomber l'API d'après le code (`SurveillancePeriodique` s'appuie sur « Surveiller ne lève jamais »,
  non revérifié). La sauvegarde automatique cherchera `pg_dump` et un disque réglé : échec noté au journal tant que l'image n'a
  pas le client PostgreSQL.
- `AddAuthentication` d'ASP.NET Core enregistre aussi sa propre DataProtection (clés par défaut dans le profil du compte ;
  comportement du framework, non vérifié sous Linux) ; elle n'est pas utilisée par les jetons JWT de l'API. Si elle l'était un jour, la régler sur le même volume (non nécessaire à ce jour).

## Étape 1 : image de l'API et docker-compose local

Fichiers écrits le 2026-11-20. **Docker n'est pas installé sur le poste de développement** : rien n'a été construit ni démarré.
Seuls le contenu des fichiers (tests `DemoCloudConteneurTests`), le build et les tests de la solution sous Windows sont vérifiés.
Tout ce qui dépend de l'exécution est listé dans « À vérifier au premier démarrage ».

| Fichier | Rôle |
|---|---|
| `src/Pharma.Api/Dockerfile` | Image de l'API en deux étapes : compilation (`dotnet/sdk:10.0`), exécution (`dotnet/aspnet:10.0`, avec ICU) |
| `.dockerignore` | Contexte de construction : seuls les projets de l'API et `database/migration` (ni poste, ni tests, ni docs, ni secrets) |
| `deploy/demo/docker-compose.yml` | Services `db` (postgres:18) et `api` ; volumes `db-donnees` et `api-donnees` ; aucun service central |
| `deploy/demo/.env.example` | Modèle des variables (valeurs factices `A-REMPLACER…`) ; à copier en `deploy/demo/.env` (ignoré par git) |
| `deploy/demo/init/10_base_officine.sh` | Création de la base au **premier** démarrage de `db` (fins de ligne LF, `set -euo pipefail`) |

### Image de l'API

- Restauration NuGet d'abord (`NuGet.Config`, `global.json`, `Directory.*.props`, csproj de Domain, Shared, Application,
  Infrastructure et Api), puis copie des sources, puis `dotnet publish -c Release`. `appsettings.Development.json` et
  `appsettings.Https.json` : exclus du contexte et retirés de la publication.
- Exécution : `tzdata`, `curl` (HEALTHCHECK), **client PostgreSQL 18** (`postgresql-client-18`) pris au dépôt apt officiel
  PostgreSQL (PGDG, `apt.postgresql.org`, clé `ACCC4CF8.asc`), la distribution de l'image ne le fournissant pas forcément ;
  la version majeure se règle par `--build-arg PG_MAJEURE=…` et doit rester **supérieure ou égale à celle du serveur**.
- Réglages posés : `TZ=Africa/Algiers`, `ASPNETCORE_URLS` et `Urls` = `http://+:8080` (le second, lu après `appsettings.json`,
  doit l'emporter sur son `Urls`), `Donnees__Racine=/var/lib/pharmaofficine`,
  `Sauvegardes__PgDump=/usr/lib/postgresql/18/bin/pg_dump`.
- Compte `app` (non administrateur, fourni par les images .NET) ; `/var/lib/pharmaofficine` créé et attribué à `app`.
- `HEALTHCHECK` : `curl -fsS http://localhost:8080/api/healthcheck` ; `EXPOSE 8080`.
- Aucun secret dans l'image : `ConnectionStrings__Pharmacie` et `Jwt__Cle` sont donnés au lancement.

### Base de démonstration (`db`)

Au premier démarrage (volume `db-donnees` vide), le script d'init, en superutilisateur puis avec le compte `pharma_app` :

1. crée le rôle `pharma_app` (LOGIN, CREATEDB), mot de passe lu dans le secret `pharma_app_mdp` ;
2. crée `pharmacie_demo` (OWNER `pharma_app`, UTF8, template0 : le propriétaire crée `pg_trgm` et `pgcrypto`) ;
3. passe **toutes les migrations** `database/migration/NNN_*.sql`, dans l'ordre des numéros, sans les scripts « b »
   (`101b`, `111b`), comme `scripts/initialiser_base_locale.ps1` pour les bases de développement, avec `ON_ERROR_STOP` et
   `PGOPTIONS=-c pharma.reglage_central=on` ;
4. passe **toutes les données fictives** `database/data/*.sql`, par ordre de nom (`905_jeu_de_test.sql` ne fait rien tant que
   la nomenclature nationale n'est pas importée) ;
5. vérifie le fuseau de la base (`Africa/Algiers`), la présence des données fictives (réception `BL-DEMO-1`) et des trois
   comptes de démonstration ; sinon l'init **échoue** (rien n'est caché).

**Choix A du titulaire (2026-11-20)** : la base de démonstration est construite comme les bases de développement, et non par
la voie de la mise en production (`database/init/schema.sql` + `seed_referentiel.sql`). Raison constatée : `seed_referentiel.sql`
crée le compte `titulaire` (mot de passe provisoire) et `900_donnees_demo.sql` s'arrête alors sans rien faire, la base n'aurait
eu aucune donnée fictive. Séquence validée le 2026-11-20 sur une base jetable locale (supprimée ensuite) : 152 migrations puis
900 à 905, trois comptes présents, `BL-DEMO-1` présente, fuseau `Africa/Algiers`. Ce rejeu complet a révélé un défaut du
script `149` sur une base **neuve** (garde `::regprocedure` évaluée sur une fonction encore absente), corrigé (`to_regprocedure`)
et repassé sans effet sur `pharmacie_dev` et `pharmacie_test`.

**Non fait à cette étape** : `111b` (séparation des rôles : journaux **non protégés** dans cette base de démonstration) et
l'import de la nomenclature nationale, donc le grand jeu de test `905` (étape 2).

Comptes de démonstration : `titulaire`, `assistant`, `vendeur`, mot de passe `demo` (données fictives, jamais en production).

### Prérequis (poste Windows du titulaire)

Manuel détaillé pas à pas (virtualisation, WSL 2, Docker Desktop, licence, réglages, problèmes fréquents, désinstallation) :
[installation-docker.md](installation-docker.md).

1. Windows 10/11 64 bits avec la virtualisation activée (BIOS/UEFI).
2. **WSL 2** : dans un PowerShell administrateur, `wsl --install`, puis redémarrer.
3. **Docker Desktop** pour Windows (site officiel de Docker), moteur WSL 2 (« Use the WSL 2 based engine ») ; démarrer Docker Desktop.
4. Vérifier : `docker version` et `docker compose version` (Compose v2 ; la source de secret `environment:` du fichier compose
   demande une version récente de Compose, à constater).

### Lancement

Depuis la racine du dépôt (PowerShell) :

```powershell
# 1. Variables : copier le modèle, puis remplacer CHAQUE valeur A-REMPLACER (générateur indiqué dans le fichier)
Copy-Item deploy\demo\.env.example deploy\demo\.env
notepad deploy\demo\.env

# 2. Port 5080 : c'est aussi celui de l'API de développement ; l'arrêter avant, ou régler PORT_API_DEMO dans deploy\demo\.env
netstat -ano | findstr :5080

# 3. Construction et démarrage
docker compose -f deploy/demo/docker-compose.yml --env-file deploy/demo/.env up -d --build

# 4. Suivi
docker compose -f deploy/demo/docker-compose.yml --env-file deploy/demo/.env ps
docker compose -f deploy/demo/docker-compose.yml --env-file deploy/demo/.env logs db
docker compose -f deploy/demo/docker-compose.yml --env-file deploy/demo/.env logs api
curl.exe http://127.0.0.1:5080/api/healthcheck

# Arrêt (les volumes, donc la base et les données de l'API, sont gardés)
docker compose -f deploy/demo/docker-compose.yml --env-file deploy/demo/.env down
```

`docker compose … down -v` **supprime les volumes** (base de démonstration et données de l'API, dont les clés) : c'est le seul
moyen de relancer le script d'init ; à ne faire qu'en connaissance de cause.

Sécurité de cette étape : l'API écoute en HTTP dans le conteneur ; `AutoriserHttpReseau=true` n'est réglé que parce que le port
est **lié à 127.0.0.1** (rien n'est ouvert sur le réseau). La base n'a aucun port publié. Pour une démo publique : HTTPS devant
l'API (proxy inverse déclaré dans `ProxysConnus`) et certificat pour les clés DataProtection (`Protection:CertificatFichier`).

### À vérifier au premier démarrage

- Distribution de l'étiquette `mcr.microsoft.com/dotnet/aspnet:10.0` (Debian ou Ubuntu : `cat /etc/os-release` dans le
  conteneur) et présence d'ICU (culture fr-FR : formats des montants et des dates dans l'API et les PDF).
- Dépôt PGDG : disponibilité de `<distribution>-pgdg` et du paquet `postgresql-client-18` ; chemin
  `/usr/lib/postgresql/18/bin/pg_dump` (le build s'arrête s'il manque) ; empreinte de la clé PGDG non contrôlée par le build.
- Paquet `tzdata` et `TZ=Africa/Algiers` : heure affichée et avertissement de fuseau au journal de l'API.
- Compte `app` présent dans l'image (`install -d -o app` échoue sinon) ; droits du volume `api-donnees` (Docker recopie les droits
  du dossier de l'image dans un volume nommé neuf) ; écriture des clés DataProtection dans `cles-protection/`.
- Port réellement écouté (8080) : `Urls` en variable d'environnement doit l'emporter sur le `Urls` d'`appsettings.json`.
- Image `postgres:18` : volume monté sur `/var/lib/postgresql` (emplacement des données propre à la version 18 de l'image).
- Secrets compose tirés de `environment:` et lus par l'image postgres (`POSTGRES_PASSWORD_FILE`) et par le script d'init.
- Script d'init : lancé (exécutable) ou « sourcé » selon les droits du fichier monté depuis Windows ; connexion de `pharma_app`
  par le socket local pendant l'init ; `\restrict` de `schema.sql` et `\getenv` acceptés par le psql de l'image ; durée de
  l'init (`start_period` de 120 s pour `db`).
- Données fictives et comptes de démonstration chargés (l'init échoue sinon) : séquence déjà validée hors conteneur, à constater dans le conteneur.
- `GET /api/healthcheck` en 200 depuis le PC ; connexion au poste ou au navigateur (`/mobile/`) sur `http://127.0.0.1:5080`.
- DataProtection propre à `AddAuthentication` (clés dans le profil du compte `app`, non persistées) : avertissements possibles.
- Sauvegarde automatique (`pg_dump` du conteneur) : disque de destination non réglé à cette étape.

### Reste pour l'étape 2

- Trancher le point des comptes de démo (A, B ou C ci-dessus) ; charger `905_jeu_de_test.sql` et la nomenclature nationale.
- Mode démo : comptes d'essai, remise à zéro de la base.
- Super admin de la démo (`dotnet Pharma.Api.dll --super-admin` dans le conteneur, interactif : à essayer).
- Étape 3 : tests de démarrage du conteneur, santé, PDF (Liberation Sans, texte arabe), sauvegarde et restauration dans le conteneur.
