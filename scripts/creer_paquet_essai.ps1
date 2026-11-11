<#
.SYNOPSIS
    Fabrique le paquet d'essai de Pharma Officine, à copier sur le PC d'un pharmacien qui veut l'essayer.

.DESCRIPTION
    Le paquet contient :
      api\          le serveur (Pharma.Api), publié avec .NET inclus : rien à installer à part PostgreSQL ;
      poste\        le poste de travail (Pharma.Desktop), publié de même ;
      database\     les scripts de la base et les données de démonstration (fictives) ;
      installer.ps1 crée la base de démonstration et la configuration du serveur, propre à ce PC ;
      demarrer.cmd  lance le serveur puis le poste ;
      LISEZMOI.txt  le mode d'emploi.
    Aucune clé ni aucun mot de passe n'est mis dans le paquet : installer.ps1 les tire au sort sur le PC du pharmacien.
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
    [switch]$Zip
)

$ErrorActionPreference = "Stop"
$racine = Split-Path $PSScriptRoot -Parent
if (-not $Sortie) { $Sortie = Join-Path (Split-Path $racine -Parent) "PharmaOfficine-essai" }

if (Test-Path $Sortie) {
    Write-Host "Le dossier $Sortie existe : il est vidé."
    Remove-Item $Sortie -Recurse -Force
}
New-Item -ItemType Directory -Force $Sortie | Out-Null

function Publier([string]$projet, [string]$dossier) {
    Write-Host "Publication de $projet..."
    & dotnet publish (Join-Path $racine "src\$projet\$projet.csproj") -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -o (Join-Path $Sortie $dossier) -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Échec de la publication de $projet" }
}
Publier "Pharma.Api" "api"
Publier "Pharma.Desktop" "poste"

# La configuration de développement (base pharmacie_dev, clé d'exemple) ne part pas dans le paquet :
# installer.ps1 écrit appsettings.Production.json sur le PC du pharmacien
Remove-Item (Join-Path $Sortie "api\appsettings.Development.json") -ErrorAction SilentlyContinue

# Base : scripts numérotés (structure et référentiel), puis données fictives de démonstration
$db = New-Item -ItemType Directory -Force (Join-Path $Sortie "database\data")
Copy-Item (Join-Path $racine "database\0*.sql") (Join-Path $Sortie "database")
Copy-Item (Join-Path $racine "database\data\9*.sql") $db

Copy-Item (Join-Path $PSScriptRoot "paquet\*") $Sortie
Write-Host "Paquet prêt : $Sortie"

if ($Zip) {
    $archive = "$Sortie.zip"
    if (Test-Path $archive) { Remove-Item $archive -Force }
    Compress-Archive -Path (Join-Path $Sortie "*") -DestinationPath $archive
    Write-Host "Archive : $archive"
}
