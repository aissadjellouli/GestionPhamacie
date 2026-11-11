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

Le dossier `database/init/` contient trois fichiers :

| Fichier | Rôle |
| --- | --- |
| `schema.sql` | Crée toute la structure (tables, vues, fonctions, déclencheurs). Équivaut aux scripts `001` à `059`. |
| `seed_referentiel.sql` | Référentiel de départ : rôles, droits, paramètres, CNAS et CASNOS, substances surveillées, coffre, compte titulaire provisoire. |
| `clean.sql` | Remet une base à vierge. À utiliser uniquement pour recommencer une installation ratée ou une répétition. |

- [ ] **2.1** Créer la structure :
  ```powershell
  & psql -h localhost -U pharma_app -d <base> -v ON_ERROR_STOP=1 -f database\init\schema.sql
  ```
  Si la base contient déjà le schéma, le script s'arrête sans rien faire.
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

- [ ] **3.1** Publier l'API dans un dossier d'installation :
  ```powershell
  dotnet publish src\Pharma.Api -c Release -o "C:\Program Files\PharmaOfficine\Api"
  ```
  Le serveur a besoin du runtime ASP.NET Core 10, sauf si la publication est autonome.
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
  - `Urls` : `http://0.0.0.0:5080` par défaut.
- [ ] **3.3** Installer l'API comme service Windows, démarré automatiquement (nom attendu : « PharmaOfficine API ») :
  ```powershell
  sc.exe create "PharmaOfficine API" binPath= "C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe" start= auto
  sc.exe start "PharmaOfficine API"
  ```
- [ ] **3.4** Ouvrir le port 5080 du pare-feu Windows aux postes de la pharmacie.
- [ ] **3.5** Vérifier : `http://<serveur>:5080/api/auth/comptoir` répond depuis un poste.
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

- [ ] **4.1** Installer le poste (publication de `src/Pharma.Desktop`). Il a besoin du runtime .NET 10 Desktop et de WebView2, présent sur Windows 10 et 11 à jour.
- [ ] **4.2** Variables d'environnement du poste :
  - `PHARMA_API=http://<serveur>:5080/` ;
  - `PHARMA_POSTE` : un nom **différent pour chaque poste** (ex. « Caisse 1 », « Caisse 2 »). Ce nom identifie la caisse (le tiroir) que partagent les employés du poste. Deux postes portant le même nom partageraient la même caisse.
- [ ] **4.3** Ouvrir le poste et vérifier que l'écran de connexion s'affiche.

## Étape 5 — Première connexion et sécurité

- [ ] **5.1** Se connecter avec **`titulaire` / `Provisoire-2026`**.
- [ ] **5.2** L'écran « Choisissez votre mot de passe » s'ouvre de force. Le titulaire y choisit son mot de passe personnel (8 caractères minimum). Tant que ce n'est pas fait, le serveur refuse toute autre action.
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
  - versement des caisses au coffre (manuel ou automatique).
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
3. Appliquer **uniquement les nouveaux scripts** `database/0xx_*.sql`, dans l'ordre :
   - avec `psql -v ON_ERROR_STOP=1 -f`, hors transaction ;
   - ils sont réexécutables sans dégât ;
   - **ne jamais** relancer `init/schema.sql` ni `init/clean.sql` sur une base en service.
4. Remplacer les fichiers de l'API et des postes, puis redémarrer le service.
5. Nouvelle nomenclature du ministère : la réimporter dans Administration › Nomenclature. L'import remplace l'ancienne liste sans toucher au catalogue.

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
4. Sauvegarde plus ancienne que la version du logiciel : appliquer les scripts `database/0xx_*.sql` plus récents (voir « Mises à jour du logiciel »).
5. Redémarrer le service. Les documents restés sur les postes (« A envoyer ») repartent dans la GED à la connexion suivante.
6. Ressaisir les opérations faites depuis la sauvegarde (en général la journée) ; sur un nouveau PC, ressaisir le mot de passe des
   notifications (chiffré pour le PC d'origine, voir [notifications.md](notifications.md)).

La copie de la GED sur le disque externe n'a qu'un exemplaire par document : un document abîmé sur le serveur remplace sa copie à la
sauvegarde suivante. Garder deux disques externes en rotation (étape 8.2).
