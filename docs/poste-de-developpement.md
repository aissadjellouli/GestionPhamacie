# Installer un poste de développement

> Nouveau développeur sur un PC neuf : suivre d'abord la liste complète [nouveau-poste-developpeur.md](nouveau-poste-developpeur.md) (accès, logiciels, git, bases, Visual Studio, central, Docker, règles du projet).

Procédure suivie pour installer PostgreSQL, créer les bases locales et lancer l’API et le poste client depuis Visual Studio. Temps total : environ 20 minutes.

## 1. Prérequis

| Outil | Version utilisée | Remarque |
| --- | --- | --- |
| Windows | 10 version 2004 ou plus récent, ou 11 | Exigé par le poste client (WebView2) |
| SDK .NET | 10.0.100 ou plus récent | Voir `global.json` |
| Visual Studio | Community 2026 (18.x) | Ouvre `PharmaOfficine.slnx` ou `PharmaOfficine.sln` |
| PostgreSQL | 18.6 | 17 suffit pour les bases d'officine seulement ; le serveur central exige 18 |
| Runtime WebView2 | celui de Windows à jour | Déjà présent sur Windows 10/11 à jour |

## 2. Installer PostgreSQL 18

1. Télécharger l’installateur Windows depuis https://www.postgresql.org/download/windows/ (fichier `postgresql-18.x-x-windows-x64.exe`).
   - C’est la version libre et gratuite de PostgreSQL (licence PostgreSQL), y compris pour un usage commercial.
   - L’installateur est publié par EnterpriseDB. Pour vérifier le fichier :
     ```powershell
     Get-AuthenticodeSignature "$env:USERPROFILE\Downloads\postgresql-18.6-5-windows-x64.exe" | Format-List Status, SignerCertificate
     ```
     Résultat attendu : `Status : Valid`, signataire `CN=EnterpriseDB Corporation`.
2. Lancer l’installateur et garder :
   - composants : **PostgreSQL Server** et **Command Line Tools** (Stack Builder inutile) ;
   - **mot de passe du super-utilisateur `postgres`** : le choisir et le noter ;
   - **port** : `5432` ;
   - locale : celle par défaut.
3. Vérifier :
   ```powershell
   & "C:\Program Files\PostgreSQL\18\bin\psql.exe" --version
   Get-Service postgresql*
   ```
   Attendu : `psql (PostgreSQL) 18.x` et un service `Running`.

Les extensions `pg_trgm` et `pgcrypto` utilisées par les scripts sont incluses.

## 3. Créer les bases `pharmacie_dev` et `pharmacie_test`

Dans une fenêtre PowerShell :

```powershell
# Autorise les scripts pour cette fenêtre seulement (Windows les bloque par défaut)
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass

cd C:\MesDev\PharmaOfficine
.\scripts\initialiser_base_locale.ps1
```

Le script :
- trouve tout seul la dernière version de PostgreSQL installée (sinon : `-PgBin "C:\Program Files\PostgreSQL\18\bin"`) ;
- règle l’encodage UTF-8 de psql (les scripts contiennent des accents) ;
- demande le mot de passe de `postgres` ;
- crée le compte applicatif `pharma_app` (mot de passe de développement `pharma_dev`) ;
- crée les bases manquantes, puis applique `database/migration/001` à `009` dans l’ordre et les données fictives `database/data/9xx_*.sql`.

**Connexion de l'API en développement** : le mot de passe n'est jamais dans `src/Pharma.Api/appsettings.json` (versionné et livré dans le paquet, `Password=` vide). Créer une fois, sur le poste de développement, `src/Pharma.Api/appsettings.Development.json` (ignoré par git, retiré du paquet ; Visual Studio le charge avec l'environnement `Development`) :
```json
{
  "ConnectionStrings": { "Pharmacie": "Host=localhost;Port=5432;Database=pharmacie_test;Username=pharma_app;Password=pharma_dev" }
}
```
Les tests d'intégrité lisent la même connexion (ou la variable `PHARMA_TEST_CNX`). Chez le pharmacien, `installer.ps1` tire au sort le mot de passe et l'écrit dans `apiappsettings.Production.json`.

Les scripts SQL sont **idempotents** (voir [base-de-donnees.md](base-de-donnees.md#scripts-idempotents)) : sur une base existante, le script la **met à jour** sans rien perdre.

### Mettre à jour une base après un nouveau script SQL

Par exemple après l’arrivée de `009_caisse.sql`, sans mot de passe `postgres` :
```powershell
.\scripts\initialiser_base_locale.ps1 -MiseAJour
```
Ne pas oublier d’arrêter l’API avant, puis de la relancer.

### Repartir de zéro

Pour remettre à zéro une seule base (par exemple `pharmacie_test` après des essais de rappel de lot, qui bloquent un lot définitivement) :
```powershell
.\scripts\initialiser_base_locale.ps1 -Bases pharmacie_test -Recreer
```
La base est supprimée, données comprises : le script demande d’abord de retaper son nom (`pharmacie_test`) ; une autre saisie arrête tout sans rien toucher. Pour un enchaînement automatique, sans question : ajouter `-Force`.

Options : `-SansDemo` pour ne pas charger les données fictives, `-Bases` pour choisir les bases.

### Basculer entre `pharmacie_dev` et `pharmacie_test` (2026-11-27)

**Ponctuellement** (une seule fenêtre PowerShell, rien n'est changé ailleurs) :
```powershell
$env:ConnectionStrings__Pharmacie = "Host=localhost;Port=5432;Database=pharmacie_test;Username=pharma_app;Password=pharma_dev"
dotnet run --project src\Pharma.Api
```

**Durablement** (Visual Studio et tous les programmes lancés ensuite) : trois variables **de l'utilisateur Windows** vont ensemble.
L'API lit la base dans `ConnectionStrings__Pharmacie` (prioritaire sur `appsettings.Development.json`) ; le poste lit l'adresse de l'API
dans `poste.json` (`AdresseApi`), sinon dans `PHARMA_API`, sinon `http://localhost:5080/` ; le dossier de synchronisation contient la clé
et le certificat **propres à chaque base** (une base reliée au central avec le certificat de l'autre serait refusée).

| Variable (utilisateur) | `pharmacie_dev` | `pharmacie_test` |
|---|---|---|
| `ConnectionStrings__Pharmacie` | `…;Database=pharmacie_dev;…` | `…;Database=pharmacie_test;…` |
| `PHARMA_API` (poste) | absente, ou `http://localhost:5080/` | `http://localhost:5081/` |
| `Synchronisation__Dossier` | `…\central-dev\sync-pharmacie_dev` | `…\central-dev\sync-pharmacie_test` |
| Port de l'API (profil ou `--urls`) | 5080 | 5081 |

Passer sur `pharmacie_test`, dans **sa** fenêtre PowerShell (le mot de passe de la base est gardé tel quel, jamais affiché) :
```powershell
$v = [Environment]::GetEnvironmentVariable("ConnectionStrings__Pharmacie", "User")
[Environment]::SetEnvironmentVariable("ConnectionStrings__Pharmacie", ($v -replace "Database=pharmacie_dev", "Database=pharmacie_test"), "User")
[Environment]::SetEnvironmentVariable("PHARMA_API", "http://localhost:5081/", "User")
$s = [Environment]::GetEnvironmentVariable("Synchronisation__Dossier", "User")
[Environment]::SetEnvironmentVariable("Synchronisation__Dossier", ($s -replace "sync-pharmacie_dev", "sync-pharmacie_test"), "User")
```

Revenir sur `pharmacie_dev` :
```powershell
$v = [Environment]::GetEnvironmentVariable("ConnectionStrings__Pharmacie", "User")
[Environment]::SetEnvironmentVariable("ConnectionStrings__Pharmacie", ($v -replace "Database=pharmacie_test", "Database=pharmacie_dev"), "User")
[Environment]::SetEnvironmentVariable("PHARMA_API", $null, "User")
$s = [Environment]::GetEnvironmentVariable("Synchronisation__Dossier", "User")
[Environment]::SetEnvironmentVariable("Synchronisation__Dossier", ($s -replace "sync-pharmacie_test", "sync-pharmacie_dev"), "User")
```

Contrôle (sans le mot de passe) :
```powershell
(([Environment]::GetEnvironmentVariable("ConnectionStrings__Pharmacie", "User")) -split ";" | Where-Object { $_ -notmatch "^\s*Password=" }) -join ";"
[Environment]::GetEnvironmentVariable("PHARMA_API", "User")
[Environment]::GetEnvironmentVariable("Synchronisation__Dossier", "User")
```

**Ensuite, toujours** :
1. Fermer Visual Studio, l'API, le poste et le central lancés depuis le dépôt, puis rouvrir Visual Studio : une variable n'est vue que par
   les programmes démarrés **après** son changement.
2. Lancer l'API sur le bon port (5080 ou 5081) puis le poste. **Attendu** : barre d'état « Connecté », données de la bonne base.
3. Si le poste a une `AdresseApi` remplie dans `C:\ProgramData\PharmaOfficine\poste.json`, elle l'emporte sur `PHARMA_API` : la vider
   ou la changer aussi.

**Chaque base a sa propre offre** (code de l'officine différent) : un code de transition par base
([signer-code-activation.md](signer-code-activation.md) ; `pharmacie_dev` saisi le 2026-11-26, `pharmacie_test` à saisir). Sans code, la
base est à l'offre de base : 2 postes, écrans des packs fermés.

### Vérifier la base

```powershell
$env:PGPASSWORD = "pharma_dev"
& "C:\Program Files\PostgreSQL\18\bin\psql.exe" -h localhost -U pharma_app -d pharmacie_dev -c "SELECT count(*) AS produits FROM produit;"
Remove-Item Env:\PGPASSWORD
```

Contenu attendu avec les données fictives : 30 tables, 8 vues, extensions `pg_trgm` et `pgcrypto`, encodage UTF8, 3 comptes, 9 produits, 10 lots, 300 ventes, 3 boîtes étiquetées, 7 alertes ouvertes.

## 4. Lancer l’API et le poste client depuis Visual Studio

1. Ouvrir `PharmaOfficine.slnx` (ou `PharmaOfficine.sln`).
2. Dans la barre d’outils, liste à côté du bouton ▶ : choisir **« API + Poste client »**. Ce profil est défini dans `PharmaOfficine.slnLaunch` : il démarre `Pharma.Api` puis `Pharma.Desktop`.
3. **F5**. L’API écoute sur http://localhost:5080 (contrôle : http://localhost:5080/api/healthcheck), puis la fenêtre du poste s’ouvre.

Sans ce profil : clic droit sur la solution > **Configurer les projets de démarrage** > **Plusieurs projets de démarrage** > `Pharma.Api` puis `Pharma.Desktop` sur **Démarrer**.

### Lancer API + Poste + Central dans Visual Studio

Outil de développement, jamais de production : `scripts\dev\preparer_central_vs.ps1`. Il relie l'officine `pharmacie_dev` au serveur
central de développement `pharmacie_cloud_dev`, tous deux lancés en débogage par Visual Studio (profil **« API + Poste + Central »**
de `PharmaOfficine.slnLaunch` : `Pharma.Cloud.Api`, `Pharma.Api`, `Pharma.Desktop`). Aucune base n'est créée, supprimée ni restaurée,
aucun compte de l'application n'est créé ; toute autre base est refusée.

**1. Préparer (une fois)** — dans une fenêtre PowerShell, à la racine du dépôt, avec le mot de passe de `pharma_app` (section 3) dans
`PGPASSWORD`, effacé ensuite (`Remove-Item Env:\PGPASSWORD`) ; sans `PGPASSWORD`, il est demandé en saisie masquée :
```powershell
.\scripts\dev\preparer_central_vs.ps1 -Preparer -WhatIf     # voir ce qui serait fait
.\scripts\dev\preparer_central_vs.ps1 -Preparer             # idempotent : le relancer reprend ce qui manque
```
Ce que fait `-Preparer` :
- contrôles : `psql`, `dotnet`, droits de `pharma_app` (pas superutilisateur, `CREATEROLE`, option ADMIN sur `pharma_cloud_api`),
  `pharmacie_cloud_dev` à jour (témoin du script central 028), port 5090 libre ;
- dossier `%LOCALAPPDATA%\PharmaOfficine\central-dev` (hors dépôt) : `autorite` (autorité et certificat HTTPS du central) et
  `sync-pharmacie_dev` (clé et certificat de synchronisation de l'officine, propres à cette base), réservés au compte courant, à SYSTEM
  et aux Administrateurs ; `outil` (outil d'administration du central, compilé hors des dossiers `bin` que Visual Studio verrouille) ;
  `etat.json` (code provisoire de l'officine au central, sans secret) ;
- rôle de connexion **`pharma_cloud_dev_vs`** (membre de `pharma_cloud_api`, mot de passe tiré au sort) ; le rôle `pharma_cloud_api`
  lui-même n'est pas modifié ;
- variables d'environnement **de l'utilisateur Windows** (mots de passe tirés au sort, réutilisés s'ils existent, jamais affichés) :
  `PHARMA_CLOUD_DOSSIER`, `PHARMA_CLOUD_MOT_DE_PASSE`, `PHARMA_CLOUD_CONNEXION` (rôle `pharma_cloud_dev_vs`),
  `ConnectionStrings__Pharmacie` (`pharmacie_dev`), `Synchronisation__Dossier` (`sync-pharmacie_dev`). Une variable déjà présente avec
  une autre valeur n'est jamais écrasée : le script s'arrête. `PHARMA_CLOUD_ADMIN_CONNEXION` (propriétaire de la base centrale) n'est
  **jamais** posée au niveau utilisateur : elle n'est passée qu'au processus de l'outil d'administration ;
- rôle de connexion `pharma_cloud_etat_vs` (membre de `pharma_cloud_etat`, script central `029` : lecture technique seule) et variable
  `PHARMA_CLOUD_ETAT_CONNEXION`, pour la **page d'état** ci-dessous ; si `029` n'est pas passé sur `pharmacie_cloud_dev` (ou si
  `pharma_app` n'a pas l'option ADMIN sur `pharma_cloud_etat`), cette étape est sautée avec un avertissement, le reste est préparé ;
- officine **« Développement (pharmacie_dev) »** créée au central si elle manque, sinon code neuf. Le **code d'activation** (usage
  unique, 24 h) est affiché, jamais écrit.

**2. Redémarrer Visual Studio** : fermer toutes ses fenêtres puis le rouvrir (il lit les variables d'utilisateur à son démarrage).

**3. Profil « API + Poste + Central », F5.** Le central écoute sur `https://127.0.0.1:5090/` (réglages `Cloud:Adresse` et `Cloud:Port`
de son `appsettings.json` ; son `launchSettings.json` n'a pas d'`applicationUrl`, elle serait ignorée), l'API sur http://localhost:5080.

**4. Activer par l'écran** (compte `titulaire`) :
```powershell
.\scripts\dev\preparer_central_vs.ps1 -Activer      # vérifie 5080 et 5090, rappelle la marche à suivre, donne un code neuf si besoin
```
Administration > Synchronisation > onglet **Réglages** : adresse du serveur central `https://127.0.0.1:5090/`, cocher
**Synchronisation activée**, **Enregistrer** ; onglet **Activation** : coller le code, **Activer**. Le chargement complet part en
arrière-plan. Suivi : `.\scripts\dev\preparer_central_vs.ps1 -Etat` (lecture seule) ou l'onglet **État**.

**Page d'état du central (développement seulement).** Pendant le débogage, ouvrir **http://127.0.0.1:5092** : officines, certificats
(début d'empreinte, validité, révocation), codes d'activation (utilisés ou non, expiration ; jamais le code ni son empreinte),
chargement complet (en cours / terminé, lots, lignes), 20 derniers lots reçus par officine, époques, 50 dernières lignes du journal de
sécurité ; rafraîchie toutes les 10 s, `http://127.0.0.1:5092/etat.json` pour la même chose en JSON. Aucune donnée de patient, aucune table
miroir, aucune écriture. C'est un **second serveur web séparé** dans le processus du central, en HTTP sur 127.0.0.1 seulement (toute autre
adresse est refusée), sans aucune route `/api/v1` ; il ne démarre que si l'environnement est `Development`, `Cloud:PageEtat` vaut `true`
(posé par le profil de lancement `src\Pharma.Cloud.Api\Properties\launchSettings.json`) et `PHARMA_CLOUD_ETAT_CONNEXION` existe. Sinon un
avertissement est journalisé et le central démarre normalement. Port : `Cloud:PortPageEtat` (5092 par défaut). Lecture avec le rôle
`pharma_cloud_etat` (RLS appliquée, officine posée dans chaque transaction) : la page refuse une connexion superutilisateur, `BYPASSRLS`
ou propriétaire des tables.

**Écrans du central (lot S7.1, développement).** Profil **« Central + Écrans »** (central + écrans seuls) ou **« API + Poste + Central »**
(les quatre) : le navigateur s'ouvre sur **https://127.0.0.1:5094/** (projet `src\Pharma.Cloud.Web`, processus séparé, refus de démarrer hors
boucle locale). Prérequis : scripts centraux `030` et `031` passés sur `pharmacie_cloud_dev`, puis `-Preparer` relancé (rôle `pharma_cloud_ecrans_dev_vs`, variable `PHARMA_CLOUD_ECRANS_CONNEXION`, dossier des codes, compte `editeur.dev` dont le mot de passe provisoire est
affiché une fois). Le code de second facteur est écrit dans `%LOCALAPPDATA%\PharmaOfficine\central-dev\codes\editeur.dev.txt` (mode fichier,
développement seulement). Certificat HTTPS : celui de développement d'ASP.NET (`dotnet dev-certs https --trust`, il couvre `127.0.0.1`).
Détails : [ecrans-central.md](ecrans-central.md).

**Accès à distance (lot S7.2b) : comment tester.** Prérequis : scripts centraux `032` puis `033` passés sur `pharmacie_cloud_dev` (après
relecture), `-Preparer` relancé (compte `titulaire.dev`), synchronisation du poste activée (étapes ci-dessus). Profil **« API + Poste + Central »**,
F5 ; dans le poste, se connecter avec le compte **titulaire** de l'officine, Administration › Synchronisation › **Accès à distance** :
identifiant `titulaire.dev`, mot de passe du titulaire du poste, « Générer le code » → code en grand, valable 30 minutes. Puis
https://127.0.0.1:5094/ : connexion `titulaire.dev`, « Rattacher une officine », saisir le code → tableau de bord ; le journal des accès de
l'officine montre « Poste de l'officine ». À vérifier aussi : identifiant inconnu ou `editeur.dev` → même message ; 6e demande dans l'heure
→ « Trop de codes demandés » ; employé non titulaire → texte sans bouton ; synchronisation décochée → explication sans bouton.

**Multi-officines et ordres (lot S7.3) : comment tester.** Prérequis : script central `034` passé sur `pharmacie_cloud_dev` (après 033,
après relecture), `titulaire.dev` rattaché (ci-dessus). Dans le poste (titulaire), Accès à distance › **Comptes rattachés** : la ligne
`titulaire.dev` ; « Droits », cocher « Ordres à distance », mot de passe → enregistré. Sur https://127.0.0.1:5094/ : « Mes officines »
→ lien « Ordres » ; « Recevoir un code de confirmation » (code dans `codes\titulaire.dev.txt`), « Demander une vérification
d’intégrité » → statut « En attente » puis « Appliqué » après le passage de synchronisation suivant ; « Demander une sauvegarde » →
« À confirmer sur le poste » (Synchronisation › Ordres du central, « Confirmer »). « Vue d’ensemble » : totaux par officine et total
du groupe. À vérifier aussi : même type déjà en cours → refusé ; « Retirer » `titulaire.dev` au poste → le tableau de bord est refusé
tout de suite et l’ordre ouvert passe « Annulé » ; employé non titulaire → aucune liste.

**Réglages communs (lot S7.4) : comment tester.** `pharmacie_test` est reliée au central de développement (officine
`142d3c7d-c7a3-4e40-84dc-04d4440af2dc`) et le compte web `titulaire.dev` y est rattaché avec le droit `ordres`. Prérequis : script
central `035` passé sur `pharmacie_cloud_dev` (après 034, après relecture). Sur https://127.0.0.1:5094/ (`titulaire.dev`) : « Mes
officines » → « Réglages communs » ; choisir « Péremption : alerte orange », valeur 150, l’officine cochée, « Recevoir un code de
confirmation » (code dans `%LOCALAPPDATA%\PharmaOfficine\central-dev\codes\titulaire.dev.txt`), « Proposer » → état « Proposé ». Au
poste branché sur `pharmacie_test` (titulaire), après le passage de synchronisation suivant : Administration › Synchronisation ›
Réglages mutualisés → bandeau « 1 proposition en attente », tableau actuel (barré) / proposé, mot de passe du titulaire,
« Accepter et appliquer » → Administration › Seuils montre 150 (ligne dans « Décisions précédentes »), journal des actions « seuils d’alerte » ; au central l’état
passe « Accepté ». À vérifier aussi : mot de passe faux → reste à confirmer ; « Refuser » (motif + mot de passe) → « Refusé » au central ;
deuxième proposition tant que la première attend → « Rien n’est envoyé » ; rouge 200 (orange actuel 150) → « Accepter » puis refus
avec le motif ; employé non titulaire → proposition visible sans boutons ; non décidé en 24 heures → « Expiré ».

**Lignes absentes (lot S7.5) : comment voir l'écran.** `pharmacie_test` est reliée au central de développement (officine
`142d3c7d-c7a3-4e40-84dc-04d4440af2dc`). Prérequis : script central `036` passé sur `pharmacie_cloud_dev` (après 035, après
relecture). Éditeur : https://127.0.0.1:5094/ (`editeur.dev`) → Officines → l'officine → rubrique « Lignes absentes »
(`/officines/142d3c7d-c7a3-4e40-84dc-04d4440af2dc?rubrique=absentes`) : quatre tuiles et tableau. Titulaire (`titulaire.dev`) :
`/mes-officines/142d3c7d-c7a3-4e40-84dc-04d4440af2dc` (tuile « Lignes absentes ») et `/mes-officines/ensemble` (colonne
« Absentes »). Sans aucune ligne absente : 0 et « Aucune ligne absente. ». **Provoquer des lignes absentes** (à faire seulement
avec l'accord du titulaire, de préférence sur un central jetable : la ligne ajoutée reste au central) : ajouter au central, par le
propriétaire et dans une transaction qui pose `app.officine_id`, une ligne « fantôme » dans une table miroir de l'officine (par exemple
`alerte`, `id` inexistant au poste, `sync_epoque` = époque courante, `sync_numero` ≤ dernier lot reçu), puis au poste (titulaire)
Administration › Synchronisation › État › « Recharger toutes les données » : à la fin du chargement, la ligne est marquée absente
(cause « Non expliquée », n° du chargement). Une restauration réelle (nouvelle époque, § 30 de la conception) donne les causes
« Nouvelle époque » puis « Restauration d'une sauvegarde » après la vérification d'intégrité.

**Codes du central par Gmail (lot S7.6) : passer du mode fichier au Gmail en local.** Le canal et le serveur d'e-mail des codes de
second facteur se règlent dans l'écran **« Envoi des codes »** des écrans du central (éditeur), jamais dans des variables Windows.
Prérequis : script central `037` passé sur `pharmacie_cloud_dev` (après 036, après relecture) ; variable `PHARMA_CLOUD_DOSSIER` posée par
`-Preparer` (les clés qui chiffrent le mot de passe SMTP vont dans son sous-dossier `cles-protection`).
1. **Créer un mot de passe d'application Gmail** (compte Gmail de l'éditeur, jamais le mot de passe du compte) : compte Google ›
   **Sécurité** › activer la **Validation en deux étapes** ; puis, toujours dans Sécurité, **« Mots de passe des applications »**
   (https://myaccount.google.com/apppasswords), nom « Écrans du central », **Créer** : Google affiche UNE fois un mot de passe de
   **16 caractères** (en 4 groupes de 4) ; le saisir sans les espaces. Il ne se garde nulle part ailleurs (ni fichier, ni dépôt, ni message).
   Sans validation en deux étapes, ou si l'administrateur d'un compte Google professionnel l'a interdit, l'entrée n'apparaît pas.
2. **Par la console (le plus simple, comme en production)** : dans une session PowerShell, à la racine du dépôt :
   ```powershell
   $env:PHARMA_CLOUD_ADMIN_CONNEXION = "Host=localhost;Port=5432;Database=pharmacie_cloud_dev;Username=pharma_app;Password=pharma_dev"
   dotnet run --project src\Pharma.Cloud.Api -- --envoi-codes --serveur smtp.gmail.com --port 587 --tls starttls `
       --identifiant <adresse Gmail de l'éditeur> --operateur "<nom>" --essai <votre adresse>
   Remove-Item Env:\PHARMA_CLOUD_ADMIN_CONNEXION
   ```
   Le mot de passe d'application est demandé au clavier (rien ne s'affiche). Adresse qui recevra les codes, même session :
   `dotnet run --project src\Pharma.Cloud.Api -- --contact-compte editeur.dev --email <votre adresse> --auteur "<nom>"`
   (idem `titulaire.dev`) ; l'étape 3 devient inutile. Ou bien, à l'écran :
   **Régler, encore en mode fichier** (codes dans `%LOCALAPPDATA%\PharmaOfficine\central-dev\codes\`) : https://127.0.0.1:5094/,
   `editeur.dev`, menu **« Envoi des codes »** : canal **E-mail**, serveur `smtp.gmail.com`, port `587`, sécurité **STARTTLS**,
   identifiant et expéditeur = l'adresse Gmail, mot de passe = celui de l'étape 1. **« Envoyer un e-mail d'essai »** à votre adresse
   (rien n'est enregistré) → « E-mail d'essai envoyé ». Puis « Recevoir un code de confirmation » (code dans le fichier), saisir le code,
   **« Enregistrer »**. Le mot de passe n'est plus jamais affiché (« Renseigné : vide = inchangé »).
3. **Contact du compte** : menu **« Comptes »** → `editeur.dev` › « Modifier » : l'e-mail qui recevra les codes (votre adresse réelle).
   **Dès cette étape, en mode fichier, chaque code part aussi par Gmail** à l'adresse du compte (et reste écrit dans le fichier) :
   l'étape 4 n'est utile que pour essayer le comportement de production (plus de fichier).
4. **Passer au réglage en base** (optionnel) : lancer les écrans avec l'argument `--Ecrans:ModeCodes=reglage` :
   `dotnet run --project src\Pharma.Cloud.Web -- --Ecrans:ModeCodes=reglage`, ou Visual Studio › propriétés de débogage du profil
   `Pharma.Cloud.Web` › arguments de ligne de commande (Visual Studio l'écrit dans `Properties\launchSettings.json` : ne pas le commiter). La connexion envoie alors le code par Gmail.
   Revenir au mode fichier : retirer l'argument (`appsettings.Development.json` garde `fichier`). Si le réglage est incomplet ou
   illisible (dossier de clés changé), les codes repartent dans le fichier (développement seulement).
Les variables `PHARMA_CLOUD_SMTP_*` ne servent plus aux écrans en développement ; elles restent lues par la console pour le départ
d'une officine (prioritaires ; sinon le compte du réglage « Envoi des codes »). Aucune adresse ni aucun secret réel dans le dépôt.

**Reste S7.2 / S7.3 (scripts central 038 et local 160) : comment voir les écrans.** Prérequis (après relecture, sauvegarde `pg_dump -Fc`
`--enable-row-security` du central et accord du titulaire) : script central `038` sur `pharmacie_cloud_dev` (après 037), script local
`160` sur la base du poste (`pharmacie_test`, après 159) ; aucun `002_tables_miroir.sql` à repasser.
- **Remise à zéro d'un mot de passe** (éditeur `editeur.dev`) : menu **« Comptes »** → `titulaire.dev` › « Modifier » → bouton
  **« Mot de passe »** (`/comptes?compte=<n° du compte>&action=mot-de-passe`) : motif, identifiant ressaisi, « Recevoir un code de
  confirmation » (fichier `editeur.dev.txt`), « Remettre à zéro » → page **« Mot de passe provisoire »** (montré une fois). Sur
  `editeur.dev` lui-même : « votre propre compte », aucun formulaire. Le titulaire se reconnecte avec le provisoire et doit le changer.
  Penser à noter le nouveau mot de passe de `titulaire.dev`.
- **Message sur les postes** (titulaire `titulaire.dev`, droit « ordres » accordé depuis le poste) : « Mes officines » → « Ordres »
  (`/mes-officines/<officine>/ordres`), panneau **« Message sur les postes »** : texte (200 caractères au plus), « Durée d'affichage » (1 à 7 jours),
  code, « Envoyer le message ». **Provoquer l'affichage** : au poste branché sur `pharmacie_test`, attendre le passage de synchronisation
  suivant (une minute environ) : la pastille grise **« Message »** apparaît dans la barre d'état de tous les employés connectés ; un clic
  ouvre la fenêtre ; « Ne plus afficher » (titulaire seulement) la retire de tous les postes. Au central, l'ordre passe « Appliqué ».
  À voir aussi : avec une anomalie (sauvegarde en retard…), la pastille passe dans « À traiter » ; un message de plus de 200 caractères
  ou avec « www. » est refusé par l'écran.
- **Retrait d'un rattachement** (titulaire) : « Mes officines » → « Retirer » (`/mes-officines/<officine>/retirer`) : nom de l'officine
  ressaisi, code → l'officine disparaît de la liste. Pour la retrouver : nouveau code tiré au poste (Accès à distance), puis « Rattacher ».

**5. Retirer** (central arrêté) : `.\scripts\dev\preparer_central_vs.ps1 -Retirer -WhatIf`, puis sans `-WhatIf`. Retire les variables
posées par le script, les rôles `pharma_cloud_dev_vs` et `pharma_cloud_etat_vs` et le dossier `%LOCALAPPDATA%\PharmaOfficine\central-dev` ; rien n'est supprimé en
base (l'officine reste au central, les réglages de synchronisation restent dans `pharmacie_dev` : décocher **Synchronisation activée**
à l'écran avant). Redémarrer ensuite Visual Studio.

À savoir :
- **Toute API lancée par ce compte Windows** (profil « API + Poste client », `dotnet run`) prend alors `pharmacie_dev` et ce dossier de
  synchronisation : les variables d'environnement l'emportent sur `appsettings.Development.json` (ordre des sources d'ASP.NET Core :
  `appsettings.json`, `appsettings.Development.json`, variables d'environnement, ligne de commande ; seul `appsettings.Https.json`,
  chargé après, l'emporte encore, et il ne contient que les adresses d'écoute). Pour revenir à `pharmacie_test` : `-Retirer`.
- Les tests ne lisent pas `ConnectionStrings__Pharmacie` : ils lisent `PHARMA_TEST_CNX` ou `appsettings.Development.json`.
- Premier chargement complet : durée non mesurée sur ce poste (6 à 10 minutes supposées pour les données de démonstration, au
  rythme de 60 lots par minute). **Maj+F5** coupe l'envoi en cours : il reprend au F5 suivant.
- L'outil d'essai [essai-cloud-officine.md](essai-cloud-officine.md) est indépendant : il ne touche ni `pharmacie_cloud_dev` ni ces variables.

### Comptes de démonstration

| Identifiant | Mot de passe | Rôle |
| --- | --- | --- |
| `titulaire` | `demo` | Pharmacien titulaire (tous les droits) |
| `assistant` | `demo` | Pharmacien assistant |
| `vendeur` | `demo` | Vendeur |

Sur un PC dont le poste n'est pas encore autorisé pour la base, seul `titulaire` se connecte, par le lien « Connexion du titulaire
(Administration seulement) », le temps de générer le code du poste (Administration › Postes autorisés).

### Compte super admin d'essai : connexion et récupération (2026-11-28)

Le super admin (restauration, reprise de données) n'est **pas** dans la base : son compte est un fichier chiffré, lu par l'API au
chemin réglé par `SuperAdmin:Fichier` (variable `SuperAdmin__Fichier`). Sans réglage, l'API cherche
`C:\ProgramData\PharmaOfficine\super-admin.dat`, absent sur ce PC : **toute connexion super admin est alors refusée**, même avec le bon
mot de passe.

- **Compte d'essai** : identifiant `super.essai`, fichier `C:\MesAtlassian\smptp\super-admin.dat` (hors dépôt, à ne jamais supprimer).
  Mot de passe : dans `C:\MesAtlassian\smptp\super-admin-essai.txt`, jamais recopié ailleurs. Mode d'emploi :
  `C:\MesAtlassian\smptp\LISEZMOI-super-admin.txt`.
- Le fichier est **chiffré pour ce PC et ce compte Windows** : il ne fonctionne ni copié sur un autre PC, ni avec une API lancée sous un
  autre compte (service Windows, autre session).

**Symptôme** : « Connexion super admin » refusée ; le journal de l'API (`C:\Logs\PharmaOfficine\Api\pharma-api-<date>.log`) montre
`Super admin : connexion refusée` à chaque essai.

**Récupération** (constatée le 2026-11-28) :

1. Vérifier que le fichier existe : `Test-Path C:\MesAtlassian\smptp\super-admin.dat`.
2. Poser la variable pour le compte Windows, une fois pour toutes :
   ```powershell
   [Environment]::SetEnvironmentVariable("SuperAdmin__Fichier", "C:\MesAtlassian\smptp\super-admin.dat", "User")
   ```
3. **Fermer toutes les fenêtres de Visual Studio** (vérifier qu'aucun `devenv.exe` ne reste dans le gestionnaire des tâches), le rouvrir,
   relancer l'API. Relancer seulement l'API ne suffit pas : elle hérite des variables de Visual Studio, qui ne lit les variables Windows
   qu'à son démarrage.
4. Se connecter par « Connexion super admin » avec `super.essai`.
5. Contrôle : le journal de l'API montre `Super admin super.essai connecté`.

Pour vérifier le compte sans toucher à son API : lancer une API d'essai sur un autre port avec la variable posée dans la fenêtre
PowerShell (`$env:SuperAdmin__Fichier = "C:\MesAtlassian\smptp\super-admin.dat"`, `$env:Urls = "http://localhost:5085"`), essayer la
connexion, puis arrêter cette API.

Ni le mot de passe du super admin ni ses codes ne se retrouvent par la base : un fichier perdu ou un mot de passe oublié se refait avec
`Pharma.Api.exe --super-admin` (mot de passe actuel et code envoyé au téléphone ou à l'e-mail vérifié du compte ; le compte d'essai a
un téléphone fictif, non vérifié).

### Réglages du poste (facultatifs)

| Variable d’environnement | Rôle | Par défaut |
| --- | --- | --- |
| `PHARMA_API` | Adresse de l’API | `http://localhost:5080/` |
| `PHARMA_PHARMACIE` | Nom de la pharmacie affiché dans l’en-tête | (aucun) |
| `PHARMA_POSTE` | Nom du poste, ex. « Poste caisse 1 » | `Poste <nom du PC>` |
| `PHARMA_IMPRIMANTE_ETIQUETTES` | Adresse IP de l'imprimante thermique d'étiquettes (ZPL, port 9100) ; sans elle, seule la planche PDF est produite | (aucune) |
| `PHARMA_POSTE_IDENTITE` | Essai seulement : dossier de l'identité du poste autorisé (`poste-identite.json`) ; plusieurs postes d'essai sur un même PC | `C:\ProgramData\PharmaOfficine\Poste` |
| `PHARMA_POSTE_REGLAGES` | Essai seulement : dossier du `poste.json` (adresse de secours, bascule) ; un poste d'essai sans toucher au `poste.json` du PC | `C:\ProgramData\PharmaOfficine` |
| `WEBVIEW2_USER_DATA_FOLDER` | Essai seulement (variable de WebView2) : dossier propre du moteur web ; sans elle, deux postes lancés sur le même PC partagent le même moteur (et le port de débogage du second est ignoré) | `%LOCALAPPDATA%\Pharma.Desktop.WebView2` |

Côté API, `Sauvegardes:PgDump` (dans `appsettings.json`) indique le chemin de `pg_dump.exe` si PostgreSQL n'est pas installé dans `C:\Program Files\PostgreSQL` ; les notifications et les sauvegardes se règlent ensuite dans l'Administration (voir [notifications.md](notifications.md)).

Côté API, le dossier des images d'ordonnances scannées se règle dans `appsettings.json` (`Ordonnances:Dossier`, par défaut `C:\ProgramData\PharmaOfficine\Ordonnances`) ; les PDF produits par le poste (bons de commande, bons de retour, étiquettes, rapports de clôture) sont enregistrés dans `Documents\Pharma Officine`.

Exemple (une fois, puis redémarrer Visual Studio) : `setx PHARMA_PHARMACIE "Pharmacie [Nom]"` et `setx PHARMA_POSTE "Poste caisse 1"`.

### Clé du serveur et offre liée au serveur (depuis le 2026-11-27)

Depuis le point 5 de la revue (« horloge + matériel »), chaque serveur a une clé de machine Windows (`PharmaOfficine-Serveur`, protégée
par le TPM) et les codes d'activation **version 2** ne valent que sur les serveurs dont ils listent l'empreinte
([offre-et-options.md](offre-et-options.md), « Liaison renforcée de l'offre »).

- **La clé n'est lisible que par SYSTEM et les administrateurs** (en production, l'API tourne en service LocalSystem).
- **API lancée depuis Visual Studio (sans droits d'administrateur)** : l'API démarre et tout fonctionne (connexion, caisse, écrans),
  **sauf la clé du serveur**, qu'elle ne peut pas lire. Conséquences : Mon offre affiche « Clé de ce serveur présente mais illisible par le compte de l'API » (et `--empreinte-serveur` le dit
  aussi ; message juste depuis le 2026-11-27, avant : « clé absente ») ; un code version 2 est refusé à la
  saisie, ou passe en délai de grâce s'il est déjà enregistré. Les codes **version 1** (sans serveur, ex. bases de l'éditeur) restent
  acceptés avec l'avertissement « Code non lié au serveur ». Constaté le 2026-11-27.
- **Pour essayer l'offre liée au serveur** (clé, empreinte, code version 2) : lancer **Visual Studio en administrateur**, ou l'API
  d'essai avec le script ci-dessous.
- **Clé du PC de développement** : créée le 2026-11-27 (TPM) ; empreinte `B704-7238-E4F5-D2F7-9207-987C-1E90-820B`. Un code version 2
  signé avec cette empreinte vaut pour les bases de ce PC qui ont le même code d'officine et le même NIF (ex. `pharmacie_test` et ses
  copies jetables).

#### Script d'essai réel `C:\MesAtlassian\essai-sync\lancer-essai-final.ps1` (hors dépôt)

À lancer dans un **PowerShell administrateur** :
`powershell -ExecutionPolicy Bypass -File C:\MesAtlassian\essai-sync\lancer-essai-final.ps1`. Il ne fait pas partie du logiciel
(en production, ces étapes sont celles du manuel de MEP) ; il sert seulement aux essais réels de l'offre liée au serveur :

1. **crée la clé de ce PC** (`Pharma.Api.exe --creer-cle-serveur`) ; une clé existante n'est jamais remplacée (relancer le script est sans
   risque) ;
2. **lit l'empreinte** (`--empreinte-serveur`) et l'écrit dans `C:\MesAtlassian\essai-sync\empreinte-serveur.txt` (à donner à
   l'outil de l'éditeur : `--serveur <empreinte>`) ;
3. **lance l'API d'essai** sur la base jetable (`base-renommage.txt`, refus si le nom ne commence pas par `pharmacie_jetable_`), port
   **5086**, avec les droits d'administrateur (comme le service LocalSystem) ; le mot de passe de la base est lu dans la variable
   utilisateur `ConnectionStrings__Pharmacie`, sans être affiché ; données dans `essai-sync\donnees-final`.

L'API utilisée est celle compilée dans `essai-sync\api-bin-final3` (le chemin est dans le script) : après une correction côté serveur,
recompiler dans un nouveau dossier, mettre à jour le chemin, puis **Ctrl+C** et relancer le script. Le poste d'essai se lance à part
(`PHARMA_POSTE_IDENTITE` vers un dossier neuf pour un PC « non autorisé »).

## 5. Ligne de commande

```powershell
dotnet build PharmaOfficine.slnx
dotnet test PharmaOfficine.slnx
dotnet run --project src\Pharma.Api
dotnet run --project src\Pharma.Desktop
```

Le dossier contient deux solutions (`.sln` et `.slnx`) : toujours préciser le fichier dans les commandes `dotnet build` et `dotnet test`.

### Tests d'intégration SQL

Le projet `tests/Pharma.Infrastructure.Sql.Tests` appelle les classes `*Sql` de `Pharma.Infrastructure` sur des **bases jetables** `pharmacie_jetable_sql_*`
(jamais `pharmacie_dev`, `pharmacie_test` ni `pharmacie_cloud_dev` : le code refuse tout autre nom). Ils tournent à chaque `dotnet test` ;
pour les lancer seuls : `dotnet test tests\Pharma.Infrastructure.Sql.Tests`, pour les exclure : `--filter "Categorie!=Sql"`.

- **Connexion** : variable `PHARMA_TEST_CNX` (base `postgres`, compte avec `CREATEDB`, par exemple `pharma_app`), sinon la chaîne de `appsettings.Development.json` de l'API (la base y est ignorée). Le mot de passe n'est jamais écrit dans un fichier.
- **Sans PostgreSQL** (psql introuvable, serveur coupé, compte sans `CREATEDB`) : les tests sont **ignorés avec la raison**. `PHARMA_EXIGER_BASE=1` les fait **échouer** à la place (à poser avant une livraison).
- **Fonctionnement** : une base modèle est construite à chaque exécution depuis `database/init` (`schema.sql`, script 083, `seed_referentiel.sql`), puis une copie par classe de test (`CREATE DATABASE ... TEMPLATE`). Les copies et le modèle sont supprimés à la fin.
- **Nettoyage** : au démarrage, les bases `pharmacie_jetable_sql_*` laissées par un arrêt brutal sont supprimées si leur horodatage a plus de 15 minutes de plus que la suite en cours et si personne n'y est connecté. Purge manuelle : `SELECT datname FROM pg_database WHERE datname LIKE 'pharmacie\_jetable\_sql\_%';` puis `DROP DATABASE "nom" WITH (FORCE);` sur ces seuls noms.

**Essayer l'officine et le serveur central ensemble** : `scripts\dev\essai_cloud_officine.ps1 -Demarrer` lance une officine d'essai et un serveur central d'essai reliés entre eux, sur des bases jetables (vos bases de démonstration ne sont pas modifiées). Guide pas à pas : [essai-cloud-officine.md](essai-cloud-officine.md).

**Vidéos : jamais dans git.** Les vidéos (`.mp4`, `.mov`, `.avi`, `.mkv`, `.webm`, `.3gp`…), y compris celles des exemples
(`docs/EXEMPLE/`), restent sur le disque ou se partagent à part : elles sont exclues par `.gitignore` et ne sont ni commitées
ni rattachées à la solution. Seules les photos utiles sont gardées.

## 6. Problèmes rencontrés et solutions

| Symptôme | Cause | Solution |
| --- | --- | --- |
| `l’exécution de scripts est désactivée sur ce système` | Stratégie d’exécution PowerShell par défaut | `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass` dans la même fenêtre |
| `PostgreSQL introuvable` ou `psql introuvable` | PostgreSQL installé ailleurs que dans `C:\Program Files\PostgreSQL` | Ajouter `-PgBin "<dossier>\bin"` (le script trouve seul la dernière version installée au chemin standard) |
| Accents illisibles ou `séquence d’octets invalide pour l’encodage « UTF8 »` | Encodage de la console Windows | Le script d’initialisation le règle seul ; pour psql lancé à la main : `$env:PGCLIENTENCODING = "UTF8"` ; pour une requête avec accents, la mettre dans un fichier UTF-8 et utiliser `psql -f` |
| `Could not load file or assembly 'Microsoft.Windows.SDK.NET'` au lancement du poste | WebView2 a besoin de la projection du SDK Windows | Corrigé : le poste cible `net10.0-windows10.0.19041.0`. Si l’erreur revient : *Générer > Régénérer la solution* |
| `MSB3027 … le fichier est verrouillé par Pharma.Api` | L’API tourne encore pendant un build | Arrêter le débogage (**Maj+F5**) avant de recompiler |
| `NU1301 … VCFNugets … 401 (Unauthorized)` en ligne de commande | Le feed NuGet VCF de la configuration NuGet globale exige une authentification | Compiler avec `--source https://api.nuget.org/v3/index.json`. Les avertissements NU1507 et NU1900 dans Visual Studio viennent de la même configuration et ne bloquent pas |
| « Serveur de la pharmacie injoignable » à la connexion | L’API n’est pas démarrée | Lancer le profil « API + Poste client » ou vérifier http://localhost:5080/api/healthcheck |
| `HttpRequestException … l’ordinateur cible l’a expressément refusée (localhost:5081)` dans Visual Studio (2026-11-28) | Le poste suit la variable utilisateur `PHARMA_API` (ici `http://localhost:5081/`) alors que l’API écoute sur un autre port (`Urls` d’`appsettings.json` l’emporte sur `launchSettings.json`) | Aligner les deux : `Urls` d’`appsettings.json` et `PHARMA_API` sur le même port, ou retirer `PHARMA_API` (le poste prend alors `http://localhost:5080/`) ; puis redémarrer Visual Studio |
| « Connexion super admin » refusée avec le bon mot de passe (2026-11-28) | L’API ne lit pas le fichier du compte : `SuperAdmin__Fichier` absente, ou posée après le démarrage de Visual Studio | Voir « Compte super admin d’essai : connexion et récupération » (§ 4) |
