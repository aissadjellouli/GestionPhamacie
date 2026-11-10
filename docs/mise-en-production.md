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
  & psql -h localhost -U postgres -d postgres -c "CREATE ROLE pharma_app LOGIN PASSWORD '<mot de passe fort>'"
  & psql -h localhost -U postgres -d postgres -c "CREATE DATABASE <base> OWNER pharma_app ENCODING 'UTF8' TEMPLATE template0"
  ```
- [ ] **1.3** N'ouvrir le port 5432 du pare-feu **qu'aux postes de la pharmacie**, et seulement si un outil y accède directement. L'application, elle, passe par l'API.

## Étape 2 — Base de données : création et référentiel (`database/init/`)

Le dossier `database/init/` contient trois fichiers :

| Fichier | Rôle |
| --- | --- |
| `schema.sql` | Crée toute la structure (tables, vues, fonctions, déclencheurs). Équivaut aux scripts `001` à `035`. |
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
  - `Jwt__Cle` : une clé aléatoire de **64 caractères minimum**, propre à la pharmacie. La valeur d'exemple d'`appsettings.json` ne doit jamais rester ;
  - `Chifa__DossierExport` : le dossier d'export de Chifa sur ce PC ;
  - `Ordonnances__Dossier` : le dossier des scans d'ordonnances (par défaut `C:\ProgramData\PharmaOfficine\Ordonnances`) ;
  - `Urls` : `http://0.0.0.0:5080` par défaut.
- [ ] **3.3** Installer l'API comme service Windows, démarré automatiquement (nom attendu : « PharmaOfficine API ») :
  ```powershell
  sc.exe create "PharmaOfficine API" binPath= "C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe" start= auto
  sc.exe start "PharmaOfficine API"
  ```
- [ ] **3.4** Ouvrir le port 5080 du pare-feu Windows aux postes de la pharmacie.
- [ ] **3.5** Vérifier : `http://<serveur>:5080/api/auth/comptoir` répond depuis un poste.

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

- [ ] **8.1** Administration › Sauvegardes : régler le dossier et la rétention, puis lancer une première sauvegarde.
- [ ] **8.2** Planifier `scripts\sauvegarde_pharmacie.ps1` chaque soir dans le Planificateur de tâches. Garder deux disques externes en rotation.
- [ ] **8.3** **Tester une restauration** sur une base de test, avant la mise en service puis chaque mois.

## Étape 9 — Reprise des données de l'ancien logiciel (si besoin)

L'ancienne base n'est jamais modifiée : on travaille sur une copie, ce qui rend le retour arrière simple.

- [ ] **9.1** **Transit** : Administration › Reprise de données (titulaire). L'assistant se connecte à l'ancienne base par ODBC, en lecture seule.
  - Sources possibles : Access, dBase, CSV, Excel, Firebird, SQLite, HFSQL par son pilote ODBC, ou une connexion saisie champ par champ.
  - L'assistant propose la correspondance des tables et des colonnes, à confirmer, puis copie les données dans le schéma `import`.
  - Les mots de passe ne sont ni extraits ni enregistrés.
- [ ] **9.2** **Rapprochement** des produits : par code-barres, n° d'enregistrement, puis nom approché à confirmer.
- [ ] **9.3** **Contrôles** : toute anomalie bloquante empêche le chargement.
- [ ] **9.4** **Chargement**, en une transaction. Un lot sans date de péremption devient « date à vérifier ».
- [ ] **9.5** **Vérification** des totaux, avec un rapport signé par le pharmacien.

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
