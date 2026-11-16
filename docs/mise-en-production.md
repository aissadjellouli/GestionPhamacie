# Manuel de mise en production

Ce manuel dit quoi faire, dans quel ordre, sur quel PC et avec quel compte. Il sert au technicien ou au titulaire. Suivre chaque parcours **de haut en bas**, sans sauter d'étape. Cocher chaque case au fur et à mesure.

Mise à jour : 2026-11-16. Les points marqués **[À CONFIRMER]** ne sont pas tranchés ou pas vérifiables dans le dépôt : ne pas les deviner, demander la réponse au titulaire ou au développeur.

## Sommaire

- [Avant de commencer](#avant-de-commencer) : rôles, comptes, conventions, ports, services, variables, fichiers
- [Parcours A, installation neuve](#parcours-a-installation-neuve) : une officine qui démarre de zéro (étapes 1 à 15)
- [Parcours B, mises à jour](#parcours-b-mises-à-jour) : une officine déjà en service (étapes 1 à 14)
- [Parcours C, 2e serveur](#parcours-c-2e-serveur) : réplication et API de secours (facultatif)
- [Parcours D, PC de secours](#parcours-d-pc-de-secours) : copie de secours (facultatif, fortement conseillé)
- [Parcours E, Serveur central de synchronisation](#parcours-e-serveur-central-de-synchronisation) : hébergement central (facultatif)
- [Procédures occasionnelles](#procédures-occasionnelles) : restauration, PC serveur en panne, bascule, retour, révocation
- [Annexes](#annexes) : ports, services, variables, fichiers, dépannage
- [Checklist avant ouverture](#checklist-avant-ouverture)
- [Checklist après chaque mise à jour](#checklist-après-chaque-mise-à-jour)

---

## Avant de commencer

### Rôles

| Rôle | Qui | Ce qu'il fait |
| --- | --- | --- |
| **Technicien** | personne qui installe | Commandes PowerShell, services, pare-feu, base |
| **Titulaire** | pharmacien titulaire | Réglages dans l'application, décisions (sauvegardes, Chifa, synchronisation) |
| **Administrateur Windows** | compte qui a les droits d'administrateur sur le PC | Installe, change les variables système, lance les scripts |
| **Superutilisateur PostgreSQL** | compte `postgres` (mot de passe noté à l'installation) | Crée les rôles, lance `111b`, les procédures de rôles |
| **Compte `pharma_app`** | compte de l'application, propriétaire de la base avant `111b` | Lance les scripts de la base (voir parcours B) |

### Les PC et les lieux

| Nom dans ce manuel | Ce que c'est |
| --- | --- |
| **PC serveur** | PC principal de l'officine : PostgreSQL et API. Toujours allumé, onduleur obligatoire |
| **Poste** | poste de caisse (un par caisse), qui ouvre Pharma Officine |
| **PC des sources** | PC qui a le code du dépôt et le SDK .NET 10 : c'est là qu'on compile (`dotnet publish`) |
| **2e serveur** | 2e PC de l'officine, réplique de la base (parcours C, facultatif) |
| **PC de secours** | PC de l'officine avec PostgreSQL et l'API, prêt à prendre le relais (parcours D, facultatif) |
| **Serveur central** | serveur de synchronisation hors officine (parcours E, facultatif) : hébergement **[À VÉRIFIER : hébergeur agréé]** |
| **Téléphone** | scanner mobile (Android ou iPhone) |
| **Clé USB** | support de transport (exemple : `E:\`). Jamais de certificat ni de secret par le réseau |

Dans les commandes, les valeurs entre `<` et `>` sont **à remplacer** (exemple : `<serveur>`, `<base>`, `<mot de passe>`). Ne jamais écrire de vrai mot de passe dans un fichier du dépôt.

### Conventions de commandes

- Une fenêtre **PowerShell « en administrateur »** : clic droit sur PowerShell, « Exécuter en tant qu'administrateur ».
- Dans chaque fenêtre PowerShell du PC serveur, définir une fois :
  ```powershell
  $psql = "C:\Program Files\PostgreSQL\18\bin\psql.exe"
  ```
  Si la version de PostgreSQL est différente, adapter le numéro de dossier.
- psql demande le mot de passe à chaque connexion. Pour éviter la saisie répétée, PostgreSQL accepte un fichier `pgpass.conf` (documentation officielle de PostgreSQL). **[À CONFIRMER : le technicien le crée-t-il ?]**
- Si PowerShell refuse un script (stratégie d'exécution), lancer : `powershell -ExecutionPolicy Bypass -File .\scripts\<script>.ps1 <paramètres>`.
- Les scripts se lancent **depuis le dossier du dépôt** (ou de la livraison), là où se trouve le dossier `scripts`.

### Décisions déjà prises

- **HTTPS seul sur le réseau de l'officine** (décision du 2026-11-16). HTTP seulement sur le PC serveur lui-même (`http://localhost:5080`). Hors développement, l'API **refuse de démarrer** tant qu'elle écoute en HTTP sur le réseau.
- **Synchronisation avec le serveur central désactivée par défaut.** Rien ne part tant que le titulaire ne l'active pas (parcours E).
- **Clé de scellement des journaux** : exportée par le titulaire dans un fichier protégé par mot de passe. Voir la section « Clé de scellement » dans les annexes.
- **Le paquet `scripts/paquet/` est pour un essai** (données de démonstration fictives). Ne jamais l'utiliser pour une officine réelle.

### Ports

| Port | Où | Qui peut s'y connecter | Rôle |
| --- | --- | --- | --- |
| 5432 | PC serveur | **Personne sur le réseau**, sauf le 2e serveur (parcours C) | PostgreSQL |
| 5080 | PC serveur | `localhost` seulement (jamais le réseau) | API en HTTP, pour le PC serveur lui-même |
| 5443 | PC serveur | Postes, téléphones (réseau privé) | API en HTTPS |
| 5090 | Serveur central | `127.0.0.1` par défaut **[À VÉRIFIER : exposition à l'hébergement]** | Serveur central (HTTPS) |

### Services Windows

| Service | Où | Démarrage | Remarque |
| --- | --- | --- | --- |
| `postgresql-x64-18` (nom donné par l'installeur PostgreSQL) | PC serveur, 2e serveur, PC de secours | Automatique | Ne pas arrêter pendant la journée |
| `PharmaOfficine API` | PC serveur | Automatique | Créé à l'étape 4.7 du parcours A |
| `PharmaOfficine API` | 2e serveur, PC de secours | **Manuel** (arrêté) | Démarré seulement en cas de bascule |
| Serveur central | Hébergement | **[À CONFIRMER : pas de service Windows dans le code, voir parcours E, étapes 3.2 et 5.2]** | |

### Variables d'environnement de l'API (système, scope Machine)

| Variable | Obligatoire | Exemple (à remplacer) | Rôle |
| --- | --- | --- | --- |
| `ConnectionStrings__Pharmacie` | oui | `Host=localhost;Port=5432;Database=<base>;Username=pharma_app;Password=<mot de passe>` | Connexion à la base. Pas de caractère « ; » dans le mot de passe |
| `Jwt__Cle` | oui | 64 caractères minimum, aléatoire | Signature des connexions. Refus si absente ou trop courte |
| `Chifa__DossierExport` | oui | `<dossier d'export de CHIFA-Officine sur ce PC>` | Par défaut `D:\CHIFA_OFFICINE\CNAS_SIGNED` : **à remplacer** |
| `Ged__Dossier` | non | `C:\ProgramData\PharmaOfficine\GED` (défaut) | Dossier de tous les documents |
| `Ged__RetentionSauvegardesJours` | non | `7` (défaut) | Conservation des copies de base dans `GED\Sauvegardes` |
| `Ordonnances__Dossier` | non | défaut : `GED\Ordonnances` | Scans d'ordonnances |
| `Journaux__Dossier`, `Journaux__TailleMaxMo` (50), `Journaux__RetentionMois` (3), `Journaux__Niveau` (`Information` ou `Warning`) | non | | Journal du serveur |
| `LimiteConnexions__ParMinute` | non | `20` (défaut) | Essais de connexion permis par minute et par adresse |
| `AutoriserHttpReseau` | **non, à ne pas régler** | | Dérogation provisoire : HTTP servi sur le réseau, en clair |
| `SecondServeur__AutreServeur` | seulement si parcours C | IP de l'autre serveur | Évite que deux API écrivent |
| `SuperAdmin:Fichier` | non | `C:\ProgramData\PharmaOfficine\super-admin.dat` (défaut) | Compte super admin |
| `Integrite:FichierCle` | non | `C:\ProgramData\PharmaOfficine\cle-integrite.dat` (défaut) | Clé de scellement |

Côté postes :

| Variable | Où | Valeur |
| --- | --- | --- |
| `PHARMA_API` | chaque poste | `https://<serveur>:5443/` (HTTPS, obligatoire sauf sur le PC serveur) |
| `PHARMA_POSTE` | chaque poste | un nom **différent** par poste, ex. `Caisse 1`, `Caisse 2` (c'est le tiroir de la caisse) |

Côté serveur central (outils d'administration) : voir parcours E, étape 3.2.

### Fichiers et dossiers à connaître

| Chemin | Où | Contenu | À sauvegarder |
| --- | --- | --- | --- |
| `C:\Program Files\PharmaOfficine\Api` | PC serveur | Programme de l'API, `appsettings.Https.json`, `database\` (scripts 001 à 136, sans `111b`) | Non (republié à chaque mise à jour) |
| `C:\ProgramData\PharmaOfficine\super-admin.dat` | PC serveur | Compte super admin, chiffré pour ce PC | **Oui**, sur le disque externe |
| `C:\ProgramData\PharmaOfficine\cle-integrite.dat` | PC serveur | Clé de scellement des journaux, chiffrée pour ce PC | **Oui**, sur le disque externe |
| `C:\ProgramData\PharmaOfficine\archives-fiscales\AAAA\MM` | PC serveur | Rapports scellés, à conserver 6 ans au moins | **Oui** |
| `C:\ProgramData\PharmaOfficine\GED` | PC serveur | Documents (ordonnances, Chifa, factures, rapports…) | Oui (sous-dossier `GED` du disque externe) |
| `C:\ProgramData\PharmaOfficine\Journaux` | PC serveur | Journal du serveur (`pharma-api-AAAAMMJJ.log`) | Non |
| `C:\ProgramData\PharmaOfficine\poste.json` | chaque poste | Réglages du poste (imprimantes, adresse de secours) | Non |
| `C:\ProgramData\PharmaOfficine\copie-secours.json` | PC serveur | Réglages et état de la copie de secours | Non |
| `C:\ProgramData\PharmaOfficine\Synchronisation` | PC serveur | Clé privée de synchronisation (DPAPI, ce PC seulement) | **Ne jamais copier sur un autre serveur** |

---

## Parcours A, installation neuve

Ordre : étapes 1 à 15. Chaque étape dépend de la précédente. Ne pas démarrer le service de l'API (étape 4.7) avant le certificat HTTPS (étape 4.6), sinon l'API refuse de démarrer.

Calendrier type :

| Jour | Étapes |
| --- | --- |
| J-15 | Répétition générale complète sur une copie (étapes 1 à 4, puis 9 à 12 si reprise de données) |
| J-7 | Formation, paramétrage et sauvegardes (étapes 8 à 11) |
| J-1 au soir | Clôture de caisse de l'ancien logiciel, sauvegarde, export, reprise (étape 12), contrôle des totaux, vérifications (étape 14) |
| J | Ouverture, accompagnement sur place (étape 15) |
| J+1 à J+30 | Ancien logiciel en lecture seule ; étiquetage progressif du stock |

**Critère de retour arrière**, fixé à l'avance : si les totaux ne concordent pas la veille au soir, on rouvre l'ancien logiciel et on reporte.

### 1. Préparer avant le jour J (J-15 à J-7)

- [ ] **1.1** Matériel. **Où** : sur place. **Compte** : aucun.
  - PC serveur toujours allumé, avec un **onduleur obligatoire** contre les coupures de courant. Désactiver la mise en veille du PC serveur (Paramètres, Système, Alimentation).
  - Un poste par caisse. Chaque poste a : une imprimante de tickets avec son pilote Windows, et un lecteur de code-barres (douchette) si utilisé.
  - Imprimante d'étiquettes réseau (facultative).
  - Deux disques externes pour les sauvegardes, et une clé USB pour les certificats et la clé de scellement.
  - **Attendu** : tout le matériel est là et branché.

- [ ] **1.2** Versions de Windows. **Où** : chaque PC. **Compte** : aucun.

  | Machine | Minimum technique | Recommandé |
  | --- | --- | --- |
  | Poste de caisse | Windows 10 version 2004 (build 19041) | Windows 11 (22H2 ou plus), ou Windows 10 IoT Enterprise LTSC |
  | PC serveur | Windows 10 ou Windows Server 2016 | Windows 11 Pro, ou Windows Server 2019, 2022 ou 2025 |

  Windows 10 22H2 n'est plus maintenu depuis le 14 octobre 2025, sauf éditions LTSC. Pour des données de santé, préférer Windows 11. Le logiciel n'a été essayé que sous Windows 11 : **faire un essai sur le système réel avant le jour J**.
  Les téléphones n'ont besoin que d'un navigateur récent.

- [ ] **1.3** Réseau. **Où** : PC serveur et box. **Compte** : administrateur Windows.
  - Donner au PC serveur une **adresse IP fixe** (réglage de la box ou du PC). Noter l'adresse.
  - Vérifier : `ipconfig` affiche l'adresse retenue.
  - Wi-Fi seulement si le scanner mobile est utilisé.

- [ ] **1.4** Informations à demander à la pharmacie. **Où** : papier, au titulaire.
  - Nom, adresse, téléphone de la pharmacie.
  - **RC, NIF, NIS, AI.**
  - N° d'officine Chifa.
  - Liste des employés et de leurs rôles (assistant, vendeur, stagiaire).
  - Banques.
  - Organismes conventionnés (par exemple les militaires) avec leur adresse et leur NIF.
  - Compte e-mail (SMTP) et/ou modem GSM pour les alertes au titulaire.
  - Le logiciel **CHIFA-Officine** est installé sur le PC serveur, et son dossier d'export est connu (**[À CONFIRMER : procédure d'installation fournie par la CNAS, hors du dépôt]**). Lecteur de carte et Token : **[À CONFIRMER : à installer sur quel PC]**.

- [ ] **1.5** Nomenclature nationale. **Où** : PC des sources ou PC serveur. **Compte** : aucun.
  - Le fichier livré est `database\data\NOMENCLATURE NATIONALE - VERSION JUIN 2026.xlsx`.
  - Si le ministère a publié une version plus récente (miph.gov.dz), la garder pour l'étape 9.8.

- [ ] **1.6** Outils sur le PC des sources. **Où** : PC des sources. **Compte** : administrateur Windows.
  - Installer le **SDK .NET 10** (nécessaire pour `dotnet publish`). **[À CONFIRMER : qui compile les livraisons de production ?]**
  - Vérifier : `dotnet --version` affiche une version 10.x.
  - **Attendu** : la commande répond. Si elle ne répond pas, installer le SDK puis rouvrir PowerShell.

- [ ] **1.7** Répétition. **Où** : copie de test. Pour une reprise de données, faire une répétition générale complète sur une **copie** de l'ancienne base (voir étape 12). L'ancienne base n'est jamais modifiée.

- [ ] **1.8** Décider si un PC de secours est prévu. Si oui, **réaliser le parcours D maintenant**, après l'étape 4 (il dépend seulement du PC serveur installé).

### 2. PC serveur : PostgreSQL

- [ ] **2.1** Installer PostgreSQL 18, avec ses outils `psql` et `pg_dump` (inclus par défaut dans l'installeur). **Où** : PC serveur. **Compte** : administrateur Windows.
  - Noter le mot de passe du superutilisateur `postgres` : il ne sert qu'à l'installation et aux procédures de la base. Le garder au coffre.
  - Garder le port `5432` proposé par défaut.
  - Prérequis : Windows 10 ou plus (runtime C « UCRT »).
  - **Attendu** : le service `postgresql-x64-18` existe et démarre.
  - Vérifier :
    ```powershell
    & "C:\Program Files\PostgreSQL\18\bin\psql.exe" --version
    Get-Service postgresql* | Select-Object Name, Status, StartType
    ```
    La version commence par `psql (PostgreSQL) 18`. Le service est `Running` et `Automatic`.
  - **Si échec** : relancer l'installeur ; vérifier qu'aucune autre version PostgreSQL n'est en conflit sur le port 5432.

- [ ] **2.2** Créer le compte de l'application et la base. **Où** : PC serveur. **Compte** : superutilisateur `postgres`.
  Ouvrir psql en interactif :
  ```powershell
  & $psql -h localhost -U postgres -d postgres
  ```
  Taper ensuite, ligne par ligne (le mot de passe est demandé par la commande `\password`, il n'est jamais écrit dans une commande) :
  ```sql
  CREATE ROLE pharma_app LOGIN CREATEDB;
  \password pharma_app
  CREATE DATABASE <base> OWNER pharma_app ENCODING 'UTF8' TEMPLATE template0;
  \q
  ```
  - `<base>` : exemple `pharmacie`. Mot de passe : **propre à cette pharmacie**, jamais celui de démonstration, 12 caractères minimum, noté au coffre.
  - `CREATEDB` : permet au super admin de restaurer une sauvegarde depuis l'application. Compte déjà créé sans ce droit : `ALTER ROLE pharma_app CREATEDB;`.
  - **Attendu** : `CREATE ROLE`, `\password` demande deux fois le mot de passe, `CREATE DATABASE`.
  - **Si échec** : `already exists` signifie que le compte ou la base existe déjà. Vérifier avec `\du pharma_app` et `\l`.

- [ ] **2.3** Régler le fuseau horaire de Windows (Algérie, UTC+1 toute l'année). **Où** : PC serveur. **Compte** : administrateur Windows.
  ```powershell
  Set-TimeZone -Id "W. Central Africa Standard Time"
  Get-TimeZone
  ```
  - **Attendu** : `Get-TimeZone` affiche « (UTC+01:00) Afrique centrale - Ouest ». Sinon, l'API écrit un avertissement au démarrage et les dates des ventes sont décalées.
  - **Si échec** : régler à la main dans Paramètres, Heure et langue, Date et heure, Fuseau horaire.

- [ ] **2.4** Vérifier que PostgreSQL n'est pas ouvert au réseau. **Où** : PC serveur. **Compte** : administrateur Windows.
  ```powershell
  Get-NetTCPConnection -LocalPort 5432 -State Listen | Select-Object LocalAddress
  ```
  - **Attendu** : seulement `127.0.0.1` et `::1`. Aucune règle de pare-feu n'ouvre 5432 aux postes : l'application passe par l'API, pas par PostgreSQL.
  - **Si échec** : une adresse du réseau apparaît. Vérifier `listen_addresses` dans le fichier `postgresql.conf` de PostgreSQL et le remettre sur `localhost`.

### 3. Base de données : structure, référentiel, droits

Lancer toutes les commandes **depuis le dossier du dépôt** (ou de la livraison). Chaque commande demande le mot de passe de `pharma_app`.

- [ ] **3.1** Créer la structure. **Où** : PC serveur. **Compte** : `pharma_app`.
  ```powershell
  & $psql -h localhost -U pharma_app -d <base> -v ON_ERROR_STOP=1 -f database\init\schema.sql
  ```
  - `schema.sql` crée tout (tables, vues, fonctions, déclencheurs) et équivaut aux scripts `001` à `136`, **sauf** le fuseau horaire (étape 3.2) et les droits (étape 3.4).
  - **Attendu** : pas d'erreur, retour à l'invite.
  - Si la base contient déjà le schéma, le script s'arrête sans rien faire. Ce n'est pas une erreur, mais une base neuve doit être vide : voir « Recommencer depuis zéro » plus bas.
  - **Si échec** : lire la première erreur. Ne jamais relancer `schema.sql` sur une base en service.

- [ ] **3.2** Régler le fuseau horaire de la base. **Où** : PC serveur. **Compte** : `pharma_app`.
  ```powershell
  & $psql -h localhost -U pharma_app -d <base> -v ON_ERROR_STOP=1 -f database\migration\083_fuseau_horaire.sql
  & $psql -h localhost -U pharma_app -d <base> -tAc "show timezone"
  ```
  - Ce réglage n'est **pas** repris dans `schema.sql` : il faut le lancer à part. Sans risque, réexécutable.
  - **Attendu** : `Africa/Algiers`.
  - L'API ajoute aussi `Timezone=Africa/Algiers` à sa chaîne de connexion si elle n'en précise pas.

- [ ] **3.3** Charger le référentiel de départ. **Où** : PC serveur. **Compte** : `pharma_app`.
  ```powershell
  & $psql -h localhost -U pharma_app -d <base> -v ON_ERROR_STOP=1 -f database\init\seed_referentiel.sql
  ```
  - Contenu : identité par défaut, rôles, droits, organismes CNAS, CASNOS et MDN, substances surveillées, coffre, catégories, motifs, réglages de synchronisation (désactivée), et le compte de départ `titulaire`.
  - **Attendu** : le message « Référentiel chargé. Connexion : titulaire / Provisoire-2026 (à changer à la première connexion). »
  - **Interdit** : ne jamais lancer les fichiers `database\data\9xx_*.sql`. Ce sont des données fictives (patients, produits, comptes « demo »), réservées aux tests.

- [ ] **3.4** Séparer les rôles (journaux inaltérables). **Où** : PC serveur. **Compte** : **superutilisateur** `postgres` (pas `pharma_app`).
  ```powershell
  & $psql -h localhost -U postgres -d <base> -v ON_ERROR_STOP=1 -f database\migration\111b_roles_integrite.sql
  ```
  - Le script crée le rôle sans connexion `pharma_proprietaire`, lui donne la base et les objets, et retire à `pharma_app` la modification et la suppression des journaux (tables chaînées, `sceau_integrite`, `verification_integrite`, `journal_audit`, `journal_sauvegarde`, `boite_evenement`, `registre_psychotrope`).
  - Sans cette étape, `pharma_app` peut désactiver les déclencheurs qui protègent les journaux. La vérification nocturne détecte alors la falsification, mais ne l'empêche pas.
  - **Attendu** : la dernière ligne affichée est `objets_encore_a_pharma_app` avec la valeur **0**.
  - **Si échec** : `à exécuter par un superutilisateur` : le compte utilisé n'est pas `postgres`. Recommencer avec `postgres`.
  - Après cette étape, les **futurs** scripts se lancent avec `postgres` et `SET ROLE pharma_proprietaire` (voir parcours B, étape 9). Ne plus jamais les lancer avec `pharma_app`.

- [ ] **3.5** Contrôler la base. **Où** : PC serveur. **Compte** : `pharma_app`.
  ```powershell
  & $psql -h localhost -U pharma_app -d <base> -tAc "SELECT count(*) FROM utilisateur"
  & $psql -h localhost -U pharma_app -d <base> -tAc "SELECT count(*) FROM vente"
  ```
  - **Attendu** : le premier nombre est au moins `1` (compte titulaire). Le second vaut `0` (base vierge, aucune donnée de démonstration).
  - **Si échec** : `relation does not exist` : `schema.sql` n'a pas été lancé correctement (étape 3.1).

- **Recommencer depuis zéro** (seulement avant la mise en service, jamais sur une base réelle) :
  1. Faire une sauvegarde (étape 11.2).
  2. Lancer la remise à vide en confirmant le nom de la base. Sans `-v confirmer=<base>` identique, rien n'est supprimé :
     ```powershell
     & $psql -h localhost -U pharma_app -d <base> -v ON_ERROR_STOP=1 -v confirmer=<base> -f database\init\clean.sql
     ```
  3. Reprendre à l'étape 3.1. Le compte doit être propriétaire de la base (c'est le cas à l'étape 2.2 ; après `111b`, `clean.sql` ne convient plus : **[À CONFIRMER : procédure de remise à vide après 111b]**).

### 4. PC serveur : API, configuration, HTTPS, service

- [ ] **4.1** Publier l'API. **Où** : PC des sources. **Compte** : administrateur Windows. Depuis le dossier du dépôt :
  ```powershell
  dotnet publish src\Pharma.Api -c Release -r win-x64 --self-contained true -o "<dossier de livraison>\Api"
  ```
  - `--self-contained true` : aucun runtime .NET à installer sur le PC serveur.
  - Le dossier publié contient `Pharma.Api.exe` et `database\` (scripts `001` à `136`, sans `111b`, qui se lance à part).
  - Contrepartie : les correctifs de sécurité de .NET n'arrivent qu'avec une nouvelle publication (parcours B).
  - **Attendu** : `Pharma.Api.exe` et le dossier `database` sont présents dans `<dossier de livraison>\Api`.
  - **Si échec** : SDK absent (étape 1.6) ou version différente de .NET 10.

- [ ] **4.2** Copier l'API sur le PC serveur. **Où** : PC serveur (depuis la clé USB). **Compte** : administrateur Windows.
  ```powershell
  New-Item -ItemType Directory -Force "C:\Program Files\PharmaOfficine" | Out-Null
  Copy-Item -Recurse -Force "E:\<dossier de livraison>\Api" "C:\Program Files\PharmaOfficine\"
  Test-Path "C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe"
  ```
  - **Attendu** : `True`.

- [ ] **4.3** Copier le dossier `scripts` du dépôt (ou de la livraison) sur le PC serveur. **Où** : PC serveur. **Compte** : administrateur Windows. Il contient `certificat_https.ps1`, `sauvegarde_pharmacie.ps1`, `restaurer_pharmacie.ps1`, `basculer-pc-secours.ps1` et `second-serveur\`. **[À CONFIRMER : dossier de destination officiel sur le PC serveur, par exemple `C:\Program Files\PharmaOfficine\scripts`.]**
  - Ces scripts **ne sont pas** dans le dossier publié de l'API.

- [ ] **4.4** Régler les variables d'environnement de l'API. **Où** : PC serveur. **Compte** : administrateur Windows.
  Ouvrir une **nouvelle** fenêtre PowerShell en administrateur. Remplacer les valeurs `<...>` :
  ```powershell
  [Environment]::SetEnvironmentVariable("ConnectionStrings__Pharmacie", "Host=localhost;Port=5432;Database=<base>;Username=pharma_app;Password=<mot de passe pharma_app>", "Machine")
  [Environment]::SetEnvironmentVariable("Chifa__DossierExport", "<dossier d'export de CHIFA-Officine>", "Machine")
  ```
  Générer la clé de signature (64 caractères minimum, 72 ici). La noter au coffre, puis la coller dans la commande suivante :
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
  - **Vérifier** sans afficher les secrets :
    ```powershell
    [Environment]::GetEnvironmentVariable("Jwt__Cle", "Machine").Length    # doit être 64 ou plus
    [Environment]::GetEnvironmentVariable("ConnectionStrings__Pharmacie", "Machine") -ne $null
    ```
  - **Attendu** : `True`, et un nombre ≥ 64.
  - **Si échec** : les variables ne sont pas vues par l'API tant qu'elle n'a pas été redémarrée (étape 4.7 : le service se lance avec les variables système).

- [ ] **4.5** Régler les réglages facultatifs (seulement si besoin). **Où** : PC serveur. **Compte** : administrateur Windows. Mêmes commandes que 4.4 (`SetEnvironmentVariable`, scope `Machine`) :
  - `Ged__Dossier` : dossier unique des documents (défaut `C:\ProgramData\PharmaOfficine\GED`) ;
  - `Ged__RetentionSauvegardesJours` : défaut `7` ;
  - `Ordonnances__Dossier` : à régler seulement pour sortir les scans de la GED ;
  - `Journaux__Dossier`, `Journaux__TailleMaxMo`, `Journaux__RetentionMois`, `Journaux__Niveau` : journal du serveur (un fichier par jour) ;
  - `LimiteConnexions__ParMinute` : défaut `20`.
  - **Ne pas** régler `AutoriserHttpReseau`.
  - `Sauvegardes__PgDump` : chemin de `pg_dump.exe` si l'API ne le trouve pas. **[À CONFIRMER : comportement de l'API quand la valeur est vide.]**
  - Ne pas modifier `Urls` dans `appsettings.json` : c'est le fichier `appsettings.Https.json` (étape 4.6) qui prend le relais.

- [ ] **4.6** Chiffrer les échanges (HTTPS). **Où** : PC serveur. **Compte** : administrateur Windows. Clé USB branchée (ici `E:\`).
  Ce script crée l'autorité de la pharmacie (une fois), le certificat du serveur, écrit `appsettings.Https.json`, ouvre le port 5443 en réseau **privé** seulement, et exporte le certificat **public** de l'autorité et son empreinte sur la clé USB.

  0. **Se placer dans le dossier qui contient `scripts`** (celui de l'étape 4.3) :
     ```powershell
     Set-Location "<dossier qui contient scripts>"
     Test-Path .\scripts\certificat_https.ps1
     ```
     - **Attendu** : `True`. Si PowerShell refuse ensuite le script, voir « Conventions de commandes » (stratégie d'exécution).
  1. **Essai à blanc d'abord** (rien n'est écrit) :
     ```powershell
     .\scripts\certificat_https.ps1 -DossierApi "C:\Program Files\PharmaOfficine\Api" -DossierExport E:\ -PareFeu -WhatIf
     ```
     - **Attendu** : chaque action est annoncée avec la mention `[SIMULATION]`, et la fin dit « rien n'a été modifié ».
  2. **Lancement réel** :
     ```powershell
     .\scripts\certificat_https.ps1 -DossierApi "C:\Program Files\PharmaOfficine\Api" -DossierExport E:\ -PareFeu
     ```
     - Le script affiche les noms et adresses du certificat (par défaut : `localhost`, `127.0.0.1`, le nom du PC et ses adresses IPv4 privées), puis l'empreinte SHA-256 de l'autorité.
     - Noter cette empreinte. Elle est aussi écrite dans `E:\empreinte-autorite-pharmacie.txt`.
  3. **Noms et adresses supplémentaires** : un poste ne fait confiance au serveur que pour un nom ou une adresse **présents dans le certificat**. Rien n'est deviné : ajouter seulement ce qui sert, par exemple :
     - adresse VPN du PC serveur (accès du titulaire, parcours « acces-distant.md ») : `-AdressesSupplementaires <adresse VPN>` ;
     - nom et IP du 2e serveur, si le parcours C est fait : `-NomsSupplementaires <nom> -AdressesSupplementaires <IP>`.
     Relancer alors la commande complète avec les paramètres ajoutés. Chaque relance est sûre (idempotente).
  4. **Fichiers créés** : vérifier.
     ```powershell
     Get-Content "C:\Program Files\PharmaOfficine\Api\appsettings.Https.json"
     Get-ChildItem E:\ | Select-Object Name
     Get-NetFirewallRule -DisplayName "PharmaOfficine API (https 5443)" | Select-Object DisplayName, Enabled
     ```
     - **Attendu** : `appsettings.Https.json` contient `"Url": "http://localhost:5080"` et `"Url": "https://0.0.0.0:5443"`. La clé USB contient `autorite-pharmacie.crt` et `empreinte-autorite-pharmacie.txt`. La règle de pare-feu est `Enabled : True`.
  - **Décision** : le certificat du serveur est valable 825 jours. Relancer le script avant son échéance (il le renouvelle).
  - **Si échec** : « Dossier de l'API introuvable » : étape 4.2 non faite. « Adresse IP supplémentaire invalide » : vérifier la syntaxe.

- [ ] **4.7** Installer l'API comme service Windows (démarrage automatique). **Où** : PC serveur. **Compte** : administrateur Windows.
  Le service doit démarrer **après** l'étape 4.6.
  ```powershell
  New-Service -Name "PharmaOfficine API" -BinaryPathName '"C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe"' -DisplayName "PharmaOfficine API" -StartupType Automatic
  Start-Service "PharmaOfficine API"
  Get-Service "PharmaOfficine API"
  ```
  - **Attendu** : `Status : Running`.
  - Les guillemets autour du chemin sont **obligatoires** (le chemin contient des espaces). Les versions précédentes de ce manuel donnaient une commande `sc.exe` sans ces guillemets : elle était fausse.
  - **Si échec** : le service se ferme aussitôt. Lire le journal du serveur (voir « Dépannage » en annexe) : le message « Démarrage refusé : l'API écoute en HTTP (non chiffré) » signifie que l'étape 4.6 n'est pas faite. Un message sur `Jwt:Cle` signifie que la variable (4.4) est absente ou trop courte.

- [ ] **4.8** Vérifier l'API sur le PC serveur. **Où** : PC serveur. **Compte** : administrateur Windows.
  ```powershell
  (Invoke-WebRequest http://localhost:5080/api/sante -UseBasicParsing).StatusCode
  (Invoke-WebRequest http://localhost:5080/api/auth/comptoir -UseBasicParsing).StatusCode
  ```
  - **Attendu** : `200` pour les deux (le second peut renvoyer un code différent si aucune session n'est ouverte ; dans ce cas, un code de réponse, même 401, prouve que l'API répond).
  - **Si échec** : ouvrir le service (`Get-Service`), puis le journal.

- [ ] **4.9** Sauvegarder la clé de scellement créée au premier démarrage. **Où** : PC serveur. **Compte** : administrateur Windows.
  - Vérifier que le fichier existe : `Test-Path "C:\ProgramData\PharmaOfficine\cle-integrite.dat"` → `True`.
  - Copier `cle-integrite.dat` sur le **disque externe**, dans un dossier à part (`E:\Securite\`). Le fichier n'est lisible que sur **ce** PC. Sans lui, les sceaux déjà posés ne peuvent plus être vérifiés.
  - **Attendu** : copie faite et vérifiée (taille identique).

### 5. Postes de caisse

Faire ces étapes **sur chaque poste**, une caisse après l'autre.

- [ ] **5.1** Vérifier le navigateur intégré (WebView2). **Où** : chaque poste. **Compte** : administrateur Windows.
  - Il est présent sur Windows 10 et 11 à jour. Vérifier dans Paramètres, Applications, Applications installées : « Microsoft Edge WebView2 Runtime ».
  - S'il manque, l'installer (installeur gratuit de Microsoft) avant d'ouvrir le poste.
  - **Attendu** : l'élément est présent.

- [ ] **5.2** Installer les pilotes des imprimantes de tickets et d'étiquettes, et brancher la douchette. **Où** : chaque poste. **Compte** : administrateur Windows. Les pilotes viennent du fabricant. Imprimer une page de test depuis Windows.

- [ ] **5.3** Copier l'autorité de la pharmacie sur le poste. **Où** : chaque poste. **Compte** : administrateur Windows.
  - Copier `autorite-pharmacie.crt` de la clé USB vers un dossier temporaire du poste (par exemple `C:\Temp\`). **Jamais** par le réseau HTTP.
  - Vérifier l'empreinte : la commande compare le fichier copié avec l'empreinte notée par le script (3e ligne de `empreinte-autorite-pharmacie.txt`) :
    ```powershell
    $calculee = (Get-FileHash "C:\Temp\autorite-pharmacie.crt" -Algorithm SHA256).Hash
    $attendue = ((Get-Content "E:\empreinte-autorite-pharmacie.txt")[2]) -replace '[^0-9A-Fa-f]', ''
    "Calculée : $calculee"; "Attendue : $attendue"; $calculee -eq $attendue
    ```
  - **Attendu** : `True` (la comparaison ignore les `:` et la casse).
  - **Si échec** : `False` = ne pas installer, ne pas continuer, refaire la copie depuis la clé USB.

- [ ] **5.4** Installer l'autorité dans les racines de confiance de l'ordinateur. **Où** : chaque poste. **Compte** : administrateur Windows.
  ```powershell
  certutil -addstore -f Root "C:\Temp\autorite-pharmacie.crt"
  Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.Subject -eq "CN=PharmaOfficine Autorite locale" } | Select-Object Subject, NotAfter
  ```
  - Alternative graphique : double-clic sur le fichier, « Installer le certificat… », « Ordinateur local », « Placer tous les certificats dans le magasin suivant », **Autorités de certification racines de confiance**.
  - **Attendu** : la commande `Get-ChildItem` affiche une ligne.
  - Supprimer ensuite le fichier du dossier temporaire.

- [ ] **5.5** Installer le programme du poste. **Où** : chaque poste. **Compte** : administrateur Windows. Depuis le PC des sources :
  ```powershell
  dotnet publish src\Pharma.Desktop -c Release -r win-x64 --self-contained true -o "<dossier de livraison>\Poste"
  ```
  Copier le dossier obtenu sur chaque poste, par exemple dans `C:\Program Files\PharmaOfficine\Poste`. Créer sur le bureau un raccourci vers `Pharma.Desktop.exe`.
  - Aucun runtime .NET à installer (publication autonome).
  - Il n'existe pas encore d'installateur ni de mise à jour automatique des postes : la copie, les variables et le raccourci se font à la main.

- [ ] **5.6** Régler les variables du poste. **Où** : chaque poste. **Compte** : administrateur Windows.
  ```powershell
  [Environment]::SetEnvironmentVariable("PHARMA_API", "https://<serveur>:5443/", "Machine")
  [Environment]::SetEnvironmentVariable("PHARMA_POSTE", "Caisse 1", "Machine")
  ```
  - `<serveur>` : nom ou IP du PC serveur, **présent dans le certificat** (étape 4.6). La barre finale est ajoutée si elle manque.
  - `PHARMA_POSTE` : **un nom différent pour chaque poste** (`Caisse 1`, `Caisse 2`…). Deux postes avec le même nom partagent la même caisse.
  - Sur le PC serveur lui-même, `http://localhost:5080/` reste permis.
  - **Attendu** : fermer et rouvrir le poste après le réglage.

- [ ] **5.7** Ouvrir Pharma Officine. **Où** : chaque poste. **Compte** : employé.
  - **Attendu** : l'écran de connexion s'affiche, sans avertissement de certificat.
  - **Si échec** : avertissement de certificat = l'autorité n'est pas installée (5.4) ou le nom du serveur n'est pas dans le certificat (4.6, option 3). Message « serveur injoignable » = l'API ne tourne pas ou le pare-feu bloque 5443.

### 6. Téléphones (scanner mobile, facultatif)

- [ ] **6.1** Transférer l'autorité sur le téléphone **hors réseau HTTP** : câble USB ou clé USB OTG. Jamais par un lien HTTP ni par courriel.
- [ ] **6.2** Vérifier l'empreinte **avant** d'installer. Elle est affichée dans les détails du certificat du téléphone. Elle doit être **identique**, caractère pour caractère, à `empreinte-autorite-pharmacie.txt`. Sinon, ne pas installer.
  - **Android** : Paramètres, Sécurité, Chiffrement et identifiants, Installer un certificat, **Certificat CA**.
  - **iPhone** : installer le profil, puis Réglages, Général, Informations, **Réglages des certificats** (activer la confiance).
  - Pas de QR code de l'empreinte : texte seulement.
- [ ] **6.3** Ouvrir le scanner mobile : `https://<serveur>:5443/mobile/`. **Attendu** : la page s'ouvre sans avertissement. Mode d'emploi : [scanner-mobile.md](scanner-mobile.md).

### 7. Fermer le réseau HTTP et vérifier le HTTPS

Faire cette étape **après** les postes et les téléphones (sinon ils n'ont plus d'accès).

- [ ] **7.1** Vérifier qu'aucune règle de pare-feu n'ouvre le port 5080. **Où** : PC serveur. **Compte** : administrateur Windows.
  ```powershell
  Get-NetFirewallRule -Direction Inbound -Enabled True | Where-Object { ($_ | Get-NetFirewallPortFilter).LocalPort -contains '5080' } | Select-Object DisplayName
  ```
  - **Attendu** : aucune ligne. Si une règle apparaît, la désactiver : `Disable-NetFirewallRule -DisplayName '<nom de la règle>'`. Le script `certificat_https.ps1` avertit aussi s'il en trouve une.

- [ ] **7.2** Contrôler les écoutes. **Où** : PC serveur. **Compte** : administrateur Windows.
  ```powershell
  netstat -ano | findstr :5080
  netstat -ano | findstr :5443
  ```
  - **Attendu** : 5080 n'est écouté que sur `127.0.0.1` et `[::1]`. 5443 est écouté sur `0.0.0.0`.

- [ ] **7.3** Contrôler depuis un poste. **Où** : un poste. **Compte** : employé.
  ```powershell
  (Invoke-WebRequest https://<serveur>:5443/api/sante -UseBasicParsing).StatusCode
  (Invoke-WebRequest https://<serveur>:5443/api/sante -UseBasicParsing).Headers["Strict-Transport-Security"]
  Invoke-WebRequest http://<serveur>:5080/ -UseBasicParsing
  Invoke-WebRequest https://<serveur>:5443/mobile/autorite-pharmacie.crt -UseBasicParsing
  ```
  - **Attendu** :
    - le premier affiche `200`, sans erreur de certificat ;
    - l'en-tête `Strict-Transport-Security` est présent ;
    - la commande en `http://…:5080` **échoue** (connexion refusée ou délai dépassé) ;
    - la commande du fichier `.crt` **échoue** avec un code 404 : le certificat n'est plus publié par l'API.

- [ ] **7.4** Essai réel sur un vrai poste, avant le jour J : connexion, vente, impression, scanner mobile en HTTPS. **Où** : un poste et un téléphone. **Compte** : employé.

### 8. Premier démarrage et sécurité

- [ ] **8.1** Se connecter avec le compte de départ. **Où** : un poste. **Compte** : `titulaire` / `Provisoire-2026`.
- [ ] **8.2** Choisir le mot de passe personnel (écran « Choisissez votre mot de passe », 8 caractères minimum). Tant que ce n'est pas fait, le serveur refuse toute autre action, code comptoir compris.
  - À savoir : changer un mot de passe, désactiver un compte (employé parti, téléphone perdu) ou effacer un code comptoir ferme aussitôt les connexions déjà ouvertes de ce compte, sur tous les postes et téléphones.

### 9. Paramétrage (titulaire)

Dans l'ordre. Chaque réglage est fait dans l'application, par le titulaire ou sous sa supervision.

- [ ] **9.1** Administration, Langue et pharmacie : nom de la pharmacie, titulaire, adresse, téléphone, **RC, NIF, NIS, AI**, n° d'officine Chifa. Ces informations sont imprimées sur les tickets, factures et rapports.
- [ ] **9.2** Même page, Organismes payeurs : ajouter les organismes conventionnés (militaires…), avec adresse et NIF.
- [ ] **9.3** Administration, Utilisateurs et droits : créer **un compte par employé**, avec un identifiant et un mot de passe personnels, et le bon rôle (assistant, vendeur, stagiaire).
- [ ] **9.4** Vérifier les droits de chaque rôle. Réservés par défaut au titulaire : trésorerie, factures, inventaire complet, organismes. Le titulaire les accorde ensuite à qui il veut.
- [ ] **9.5** Chaque employé définit son **code comptoir** (bandeau, « Mon code comptoir ») et peut changer son mot de passe (bandeau, « Mot de passe »).
- [ ] **9.6** Administration, Caisse et contrôle : fond de caisse par défaut, seuil d'écart, comptage aveugle, déconnexion automatique, versement des caisses au coffre (manuel ou automatique), seuil de validation des sorties d'argent par le tiroir (défaut 2 000 DA).
  - Administration, Motifs et catégories : motifs du tiroir et leur effet (sortie, entrée, aucun).
  - Administration, Contrôle interne : alerte au-delà de N sorties du tiroir par employé et par jour (défaut 5).
- [ ] **9.7** Administration, Alertes et seuils, puis Seuils de stock : seuils de péremption et de stock. Administration, Emplacements : rayons, réfrigérateur, armoire des psychotropes, réserves.
- [ ] **9.8** Administration, Nomenclature nationale : « Importer le fichier Excel… » avec le fichier de l'étape 1.5. Contrôler les compteurs (en vigueur, non renouvelés, retirés).
  - À faire **avant** toute reprise de données : la nomenclature donne la classe réglementaire des produits.
- [ ] **9.9** Trésorerie : régler la date et le solde de départ du coffre, puis déclarer les banques (une seule reçoit les paiements par carte).
- [ ] **9.10** Administration, Notifications au titulaire : SMTP et/ou modem GSM, destinataires, puis un **message d'essai**. Le modem GSM sert aussi aux codes SMS du super admin (étape 10).
- [ ] **9.11** **Sur chaque poste** : Administration, Imprimantes et étiquettes (imprimante de tickets, largeur du rouleau, ticket automatique, imprimante d'étiquettes, afficheur client). Faire un essai d'impression et un essai de l'afficheur.
- [ ] **9.12** Si le PC de secours a été installé à l'étape 1.8 (parcours D fait), régler sur **chaque poste** l'adresse de secours : Administration, Imprimantes et étiquettes, Serveur et PC de secours. Sinon, passer cette étape.

### 10. Compte super admin (reprise de données et restauration)

- [ ] **10.1** Créer le compte. **Où** : PC serveur. **Compte** : administrateur Windows. Dans une fenêtre PowerShell en administrateur, **après** l'étape 4.4 (les variables sont lues par l'API) :
  ```powershell
  & "C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe" --super-admin
  ```
  - Saisir : identifiant, mot de passe (12 caractères au moins), **téléphone mobile obligatoire** et **e-mail obligatoire** (secours si le SMS ne part pas).
  - Un code est envoyé par SMS au numéro saisi, pour le vérifier (modem GSM de l'étape 9.10). **[À CONFIRMER : sans modem, le compte peut-il être créé sans vérification ? Le manuel précédent l'affirmait ; le code ne l'a pas confirmé.]**
  - Le compte est chiffré pour ce PC (`super-admin.dat`). Il fonctionne même si la base est détruite, et il est illisible sur un autre PC.
  - **Attendu** : le compte est créé, puis l'invite revient.
- [ ] **10.2** Sauvegarder `super-admin.dat` sur le disque externe (dossier `E:\Securite\`), comme `cle-integrite.dat` (étape 4.9).
- [ ] **10.3** Pour changer le mot de passe, le téléphone ou l'e-mail : relancer la même commande. Il faut le mot de passe actuel, le code envoyé au téléphone actuel, puis le code reçu par SMS sur le nouveau numéro.
- [ ] **10.4** Téléphone et e-mail perdus : un administrateur du PC serveur supprime `super-admin.dat`, puis recrée le compte (étape 10.1). Ne jamais supprimer le compte sans cette procédure.

### 11. Sauvegardes (avant toute vraie donnée)

- [ ] **11.1** Choisir **un seul** mécanisme. **[À CONFIRMER : décision du titulaire.]**
  - (a) **Sauvegarde de l'application** : Administration, Sauvegardes. Elle copie la base sur le disque externe et dans `GED\Sauvegardes` (gardée 7 jours par défaut), et les documents de la GED sur le disque externe, dans son sous-dossier `GED` (nouveaux et modifiés seulement). Un disque absent n'empêche pas la copie dans la GED.
  - (b) **Script planifié** `scripts\sauvegarde_pharmacie.ps1`. Son en-tête indique : « Choisir ce script OU la sauvegarde automatique de l'application : pas les deux ».
  - Ne jamais garder les deux.
- [ ] **11.2** Option (a) : Administration, Sauvegardes : régler le dossier (disque externe) et la rétention, puis lancer une **première sauvegarde**. **Où** : un poste ou le PC serveur. **Compte** : titulaire.
  - **Attendu** : un fichier `pharmacie_AAAAMMJJ_HHMMSS.dump` apparaît dans le dossier. Vérifier sa lisibilité :
    ```powershell
    & "C:\Program Files\PostgreSQL\18\bin\pg_restore.exe" --list "E:\Sauvegardes\Pharmacie\<fichier>.dump" > $null
    $LASTEXITCODE
    ```
    Le résultat doit être `0`.
- [ ] **11.3** Option (b) : installer le script. **Où** : PC serveur. **Compte** : administrateur Windows.
  1. Créer le fichier de mot de passe (une seule ligne, mot de passe de `pharma_app` **dans le fichier seulement**) :
     `C:\ProgramData\PharmaOfficine\pgpass.conf`, contenu : `localhost:5432:<base>:pharma_app:<mot de passe>`.
  2. Limiter l'accès au compte qui lance la tâche :
     ```powershell
     icacls C:\ProgramData\PharmaOfficine\pgpass.conf /inheritance:r /grant:r "<compte>:R"
     ```
  3. Adapter les 4 variables en tête du script (`$DbName`, `$DossierLocal`, `$DossierExterne`, `$RetentionJours`) dans la copie livrée. **[À CONFIRMER : valeurs de `D:\` et `E:\` à remplacer.]**
  4. Planifier : Planificateur de tâches, Créer une tâche, déclencheur **chaque soir**, action : programme `powershell.exe`, arguments `-ExecutionPolicy Bypass -File "C:\...\scripts\sauvegarde_pharmacie.ps1"`.
  - **Attendu** : le journal `C:\ProgramData\PharmaOfficine\sauvegarde.log` est créé après la première exécution.
- [ ] **11.4** Tester une restauration **dans une base d'essai**, avant la mise en service puis chaque mois. **Où** : PC serveur. **Compte** : administrateur Windows.
  ```powershell
  .\scripts\restaurer_pharmacie.ps1 -Sauvegardes "E:\Sauvegardes\Pharmacie" -Base pharmacie_essai -SansGed
  ```
  - **Attendu** : le résumé affiche le nombre de ventes et la date de la dernière. La base en service n'est pas touchée.
  - Supprimer ensuite la base d'essai : `& $psql -h localhost -U postgres -d postgres -c "DROP DATABASE pharmacie_essai"`.
  - Voir la procédure complète dans « Restauration » (procédures occasionnelles).

### 12. Reprise des données de l'ancien logiciel (si besoin)

L'ancienne base n'est **jamais** modifiée : on travaille sur une copie. Prérequis : étapes 9.8 (nomenclature) et 10.1 (super admin) faites.

- [ ] **12.1** Super admin : sur l'écran de connexion, « Connexion super admin ». Ce compte ne voit que la reprise de données, et se déconnecte après 10 minutes sans activité. L'**extraction**, le **chargement** et l'**abandon** d'une reprise demandent chacun : un avertissement, le mot de passe ressaisi, puis le **code à 6 chiffres reçu par SMS** (par e-mail si le SMS ne part pas), valable 5 minutes, à usage unique. 3 codes faux bloquent les validations 15 minutes.
- [ ] **12.2** Transit : Administration, Reprise de données (super admin). L'assistant lit l'ancienne base en lecture seule.
  - Sources : fichiers CSV ou Excel (lus sans pilote, encodage UTF-8 ou ANSI et séparateur détectés ; si les accents sont faux, imposer l'encodage dans « Encodage des fichiers »), ou une base accessible par un pilote ODBC installé sur le PC, ou une connexion saisie champ par champ.
  - L'assistant propose la correspondance des tables et des colonnes, à confirmer, puis copie les données dans le schéma `import`.
  - Les mots de passe ne sont ni extraits ni enregistrés.
- [ ] **12.3** Rapprochement des produits : par code-barres, n° d'enregistrement, puis nom approché à confirmer. Un produit absent de la nomenclature est créé « à vérifier » et refusé en caisse jusqu'à la confirmation de sa classe par le titulaire.
- [ ] **12.4** Contrôles : toute anomalie bloquante empêche le chargement.
- [ ] **12.5** Chargement, en une transaction. Un lot sans date de péremption devient « date à vérifier ». Le prix d'achat de chaque lot est repris s'il est dans l'export, sinon celui du produit.
  - Les données chargées (réception de reprise, stock, registre des psychotropes) sont attribuées au **pharmacien titulaire**.
  - Le journal garde l'identifiant du super admin et le canal du code (numéro ou adresse masqués).
- [ ] **12.6** Vérification des totaux (unités, valeur du stock au prix d'achat, crédits clients, dettes grossistes, nombre de clients), avec un rapport signé par le pharmacien. Si les totaux ne concordent pas la veille au soir : **retour arrière** (critère du calendrier).
- [ ] **12.7** Ne **jamais** lancer les fichiers `database\data\9xx_*.sql` pendant la reprise.

### 13. Options

- [ ] **13.1** Accès du titulaire hors de la pharmacie, par VPN : voir [acces-distant.md](acces-distant.md). Ne **jamais** ouvrir l'API sur internet. Si une adresse VPN est utilisée, relancer le script de l'étape 4.6 avec `-AdressesSupplementaires <adresse VPN>`.
- [ ] **13.2** 2e serveur : parcours C (facultatif, fortement conseillé). À faire **après** la présente installation, sur un PC de l'officine.
- [ ] **13.3** Synchronisation avec le serveur central : parcours E. La synchronisation reste **désactivée** tant que le serveur central n'est pas prêt et que le titulaire ne l'a pas activée. Ne pas la toucher avant le jour J.

### 14. Vérifications avant ouverture (J-1)

- [ ] **14.1** Ouvrir une caisse, faire une vente test en espèces, l'annuler, puis clôturer : l'écart doit être nul.
- [ ] **14.2** Imprimer un ticket, une étiquette et un rapport de clôture.
- [ ] **14.3** Vérifier qu'un vendeur ne voit pas ce qui ne lui est pas permis (trésorerie, administration).
- [ ] **14.4** Administration, Contrôle interne, Intégrité des journaux : « Vérifier maintenant ». Le résultat doit être sans anomalie.
- [ ] **14.5** Faire une sauvegarde complète juste avant l'ouverture (étape 11).
- [ ] **14.6** Compléter la [checklist avant ouverture](#checklist-avant-ouverture).

### 15. Jour J et suite

- [ ] **15.1** Ouverture sur le nouveau logiciel, avec accompagnement sur place.
- [ ] **15.2** J+1 à J+30 : l'ancien logiciel reste en **lecture seule**. Étiqueter le stock progressivement (Achats, Étiquettes internes, ou à la fin de chaque inventaire tournant).

---

## Parcours B, mises à jour

Pour une officine **déjà en service**. Ordre : étapes 1 à 14. Ne jamais relancer `schema.sql` ni `clean.sql` sur une base en service.

Les scripts `database\migration\0xx_*.sql` sont réexécutables sans dégât. Ils se lancent **hors transaction**, avec `-v ON_ERROR_STOP=1`.

Les publications étant autonomes (.NET inclus), Windows Update ne corrige **pas** .NET. À chaque correctif de sécurité .NET important, republier l'API et les postes avec le SDK à jour, même sans nouvelle fonction.

- [ ] **1.** Lire la note de version et choisir un créneau **sans caisse ouverte**. **Où** : PC serveur. **Compte** : aucun. Prévenir les employés.
  - **Préparer la livraison avant le créneau** (le service reste en marche pendant ce temps). **Où** : PC des sources. **Compte** : administrateur Windows.
    ```powershell
    dotnet publish src\Pharma.Api -c Release -r win-x64 --self-contained true -o "<dossier de livraison>\Api"
    dotnet publish src\Pharma.Desktop -c Release -r win-x64 --self-contained true -o "<dossier de livraison>\Poste"
    ```
  - Copier `<dossier de livraison>` (avec son dossier `Api\database\migration`) et le dossier `scripts` du dépôt sur la clé USB. Copier aussi `database\migration\111b_roles_integrite.sql` du dépôt (il est exclu du dossier publié) si la séparation des rôles n'est pas encore faite.
  - **Attendu** : `Api\Pharma.Api.exe`, `Api\database\migration\136_synchronisation_corrections_revue.sql` et `Poste\Pharma.Desktop.exe` sont présents sur la clé.
- [ ] **2.** Si l'officine est synchronisée avec le serveur central : **mettre le serveur central à jour d'abord** (parcours E, étape 9), puis l'officine.
- [ ] **3.** Sauvegarder la base avant toute modification. **Où** : PC serveur. **Compte** : `pharma_app`.
  ```powershell
  & "C:\Program Files\PostgreSQL\18\bin\pg_dump.exe" -h localhost -U pharma_app -Fc -f "E:\Sauvegardes\Pharmacie\pharmacie_avant_maj_<AAAAMMJJ>.dump" <base>
  ```
  - Copier aussi `super-admin.dat` et `cle-integrite.dat` (sauvegarde du disque externe, déjà faite à l'étape 4.9 : vérifier qu'elle est à jour).
  - **Attendu** : le fichier `.dump` existe. Vérifier sa lisibilité : `pg_restore --list` renvoie `0` (voir parcours A, étape 11.2).
- [ ] **4.** Arrêter le service. **Où** : PC serveur. **Compte** : administrateur Windows.
  ```powershell
  Stop-Service "PharmaOfficine API"
  Get-Service "PharmaOfficine API"
  ```
  - **Attendu** : `Status : Stopped`.
- [ ] **5.** Vérifier l'état de la base (lecture seule). **Où** : PC serveur. **Compte** : `pharma_app`.
  ```powershell
  & $psql -h localhost -U pharma_app -d <base> -tAc "SELECT count(*) FROM pg_roles WHERE rolname = 'pharma_proprietaire'"
  & $psql -h localhost -U pharma_app -d <base> -tAc "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = current_database()"
  & $psql -h localhost -U pharma_app -d <base> -c "SELECT tableowner, count(*) FROM pg_tables WHERE schemaname = 'public' GROUP BY tableowner"
  ```
  - **Lire le résultat** :
    - la première valeur : `1` = la séparation des rôles (`111b`) est **déjà faite** : aller à l'étape 9 (chemin 2). `0` = pas encore : suivre les étapes 6 à 8 (chemin 1) ;
    - la deuxième et la troisième : le propriétaire de la base et des tables. Si le propriétaire est `pharma_app`, la base n'est pas encore séparée.
- [ ] **6.** **Chemin 1** (séparation pas encore faite) : appliquer les scripts **083 à 100**. **Où** : PC serveur. **Compte** : `pharma_app`.
  ```powershell
  $dossier = "E:\<dossier de livraison>\Api\database\migration"   # préparé à l'étape 1
  Get-ChildItem $dossier -Filter *.sql | Where-Object { $_.Name -match '^(08[3-9]|09\d|100)_' } | Sort-Object Name | ForEach-Object {
      Write-Host $_.Name
      & $psql -h localhost -U pharma_app -d <base> -v ON_ERROR_STOP=1 -q -f $_.FullName
      if ($LASTEXITCODE -ne 0) { throw "Arrêt sur $($_.Name)" }
  }
  ```
  - **Attendu** : chaque nom s'affiche sans erreur.
  - **Si échec** : le script s'arrête au premier fichier en erreur. Corriger la cause, puis relancer la commande (les scripts sont réexécutables).
  - Le script `113` et le script `119` n'existent pas dans le dépôt : c'est voulu.
- [ ] **7.** **Chemin 1** : contrôler les n° de facture Chifa **avant** `101`. **Où** : PC serveur. **Compte** : `pharma_app`. Ouvrir psql en interactif (`& $psql -h localhost -U pharma_app -d <base>`) et lancer ces requêtes en lecture seule. Garder le résultat :
  ```sql
  -- n° de facture Chifa non conformes (8 chiffres). S'il y en a, 101 ne pose pas le contrôle « 8 chiffres » et les affiche : les corriger avec le titulaire, puis relancer 101.
  SELECT id, numero, date_vente, chifa_facture, num_facture_chifa FROM vente
  WHERE chifa_facture !~ '^[0-9]{8}$' OR num_facture_chifa !~ '^[0-9]{8}$' ORDER BY id;
  -- conflits : les deux colonnes remplies et différentes. 101 les range dans vente_chifa_facture_conflit, à trancher par le titulaire (Administration, Conflits de n° de facture Chifa).
  SELECT id, numero, date_vente, num_facture_chifa, chifa_facture FROM vente
  WHERE num_facture_chifa IS NOT NULL AND chifa_facture IS NOT NULL AND btrim(num_facture_chifa) <> btrim(chifa_facture) ORDER BY id;
  -- recopie impossible : n° de l'ancienne colonne déjà porté par une autre vente non annulée.
  SELECT v.id, v.numero, v.num_facture_chifa, a.numero AS deja_sur FROM vente v
  JOIN vente a ON a.id <> v.id AND a.statut <> 'annulee' AND a.chifa_facture = btrim(v.num_facture_chifa)
  WHERE v.chifa_facture IS NULL AND v.statut <> 'annulee';
  ```
  - **Décision** : si une requête renvoie des lignes, **ne pas continuer** avant d'en parler au titulaire. Ces anomalies ne se corrigent pas sans lui.
  - **Attendu** : aucune ligne, ou des lignes examinées et tranchées.
- [ ] **8.** **Chemin 1** : appliquer les scripts **101 à 111** (sauf `111b`). **Où** : PC serveur. **Compte** : `pharma_app`.
  ```powershell
  Get-ChildItem $dossier -Filter *.sql | Where-Object { $_.Name -match '^(10[1-9]|110|111)_' } | Sort-Object Name | ForEach-Object {
      Write-Host $_.Name
      & $psql -h localhost -U pharma_app -d <base> -v ON_ERROR_STOP=1 -q -f $_.FullName
      if ($LASTEXITCODE -ne 0) { throw "Arrêt sur $($_.Name)" }
  }
  ```
  - Après `101` : le contrôle de conflit doit être tranché dans l'application (Administration, Conflits de n° de facture Chifa). Vérifier : `SELECT count(*) FROM vente_chifa_facture_conflit WHERE tranche_le IS NULL;` doit valoir `0`. **[À CONFIRMER : faut-il attendre ce `0` avant la remise en service ?]**
  - `num_facture_chifa` est **conservée** (obsolète). Sa suppression (script `101b`) est destructive, **à valider par le titulaire**. **Le script `101b` n'existe pas dans le dépôt** : rien à lancer. **[À CONFIRMER : écriture de `101b` et décision du titulaire.]**
- [ ] **9.** **Séparation des rôles** (`111b`), **une fois par base**. **Chemin 1** : juste après l'étape 8. **Chemin 2** : si ce n'est pas encore fait.
  - **Où** : PC serveur. **Compte** : **superutilisateur** `postgres`.
    ```powershell
    & $psql -h localhost -U postgres -d <base> -v ON_ERROR_STOP=1 -f "E:\111b_roles_integrite.sql"   # copié à l'étape 1
    ```
  - **Attendu** : la dernière valeur affichée, `objets_encore_a_pharma_app`, vaut **0**.
  - Ensuite, **tous les scripts suivants** (112 à 136) se lancent **avec `postgres` et `SET ROLE pharma_proprietaire`**. Ne plus utiliser `pharma_app` pour les scripts.
  - **Chemin 2** (déjà fait) : passer directement ici, et lancer aussi les scripts 083 à 111 (réexécutables) avec `postgres` et `SET ROLE` (**[À CONFIRMER : méthode retenue : le manuel propose `postgres`, superutilisateur, qui peut prendre le rôle `pharma_proprietaire`]**).
- [ ] **10.** Appliquer les scripts **112 à 136** **avec le rôle propriétaire**. **Où** : PC serveur. **Compte** : `postgres` avec `SET ROLE pharma_proprietaire`.
  ```powershell
  Get-ChildItem $dossier -Filter *.sql | Where-Object { $_.Name -match '^(11[2-9]|1[2-3]\d)_' } | Sort-Object Name | ForEach-Object {
      Write-Host $_.Name
      & $psql -h localhost -U postgres -d <base> -c "SET ROLE pharma_proprietaire" -v ON_ERROR_STOP=1 -q -f $_.FullName
      if ($LASTEXITCODE -ne 0) { throw "Arrêt sur $($_.Name)" }
  }
  ```
  - **[À CONFIRMER : la commande `-c "SET ROLE …"` suivie de `-f` dans la même session doit être testée sur une copie avant la production.]**
  - Si la base est en chemin 2, lancer aussi les scripts 083 à 111 avec le même schéma de commande.
- [ ] **11.** Remplacer les fichiers de l'API. **Où** : PC serveur. **Compte** : administrateur Windows.
  - Utiliser le dossier `Api` préparé à l'étape 1 (clé USB).
  - Renommer l'ancien dossier (`C:\Program Files\PharmaOfficine\Api` devient `Api_ancien`) pour pouvoir revenir en arrière. Copier le nouveau dossier à la place.
  - `appsettings.Https.json` n'est pas dans le dossier publié : **vérifier qu'il est recopié** depuis `Api_ancien` si absent. Les variables d'environnement restent en place.
- [ ] **12.** Démarrer le service et vérifier. **Où** : PC serveur. **Compte** : administrateur Windows.
  ```powershell
  Start-Service "PharmaOfficine API"
  Get-Service "PharmaOfficine API"
  (Invoke-WebRequest http://localhost:5080/api/sante -UseBasicParsing).StatusCode
  ```
  - **Attendu** : `Running`, et `200`.
  - **Si échec** : journal du serveur (annexe, dépannage). Pour revenir en arrière : arrêter le service, remettre `Api_ancien` à la place de `Api`, redémarrer. Les scripts de base déjà appliqués restent (ils sont sans perte).
- [ ] **13.** Mettre à jour les postes. **Où** : chaque poste. **Compte** : administrateur Windows.
  - Fermer Pharma Officine sur le poste, puis remplacer le dossier du poste par le dossier `Poste` préparé à l'étape 1 (clé USB). Les variables `PHARMA_API` et `PHARMA_POSTE` ne changent pas.
  - Si la nomenclature nationale a été mise à jour : Administration, Nomenclature, « Importer le fichier Excel… ». L'import remplace l'ancienne liste sans toucher au catalogue.
- [ ] **14.** Vérifications après mise à jour. **Où** : un poste. **Compte** : titulaire.
  - Une vente test, une clôture de caisse.
  - Administration, Contrôle interne, Intégrité des journaux, « Vérifier maintenant ».
  - Si l'officine a un 2e serveur : lancer `verifier-second-serveur.ps1` sur le 2e serveur (parcours C, étape 12). Les deux serveurs doivent avoir la **même version** (l'écran « 2e serveur » signale un écart en rouge).

---

## Parcours C, 2e serveur

Option facultative, fortement conseillée. L'officine fonctionne sans 2e serveur. Le 2e serveur reçoit **en continu** la base du PC serveur (réplication PostgreSQL en streaming, avec slot), et garde une API de secours **arrêtée**. En cas de panne du principal, une **personne** décide la bascule (procédure F3).

Toujours **une seule base en écriture** : la base du 2e serveur est en lecture seule tant qu'elle n'est pas basculée. Les postes ne l'utilisent pas.

Scripts (dans `scripts\second-serveur\`, à copier sur les deux serveurs avec `commun.ps1` à côté des autres). Ils demandent un administrateur Windows et le superutilisateur PostgreSQL. **Chacun a un mode `-Essai`** qui n'écrit rien : **toujours le lancer d'abord**.

| Script | Où | Rôle |
| --- | --- | --- |
| `preparer-principal.ps1` | PC serveur | Rôle de réplication, `postgresql.conf`, `pg_hba.conf`, slot `second_serveur`, `pg_monitor` pour `pharma_app` |
| `initialiser-second-serveur.ps1` | 2e serveur | `pg_basebackup` avec slot et `-R`, démarrage, contrôle « lecture seule », import de la clé de scellement |
| `verifier-second-serveur.ps1` | l'un ou l'autre | Rôle, réplication, version de l'API, service, empreinte de la clé, dossiers ; inscrit le relevé sur le principal |
| `basculer-second-serveur.ps1` | 2e serveur | **Bascule** (procédure F3) |
| `remettre-en-second.ps1` | ancien principal | **Retour** (procédure F4) |

### À préparer

- [ ] **1.** Matériel et versions. **Où** : les deux serveurs. **Compte** : administrateur Windows.
  - 2e serveur : un PC de l'officine **allumé en permanence**, sur le même réseau local (sans Internet), avec une **IP fixe**.
  - **La même version majeure de PostgreSQL** sur les deux serveurs (parcours A, étape 2.1).
  - **La même version de l'API**, publiée de la même façon (parcours A, étape 4.1), dans `C:\Program Files\PharmaOfficine\Api`. **[À CONFIRMER : livraison officielle de l'API sur le 2e serveur.]**
  - Pare-feu du 2e serveur : port 5080 ouvert, 5443 ouvert aux postes et au PC serveur. Port 5432 seulement pour l'autre serveur (voir étape 4 ci-dessous).

- [ ] **2.** Copier le dossier `scripts\second-serveur` sur les **deux** serveurs. **Où** : les deux serveurs. **Compte** : administrateur Windows. **[À CONFIRMER : dossier de destination.]**

- [ ] **3.** Principal, essai. **Où** : PC serveur. **Compte** : administrateur Windows (+ mot de passe `postgres` demandé).
  ```powershell
  cd "<dossier>\scripts\second-serveur"
  .\preparer-principal.ps1 -AdresseSecond <IP du 2e> -AdressePrincipal <IP du principal> -Essai
  ```
  - **Attendu** : les actions prévues sont affichées, rien n'est modifié.

- [ ] **4.** Principal, lancement réel. **Où** : PC serveur. **Compte** : administrateur Windows.
  ```powershell
  .\preparer-principal.ps1 -AdresseSecond <IP du 2e> -AdressePrincipal <IP du principal>
  ```
  - Noter le **mot de passe du rôle de réplication** (12 caractères au moins, au coffre).
  - Si le script demande un redémarrage de PostgreSQL, ajouter `-Redemarrer` à un moment calme.
  - Ouverture du port 5432 vers le 2e serveur seulement : **[À CONFIRMER : les scripts ne créent pas de règle de pare-feu. Commande proposée à valider : `New-NetFirewallRule -DisplayName "PostgreSQL réplication 2e serveur" -Direction Inbound -Protocol TCP -LocalPort 5432 -RemoteAddress <IP du 2e> -Action Allow`.]`
  - **Attendu** : le script se termine sans erreur.

- [ ] **5.** Principal : déclarer le 2e serveur à l'API. **Où** : PC serveur. **Compte** : administrateur Windows.
  ```powershell
  [Environment]::SetEnvironmentVariable("SecondServeur__AutreServeur", "<IP du 2e>", "Machine")
  Restart-Service "PharmaOfficine API"
  ```
  - Cette variable est équivalente à `"SecondServeur": { "AutreServeur": "<IP du 2e>" }` dans la configuration de l'API. Ne pas écrire la clé à deux endroits.

- [ ] **6.** Titulaire : exporter la clé de scellement. **Où** : un poste ou le PC serveur. **Compte** : titulaire, dans l'application.
  - Administration, 2e serveur, **Clé de scellement** : saisir un mot de passe de 12 caractères au moins (deux fois), choisir le dossier de la clé USB, « Exporter la clé ».
  - Fichier produit : `cle-scellement-AAAAMMJJ-<empreinte>.pocle` (protégé par le mot de passe). La clé n'est jamais en clair.
  - Ranger la clé USB **au coffre** et le mot de passe **à part**.

- [ ] **7.** 2e serveur : mêmes variables que le principal. **Où** : 2e serveur. **Compte** : administrateur Windows.
  - `ConnectionStrings__Pharmacie` : `Host=localhost` (la base locale), même compte `pharma_app`, même mot de passe.
  - `Jwt__Cle` : **la même valeur** que le principal (transmise au coffre, jamais par courriel).
  - `Chifa__DossierExport` : propre à ce PC (étape 4.4 du parcours A).
  - **Ne pas** régler `AutoriserHttpReseau`.
  - Vérifier la longueur de `Jwt__Cle` (64 ou plus) comme à l'étape 4.4 du parcours A.

- [ ] **8.** 2e serveur : certificat HTTPS. **Où** : 2e serveur. **Compte** : administrateur Windows.
  - Refaire l'étape 4.6 du parcours A **sur le 2e serveur**, avec `-NomsSupplementaires <nom du 2e>` et `-AdressesSupplementaires <IP du 2e>`.
  - **[À CONFIRMER, point bloquant : le script crée une autorité de certification **par PC**. Les postes ne font confiance qu'à l'autorité installée à l'étape 5.4. Il faut décider : installer sur le 2e serveur **la même autorité** que le PC serveur (le script ne sait pas exporter la clé privée de l'autorité : procédure à définir), ou réinstaller l'autorité du 2e serveur sur tous les postes et téléphones.]**
  - Sans réponse, ne pas basculer les postes vers le 2e serveur en HTTPS.

- [ ] **9.** 2e serveur : initialiser la réplique. **Où** : 2e serveur. **Compte** : administrateur Windows (+ mot de passe `postgres`, mot de passe de réplication).
  ```powershell
  cd "<dossier>\scripts\second-serveur"
  .\initialiser-second-serveur.ps1 -Principal <IP du principal> -FichierCle E:\cle-scellement-<...>.pocle -Essai
  .\initialiser-second-serveur.ps1 -Principal <IP du principal> -FichierCle E:\cle-scellement-<...>.pocle
  ```
  - Le script demande de taper `OUI`, puis le mot de passe du rôle de réplication, puis **le titulaire** tape le mot de passe du fichier de clé.
  - **Attendu** : le contrôle « lecture seule » réussit. Le service de l'API reste **arrêté**.
  - **Si échec** : relire le message du script. Ne pas relancer sans comprendre la cause (il peut réinitialiser le dossier de données ; l'ancien dossier est gardé).

- [ ] **10.** 2e serveur : déclarer le principal, service en démarrage manuel. **Où** : 2e serveur. **Compte** : administrateur Windows.
  ```powershell
  [Environment]::SetEnvironmentVariable("SecondServeur__AutreServeur", "<IP du principal>", "Machine")
  Set-Service "PharmaOfficine API" -StartupType Manual
  Get-Service "PharmaOfficine API"
  ```
  - **Attendu** : `Stopped`. Le démarrage est fait seulement en cas de bascule.

- [ ] **11.** Titulaire : réglages propres à chaque serveur. **Où** : application, sur le PC serveur. **Compte** : titulaire.
  - Administration, 2e serveur, **Réglages par serveur** : cliquer sur un serveur, régler son dossier d'échange **CHIFA-Officine** et son dossier du **logiciel MDN** (vide : réglages `Chifa__DossierExport` et `Mdn:Dossier` de ce serveur), cocher « CHIFA-Officine installé », « Lecteur de carte et Token disponibles », « Logiciel MDN installé », « Douchette branchée et testée ».
  - Ces réglages sont dans la base (table `serveur_officine`) et sont répliqués. À la bascule, l'API du 2e serveur prend **les dossiers de sa propre ligne**.

- [ ] **12.** Contrôler la réplique. **Où** : 2e serveur. **Compte** : administrateur Windows (+ mot de passe de `pharma_app` pour inscrire le relevé).
  ```powershell
  cd "<dossier>\scripts\second-serveur"
  .\verifier-second-serveur.ps1 -Principal <IP du principal> -DossierChifa "<dossier CHIFA du 2e>" -DossierMdn "<dossier MDN du 2e>"
  ```
  - Puis, titulaire : Administration, 2e serveur, **État de la réplication** : « 2e serveur connecté », retard en secondes et en octets, slot actif. Réglages par serveur : versions identiques, clé présente et **identique**, dossiers joignables.
  - **Attendu** : tout est vert. Un écart de version est affiché en rouge.

- [ ] **13.** Postes : adresse de secours. **Où** : chaque poste. **Compte** : titulaire.
  - Administration, Imprimantes et étiquettes, **Serveur et PC de secours** : « PC de secours » = adresse de l'API du 2e serveur, en HTTPS : `https://<IP du 2e>:5443/`. **[À CONFIRMER : le manuel précédent donnait `http://…:5080/`, incompatible avec la décision HTTPS seul.]**
  - Délai avant le bandeau : 60 s par défaut.

- [ ] **14.** Alertes. **Où** : application. **Compte** : titulaire.
  - Le titulaire est prévenu (SMS ou e-mail des alertes de sauvegarde, au plus une fois par heure) si le 2e serveur décroche ou si le retard dépasse les seuils (300 s et 64 Mo par défaut). Vérifier les seuils dans l'écran « 2e serveur ».
  - **Rattrapage** : au redémarrage de l'un ou l'autre PC, la réplication reprend seule.
  - **Limite** : un 2e serveur éteint longtemps fait grossir le retard sur le principal, jusqu'à `max_slot_wal_keep_size` (20 Go par défaut). Au-delà, le 2e serveur doit être **réinitialisé** (étape 9).

---

## Parcours D, PC de secours

Option facultative, **fortement conseillée**. Un PC de l'officine (souvent un poste de caisse) reçoit une **copie de secours** de la base, à chaque sauvegarde. En cas de panne du PC serveur, la pharmacie repart dessus (procédure F2).

Les copies ne sont **pas chiffrées** : les ranger comme les sauvegardes (accès restreint au dossier).

- [ ] **1.** Choisir le PC de secours (PC de l'officine, allumé et branché). **Où** : sur place. **Compte** : aucun.
- [ ] **2.** PostgreSQL, même version majeure que le PC serveur (parcours A, étape 2.1). **Où** : PC de secours. **Compte** : administrateur Windows.
- [ ] **3.** Compte `pharma_app` avec `CREATEDB` (parcours A, étape 2.2), **sans créer de base**. **Où** : PC de secours. **Compte** : superutilisateur `postgres`.
  ```sql
  CREATE ROLE pharma_app LOGIN CREATEDB;
  \password pharma_app
  ```
  Créer aussi le rôle `pharma_proprietaire` (sans connexion) : `CREATE ROLE pharma_proprietaire NOLOGIN;`. **[À CONFIRMER : la restauration rejoue les propriétaires des objets ; les rôles doivent exister avant la restauration.]**
- [ ] **4.** API à la **même version** que le PC serveur, dans `C:\Program Files\PharmaOfficine\Api` (parcours A, étape 4.1 et 4.2). **Où** : PC de secours. **Compte** : administrateur Windows.
- [ ] **5.** Variables de l'API (parcours A, étape 4.4) : `ConnectionStrings__Pharmacie` (base `<base>`, même compte, même mot de passe), `Jwt__Cle` **[À CONFIRMER : même valeur que le PC serveur ? Sinon, reconnexion obligatoire après bascule.]**, `Chifa__DossierExport` propre à ce PC.
- [ ] **6.** Certificat HTTPS : refaire le parcours A, étape 4.6, **sur le PC de secours**, avec `-NomsSupplementaires <nom du PC de secours> -AdressesSupplementaires <IP du PC de secours>`. **[Même point bloquant que le 2e serveur (parcours C, étape 8) : l'autorité est propre à chaque PC.]**
- [ ] **7.** Service de l'API : créer, puis **arrêter et passer en démarrage manuel** (la bascule le démarre). **Où** : PC de secours. **Compte** : administrateur Windows.
  ```powershell
  New-Service -Name "PharmaOfficine API" -BinaryPathName '"C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe"' -DisplayName "PharmaOfficine API" -StartupType Manual
  Get-Service "PharmaOfficine API"
  ```
  - **Attendu** : `Stopped`.
- [ ] **8.** Clé de scellement : l'importer sur le PC de secours. **Où** : PC de secours. **Compte** : administrateur Windows. Le titulaire tape le mot de passe du fichier.
  ```powershell
  & "C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe" --importer-cle E:\cle-scellement-<...>.pocle
  & "C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe" --empreinte-cle
  ```
  - **Attendu** : l'empreinte affichée est **la même** que sur l'écran du titulaire.
  - Ajouter `--remplacer` seulement si le titulaire le demande. **[À CONFIRMER : comportement de `--importer-cle` si une clé existe déjà.]**
  - L'API refuse de démarrer si la clé est illisible : elle n'est jamais remplacée en silence.
- [ ] **9.** Dossier partagé de la copie de secours. **Où** : PC de secours. **Compte** : administrateur Windows.
  ```powershell
  New-Item -ItemType Directory -Force "D:\CopieSecours" | Out-Null
  New-SmbShare -Name CopieSecours -Path "D:\CopieSecours" -FullAccess "<compte autorisé>"
  ```
  - **[À CONFIRMER : le service de l'API du PC serveur tourne en compte système local par défaut ; il accède au partage avec le compte de l'ordinateur. Quel compte donner ? Tester.]**
  - **Attendu** : le partage existe : `\\<PC de secours>\CopieSecours`.
- [ ] **10.** PC serveur : configurer la copie de secours. **Où** : application, sur le PC serveur. **Compte** : titulaire.
  - Administration, Sauvegardes, **Copie de secours** : dossier `\\<PC de secours>\CopieSecours`, nombre de copies conservées, délai d'alerte (26 h par défaut).
  - Après chaque sauvegarde réussie, le fichier est copié, puis **vérifié** (taille et empreinte SHA-256). L'empreinte est notée à côté (`<copie>.sha256`).
  - Échec de copie ou dernière copie trop vieille : le titulaire est prévenu. Réglages et état : `C:\ProgramData\PharmaOfficine\copie-secours.json`.
- [ ] **11.** Vérifier la première copie. **Où** : PC de secours. **Compte** : administrateur Windows.
  ```powershell
  Get-ChildItem "D:\CopieSecours" | Select-Object Name, Length
  ```
  - **Attendu** : un `pharmacie_*.dump` et son fichier `.sha256`.
- [ ] **12.** Test de la copie, **chaque mois** : titulaire, Administration, Sauvegardes, **« Tester la restauration de la dernière copie »**. Résultat affiché et inscrit au journal. La base en service n'est pas touchée.
  - Simulation sur le PC de secours, sans rien modifier :
    ```powershell
    .\basculer-pc-secours.ps1 -Copies "D:\CopieSecours" -Essai
    ```
    Le script se trouve dans `scripts\` (étape 4.3 du parcours A). **[À CONFIRMER : copie du script sur le PC de secours.]**
- [ ] **13.** Postes : adresse de secours (parcours C, étape 13) : `https://<IP du PC de secours>:5443/`, délai 60 s. **Où** : chaque poste. **Compte** : titulaire.

---

## Parcours E, Serveur central de synchronisation

Option facultative. Le serveur central reçoit la copie des données des officines (`docs/conception-synchronisation.md`, section 12). **Il tourne pour l'instant sur le PC de développement** (base `pharmacie_cloud_dev`, port 5090). L'hébergement réel est **[À VÉRIFIER : hébergeur agréé]**.

Règles : aucun secret dans un fichier du dépôt ni dans `appsettings*.json`. Tout passe par des variables d'environnement du compte de service. **Ne jamais copier le dossier `Synchronisation` d'un serveur d'officine à l'autre.**

Les scripts de la base centrale sont dans `database\cloud\` (001 à 009). Ils ne se passent **jamais** sur la base d'une officine.

### 1. Hébergement et prérequis

- [ ] **1.1** Hébergement agréé. **[À VÉRIFIER : hébergeur, emplacement, exposition réseau, reverse proxy.]**
- [ ] **1.2** PostgreSQL sur ce serveur (même version majeure que les officines, ou plus récente). **Où** : serveur central. **Compte** : administrateur.
- [ ] **1.3** Un **compte propriétaire** de la base (distinct du rôle applicatif) : **[À CONFIRMER : nom du compte propriétaire et nom de la base de production.]**

### 2. Base centrale

- [ ] **2.1** Créer la base. **Où** : serveur central. **Compte** : superutilisateur `postgres`.
  ```sql
  CREATE DATABASE <base centrale> OWNER <propriétaire> ENCODING 'UTF8' TEMPLATE template0;
  ```
- [ ] **2.2** Lancer les scripts **dans l'ordre**, avec le compte propriétaire. **Où** : serveur central. **Compte** : `<propriétaire>`.
  ```powershell
  foreach ($f in "001_base_centrale.sql", "002_tables_miroir.sql", "003_chargement.sql", "004_reglages.sql", "005_descendant.sql", "006_debit_chargement.sql", "007_familles_banque_clients.sql", "008_famille_banque_elargie.sql", "009_corrections_revue.sql") {
      & $psql -h <hôte> -U <propriétaire> -d <base centrale> -v ON_ERROR_STOP=1 -q -f "database\cloud\$f"
      if ($LASTEXITCODE -ne 0) { throw "Arrêt sur $f" }
  }
  ```
  - `001` crée le rôle applicatif `pharma_cloud_api` **sans connexion**.
  - **Attendu** : aucune erreur.
- [ ] **2.3** Tests (en transaction annulée). **Où** : serveur central. **Compte** : `<propriétaire>`. Lancer, chacun doit finir par « tous réussis » :
  ```powershell
  foreach ($t in "001_base_centrale_tests.sql", "003_chargement_tests.sql", "004_reglages_tests.sql", "005_descendant_tests.sql", "006_debit_chargement_tests.sql", "007_familles_banque_clients_tests.sql", "008_famille_banque_elargie_tests.sql", "009_corrections_revue_tests.sql") {
      & $psql -h <hôte> -U <propriétaire> -d <base centrale> -v ON_ERROR_STOP=1 -q -f "database\cloud\tests\$t"
  }
  ```
  - Il n'existe pas de fichier de test pour `002`. **[À CONFIRMER : faut-il lancer les tests sur la base de production, ou seulement sur une copie ?]**
- [ ] **2.4** Mot de passe du rôle applicatif. **Où** : serveur central. **Compte** : superutilisateur `postgres`.
  ```sql
  ALTER ROLE pharma_cloud_api LOGIN;
  \password pharma_cloud_api
  ```
  - Mot de passe tiré au sort, noté au coffre, **jamais** dans un script.
  - `pharma_cloud_api` n'est **pas** propriétaire des tables (la sécurité au niveau des lignes s'applique).

### 3. Compte de service et variables

- [ ] **3.1** Créer le dossier de l'autorité **hors de tout dépôt de sources**, par exemple `D:\PharmaCentral\Autorite`. Accès réservé au compte du service et aux administrateurs. **Où** : serveur central. **Compte** : administrateur Windows.
  ```powershell
  New-Item -ItemType Directory -Force "D:\PharmaCentral\Autorite" | Out-Null
  icacls "D:\PharmaCentral\Autorite" /inheritance:r /grant:r "Administrateurs:(OI)(CI)F" "<compte du service>:(OI)(CI)M"
  ```
- [ ] **3.2** Variables d'environnement. **Où** : serveur central. **Compte** : administrateur Windows. Pour les commandes d'administration, les mettre dans la **session** PowerShell :
  ```powershell
  $env:PHARMA_CLOUD_DOSSIER = "D:\PharmaCentral\Autorite"
  $env:PHARMA_CLOUD_MOT_DE_PASSE = "<mot de passe des clés, 16 caractères au moins>"
  $env:PHARMA_CLOUD_CONNEXION = "Host=<hôte>;Port=5432;Database=<base centrale>;Username=pharma_cloud_api;Password=<mot de passe>"
  $env:PHARMA_CLOUD_ADMIN_CONNEXION = "Host=<hôte>;Port=5432;Database=<base centrale>;Username=<propriétaire>;Password=<mot de passe>"
  ```
  - `PHARMA_CLOUD_MOT_DE_PASSE` : **sans lui, l'autorité est perdue**. À ranger au coffre.
  - `PHARMA_CLOUD_ADMIN_CONNEXION` : outil d'administration seulement, jamais le service.
  - Pour le **service** qui tourne en continu, ces variables doivent être réglées au niveau de la machine ou du compte de service. **[À CONFIRMER : le code ne fournit pas de service Windows pour le serveur central ; mode de lancement et compte du service à définir.]**
- [ ] **3.3** Publier le serveur central. **Où** : PC des sources. **Compte** : administrateur Windows. **[À CONFIRMER : même forme que l'API, `dotnet publish src\Pharma.Cloud.Api -c Release -r win-x64 --self-contained true -o "<dossier>\Cloud"`, à valider.]**

### 4. Autorité et certificat du serveur central

- [ ] **4.1** Créer l'autorité (une seule fois). **Où** : serveur central. **Compte** : administrateur Windows.
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --initialiser-autorite
  ```
  - **Attendu** : l'empreinte SHA-256 de l'autorité s'affiche. **La noter.** L'autorité n'est jamais remplacée.
  - Sauvegarder séparément `autorite.crt`, `autorite.cle` (chiffrée) et le mot de passe.
- [ ] **4.2** Créer le certificat HTTPS du serveur (2 ans, à refaire avant l'échéance). **Où** : serveur central. **Compte** : administrateur Windows.
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --certificat-serveur --noms <nom DNS>,<adresse IP>
  ```

### 5. Démarrage

- [ ] **5.1** Réglages de l'adresse d'écoute dans la configuration (`Cloud:Adresse`, `Cloud:Port`). Par défaut : `127.0.0.1`, port `5090`. **[À VÉRIFIER : adresse réelle et exposition par reverse proxy à l'hébergement. Ne pas mettre `0.0.0.0` sans avoir vérifié.]**
- [ ] **5.2** Démarrer : `& "<dossier>\Cloud\Pharma.Cloud.Api.exe"`. **Où** : serveur central. **Compte** : compte de service.
  - Seul le port HTTPS est ouvert. La base n'est jamais exposée.
  - **Attendu** : le serveur écoute sans erreur. **[À CONFIRMER : point de contrôle de santé (aucune route de santé n'est prévue dans le code).]**

### 6. Tâches planifiées

- [ ] **6.1** Chaque mois : `Pharma.Cloud.Api.exe --partitions` (partitions mensuelles des lots reçus). **Où** : serveur central. **Compte** : compte de service. Planificateur de tâches.
- [ ] **6.2** Chaque jour (facultatif) : `Pharma.Cloud.Api.exe --purger-lots`. **Sans effet** tant que l'effacement des lots trop anciens n'est pas activé (défaut). Voir les réglages (étape 7.3).

### 7. Réglages du serveur central (titulaire et technicien)

Outil d'administration (`PHARMA_CLOUD_ADMIN_CONNEXION`). Chaque réglage est inscrit au journal de sécurité. `Pharma.Cloud.Api.exe --reglages` affiche tout.

- [ ] **7.1** Familles de données nominatives. Défauts : `employes`, `banque` et `clients_factures` **non** envoyées ; `pharmacie`, `fournisseurs`, `courriel_commandes` **oui**.
  - Valeur globale : `Pharma.Cloud.Api.exe --regler-famille <famille> oui|non`
  - Surcharge pour une officine : `--regler-famille <famille> oui|non --officine <code>`
  - Retour à la valeur globale : `--regler-famille <famille> global --officine <code>`
  - Le réglage **descend** dans la base de l'officine à son prochain passage (une minute environ). Visible en lecture seule dans l'officine : Administration, Synchronisation, Réglages.
  - Décocher une famille ne supprime rien. Pour effacer ce qui a déjà été reçu : `--effacer-famille <famille> [--officine <code>]` affiche d'abord ce qui serait effacé (simulation). L'effacement a lieu **seulement** avec `--confirmer <famille>`. **C'est une suppression de données : validation du titulaire obligatoire.**
  - Cocher de nouveau une famille ne renvoie pas les valeurs déjà retirées (elles repartent à la prochaine modification, ou par « Recharger toutes les données »).
- [ ] **7.2** Conservation des lots bruts : 6 ans par défaut, **6 ans minimum** (refus en dessous), pas de maximum.
  - `--conservation <ans>` (globale), `--conservation <ans> --officine <code>` (surcharge), `--conservation global --officine <code>` (retour).
- [ ] **7.3** Effacement des lots trop anciens : **désactivé par défaut**. `--purger-lots --simulation` compte ce qui serait effacé. `--purge-lots activer` (ou `desactiver`) pour changer le réglage. **[À VÉRIFIER avant d'activer : durées réglementaires (6 ans fiscaux ; registre des psychotropes : durée à confirmer).]**
- [ ] **7.4** Plafonds (valeurs à connaître) :
  - `cloud_parametre.quota_octets_jour` : 512 Mio par jour par officine (`UPDATE cloud_parametre SET quota_octets_jour = <octets>`) ;
  - `cloud_parametre.quota_chargement_octets` : 8 Gio par chargement complet (`UPDATE cloud_parametre SET quota_chargement_octets = <octets>`) ;
  - sur 24 h glissantes : `chargements_max_24h` et `quota_chargements_octets_24h` (script `009`) ;
  - débit : 60 lots par minute par certificat, 600 pendant un chargement (`Cloud:LotsParMinute`, `Cloud:LotsParMinuteChargement`).
- [ ] **7.5** Renouvellement des certificats d'officine : accepté seulement dans les 60 derniers jours, une fois par jour au plus.
- [ ] **7.6** Révocation : voir procédure F5.
- [ ] **7.7** Proxy inverse : les limites par adresse lisent l'adresse de la connexion. Derrière un proxy, c'est celle du proxy. **[À VÉRIFIER à l'hébergement.]** Réglage : `Cloud:LimiteParAdresseMinute` (600 par défaut).

### 8. Activer une officine

Ordre pour **chaque** officine.

- [ ] **8.1** Créer l'officine. **Où** : serveur central. **Compte** : compte d'administration.
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --creer-officine "<nom commercial>"
  ```
  - Affiche le **code provisoire** de l'officine et un **code d'activation** du serveur principal (usage unique, 24 h).
  - Pour le 2e serveur de la même officine : `Pharma.Cloud.Api.exe --code-activation <code officine> --serveur second` (son propre code, son propre certificat).
- [ ] **8.2** Transmettre le code au titulaire **par un canal sûr**, jamais dans le même message que le nom de l'officine.
- [ ] **8.3** Régler la synchronisation dans l'officine. **Où** : PC serveur de l'officine, application. **Compte** : titulaire (droit `admin.parametres`).
  - Administration, Synchronisation, sous-rubrique **Réglages** : adresse du serveur central (`https://…`), cocher « Synchronisation activée », Enregistrer.
- [ ] **8.4** Activer. Même écran, sous-rubrique **Activation** : coller le code, Activer.
  - La clé privée est créée sur le PC serveur (`C:\ProgramData\PharmaOfficine\Synchronisation`, protection DPAPI machine). Elle ne quitte jamais ce PC.
  - À la première activation, le serveur central adopte le code de l'officine installée.
- [ ] **8.5** Vérifier dans **État** : « activé (certificat) », dernier envoi et dernier accusé à jour en une à deux minutes.
- [ ] **8.6** Activer par **groupes progressifs** (ex. 10 officines, puis 100, puis le reste). Attendre la fin du chargement complet de chaque groupe avant le suivant (État : chargement « terminé ». Central : table `chargement`, état `termine`).

### Procédure d'activation des officines

Ce qui se passe à la première activation :

1. Le serveur central adopte le code de l'officine installée (`sync_parametre.officine_id`).
2. Le réglage des familles nominatives descend dans la base de l'officine (table `reglage_central`). Tant qu'il n'est pas reçu, rien de nominatif ne part.
3. Un **chargement complet** part : toutes les données envoyées. Il est débité à 600 lots par minute, et ses lots ne comptent pas dans le quota du jour.
4. Ensuite, seules les modifications partent, chaque minute.

Une officine installée ou mise à jour **avant** le serveur central n'envoie rien : la synchronisation est désactivée par défaut, aucun déclencheur, aucun coût sur les ventes.

**Compatibilité des versions** : le serveur central accepte la version actuelle du contrat et la précédente. Aujourd'hui, une seule version existe et une seule est acceptée (lots `sync-v1`, éléments descendants `desc-v1`). Ordre de mise à jour : **serveur central d'abord**, officines ensuite.

**Volume** : compter environ 10 000 lignes par minute (mesure faite sur la base de démonstration : 34 000 lignes). Vérifier le quota journalier avant le chargement d'une grosse officine. **[À VÉRIFIER par mesure.]**

**Fin de chargement** : le serveur central marque « absentes de l'officine » (`sync_absente_le`) les lignes qu'il a et que l'officine n'a plus. Il ne les efface **jamais**. Le titulaire décide (ressaisie, annulation tracée ou justification). Détail : table `chargement` (`absentes_par_table`) et journal de sécurité (`chargement_absentes`).

### Chargement complet (quand, et que faire)

- **Quand** : à la première activation, à la réactivation (les modifications faites pendant la désactivation ne sont pas inscrites), sur demande (Administration, Synchronisation, État, « Recharger toutes les données », droit `admin.parametres`, confirmation), et automatiquement si le serveur central a perdu des lots que l'officine n'a plus.
- **Effet** : l'état actuel de chaque donnée envoyée repart, minimisé (codes patients, colonnes retirées, textes filtrés), par lots de 500 lignes ou 1 Mo. Le travail continue : les ventes ne sont retenues que le temps d'un verrou court (2 s au plus).
- **Suivi** : la progression s'affiche (lignes accusées / total). Après une coupure, l'envoi reprend au premier lot non accusé.

### Lots manquants

Le serveur central a perdu des lots déjà accusés (restauration du central) :

- si l'officine les a encore (`sync_lot`), elle les renvoie à l'identique, sans intervention ;
- sinon, elle demande **d'elle-même** un chargement complet (message « rechargement complet de l'officine demandé »). Au passage suivant, l'instantané est pris et annoncé. Le central abandonne les lots perdus (journal de sécurité `lots_abandonnes`) et l'envoi reprend normalement. **Ne rien supprimer dans l'officine.**
- si le même message revient alors qu'un chargement « lots manquants » est déjà annoncé (« reprise impossible automatiquement … procédure »), comparer `officine_epoque`, `lot_recu` et `chargement` du central avec `sync_lot` et `sync_chargement` de l'officine. Les opérations restent en attente dans l'officine : rien n'est perdu. **Appeler le développeur.**
- **Restauration de la base de l'officine** (n° de lots revenus en arrière : lots « déjà reçus avec un autre contenu ») : nouvelle époque, **non réalisée** à ce jour. **[À FAIRE]**

### 9. Mise à jour du serveur central

- [ ] **9.1** Sauvegarder la base centrale. **Où** : serveur central. **Compte** : propriétaire.
- [ ] **9.2** Repasser `002` (colonnes `sync_chargement`, `sync_absente_le`, etc.), puis `003` à `009`, **dans cet ordre**, avec le compte propriétaire (même boucle qu'à l'étape 2.2, en partant de `002`). Scripts idempotents.
- [ ] **9.3** Relancer les tests de l'étape 2.3 sur la copie.
- [ ] **9.4** Redémarrer le serveur central. **Avant** toute mise à jour des officines.

---

## Procédures occasionnelles

Chaque procédure se lit dans l'ordre, du haut vers le bas.

### F1. Restauration (base ou GED détruite)

Les sauvegardes utiles sont sur le **disque externe**. La copie de la base dans `GED\Sauvegardes` est sur le même PC : elle disparaît avec lui.

- Base : `pharmacie_AAAAMMJJ_HHMMSS.dump` dans le dossier réglé dans Administration, Sauvegardes (les 14 dernières par défaut).
- GED : sous-dossier `GED` de ce même dossier.

**Méthode 1 : depuis l'application** (super admin, étape 10). L'API doit tourner sur le PC serveur. La base peut être détruite : le compte du super admin et le dossier des sauvegardes sont hors de la base.

- [ ] **1.** Sur un poste, « Connexion super admin », écran **Restauration**.
- [ ] **2.** **Base** : choisir la sauvegarde (disque externe ou `GED\Sauvegardes`, la plus récente en haut), puis valider : avertissement, mot de passe, code reçu par SMS (e-mail en secours).
- [ ] **3.** Le serveur, en tâche de fond, met les postes en attente, fait une **copie de sécurité** de la base actuelle (`GED\Sauvegardes\pharmacie_avant_restauration_….dump`, à supprimer à la main quand elle n'est plus utile), restaure la sauvegarde dans une base neuve, la **met à la version du logiciel** (scripts `0xx` livrés avec l'API), puis lui donne le nom de la base en service. L'ancienne est **gardée** sous `<base>_avant_restauration_AAAAMMJJ_HHMM`. L'écran suit les étapes.
  - Un échec avant l'échange laisse la base en service intacte.
  - Il faut le droit `CREATEDB` du compte `pharma_app` (parcours A, étape 2.2).
- [ ] **4.** **GED** : « Recopier les documents de la GED ». Les documents du disque externe absents ou plus anciens au serveur sont recopiés. Rien n'est supprimé.
- [ ] **5.** Contrôler les totaux et ressaisir les opérations faites depuis la sauvegarde (en général la journée). La restauration est inscrite au journal de la base restaurée.

**Méthode 2 : sans l'application** (API hors service, PC serveur remplacé).

- [ ] **1.** PC serveur remplacé : installer PostgreSQL (parcours A, étape 2) et recréer le compte `pharma_app` avec `CREATEDB`. **Ne pas** lancer `schema.sql` : la sauvegarde contient toute la structure.
- [ ] **2.** Arrêter le service : `Stop-Service "PharmaOfficine API"` (s'il existe).
- [ ] **3.** Lancer, dans PowerShell sur le PC serveur (le mot de passe de `postgres` est demandé) :
  ```powershell
  .\scripts\restaurer_pharmacie.ps1 -Sauvegardes "E:\Sauvegardes\Pharmacie"
  ```
  - La dernière sauvegarde est vérifiée (`pg_restore --list`) puis restaurée. Une sauvegarde illisible est refusée : en choisir une plus ancienne avec `-Fichier "<fichier>.dump"`.
  - Une base existante du même nom n'est **jamais** supprimée : le script s'arrête, ou, avec `-Remplacer`, la renomme `pharmacie_avant_restauration_AAAAMMJJ_HHMM`.
  - La GED est recopiée dans `C:\ProgramData\PharmaOfficine\GED` sans rien supprimer, en gardant un document plus récent déjà présent.
  - `-SansGed` ou `-SansBase` pour ne restaurer que l'une des deux. Les paramètres `-Base`, `-DossierGed`, `-PgBin`, `-HostPg`, `-Port` existent aussi.
  - **Attendu** : le script se termine sans erreur et affiche le nombre de ventes et la date de la dernière.
- [ ] **4.** Sauvegarde plus ancienne que la version du logiciel : appliquer les scripts `database\migration\0xx_*.sql` plus récents (parcours B, étapes 5 à 10).
- [ ] **5.** Démarrer le service et vérifier : `Start-Service "PharmaOfficine API"`, puis `http://localhost:5080/api/sante`. Les documents restés sur les postes (« A envoyer ») repartent dans la GED à la connexion suivante.
- [ ] **6.** Ressaisir les opérations faites depuis la sauvegarde. Sur un nouveau PC, ressaisir le mot de passe des notifications (chiffré pour le PC d'origine).
- [ ] **7.** Si le PC serveur a changé : refaire le parcours A, étapes 4.6 et 4.9. **L'autorité HTTPS est propre au PC** : relancer le script crée une nouvelle autorité. Il faut alors **réinstaller** `autorite-pharmacie.crt` sur tous les postes et téléphones (parcours A, étapes 5.3 à 5.4 et 6.2). **[Constaté dans le script : clé de l'autorité non exportable.]**

> La copie de la GED sur le disque externe n'a qu'un exemplaire par document : un document abîmé sur le serveur remplace sa copie à la sauvegarde suivante. Garder deux disques externes en rotation.

### F2. PC serveur en panne (bascule sur le PC de secours)

Prérequis : parcours D fait. La pharmacie repart à partir de la **dernière copie de secours**. La bascule fait perdre les opérations faites depuis cette copie.

- [ ] **1.** Constater la panne. Sur les postes, bandeau « Serveur injoignable » après le délai réglé. **Vérifier d'abord le PC serveur** : allumé, réseau, service `PharmaOfficine API`. Si le PC serveur répond, ne pas basculer.
- [ ] **2.** Sur le PC de secours, PowerShell **en administrateur**, dans le dossier `scripts` :
  ```powershell
  .\basculer-pc-secours.ps1 -Copies "D:\CopieSecours" -Essai
  ```
  - Simulation : contrôles seulement, rien n'est modifié. Vérifier le résultat.
- [ ] **3.** Lancer la bascule (le mot de passe de `postgres` est demandé) :
  ```powershell
  .\basculer-pc-secours.ps1 -Copies "D:\CopieSecours"
  ```
  - Le script prend la copie la plus récente (ou `-Fichier`), contrôle l'empreinte SHA-256 et la lisibilité, restaure dans une base neuve, **rejoue tous les scripts de la base** livrés avec l'API (le dernier rejoué est affiché : il doit être le dernier de la version installée), garde une base existante sous `<base>_avant_bascule_<date>` (jamais supprimée), démarre le service et affiche l'adresse à donner aux postes.
  - **Attendu** : le script se termine sans erreur et affiche l'adresse du PC de secours.
- [ ] **4.** Sur **chaque poste** : bouton **« Basculer sur le 2e serveur »** du bandeau (il ne règle le poste que si l'API de secours répond déjà), ou rubrique Serveur et PC de secours. Puis fermer et rouvrir Pharma Officine.
- [ ] **5.** GED : recopier le dossier `GED` du disque externe si besoin : `.\scripts\restaurer_pharmacie.ps1 -SansBase` (paramètre `-DossierGed` du PC de secours).
- [ ] **6.** Ressaisir les opérations faites depuis la copie. Ressaisir le mot de passe des notifications.
- [ ] **7.** Retour sur le PC serveur réparé : procédure F4.

### F3. Bascule du 2e serveur

Prérequis : parcours C fait. Décision du titulaire.

- [ ] **1.** Constater la panne (bandeau « Serveur injoignable » sur les postes). Vérifier le PC principal (allumé, réseau, services). Le titulaire décide.
- [ ] **2.** Sur le PC principal, si possible : arrêter le service de l'API et PostgreSQL, ou **débrancher son câble réseau**.
- [ ] **3.** Sur le 2e serveur, PowerShell **en administrateur**, dossier `scripts\second-serveur` :
  ```powershell
  .\basculer-second-serveur.ps1 -AncienPrincipal <IP du principal> -Essai
  .\basculer-second-serveur.ps1 -AncienPrincipal <IP du principal>
  ```
  - Le script **refuse** si l'ancien principal répond encore (PostgreSQL ou API). Il demande de taper `BASCULER`.
  - **Attendu** : le script affiche l'heure de la dernière transaction reçue. Ce qui a été saisi après (rare) est à ressaisir.
- [ ] **4.** Sur chaque poste : bouton **« Basculer sur le 2e serveur »**, puis fermer et rouvrir Pharma Officine.
- [ ] **5.** Titulaire : écran « 2e serveur ». Ce serveur est maintenant « Principal ». Vérifier les dossiers Chifa et MDN, la clé (identique). **CHIFA-Officine, lecteur de carte et Token** : à confirmer sur ce PC (question posée à la CNAS, `docs/taches/2026-11-15-recette-complete/demande-cnas.md`).
- [ ] **6.** Le principal ne doit **jamais** redevenir actif sans la procédure F4 : l'API refuse de démarrer si l'autre serveur a aussi une base principale.

### F4. Retour du serveur réparé

- [ ] **1.** **Ne pas redémarrer l'API de l'ancien principal** : elle refuserait de démarrer si `SecondServeur__AutreServeur` est réglé.
- [ ] **2.** Sur le **nouveau** principal, PowerShell en administrateur :
  ```powershell
  .\preparer-principal.ps1 -AdresseSecond <IP de l'ancien> -AdressePrincipal <IP du nouveau> -Essai
  .\preparer-principal.ps1 -AdresseSecond <IP de l'ancien> -AdressePrincipal <IP du nouveau>
  ```
  (slot de réplication, `pg_hba.conf`).
- [ ] **3.** Sur l'**ancien** principal :
  ```powershell
  .\remettre-en-second.ps1 -NouveauPrincipal <IP du nouveau> -Essai
  .\remettre-en-second.ps1 -NouveauPrincipal <IP du nouveau>
  ```
  - Son ancienne base est **gardée** à côté (`<dossier>_ancien_principal_<date>`). Elle peut contenir les dernières saisies d'avant la panne : **à examiner avant toute suppression**.
  - Il redevient 2e serveur et rattrape le nouveau principal.
- [ ] **4.** Rebascule à un moment calme (facultatif, pour revenir à la répartition d'origine) : arrêter l'API et PostgreSQL du principal actuel, lancer `basculer-second-serveur.ps1` sur l'autre, puis `remettre-en-second.ps1` sur celui qu'on vient d'arrêter, et remettre l'adresse des postes (Serveur et PC de secours).

### F5. Révocation d'un certificat (PC volé, officine résiliée)

- [ ] **1.** Lister les certificats. **Où** : serveur central. **Compte** : compte d'administration.
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --lister
  ```
- [ ] **2.** Révoquer un certificat :
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --revoquer <empreinte> --motif "<motif>"
  ```
  Ou tous les certificats d'un serveur d'officine (PC volé) : `--revoquer-serveur <code officine> <serveur> --motif "<motif>"`. Révoquer un certificat révoque aussi ceux obtenus en le renouvelant.
- [ ] **3.** Effet au prochain appel. Un nouveau code d'activation permet de réactiver l'officine. Le certificat (3 ans) se renouvelle seul 60 jours avant l'échéance.

---

## Annexes

### A1. Dépannage

| Symptôme | Cause probable | Action |
| --- | --- | --- |
| Le service se ferme aussitôt | `Démarrage refusé : l'API écoute en HTTP (non chiffré) sur le réseau` | Parcours A, étape 4.6 non faite, ou `appsettings.Https.json` absent |
| Le service se ferme : message sur `Jwt:Cle` | Variable absente ou de moins de 64 caractères | Parcours A, étape 4.4 |
| Service `Running`, mais rien ne répond | Pare-feu ou mauvais port | `netstat -ano \| findstr :5443`, puis règle `PharmaOfficine API (https 5443)` |
| Avertissement de certificat sur un poste | Autorité non installée, ou nom du serveur absent du certificat | Parcours A, étape 5.4, ou relancer le script de l'étape 4.6 avec `-NomsSupplementaires` / `-AdressesSupplementaires` |
| Avertissement d'heure dans le journal au démarrage | Fuseau Windows incorrect | Parcours A, étape 2.3 |
| Le poste dit « Serveur injoignable » | API arrêtée, ou adresse `PHARMA_API` erronée | Vérifier le service, puis la variable |
| Message « base en lecture seule » | Base d'un 2e serveur non basculé | Normal : écrire sur le PC principal |
| `Get-NetFirewallRule` montre une règle sur 5080 | Règle héritée | `Disable-NetFirewallRule -DisplayName '<nom>'` |
| Deux postes partagent la même caisse | Même `PHARMA_POSTE` | Donner un nom unique à chaque poste |

Journal du serveur : `C:\ProgramData\PharmaOfficine\Journaux\pharma-api-AAAAMMJJ.log` (un fichier par jour).

### A2. Clé de scellement des journaux

- Au premier démarrage, l'API crée `C:\ProgramData\PharmaOfficine\cle-integrite.dat`. Elle est chiffrée pour ce PC.
- Elle se **sauvegarde avec le PC serveur** (copie du fichier sur le disque externe). Sans elle, les sceaux déjà posés ne peuvent plus être vérifiés (ils sont signalés « faits avec une autre clé »).
- Elle n'est lisible que sur ce PC. Pour un autre PC (PC de secours, 2e serveur) : **export** par le titulaire (Administration, 2e serveur, Clé de scellement, fichier protégé par mot de passe), puis **import** (`--importer-cle`) : voir parcours C, étape 6, et parcours D, étape 8.
- Contrôle : `Pharma.Api.exe --empreinte-cle` affiche la même empreinte que l'écran du titulaire.
- Ranger la clé USB **au coffre** et le mot de passe **à part**. Le chiffrement porte sur la clé seulement, pas sur les données.

### A3. Archives fiscales

- Les rapports de clôture et mensuels scellés sont archivés dans `C:\ProgramData\PharmaOfficine\archives-fiscales\AAAA\MM`.
- Ce dossier se **sauvegarde avec le PC serveur** et se **conserve 6 ans au moins** (`parametre_fiscal.conservation_annees`).
- Ne **jamais** purger le journal ni les sceaux : la base le refuse.

### A4. Synchronisation : fichiers et règles

- **Ne jamais** copier le dossier `Synchronisation` d'un serveur à l'autre : chaque serveur a sa clé (DPAPI : illisible ailleurs).

### A5. Réglages du poste (`poste.json`)

Fichier `C:\ProgramData\PharmaOfficine\poste.json` du poste. Clés connues : `AdresseApi` (prioritaire sur `PHARMA_API` quand il est rempli), `AdresseApiSecours`, `DelaiServeurInjoignableSecondes` (60 par défaut, entre 10 et 3 600). Les réglages se font dans l'application (Administration, Imprimantes et étiquettes) ; ne pas éditer à la main sans raison.

### A6. Développement (rappel)

- À chaque nouveau script `0xx`, régénérer `database/init/schema.sql` (`pg_dump --schema-only --no-owner --no-privileges` d'une base construite par les scripts) et compléter `seed_referentiel.sql` s'il ajoute des données de référence.
- Le paquet `scripts\paquet\installer.ps1` installe une **base d'essai avec données fictives** : jamais pour une officine réelle.

---

## Checklist avant ouverture

Cocher tout avant le jour J.

- [ ] Onduleur branché, PC serveur allumé en permanence, mise en veille désactivée
- [ ] PostgreSQL : service démarré automatiquement ; 5432 écouté seulement sur `127.0.0.1` (parcours A, étape 2.4)
- [ ] Base : `schema.sql`, `083`, `seed_referentiel.sql`, `111b` faits, `objets_encore_a_pharma_app = 0`
- [ ] Variables de l'API réglées (`ConnectionStrings__Pharmacie`, `Jwt__Cle` ≥ 64, `Chifa__DossierExport`)
- [ ] Certificat HTTPS créé ; `appsettings.Https.json` présent ; règle de pare-feu 5443 active
- [ ] Service `PharmaOfficine API` en démarrage automatique et `Running`
- [ ] `http://localhost:5080/api/sante` répond ; le réseau ne répond pas sur 5080
- [ ] Tous les postes : autorité installée (empreinte vérifiée), `PHARMA_API` en HTTPS, `PHARMA_POSTE` unique, écran de connexion sans avertissement
- [ ] Téléphones : autorité installée (empreinte vérifiée), scanner mobile testé
- [ ] `cle-integrite.dat` et `super-admin.dat` sauvegardés sur le disque externe, avec le mot de passe de la clé de scellement au coffre
- [ ] Compte titulaire : mot de passe personnel choisi ; comptes des employés créés ; droits vérifiés
- [ ] Administration : identité, RC, NIF, NIS, AI, n° Chifa, organismes, paramétrage (caisse, alertes, emplacements), imprimantes **sur chaque poste**
- [ ] Nomenclature nationale importée, compteurs contrôlés
- [ ] Notifications : message d'essai reçu
- [ ] Compte super admin créé ; téléphone et e-mail renseignés
- [ ] Sauvegarde de l'application réglée, première sauvegarde faite, fichier lisible (ou script planifié, mais **un seul** des deux)
- [ ] Test de restauration dans une base d'essai fait, base d'essai supprimée
- [ ] Reprise de données (si besoin) : totaux contrôlés et rapport signé par le pharmacien
- [ ] Synchronisation : **désactivée** (sauf décision du titulaire)
- [ ] Vérifications avant ouverture (parcours A, étape 14) faites
- [ ] Essai réel sur un vrai poste, en HTTPS (connexion, vente, impression, scanner mobile)

## Checklist après chaque mise à jour

- [ ] Sauvegarde faite avant la mise à jour, et lisible (`pg_restore --list`)
- [ ] Serveur central mis à jour d'abord (si l'officine est synchronisée)
- [ ] Service arrêté puis redémarré, `http://localhost:5080/api/sante` répond
- [ ] Scripts `database\migration` appliqués dans l'ordre, sans erreur ; pas de `schema.sql` ni de `clean.sql`
- [ ] Scripts 112 et suivants lancés avec le rôle propriétaire (`pharma_proprietaire`), jamais avec `pharma_app`
- [ ] API republiée et remplacée ; `appsettings.Https.json` toujours présent
- [ ] Postes republiés ; chaque poste ouvre l'écran de connexion
- [ ] Nomenclature réimportée si elle a changé
- [ ] Une vente test, une clôture, « Vérifier maintenant » de l'intégrité des journaux
- [ ] Si 2e serveur : `verifier-second-serveur.ps1` lancé sur le 2e serveur, versions identiques
- [ ] Si .NET a reçu un correctif de sécurité : republier l'API et les postes avec le SDK à jour
- [ ] Journal du serveur relu : aucune erreur au démarrage
