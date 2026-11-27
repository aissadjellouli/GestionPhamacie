<#
.SYNOPSIS
    Fabrique le paquet d'essai de Tracodex Officine, à copier sur le PC d'un pharmacien qui veut l'essayer.

.DESCRIPTION
    Le paquet contient :
      api\          le serveur (Pharma.Api), publié avec .NET inclus : rien à installer à part PostgreSQL ;
      poste\        le poste de travail (Pharma.Desktop), publié de même ;
      database\     les scripts de la base et les données de démonstration (fictives) ;
      scripts\      les scripts d'administration du 2e serveur (second-serveur\) et de la bascule sur le PC de secours ;
      installer.ps1 crée la base de démonstration et la configuration du serveur, propre à ce PC ;
      demarrer.cmd  lance le serveur puis le poste ;
      LISEZMOI.txt  le mode d'emploi.
    Aucune clé ni aucun mot de passe n'est mis dans le paquet : installer.ps1 les tire au sort sur le PC du pharmacien.
    Nos assemblys y sont obfusqués (scripts\mep\obfusquer_livraison.ps1, .NET Reactor en version complète) ; les fichiers de correspondance
    restent hors du paquet (« <Sortie>-correspondances »).
    Le paquet est fabriqué en local ; il n'est publié nulle part.

.EXAMPLE
    .\scripts\creer_paquet_essai.ps1
    Paquet dans ..\PharmaOfficine-essai (à côté du dépôt).

.EXAMPLE
    .\scripts\creer_paquet_essai.ps1 -Sortie D:\Paquets\PharmaOfficine-essai -Zip
    Paquet dans le dossier indiqué, plus une archive .zip à envoyer.
#>
param(
    [string]$Sortie = "",
    [switch]$Zip,
    [string]$Obfuscateur = "",          # dotNET_Reactor.Console.exe ; vide : cherché par scripts\mep\obfusquer_livraison.ps1
    [string]$Correspondances = "",      # archive des fichiers de correspondance ; vide : « <Sortie>-correspondances », hors du paquet
    [switch]$SansObfuscation            # paquet en clair : essai interne seulement, jamais remis à un pharmacien
)

$ErrorActionPreference = "Stop"
$racine = Split-Path $PSScriptRoot -Parent
if (-not $Sortie) { $Sortie = Join-Path (Split-Path $racine -Parent) "PharmaOfficine-essai" }

# Lot P2 de la protection du code (2026-11-28) : le paquet remis à un pharmacien est obfusqué comme une livraison. L'outil est cherché avant
# toute publication ; sans lui (ou s'il n'est pas activé), le paquet est refusé.
$obfusquer = Join-Path $PSScriptRoot "mep\obfusquer_livraison.ps1"
if (-not $SansObfuscation) { $Obfuscateur = & $obfusquer -ChercherOutil -Obfuscateur $Obfuscateur }

if (Test-Path $Sortie) {
    Write-Host "Le dossier $Sortie existe : il est vidé."
    Remove-Item $Sortie -Recurse -Force
}
New-Item -ItemType Directory -Force $Sortie | Out-Null

function Publier([string]$projet, [string]$dossier) {
    Write-Host "Publication de $projet..."
    & dotnet publish (Join-Path $racine "src\$projet\$projet.csproj") -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false -o (Join-Path $Sortie $dossier) -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Échec de la publication de $projet" }
}
Publier "Pharma.Api" "api"
Publier "Pharma.Desktop" "poste"

# La configuration de développement (base pharmacie_dev, clé d'exemple) ne part pas dans le paquet :
# installer.ps1 écrit appsettings.Production.json sur le PC du pharmacien
Remove-Item (Join-Path $Sortie "api\appsettings.Development.json") -ErrorAction SilentlyContinue
# Contrôle : aucun mot de passe dans la configuration livrée (il est tiré au sort par installer.ps1)
$livree = Get-Content (Join-Path $Sortie "api\appsettings.json") -Raw
if ($livree -match "Password=[^;`"]+") { throw "Mot de passe présent dans api\appsettings.json : paquet refusé" }

if ($SansObfuscation) { Write-Warning "Paquet NON obfusqué (-SansObfuscation) : essai interne seulement, ne pas le remettre à un pharmacien." }
else { & $obfusquer -Dossier $Sortie -Obfuscateur $Obfuscateur -Correspondances $Correspondances }

# Base : scripts numérotés (structure et référentiel), puis données fictives de démonstration
$db = New-Item -ItemType Directory -Force (Join-Path $Sortie "database\data")
# Tous les scripts numérotés (001 à 1xx) ; les scripts d'administrateur (*b_*.sql, superutilisateur) restent hors paquet
Copy-Item (Join-Path $racine "database\migration\*.sql") (Join-Path $Sortie "database") -Exclude "*b_*.sql"
Copy-Item (Join-Path $racine "database\data\9*.sql") $db

Copy-Item (Join-Path $PSScriptRoot "paquet\*") $Sortie

# Scripts d'administration du 2e serveur et de la bascule (lancés par l'administrateur, voir docs/mise-en-production.md)
$adm = New-Item -ItemType Directory -Force (Join-Path $Sortie "scripts\second-serveur")
Copy-Item (Join-Path $PSScriptRoot "second-serveur\*.ps1") $adm
Copy-Item (Join-Path $PSScriptRoot "basculer-pc-secours.ps1") (Join-Path $Sortie "scripts")

# Lot P1 de la protection du code (décision du titulaire du 2026-11-27) : la copie livrée est nettoyée (plus de .pdb ni de configuration
# de développement, scripts SQL sans commentaires) ; le dépôt garde tout
& (Join-Path $PSScriptRoot "mep\nettoyer_livraison.ps1") -Dossier $Sortie
Write-Host "Paquet prêt : $Sortie"

if ($Zip) {
    $archive = "$Sortie.zip"
    if (Test-Path $archive) { Remove-Item $archive -Force }
    Compress-Archive -Path (Join-Path $Sortie "*") -DestinationPath $archive
    Write-Host "Archive : $archive"
}
