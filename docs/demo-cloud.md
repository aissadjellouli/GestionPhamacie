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
| 1 | Dockerfile de l'API + docker-compose local (API + PostgreSQL + volume des données) | À faire |
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
