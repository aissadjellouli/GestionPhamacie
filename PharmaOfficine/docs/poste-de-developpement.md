# Installer un poste de développement

Procédure suivie pour installer PostgreSQL, créer les bases locales et lancer l’API et le poste client depuis Visual Studio. Temps total : environ 20 minutes.

## 1. Prérequis

| Outil | Version utilisée | Remarque |
| --- | --- | --- |
| Windows | 10 version 2004 ou plus récent, ou 11 | Exigé par le poste client (WebView2) |
| SDK .NET | 10.0.100 ou plus récent | Voir `global.json` |
| Visual Studio | Community 2026 (18.x) | Ouvre `PharmaOfficine.slnx` ou `PharmaOfficine.sln` |
| PostgreSQL | 18.6 | 17 convient aussi |
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

# Les scripts SQL contiennent des accents : psql doit les lire en UTF-8
$env:PGCLIENTENCODING = "UTF8"

# Le script vise PostgreSQL 17 par défaut : indiquer le dossier de la version 18
.\scripts\initialiser_base_locale.ps1 -PgBin "C:\Program Files\PostgreSQL\18\bin" -Recreer
```

Le script :
- demande le mot de passe de `postgres` ;
- crée le compte applicatif `pharma_app` (mot de passe de développement `pharma_dev`, celui de `src/Pharma.Api/appsettings.json`) ;
- crée les deux bases, applique `database/001` à `008` dans l’ordre, puis les données fictives `database/demo/900_donnees_demo.sql`.

Options : `-SansDemo` pour une base vide, sans `-Recreer` pour ne pas toucher aux bases existantes.

Pour remettre à zéro une seule base (par exemple `pharmacie_test` après des essais de rappel de lot, qui bloquent un lot définitivement) :
```powershell
.\scripts\initialiser_base_locale.ps1 -PgBin "C:\Program Files\PostgreSQL\18\bin" -Bases pharmacie_test -Recreer
```

Pour lancer l’API sur `pharmacie_test` au lieu de `pharmacie_dev` (essais d’écriture) :
```powershell
$env:ConnectionStrings__Pharmacie = "Host=localhost;Port=5432;Database=pharmacie_test;Username=pharma_app;Password=pharma_dev"
dotnet run --project src\Pharma.Api
```

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
3. **F5**. L’API écoute sur http://localhost:5080 (contrôle : http://localhost:5080/api/sante), puis la fenêtre du poste s’ouvre.

Sans ce profil : clic droit sur la solution > **Configurer les projets de démarrage** > **Plusieurs projets de démarrage** > `Pharma.Api` puis `Pharma.Desktop` sur **Démarrer**.

### Comptes de démonstration

| Identifiant | Mot de passe | Rôle |
| --- | --- | --- |
| `titulaire` | `demo` | Pharmacien titulaire (tous les droits) |
| `assistant` | `demo` | Pharmacien assistant |
| `vendeur` | `demo` | Vendeur |

### Réglages du poste (facultatifs)

| Variable d’environnement | Rôle | Par défaut |
| --- | --- | --- |
| `PHARMA_API` | Adresse de l’API | `http://localhost:5080/` |
| `PHARMA_PHARMACIE` | Nom de la pharmacie affiché dans l’en-tête | (aucun) |
| `PHARMA_POSTE` | Nom du poste, ex. « Poste caisse 1 » | `Poste <nom du PC>` |

Exemple (une fois, puis redémarrer Visual Studio) : `setx PHARMA_PHARMACIE "Pharmacie [Nom]"` et `setx PHARMA_POSTE "Poste caisse 1"`.

## 5. Ligne de commande

```powershell
dotnet build PharmaOfficine.slnx
dotnet test PharmaOfficine.slnx
dotnet run --project src\Pharma.Api
dotnet run --project src\Pharma.Desktop
```

Le dossier contient deux solutions (`.sln` et `.slnx`) : toujours préciser le fichier dans les commandes `dotnet build` et `dotnet test`.

## 6. Problèmes rencontrés et solutions

| Symptôme | Cause | Solution |
| --- | --- | --- |
| `l’exécution de scripts est désactivée sur ce système` | Stratégie d’exécution PowerShell par défaut | `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass` dans la même fenêtre |
| `psql introuvable : C:\Program Files\PostgreSQL\17\bin\psql.exe` | Le script vise la version 17 par défaut | Ajouter `-PgBin "C:\Program Files\PostgreSQL\18\bin"` |
| Accents illisibles ou `séquence d’octets invalide pour l’encodage « UTF8 »` | Encodage de la console Windows | `$env:PGCLIENTENCODING = "UTF8"` avant psql ; pour une requête avec accents, la mettre dans un fichier UTF-8 et utiliser `psql -f` |
| `Could not load file or assembly 'Microsoft.Windows.SDK.NET'` au lancement du poste | WebView2 a besoin de la projection du SDK Windows | Corrigé : le poste cible `net10.0-windows10.0.19041.0`. Si l’erreur revient : *Générer > Régénérer la solution* |
| `MSB3027 … le fichier est verrouillé par Pharma.Api` | L’API tourne encore pendant un build | Arrêter le débogage (**Maj+F5**) avant de recompiler |
| `NU1301 … VCFNugets … 401 (Unauthorized)` en ligne de commande | Le feed NuGet VCF de la configuration NuGet globale exige une authentification | Compiler avec `--source https://api.nuget.org/v3/index.json`. Les avertissements NU1507 et NU1900 dans Visual Studio viennent de la même configuration et ne bloquent pas |
| « Serveur de la pharmacie injoignable » à la connexion | L’API n’est pas démarrée | Lancer le profil « API + Poste client » ou vérifier http://localhost:5080/api/sante |
