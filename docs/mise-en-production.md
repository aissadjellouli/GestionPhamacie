# Manuel de mise en production

Toutes les étapes, dans l'ordre, pour installer Pharma Officine dans une pharmacie, du PC serveur à la première vente. Chaque étape se coche. Les commandes sont à lancer dans PowerShell, depuis le dossier du dépôt (ou de la livraison).

Conventions :
- `<serveur>` : nom ou adresse IP du PC serveur (ex. `192.168.1.10`) ;
- `<base>` : nom de la base de la pharmacie (ex. `pharmacie`) ;
- `psql` : `C:\Program Files\PostgreSQL\18\bin\psql.exe`.

---

## Étape 0 — Avant le jour J (J-15 à J-7)

- [ ] **0.1** Matériel :
  - PC serveur toujours allumé, avec un **onduleur obligatoire** contre les coupures de courant ;
  - un poste par caisse ;
  - imprimante de tickets avec son pilote Windows ;
  - imprimante d'étiquettes réseau (facultative) ;
  - deux disques externes pour les sauvegardes.
- [ ] **0.1 bis** Versions de Windows :

  | Machine | Minimum technique | Recommandé |
  | --- | --- | --- |
  | Poste de caisse | Windows 10 version 2004 (build 19041), cible du projet `Pharma.Desktop` pour WebView2 | Windows 11 (22H2 ou plus), ou Windows 10 IoT Enterprise LTSC pour un PC de caisse dédié |
  | PC serveur | Windows 10 ou Windows Server 2016 (PostgreSQL 18 exige Windows 10 ou plus : runtime C « UCRT ») | Windows 11 Pro, ou Windows Server 2019, 2022 ou 2025 |

  Windows 10 22H2 n'est plus maintenu par Microsoft depuis le 14 octobre 2025 (seules les éditions LTSC le restent) : pour des données
  de santé, préférer Windows 11. Rien n'empêche l'installation sur une version plus ancienne que le minimum, mais elle n'est ni prise
  en charge ni testée. À ce jour, le logiciel n'a été essayé que sous Windows 11 : faire un essai sur le système réel avant le jour J.
  Les téléphones n'ont besoin que d'un navigateur récent (Android ou iPhone).
- [ ] **0.2** Réseau : adresse IP fixe pour le PC serveur, et Wi-Fi si le scanner mobile est utilisé.
- [ ] **0.3** Informations à demander à la pharmacie :
  - nom, adresse, téléphone ;
  - **RC, NIF, NIS, AI** ;
  - n° d'officine Chifa ;
  - liste des employés et de leurs rôles ;
  - banques ;
  - organismes conventionnés (par exemple les militaires) ;
  - compte e-mail (SMTP) et/ou modem GSM pour les notifications.
- [ ] **0.4** Dernière nomenclature nationale (fichier Excel du ministère, miph.gov.dz). La version livrée est dans `database/data/`.
- [ ] **0.5** Si des données sont reprises de l'ancien logiciel, faire une **répétition générale complète sur une copie** de son contenu (voir l'étape 9).

---

## Étape 1 — PC serveur : PostgreSQL

- [ ] **1.1** Installer PostgreSQL 18, avec ses outils `psql` et `pg_dump`. Noter le mot de passe du super-utilisateur `postgres` et le garder en lieu sûr : il ne sert qu'à l'installation.
- [ ] **1.2** Créer le compte de l'application et la base, avec un mot de passe **propre à la pharmacie**, jamais celui de démonstration :
  ```powershell
  & psql -h localhost -U postgres -d postgres -c "CREATE ROLE pharma_app LOGIN PASSWORD '<mot de passe fort>' CREATEDB"
  & psql -h localhost -U postgres -d postgres -c "CREATE DATABASE <base> OWNER pharma_app ENCODING 'UTF8' TEMPLATE template0"
  ```
  `CREATEDB` permet au super admin de restaurer une sauvegarde depuis l'application : une base neuve est créée, puis prend le nom de la
  base en service, qui est gardée sous un autre nom. Compte déjà créé sans ce droit : `ALTER ROLE pharma_app CREATEDB`.
- [ ] **1.3** N'ouvrir le port 5432 du pare-feu **qu'aux postes de la pharmacie**, et seulement si un outil y accède directement. L'application, elle, passe par l'API.

## Étape 2 — Base de données : création et référentiel (`database/init/`)

À la mise en production, la base est **créée** avec le dossier `database/init/`, et non par les migrations. Les migrations (`database/migration/0NN_*.sql`) servent ensuite à **mettre à jour** une base déjà en service (voir « Mises à jour du logiciel »).

Le dossier `database/init/` contient trois fichiers :

| Fichier | Rôle |
| --- | --- |
| `schema.sql` | Crée toute la structure (tables, vues, fonctions, déclencheurs). Équivaut aux scripts `001` à `123`, sauf le réglage de fuseau horaire de la base (`083`, à lancer en plus : étape 2.1 bis). |
| `seed_referentiel.sql` | Référentiel de départ : rôles, droits, paramètres, CNAS et CASNOS, substances surveillées, coffre, compte titulaire provisoire. |
| `clean.sql` | Remet une base à vierge. À utiliser uniquement pour recommencer une installation ratée ou une répétition. |

- [ ] **2.1** Créer la structure :
  ```powershell
  & psql -h localhost -U pharma_app -d <base> -v ON_ERROR_STOP=1 -f database\init\schema.sql
  ```
  Si la base contient déjà le schéma, le script s'arrête sans rien faire.
- [ ] **2.1 bis** Régler le fuseau horaire de la base (Algérie, UTC+1 toute l'année) : un `ALTER DATABASE … SET timezone` n'est pas
  repris par un dump de schéma, il faut donc lancer ce script en plus (sans risque, il peut être relancé) :
  ```powershell
  & psql -h localhost -U pharma_app -d <base> -v ON_ERROR_STOP=1 -f database\migration\083_fuseau_horaire.sql
  ```
  Vérifier : `& psql -h localhost -U pharma_app -d <base> -tAc "show timezone"` affiche `Africa/Algiers` (nouvelle connexion).
  L'API ajoute de toute façon `Timezone=Africa/Algiers` à sa chaîne de connexion si celle-ci n'en précise pas.
  Windows du PC serveur doit aussi être en « (UTC+01:00) Afrique centrale - Ouest » : sinon l'API écrit un avertissement dans son
  journal au démarrage (dates des ventes calculées sur le poste et dans la base décalées).
- [ ] **2.2** Charger le référentiel :
  ```powershell
  & psql -h localhost -U pharma_app -d <base> -v ON_ERROR_STOP=1 -f database\init\seed_referentiel.sql
  ```
  Le message de fin rappelle le compte de départ : **`titulaire` / `Provisoire-2026`**.
- [ ] **2.3** **Ne jamais lancer** les fichiers `database/data/9xx_*.sql`. Ce sont des données fictives (patients, produits, comptes « demo »), réservées au développement et aux tests.
- [ ] **2.4** Pour recommencer depuis zéro, seulement avant la mise en service :
  1. faire une sauvegarde ;
  2. lancer `clean.sql` en confirmant le nom de la base :
     ```powershell
     & psql -h localhost -U pharma_app -d <base> -v ON_ERROR_STOP=1 -v confirmer=<base> -f database\init\clean.sql
     ```
     Sans `-v confirmer=<base>`, ou avec un autre nom, rien n'est supprimé.
  3. reprendre à l'étape 2.1.

## Étape 3 — API (PC serveur)

- [ ] **3.1** Publier l'API dans un dossier d'installation, en **publication autonome** (.NET inclus, comme le paquet d'essai) :
  ```powershell
  dotnet publish src\Pharma.Api -c Release -r win-x64 --self-contained true -o "C:\Program Files\PharmaOfficine\Api"
  ```
  Aucun runtime .NET n'est alors à installer sur le serveur. Sans `--self-contained true`, il faudrait le runtime ASP.NET Core 10.
  Contrepartie : les correctifs de sécurité de .NET n'arrivent qu'avec une nouvelle publication (voir « Mises à jour du logiciel »).
- [ ] **3.2** Configurer les réglages propres à la pharmacie, par variables d'environnement **système** (jamais dans le dépôt) :
  - `ConnectionStrings__Pharmacie=Host=localhost;Port=5432;Database=<base>;Username=pharma_app;Password=<mot de passe de l'étape 1.2>` ;
  - `Jwt__Cle` : une clé aléatoire de **64 caractères minimum**, propre à la pharmacie. La valeur d'exemple d'`appsettings.json` ne doit jamais rester : hors poste de développement, l'API refuse de démarrer avec elle ou avec une clé plus courte ;
  - `Ged__Dossier` : la GED, dossier unique de tous les documents (par défaut `C:\ProgramData\PharmaOfficine\GED`) :
    `Ordonnances` (scans), `Chifa` (copie des fichiers Chifa lus), `Sauvegardes` (copie de la base), et les documents produits
    par les postes, un sous-dossier par type (`Factures`, `Rapports`, `Clotures`, `Registre`, `Commandes`, `Retours`, `Etiquettes`,
    `Inventaires`, `Credits`). Les postes envoient leurs documents au serveur. Un document réexporté à l'identique n'est pas
    ajouté ; s'il a changé, la dernière version garde le nom simple et la précédente passe, datée, dans le sous-dossier `Versions`
    du type (ex. `Factures\Versions\F-2026-0021 2026-11-11 14h47.pdf`). Les documents de la GED sont gardés ; un nettoyage
    au-delà d'un nombre d'années se règle dans Administration › Sauvegardes (vide : jamais) ;
  - `Ged__RetentionSauvegardesJours` : jours de conservation de la copie de la base dans `GED\Sauvegardes` (7 par défaut,
    modifiable aussi dans Administration › Sauvegardes) ;
  - `Chifa__DossierExport` : le dossier d'export de Chifa_Officine sur ce PC (les fichiers y sont lus, puis archivés dans `GED\Chifa`) ;
  - `Ordonnances__Dossier` : à ne régler que pour sortir les scans d'ordonnances de la GED (par défaut `GED\Ordonnances` ; l'ancien
    dossier `C:\ProgramData\PharmaOfficine\Ordonnances` est vidé dans la GED au démarrage) ;
  - journal du serveur, un fichier texte par jour (`pharma-api-AAAAMMJJ.log`, puis `_2`, `_3`… au-delà de la taille maximale) :
    `Journaux__Dossier` (par défaut `C:\ProgramData\PharmaOfficine\Journaux`), `Journaux__TailleMaxMo` (50 par défaut, par fichier),
    `Journaux__RetentionMois` (3 par défaut : les fichiers plus anciens sont supprimés au démarrage puis chaque jour),
    `Journaux__Niveau` (`Information` par défaut, `Warning` pour ne garder que les avertissements et erreurs) ;
  - `Urls` : `http://0.0.0.0:5080` dans `appsettings.json`. **Attention** : hors développement, l'API **refuse de démarrer** si elle
    écoute en HTTP (non chiffré) sur une autre adresse que `localhost` / `127.0.0.1` / `::1` (ce qui est le cas de `0.0.0.0`), sauf
    réglage explicite **`AutoriserHttpReseau=true`** (variable `AutoriserHttpReseau=true`). Trois possibilités :
    1. HTTPS seulement sur le réseau (conseillé, étape 3.5 bis) : HTTP limité à `http://localhost:5080` (ou supprimé) ;
    2. HTTP et HTTPS gardés tous deux sur le réseau (ex. le temps de passer les postes en `https`, ou le scanner mobile en `https`
       et les postes encore en `http`) : régler `AutoriserHttpReseau=true` ; HTTP reste alors servi sans redirection ;
    3. HTTP seul sur le réseau (installation actuelle) : `AutoriserHttpReseau=true` — mots de passe, codes et données patients
       circulent alors en clair sur le réseau de la pharmacie.
    Quand HTTPS est configuré (`Kestrel:Endpoints`, écrit par `scripts\certificat_mobile.ps1`), l'API envoie l'en-tête HSTS sur ses
    réponses HTTPS, et redirige vers HTTPS les requêtes HTTP venues d'un autre PC, sauf si `AutoriserHttpReseau=true` (le poste
    installé sur le serveur lui-même, `127.0.0.1`, n'est jamais redirigé) ;
  - `LimiteConnexions__ParMinute` : nombre d'essais de connexion permis par minute et par adresse IP (20 par défaut ; routes
    `/api/auth/connexion`, `/api/auth/connexion-code`, `/api/auth/mot-de-passe`, `/api/super-admin/connexion` ; au-delà : réponse 429).
- [ ] **3.3** Installer l'API comme service Windows, démarré automatiquement (nom attendu : « PharmaOfficine API ») :
  ```powershell
  sc.exe create "PharmaOfficine API" binPath= "C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe" start= auto
  sc.exe start "PharmaOfficine API"
  ```
- [ ] **3.4** Ouvrir le port 5080 du pare-feu Windows aux postes de la pharmacie.
- [ ] **3.5** Vérifier : `http://<serveur>:5080/api/auth/comptoir` répond depuis un poste.
- [ ] **3.5 bis** **Chiffrer les échanges (HTTPS)** : en `http`, mots de passe, codes comptoir et connexions passent en clair sur le réseau
  de la pharmacie (Wi-Fi compris).
  - Lancer `scripts\certificat_mobile.ps1` sur le serveur (voir [scanner-mobile.md](scanner-mobile.md)) : il crée l'autorité de
    certification de la pharmacie et ouvre `https` sur le port 5443.
  - Sur chaque poste : installer le certificat de l'autorité (`autorite-pharmacie.crt`) dans « Autorités de certification racines
    de confiance » de l'ordinateur, puis régler `PHARMA_API=https://<serveur>:5443/` (étape 4).
  - Une fois tous les postes et téléphones en `https`, fermer le port 5080 du pare-feu aux postes.
  - À vérifier sur un poste avant le jour J : connexion, vente et impression en `https` (essai non encore fait sur un vrai poste).
- [ ] **3.6** **Compte super admin** (reprise de données, et plus tard restauration), sur le PC serveur, dans une invite de commandes
  administrateur :
  ```powershell
  & "C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe" --super-admin
  ```
  - identifiant, mot de passe (12 caractères au moins), **téléphone mobile obligatoire** et **e-mail obligatoire** (secours si le SMS ne part pas) ;
  - le compte est rangé hors de la base, chiffré pour ce PC : `C:\ProgramData\PharmaOfficine\super-admin.dat` (réglage `SuperAdmin__Fichier`) ;
    il fonctionne donc même si la base est détruite ; il est illisible sur un autre PC ;
  - un code est envoyé par SMS au numéro saisi pour le vérifier (modem GSM de l'étape 7.7 ; sans modem, le compte peut être créé sans vérification) ;
  - relancer la même commande pour changer le mot de passe, le téléphone ou l'e-mail : mot de passe actuel, code envoyé au téléphone actuel,
    puis code reçu par SMS sur le nouveau numéro ;
  - téléphone et e-mail perdus : un administrateur du PC serveur supprime `super-admin.dat`, puis recrée le compte.

## Étape 4 — Postes (un par caisse)

- [ ] **4.1** Installer le poste : publication autonome de `src/Pharma.Desktop`, copiée sur chaque poste (par exemple dans
  `C:\Program Files\PharmaOfficine\Poste`), avec un raccourci vers `Pharma.Desktop.exe` sur le bureau :
  ```powershell
  dotnet publish src\Pharma.Desktop -c Release -r win-x64 --self-contained true -o <dossier de livraison>\Poste
  ```
  - Aucun runtime .NET à installer sur le poste (sans `--self-contained true`, il faudrait le runtime .NET 10 Desktop).
  - **WebView2** reste nécessaire : il n'est pas inclus dans la publication. Il est présent sur Windows 10 et 11 à jour ; sur un PC
    ancien ou jamais mis à jour, installer le « Microsoft Edge WebView2 Runtime » (gratuit, Microsoft) avant d'ouvrir le poste.
  - Il n'existe pas encore d'installateur ni de mise à jour automatique des postes (prévus dans `feuille-de-route.md`) : la copie,
    les variables (étape 4.2) et le raccourci se font à la main sur chaque poste.
- [ ] **4.2** Variables d'environnement du poste :
  - `PHARMA_API=https://<serveur>:5443/` (HTTPS, étape 3.5 bis : certificat de l'autorité de la pharmacie installé sur le poste),
    ou `http://<serveur>:5080/` si HTTP est gardé sur le réseau (`AutoriserHttpReseau=true`, étape 3.2). Le poste accepte les deux ;
    la barre finale est ajoutée si elle manque ;
  - `PHARMA_POSTE` : un nom **différent pour chaque poste** (ex. « Caisse 1 », « Caisse 2 »). Ce nom identifie la caisse (le tiroir) que partagent les employés du poste. Deux postes portant le même nom partageraient la même caisse.
- [ ] **4.3** Ouvrir le poste et vérifier que l'écran de connexion s'affiche.

## Étape 5 — Première connexion et sécurité

- [ ] **5.1** Se connecter avec **`titulaire` / `Provisoire-2026`**.
- [ ] **5.2** L'écran « Choisissez votre mot de passe » s'ouvre de force. Le titulaire y choisit son mot de passe personnel (8 caractères minimum). Tant que ce n'est pas fait, le serveur refuse toute autre action, code comptoir compris.
- À savoir : changer ou réinitialiser un mot de passe, désactiver un compte (employé parti, téléphone perdu) ou effacer un code comptoir ferme aussitôt les connexions déjà ouvertes de ce compte, sur tous les postes et téléphones.
- [ ] **5.3** Administration › Langue et pharmacie : nom de la pharmacie, titulaire, adresse, téléphone, **RC, NIF, NIS, AI**, n° d'officine Chifa. Ces informations sont imprimées sur les tickets, factures et rapports.
- [ ] **5.4** Même page, « Organismes payeurs » : ajouter les organismes conventionnés (militaires…), avec leur adresse et leur NIF.

## Étape 6 — Employés et droits

- [ ] **6.1** Administration › Utilisateurs et droits : créer un compte par employé, avec un identifiant et un mot de passe personnels, et le bon rôle (assistant, vendeur, stagiaire).
- [ ] **6.2** Vérifier les droits de chaque rôle. Sont réservés par défaut au titulaire : trésorerie, factures, inventaire complet, organismes. Il les accorde ensuite à qui il veut.
- [ ] **6.3** Chaque employé définit son **code comptoir** (bandeau, « Mon code comptoir ») pour reprendre la caisse sans retaper son mot de passe. Chacun peut aussi changer son mot de passe (bandeau, « Mot de passe »).

## Étape 7 — Paramétrage

- [ ] **7.1** Administration › Caisse et contrôle :
  - fond de caisse par défaut ;
  - seuil d'écart ;
  - comptage aveugle ;
  - déconnexion automatique ;
  - versement des caisses au coffre (manuel ou automatique) ;
  - seuil de validation des sorties d'argent par le tiroir (défaut 2 000 DA : au-delà, un employé qui a le droit « Faire la relève et clôturer la caisse » valide, code comptoir possible) ;
  - Administration › Motifs et catégories : motifs du tiroir et leur effet (sortie, entrée, aucun) ; Administration › Contrôle interne : alerte au-delà de N sorties du tiroir par employé et par jour (défaut 5).
- [ ] **7.2** Administration › Alertes et seuils, et Seuils de stock : seuils de péremption et de stock.
- [ ] **7.3** Administration › Emplacements : rayons, réfrigérateur, armoire des psychotropes, réserves.
- [ ] **7.4** **Sur chaque poste** : Administration › Imprimantes et étiquettes (imprimante de tickets, largeur du rouleau, ticket automatique, imprimante d'étiquettes, afficheur client). Faire un essai d'impression et un essai de l'afficheur.
- [ ] **7.5** Administration › Nomenclature nationale : « Importer le fichier Excel… » avec le fichier de `database/data/`. Contrôler les compteurs (en vigueur, non renouvelés, retirés).
- [ ] **7.6** Trésorerie : régler la date et le solde de départ du coffre, puis déclarer les banques (une seule reçoit les paiements par carte).
- [ ] **7.7** Administration › Notifications au titulaire : SMTP et/ou modem GSM, destinataires, puis un message d'essai.

## Étape 8 — Sauvegardes (avant toute vraie donnée)

- [ ] **8.1** Administration › Sauvegardes : régler le dossier et la rétention, puis lancer une première sauvegarde. Chaque sauvegarde
  copie la base sur le disque externe et dans `GED\Sauvegardes` (gardée 7 jours par défaut), et les documents de la GED sur le disque
  externe, dans son sous-dossier `GED` (nouveaux et modifiés seulement). Disque absent : la copie de la base dans la GED est faite quand même.
- [ ] **8.2** Planifier `scripts\sauvegarde_pharmacie.ps1` chaque soir dans le Planificateur de tâches. Garder deux disques externes en rotation.
- [ ] **8.3** **Tester une restauration** dans une base d'essai, avant la mise en service puis chaque mois, sans toucher à la base
  en service : `.\scripts\restaurer_pharmacie.ps1 -Sauvegardes "E:\…" -Base pharmacie_essai -SansGed`, contrôler le résumé affiché
  (nombre de ventes, date de la dernière), puis supprimer la base d'essai. Voir « Restauration » en fin de document.

## Étape 9 — Reprise des données de l'ancien logiciel (si besoin)

L'ancienne base n'est jamais modifiée : on travaille sur une copie, ce qui rend le retour arrière simple.

- [ ] **9.0** **Super admin** (étape 3.6) : sur l'écran de connexion, « Connexion super admin ». Ce compte ne voit que la reprise de données
  (aucun écran des employés), et se déconnecte après 10 minutes sans activité. L'**extraction**, le **chargement** et l'**abandon** d'une reprise
  demandent chacun : un avertissement, le mot de passe ressaisi, puis le **code à 6 chiffres reçu par SMS** (par e-mail si le SMS ne part pas),
  valable 5 minutes, à usage unique ; 3 codes faux bloquent les validations 15 minutes.
  - Les données chargées (réception de reprise, stock, registre des psychotropes) sont attribuées au **pharmacien titulaire** ; la liste des
    reprises montre la date de l'extraction, la **date de l'import** et qui l'a lancé, et le journal garde l'identifiant du super admin et le
    canal du code (numéro ou adresse masqués).
- [ ] **9.1** **Transit** : Administration › Reprise de données (super admin). L'assistant se connecte à l'ancienne base par ODBC, en lecture seule.
  - Sources possibles : Access, dBase, CSV, Excel, Firebird, SQLite, HFSQL par son pilote ODBC, ou une connexion saisie champ par champ.
  - Fichiers CSV : lus par Pharma Officine sans pilote ODBC, chaque valeur comme du texte ; encodage (UTF-8 ou ANSI) et séparateur (virgule, point-virgule, tabulation) détectés. Si les accents sont mal affichés dans les exemples, imposer l'encodage dans « Encodage des fichiers ».
  - Importer la nomenclature nationale **avant** la reprise (étape 7.5) : elle donne la classe réglementaire (psychotrope…) des produits créés. Un produit absent de la nomenclature est créé « à vérifier » et refusé en caisse jusqu'à la confirmation de sa classe par le titulaire.
  - L'assistant propose la correspondance des tables et des colonnes, à confirmer, puis copie les données dans le schéma `import`.
  - Les mots de passe ne sont ni extraits ni enregistrés.
- [ ] **9.2** **Rapprochement** des produits : par code-barres, n° d'enregistrement, puis nom approché à confirmer.
- [ ] **9.3** **Contrôles** : toute anomalie bloquante empêche le chargement.
- [ ] **9.4** **Chargement**, en une transaction. Un lot sans date de péremption devient « date à vérifier ». Le prix d'achat de chaque lot est repris s'il est dans l'export, sinon celui du produit.
- [ ] **9.5** **Vérification** des totaux (unités, valeur du stock au prix d'achat, crédits clients, dettes grossistes, nombre de clients), avec un rapport signé par le pharmacien.

## Étape 10 — Options

- [ ] **10.1** Scanner mobile : lancer `scripts\certificat_mobile.ps1` sur le serveur, puis installer le certificat sur chaque téléphone. Voir [scanner-mobile.md](scanner-mobile.md).
- [ ] **10.2** Accès du titulaire hors de la pharmacie, par VPN : voir [acces-distant.md](acces-distant.md). Ne jamais ouvrir l'API sur internet.

## Étape 11 — Vérifications avant ouverture (J-1)

- [ ] **11.1** Ouvrir une caisse, faire une vente test en espèces, l'annuler, puis clôturer : l'écart doit être nul.
- [ ] **11.2** Imprimer un ticket, une étiquette et un rapport de clôture.
- [ ] **11.3** Vérifier qu'un vendeur ne voit pas ce qui ne lui est pas permis (trésorerie, administration).
- [ ] **11.4** Faire une sauvegarde complète juste avant l'ouverture.

## Étape 12 — Jour J et suite

- [ ] **12.1** Ouverture sur le nouveau logiciel, avec accompagnement sur place.
- [ ] **12.2** J+1 à J+30 : l'ancien logiciel reste en **lecture seule**. Étiqueter le stock progressivement (Achats › Étiquettes internes, ou à la fin de chaque inventaire tournant).

## Calendrier type

| Jour | Étape |
| --- | --- |
| J-15 | Répétition générale complète sur une copie (étapes 1 à 9) |
| J-7 | Formation, paramétrage (étapes 5 à 8) |
| J-1 au soir | Clôture de caisse de l'ancien logiciel, sauvegarde, export, reprise (étape 9), contrôle des totaux, vérifications (étape 11) |
| J | Ouverture, accompagnement sur place (étape 12) |
| J+1 à J+30 | Ancien logiciel en lecture seule ; étiquetage progressif du stock |

**Critère de retour arrière**, fixé à l'avance : si les totaux ne concordent pas la veille au soir, on rouvre l'ancien logiciel et on reporte.

---

## Mises à jour du logiciel (après la mise en production)

1. Faire une sauvegarde de la base.
2. Arrêter le service : `sc.exe stop "PharmaOfficine API"`.
3. Appliquer **uniquement les nouveaux scripts** `database/migration/0xx_*.sql`, dans l'ordre :
   - avec `psql -v ON_ERROR_STOP=1 -f`, hors transaction ;
   - ils sont réexécutables sans dégât ;
   - **ne jamais** relancer `init/schema.sql` ni `init/clean.sql` sur une base en service.
   - **Avant `101_fusion_facture_chifa.sql`** (fusion du n° de facture Chifa, 2026-11-15) : sur la base de l'officine, lancer ces vérifications (lecture seule) et garder le résultat :
     ```sql
     -- n° de facture Chifa non conformes (8 chiffres) : s'il y en a, 101 ne pose pas le contrôle « 8 chiffres » (il les affiche) ;
     -- les corriger avec le titulaire, puis relancer 101
     SELECT id, numero, date_vente, chifa_facture, num_facture_chifa FROM vente
     WHERE chifa_facture !~ '^[0-9]{8}$' OR num_facture_chifa !~ '^[0-9]{8}$' ORDER BY id;
     -- conflits : les deux colonnes remplies et différentes (101 les range dans vente_chifa_facture_conflit,
     -- à trancher par le titulaire dans Administration › Conflits de n° de facture Chifa)
     SELECT id, numero, date_vente, num_facture_chifa, chifa_facture FROM vente
     WHERE num_facture_chifa IS NOT NULL AND chifa_facture IS NOT NULL AND btrim(num_facture_chifa) <> btrim(chifa_facture) ORDER BY id;
     -- recopie impossible : n° de l'ancienne colonne déjà porté par une autre vente non annulée
     SELECT v.id, v.numero, v.num_facture_chifa, a.numero AS deja_sur FROM vente v
     JOIN vente a ON a.id <> v.id AND a.statut <> 'annulee' AND a.chifa_facture = btrim(v.num_facture_chifa)
     WHERE v.chifa_facture IS NULL AND v.statut <> 'annulee';
     ```
     Après 101 : `SELECT count(*) FROM vente_chifa_facture_conflit WHERE tranche_le IS NULL;` doit valoir 0 avant d'envisager la partie B (suppression de l'ancienne colonne `num_facture_chifa`, script 101b), qui n'est lancée qu'après validation explicite du titulaire.
4. Remplacer les fichiers de l'API et des postes, puis redémarrer le service.
   Les publications étant autonomes (.NET inclus), Windows Update ne corrige pas .NET : à chaque correctif de sécurité .NET important,
   republier l'API et les postes avec le SDK à jour et les remplacer de la même façon, même sans nouvelle fonction.
5. Nouvelle nomenclature du ministère : la réimporter dans Administration › Nomenclature. L'import remplace l'ancienne liste sans toucher au catalogue.

### Séparation des rôles (journaux inaltérables) — à faire par l'administrateur

**Non faite par le logiciel** (2026-11-16, lot 2) : elle demande un superutilisateur PostgreSQL. Tant qu'elle n'est pas faite, le compte de l'API `pharma_app` est **propriétaire** de la base : il pourrait désactiver les déclencheurs qui protègent les journaux chaînés (`ALTER TABLE … DISABLE TRIGGER`). La vérification nocturne de l'intégrité détecte alors la falsification, mais ne l'empêche pas.

Procédure (une fois par base, après `111_integrite_hachage.sql`) :

1. Faire une sauvegarde de la base ; arrêter le service : `sc.exe stop "PharmaOfficine API"`.
2. En superutilisateur : `psql -U postgres -d <base> -v ON_ERROR_STOP=1 -f database\migration\111b_roles_integrite.sql`. Le script crée le rôle sans connexion `pharma_proprietaire`, lui donne la base, le schéma et tous les objets, rend à `pharma_app` lecture, ajout, modification et suppression sur les tables métier, et lui retire `UPDATE`, `DELETE` et `TRUNCATE` sur les journaux (tables chaînées, `sceau_integrite`, `verification_integrite`, `journal_audit`, `journal_sauvegarde`, `boite_evenement`, `registre_psychotrope`) ainsi que l'écriture dans `chaine_integrite`.
3. Lire la dernière ligne affichée : `objets_encore_a_pharma_app` doit valoir 0.
4. Redémarrer le service et vérifier une vente, une clôture et Administration › Contrôle interne › Intégrité des journaux (« Vérifier maintenant »).
5. **Ensuite**, les nouveaux scripts de mise à jour et les restaurations se lancent avec un compte membre de `pharma_proprietaire` (`GRANT pharma_proprietaire TO <administrateur>;` puis `SET ROLE pharma_proprietaire;` en tête de session), jamais avec `pharma_app`.

Retour arrière : `ALTER DATABASE <base> OWNER TO pharma_app;` puis `REASSIGN OWNED BY pharma_proprietaire TO pharma_app;` (en superutilisateur).

### Clé de scellement des journaux

Au premier démarrage, l'API crée `C:\ProgramData\PharmaOfficine\cle-integrite.dat` (clé de scellement, chiffrée pour ce PC). Elle se **sauvegarde avec le PC serveur** (copie du fichier sur le disque externe, comme `super-admin.dat`) : sans elle, les sceaux déjà posés ne peuvent plus être vérifiés (ils sont signalés « faits avec une autre clé »). Elle n'est lisible que sur ce PC : un changement de PC serveur demande une procédure de reprise (voir « PC serveur en panne »). Depuis le 2026-11-16 (accord du titulaire), le titulaire **exporte** la clé dans un fichier protégé par un mot de passe qu'il choisit (Administration › 2e serveur › Clé de scellement), et elle s'**importe** sur un autre PC (`Pharma.Api.exe --importer-cle <fichier>`) : voir « 2e serveur ».

Journal fiscal des ventes (lot 3) : les rapports de clôture et mensuels scellés sont archivés dans `C:\ProgramData\PharmaOfficine\archives-fiscales\AAAA\MM` ; ce dossier se **sauvegarde avec le PC serveur** et se **conserve 6 ans au moins** (`parametre_fiscal.conservation_annees`). Ne jamais purger le journal ni les sceaux : la base le refuse.

Côté développement, à chaque nouveau script `0xx`, il faut régénérer `database/init/schema.sql` (`pg_dump --schema-only --no-owner --no-privileges` d'une base construite par les scripts) et compléter `seed_referentiel.sql` s'il ajoute des données de référence (un nouveau droit, par exemple).

## Restauration (base ou GED détruite)

Les sauvegardes utiles sont sur le **disque externe** : la copie de la base dans `GED\Sauvegardes` est sur le même PC et disparaît avec lui.

- Base : `pharmacie_AAAAMMJJ_HHMMSS.dump` dans le dossier réglé dans Administration › Sauvegardes (les 14 dernières par défaut).
- GED : sous-dossier `GED` de ce même dossier (documents nouveaux et modifiés copiés à chaque sauvegarde ; rien n'y est supprimé).

**Depuis l'application (super admin, étape 3.6)** : sur un poste, « Connexion super admin », écran **Restauration**. L'API doit tourner
sur le PC serveur ; la base, elle, peut être détruite (le compte du super admin et le dossier des sauvegardes sont gardés hors de la base).
- **Base** : choisir la sauvegarde (disque externe ou `GED\Sauvegardes`, la plus récente en haut), puis valider : avertissement, mot de
  passe, code reçu par SMS (e-mail en secours). Le serveur, en tâche de fond, met les postes en attente, fait une copie de sécurité de la
  base actuelle (`GED\Sauvegardes\pharmacie_avant_restauration_….dump`, proposée ensuite dans la liste pour revenir en arrière ;
  jamais supprimée par le nettoyage automatique, à supprimer à la main quand elle n’est plus utile), restaure
  la sauvegarde dans une base neuve, la **met à la version du logiciel** (scripts `0xx` livrés avec l'API, rejoués), puis lui donne le nom
  de la base en service, qui est **gardée** sous le nom `<base>_avant_restauration_AAAAMMJJ_HHMM`. L'écran suit les étapes. Un échec avant
  l'échange laisse la base en service intacte. Il faut le droit `CREATEDB` du compte `pharma_app` (étape 1.2).
- **GED** : « Recopier les documents de la GED » : les documents du disque externe absents ou plus anciens au serveur sont recopiés ; rien
  n'est supprimé, un document plus récent au serveur est gardé.
- La restauration est inscrite au journal de la base restaurée (titulaire comme auteur, identifiant du super admin et canal du code).

**Sans l'application** (API hors service, PC serveur remplacé) : script PowerShell, ci-dessous.

1. PC serveur remplacé : installer PostgreSQL (étape 1) et recréer le compte `pharma_app` ; ne pas lancer `init/schema.sql`, la sauvegarde contient toute la structure.
2. Arrêter le service : `sc.exe stop "PharmaOfficine API"`.
3. Lancer, dans PowerShell sur le PC serveur (le mot de passe de `postgres` est demandé) :
   ```powershell
   .\scripts\restaurer_pharmacie.ps1 -Sauvegardes "E:\Sauvegardes\Pharmacie"
   ```
   - la dernière sauvegarde est vérifiée (`pg_restore --list`) puis restaurée ; une sauvegarde illisible est refusée : en choisir une plus ancienne avec `-Fichier` ;
   - une base existante du même nom n'est jamais supprimée : le script s'arrête, ou, avec `-Remplacer`, la renomme `pharmacie_avant_restauration_AAAAMMJJ_HHMM` ;
   - la GED est recopiée dans `C:\ProgramData\PharmaOfficine\GED` sans rien supprimer, en gardant un document plus récent déjà présent ;
   - `-SansGed` ou `-SansBase` pour ne restaurer que l'une des deux.
4. Sauvegarde plus ancienne que la version du logiciel : appliquer les scripts `database/migration/0xx_*.sql` plus récents (voir « Mises à jour du logiciel »).
5. Redémarrer le service. Les documents restés sur les postes (« A envoyer ») repartent dans la GED à la connexion suivante.
6. Ressaisir les opérations faites depuis la sauvegarde (en général la journée) ; sur un nouveau PC, ressaisir le mot de passe des
   notifications (chiffré pour le PC d'origine, voir [notifications.md](notifications.md)).

La copie de la GED sur le disque externe n'a qu'un exemplaire par document : un document abîmé sur le serveur remplace sa copie à la
sauvegarde suivante. Garder deux disques externes en rotation (étape 8.2).

## PC serveur en panne (bascule sur le PC de secours)

Ajouté le 2026-11-16 (lot 5, version actuelle). La pharmacie repart sur un **PC de secours** de l'officine, à partir de la dernière
**copie de secours** de la base. Les copies restent dans l'officine (§ 4 bis de l'analyse des écarts) ; elles ne sont **pas chiffrées** :
les ranger comme les sauvegardes (accès restreint au dossier).

### À préparer à l'avance (une fois)

1. **PC de secours** : un PC de l'officine (souvent un poste de caisse) avec PostgreSQL (étape 1, même version majeure que le PC serveur,
   compte `pharma_app` avec `CREATEDB`) et l'API installée **à la même version** que le PC serveur (étape 3 : dossier
   `C:\Program Files\PharmaOfficine\Api`, service `PharmaOfficine API` créé mais **arrêté**, port 5080 ouvert au pare-feu). Ne pas créer la base.
2. **Second emplacement** : Administration › Sauvegardes › **Copie de secours** : dossier partagé du PC de secours
   (`\\PC-SECOURS\CopieSecours`), autre disque ou clé USB ; le compte du service de l'API doit pouvoir y écrire. Après chaque sauvegarde
   réussie, le fichier y est copié, puis **vérifié** (taille et empreinte SHA-256) ; l'empreinte est notée à côté (`<copie>.sha256`). Les
   copies au-delà du nombre de sauvegardes conservées sont supprimées. Échec de copie, ou dernière copie plus vieille que le délai réglé
   (26 h par défaut) : le titulaire est prévenu (SMS/e-mail des alertes de sauvegarde) ; le résultat est écrit dans le message du journal des
   sauvegardes. Réglages et état : `C:\ProgramData\PharmaOfficine\copie-secours.json` du PC serveur.
3. **Test** : chaque mois, le titulaire clique **« Tester la restauration de la dernière copie »** (même écran) : empreinte contrôlée,
   restauration dans une base temporaire `<base>_test_restauration_…`, contenu compté, base supprimée ; résultat affiché et inscrit au
   journal des opérations. La base en service n'est pas touchée. Le script s'essaie aussi sur le PC de secours, sans rien modifier :
   `.\basculer-pc-secours.ps1 -Copies "D:\CopieSecours" -Essai`.
4. **Postes** : Administration › Imprimantes et étiquettes › **Serveur et PC de secours**, sur chaque poste : adresse de l'API du PC de
   secours (ex. `http://192.168.1.20:5080/`) et délai avant le bandeau (60 s par défaut). Ces réglages vont dans
   `C:\ProgramData\PharmaOfficine\poste.json` (`AdresseApiSecours`, `DelaiServeurInjoignableSecondes`, et `AdresseApi`, prioritaire sur
   `PHARMA_API` quand il est rempli).
5. **Clé de scellement des journaux** (`cle-integrite.dat`, chiffrée pour le PC serveur seulement) : **non transportable à ce jour**. Le PC de
   secours ne peut pas la relire : voir « Limite » ci-dessous.

### Le jour de la panne

1. Constater la panne : sur les postes, bandeau « Serveur injoignable : vérifiez le PC serveur ou basculez sur le PC de secours » après le
   délai réglé. Vérifier d'abord le PC serveur (allumé, réseau, service `PharmaOfficine API`) : la bascule fait perdre les opérations faites
   depuis la dernière copie.
2. Sur le PC de secours, PowerShell **en administrateur**, dans le dossier `scripts` livré avec l'API :
   ```powershell
   .\basculer-pc-secours.ps1 -Copies "D:\CopieSecours" -Essai   # simulation : contrôles seulement, rien n'est modifié
   .\basculer-pc-secours.ps1 -Copies "D:\CopieSecours"          # bascule (mot de passe de postgres demandé)
   ```
   Le script : prend la copie la plus récente (ou `-Fichier`), contrôle l'empreinte SHA-256 et la lisibilité (`pg_restore --list`),
   restaure dans une base neuve, **rejoue tous les scripts de la base** livrés avec l'API (dernier script rejoué affiché : il doit être le
   dernier de la version installée), garde une base existante sous `<base>_avant_bascule_<date>` (jamais supprimée), démarre le service et
   affiche l'adresse à donner aux postes.
3. Sur chaque poste : bouton **« Basculer sur le 2e serveur »** du bandeau (il ne règle le poste que si l'API de secours répond déjà ;
   ou rubrique Serveur et PC de secours), puis fermer et rouvrir Pharma Officine.
4. GED : recopier le dossier `GED` du disque externe si besoin (`restaurer_pharmacie.ps1 -SansBase`, DossierGed du PC de secours).
5. Ressaisir les opérations faites depuis la copie ; ressaisir le mot de passe des notifications (chiffré pour le PC d'origine).
6. Retour sur le PC serveur réparé : sauvegarde sur le PC de secours, restauration sur le PC serveur (« Restauration » ci-dessus), puis
   vider `AdresseApi` sur chaque poste.

### Clé de scellement des journaux sur le PC de secours

L'API refuse de démarrer si la clé de scellement est illisible (elle n'est jamais remplacée en silence). Sur le PC de secours, la clé du
PC serveur n'est pas lisible telle quelle : **à préparer à l'avance**, le titulaire l'exporte (Administration › 2e serveur › Clé de
scellement, fichier protégé par son mot de passe, rangé sur une clé USB au coffre) et l'administrateur l'importe sur le PC de secours,
le titulaire tapant le mot de passe : `"C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe" --importer-cle E:\cle-scellement-….pocle`
(détail dans « 2e serveur », étape 4). Contrôle : `Pharma.Api.exe --empreinte-cle` affiche la même empreinte que l'écran du titulaire.

## 2e serveur (réplication en continu)

> **Option facultative, fortement conseillée.** L'officine fonctionne sans 2e serveur : sans `SecondServeur:AutreServeur` ni slot
> de réplication, l'API démarre normalement, aucune alerte de réplication n'est émise, et la protection des données repose sur les
> sauvegardes et la **copie de secours** (PC de secours, restauration). Le 2e serveur est conseillé parce qu'il réduit l'arrêt de
> l'officine à quelques minutes et la perte à quelques secondes en cas de panne du PC serveur. Le titulaire décide selon ses moyens.

Ajouté le 2026-11-16 (lot 5, version actuelle ; GO du titulaire « go 2e base + API, oui export clé »). Un **2e serveur** dans l'officine
(réseau local, sans Internet) reçoit **en continu** la base du serveur principal (réplication physique native de PostgreSQL, en
streaming, avec slot). Il garde une **API de secours en veille**. En cas de panne du principal, une **personne** décide la bascule : le 2e
serveur devient le principal en quelques minutes, **sans perte** des opérations déjà reçues (en général toutes, à quelques secondes près).
Toujours **une seule base par officine** (§ 6 bis de l'analyse des écarts) : la base du 2e serveur est une copie en lecture seule, jamais
utilisée par les postes tant qu'il n'est pas basculé. La copie de secours du lot 5 A (PC de secours, ci-dessus) reste en place en plus.

Scripts d'administration : `scripts\second-serveur\` (livrés dans le paquet d'installation, dossier `scripts\second-serveur\`,
avec `scripts\basculer-pc-secours.ps1` ; à copier sur les deux serveurs). `commun.ps1` doit rester à côté des autres. Ils demandent un administrateur Windows et le
superutilisateur PostgreSQL ; chacun a un mode **`-Essai`** qui n'écrit rien (contrôles et actions prévues affichés) : **toujours le
lancer d'abord**.

| Script | Où | Rôle |
|---|---|---|
| `preparer-principal.ps1` | principal | rôle de réplication, `postgresql.conf` (`wal_level = replica`, `max_wal_senders`, `max_replication_slots`, `hot_standby`, `max_slot_wal_keep_size`, `listen_addresses`), `pg_hba.conf` (copie datée gardée ; lignes pour l'adresse du 2e serveur seulement), slot `second_serveur`, `pg_monitor` pour `pharma_app` |
| `initialiser-second-serveur.ps1` | 2e serveur | API de secours arrêtée en démarrage manuel, ancien dossier de données gardé, `pg_basebackup` avec slot et `-R` (`standby.signal`), démarrage, contrôle « lecture seule », import de la clé de scellement |
| `verifier-second-serveur.ps1` | l'un ou l'autre | rôle, réplication (`pg_stat_replication` / `pg_stat_wal_receiver`), version de l'API, service, empreinte de la clé, imprimantes, dossiers ; inscrit le relevé sur le principal (écran « 2e serveur ») |
| `basculer-second-serveur.ps1` | 2e serveur | **bascule** : refuse si l'ancien principal répond encore ; `pg_promote()` ; API de secours démarrée (démarrage automatique) |
| `remettre-en-second.ps1` | ancien principal | **retour** : il redevient 2e serveur (ancienne base gardée à côté), copie complète depuis le nouveau principal |

### Choix de la bascule : script sur le 2e serveur, pas de service réseau

La bascule (promotion de la base, démarrage d'un service Windows) demande des droits administrateur. Un petit service réseau installé
sur le 2e serveur, appelé par un bouton du poste, devrait porter ces droits et les ouvrir au réseau, et ne pourrait pas s'assurer
que l'ancien principal est vraiment arrêté (coupure réseau partielle : risque de **deux serveurs qui écrivent**). La solution retenue,
la plus sûre :

- la bascule se fait **sur le 2e serveur**, par `basculer-second-serveur.ps1`, lancé par l'administrateur (ou le titulaire, avec un compte
  administrateur du PC) à la demande du titulaire. Le script **refuse** si l'ancien principal répond encore (PostgreSQL ou API) et demande
  de taper `BASCULER` ;
- le bouton **« Basculer sur le 2e serveur »** des postes (bandeau « Serveur injoignable », adresse `AdresseApiSecours`) règle le poste sur le
  2e serveur **seulement si l'API du 2e serveur répond déjà**, donc après la bascule. Sinon il affiche la consigne. Il est ouvert à tout
  employé connecté au poste (comme le bouton « PC de secours » du lot 5 A) : il ne change rien au serveur, et chaque caisse doit pouvoir
  repartir sans attendre le titulaire ;
- **jamais deux API qui écrivent** : l'API **refuse de démarrer** si sa base est en lecture seule (2e serveur non basculé), avec un message
  clair dans son journal ; et, si `SecondServeur:AutreServeur` est réglé, si l'autre serveur a lui aussi une base principale, sauf si la
  sienne est la plus récemment promue (n° de *timeline* plus grand). Ainsi, un ancien principal qui redémarre après une bascule ne
  reprend pas la main. Une écriture tentée sur une base en lecture seule est refusée par PostgreSQL (message « base en lecture seule »).

### À préparer à l'avance (une fois)

1. **2e serveur** : un PC de l'officine allumé en permanence, sur le même réseau, avec **la même version majeure de PostgreSQL** (étape 1,
   superutilisateur `postgres` ; la copie apporte ensuite tous les rôles et bases du principal) et **l'API à la même version, installée
   par le même installateur** (`scripts\paquet`, étape 3), avec le même `appsettings.Production.json` que le principal (même chaîne de
   connexion `Host=localhost`, même clé de jetons `Jwt`), sauf ce qui est propre à ce PC (dossiers Chifa / MDN, voir 5). Port 5080 ouvert au
   pare-feu ; port 5432 ouvert sur chaque serveur **pour l'autre serveur seulement** (réplication et contrôle « deux principaux » ;
   `pg_hba.conf`, recopié par `pg_basebackup` quand il est dans le dossier de données, autorise les deux adresses). Chaque **mise à jour** se fait sur les deux serveurs, même jour, même
   installateur (principal d'abord, scripts de la base appliqués sur le principal seulement : ils arrivent sur le 2e serveur par la
   réplication), puis `verifier-second-serveur.ps1` sur le 2e serveur : l'écran « 2e serveur » signale une version différente en rouge.
2. **Principal** : `.\preparer-principal.ps1 -AdresseSecond <IP du 2e> -AdressePrincipal <IP du principal> -Essai`, puis sans `-Essai`
   (mot de passe du rôle de réplication choisi et noté, 12 caractères au moins), puis `-Redemarrer` à un moment calme si demandé.
   Dans l'appsettings de l'API du principal : `"SecondServeur": { "AutreServeur": "<IP du 2e>" }`, puis redémarrer le service.
3. **Clé de scellement** : le titulaire, Administration › **2e serveur › Clé de scellement** : mot de passe (12 caractères au moins, saisi
   deux fois), dossier de la clé USB, « Exporter la clé ». Fichier `cle-scellement-AAAAMMJJ-<empreinte>.pocle` chiffré par le mot de passe
   (PBKDF2-SHA256 600 000 tours, AES-256-GCM) : la clé n'est jamais en clair, ni dans le fichier ni au journal. Ranger la clé USB **au
   coffre** et le mot de passe **à part**. Ce chiffrement porte sur la clé seulement, pas sur les données.
4. **2e serveur** : `.\initialiser-second-serveur.ps1 -Principal <IP du principal> -FichierCle E:\cle-scellement-….pocle -Essai`, puis
   sans `-Essai` (taper `OUI`, mot de passe du rôle de réplication, puis le titulaire tape le mot de passe du fichier de clé). Dans
   l'appsettings de l'API du 2e serveur : `"SecondServeur": { "AutreServeur": "<IP du principal>" }`. Le service de l'API reste
   **arrêté, en démarrage manuel**.
5. **Réglages propres à chaque serveur** : Administration › 2e serveur › **Réglages par serveur** (titulaire) : cliquer un serveur, régler son
   dossier d'échange **CHIFA-Officine** et son dossier du **logiciel MDN** (vide : réglages `Chifa:DossierExport` / `Mdn:Dossier` de
   l'appsettings de ce serveur), cocher « CHIFA-Officine installé », « Lecteur de carte et Token disponibles », « Logiciel MDN installé »,
   « Douchette branchée et testée ». Ces réglages sont dans la base (table `serveur_officine`, script 122), donc répliqués : à la bascule,
   l'API du 2e serveur prend **les dossiers de sa propre ligne** (au démarrage de l'API).
6. **Contrôle** : `.\verifier-second-serveur.ps1 -Principal <IP du principal> -DossierChifa <…> -DossierMdn <…>` sur le 2e serveur (mot de
   passe de `pharma_app` pour inscrire le relevé) ; puis l'écran du titulaire, Administration › **2e serveur › État de la réplication** :
   « 2e serveur connecté », retard en secondes et en octets, slot actif ; **Réglages par serveur** : versions identiques, clé présente et
   **identique**, dossiers joignables, imprimantes.
7. **Postes** : Administration › Imprimantes et étiquettes › Serveur et PC de secours : adresse de l'API du 2e serveur dans « PC de secours »
   (`AdresseApiSecours`, ex. `http://192.168.1.21:5080/`).
8. **Alerte** : le titulaire est prévenu (SMS / e-mail des alertes de sauvegarde, au plus une fois par heure) si le 2e serveur décroche ou si
   le retard dépasse les seuils réglés à l'écran (300 s et 64 Mo par défaut). Un 2e serveur éteint longtemps : le principal garde les
   modifications jusqu'à `max_slot_wal_keep_size` (20 Go par défaut), au-delà le 2e serveur est à réinitialiser (étape 4).
   Rattrapage : au redémarrage de l'un ou l'autre PC, la réplication reprend seule et rattrape le retard.

### Bascule (panne du principal)

1. Constater la panne (bandeau « Serveur injoignable » sur les postes) ; vérifier le principal (allumé, réseau, services). Le titulaire décide.
2. Sur le principal, si possible : arrêter le service de l'API et PostgreSQL, ou **débrancher son câble réseau**.
3. Sur le 2e serveur, PowerShell en administrateur, dossier `scripts\second-serveur` :
   ```powershell
   .\basculer-second-serveur.ps1 -AncienPrincipal <IP du principal> -Essai   # contrôles seulement
   .\basculer-second-serveur.ps1 -AncienPrincipal <IP du principal>          # taper BASCULER
   ```
   Le script affiche l'heure de la dernière transaction reçue : ce qui a été saisi après (rare) est à ressaisir.
4. Sur chaque poste : bouton **« Basculer sur le 2e serveur »** du bandeau, puis fermer et rouvrir Pharma Officine.
5. Titulaire : écran « 2e serveur » (ce serveur est maintenant « Principal », dossiers Chifa / MDN, clé identique) ; CHIFA-Officine, lecteur
   de carte et Token sur ce PC (question posée à la CNAS, `docs/taches/2026-11-15-recette-complete/demande-cnas.md`).

### Retour (ancien principal réparé)

1. **Ne pas redémarrer l'API de l'ancien principal** (elle refuserait de démarrer si `SecondServeur:AutreServeur` est réglé).
2. Sur le nouveau principal : `.\preparer-principal.ps1 -AdresseSecond <IP de l'ancien> -AdressePrincipal <IP du nouveau>` (slot, `pg_hba.conf`).
3. Sur l'ancien principal : `.\remettre-en-second.ps1 -NouveauPrincipal <IP du nouveau> -Essai`, puis sans `-Essai`. Son ancienne base est
   **gardée** à côté (`<dossier>_ancien_principal_<date>`) : elle peut contenir les dernières saisies d'avant la panne, à examiner avant
   toute suppression. Il redevient 2e serveur et rattrape le nouveau principal.
4. **Rebascule à un moment calme** (facultatif, pour revenir à la répartition d'origine) : arrêter l'API et PostgreSQL du serveur principal
   actuel, lancer `basculer-second-serveur.ps1` sur l'autre, puis `remettre-en-second.ps1` sur celui qu'on vient d'arrêter, et remettre
   `AdresseApi` des postes (rubrique Serveur et PC de secours).
