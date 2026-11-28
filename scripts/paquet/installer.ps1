<#
.SYNOPSIS
    Installe Tracodex Officine pour un essai, sur ce PC : base de démonstration et configuration du serveur.

.DESCRIPTION
    Prérequis : PostgreSQL 16 ou plus récent installé sur ce PC, et le mot de passe de son compte « postgres ».
    Le script :
      1. crée le compte « pharma_essai » et la base « pharmacie_essai » (un mot de passe est tiré au sort) ;
      2. y applique les scripts de la base, puis les données de démonstration (fictives) ;
      3. écrit api\appsettings.Production.json : connexion à la base, clé de signature tirée au sort,
         serveur joignable seulement depuis ce PC (http://localhost:5080).
    Il ne touche à aucune autre base. Relancé, il met la base à jour sans rien effacer ; -Recreer repart de zéro
    (la base d'essai est supprimée après confirmation : son nom est à retaper ; -Force supprime sans confirmation).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\installer.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\installer.ps1 -Recreer
    Repart de zéro : efface la base d'essai, après confirmation.
#>
param(
    [string]$PgBin = "",
    [int]$Port = 5432,
    [switch]$Recreer,
    [switch]$Force      # avec -Recreer : pas de confirmation (automatisation)
)

$ErrorActionPreference = "Stop"
$ici = $PSScriptRoot
$base = "pharmacie_essai"
$compte = "pharma_essai"

if ($Force -and -not $Recreer) { throw "-Force ne sert qu'avec -Recreer." }
# -Recreer efface la base d'essai et toutes ses données : le nom de la base est retapé avant que rien ne soit fait, sauf avec -Force
if ($Recreer -and -not $Force) {
    Write-Warning "-Recreer EFFACE la base $base et toutes ses données (ventes, saisies de l'essai)."
    $saisie = Read-Host "Pour confirmer, retapez le nom de la base ($base)"
    if ($saisie -cne $base) { throw "Confirmation incorrecte : rien n'a été fait." }
}

if (-not $PgBin) {
    $installation = Get-ChildItem "C:\Program Files\PostgreSQL" -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName "bin\psql.exe") } |
        Sort-Object { [int]($_.Name -replace '\D', '') } -Descending | Select-Object -First 1
    if (-not $installation) { throw "PostgreSQL est introuvable dans C:\Program Files\PostgreSQL. Installez-le (version 16 ou plus), puis relancez." }
    $PgBin = Join-Path $installation.FullName "bin"
}
$psql = Join-Path $PgBin "psql.exe"
Write-Host "PostgreSQL : $PgBin"

$env:PGCLIENTENCODING = "UTF8"
# Marqueur de la mutualisation des réglages (script 141) : les scripts rejoués ne sont jamais refusés par la garde d'une table imposée
$env:PGOPTIONS = "-c client_min_messages=warning -c pharma.reglage_central=on"

function Aleatoire([int]$longueur) {
    $octets = New-Object byte[] $longueur
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($octets)
    -join ($octets | ForEach-Object { "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789"[$_ % 56] })
}

function Psql([string]$utilisateur, [string]$motDePasse, [string]$cible, [string[]]$arguments) {
    $env:PGPASSWORD = $motDePasse
    & $psql -h localhost -p $Port -U $utilisateur -d $cible -v ON_ERROR_STOP=1 -q @arguments
    if ($LASTEXITCODE -ne 0) { throw "Échec de psql sur $cible : $($arguments -join ' ')" }
}

$config = Join-Path $ici "api\appsettings.Production.json"
try {
    $saisie = Read-Host "Mot de passe du compte postgres de PostgreSQL" -AsSecureString
    $superPwd = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($saisie))

    # Mot de passe du compte de l'essai : repris de la configuration déjà écrite, sinon tiré au sort
    $motDePasse = $null
    if ((Test-Path $config) -and -not $Recreer) {
        $existant = Get-Content $config -Raw | ConvertFrom-Json
        if ($existant.ConnectionStrings.Pharmacie -match "Password=([^;]+)") { $motDePasse = $Matches[1] }
    }
    if (-not $motDePasse) { $motDePasse = Aleatoire 24 }

    # 1. Compte et base de l'essai
    if (Psql "postgres" $superPwd "postgres" @("-tAc", "SELECT 1 FROM pg_roles WHERE rolname = '$compte'")) {
        Psql "postgres" $superPwd "postgres" @("-c", "ALTER ROLE $compte LOGIN PASSWORD '$motDePasse'")
    } else {
        Psql "postgres" $superPwd "postgres" @("-c", "CREATE ROLE $compte LOGIN PASSWORD '$motDePasse'")
    }
    if ($Recreer) { Psql "postgres" $superPwd "postgres" @("-c", "DROP DATABASE IF EXISTS $base WITH (FORCE)") }
    if (-not (Psql "postgres" $superPwd "postgres" @("-tAc", "SELECT 1 FROM pg_database WHERE datname = '$base'"))) {
        Psql "postgres" $superPwd "postgres" @("-c", "CREATE DATABASE $base OWNER $compte ENCODING 'UTF8' TEMPLATE template0")
        Write-Host "Base $base créée"
    } else {
        Write-Host "Base $base : mise à jour"
    }

    # 2. Structure et référentiel (scripts idempotents, dans l'ordre), puis données de démonstration
    foreach ($s in Get-ChildItem (Join-Path $ici "database") -Filter "*.sql" | Where-Object { $_.Name -match "^\d{3}_" } | Sort-Object Name) {
        Write-Host "  $($s.Name)"
        Psql $compte $motDePasse $base @("-f", $s.FullName)
    }
    foreach ($s in Get-ChildItem (Join-Path $ici "database\data") -Filter "*.sql" | Sort-Object Name) {
        Write-Host "  données : $($s.Name)"
        Psql $compte $motDePasse $base @("-f", $s.FullName)
    }

    # 3. Configuration du serveur, propre à ce PC
    $donnees = Join-Path $ici "donnees"
    New-Item -ItemType Directory -Force (Join-Path $donnees "Ordonnances"), (Join-Path $donnees "Chifa") | Out-Null
    $cle = if ((Test-Path $config) -and -not $Recreer -and ($existant.Jwt.Cle)) { $existant.Jwt.Cle } else { Aleatoire 72 }
    [ordered]@{
        ConnectionStrings = @{ Pharmacie = "Host=localhost;Port=$Port;Database=$base;Username=$compte;Password=$motDePasse" }
        Jwt = @{ Cle = $cle }
        Urls = "http://localhost:5080"
        Ordonnances = @{ Dossier = (Join-Path $donnees "Ordonnances") }
        Chifa = @{ DossierExport = (Join-Path $donnees "Chifa") }
        Sauvegardes = @{ PgDump = (Join-Path $PgBin "pg_dump.exe") }
    } | ConvertTo-Json -Depth 4 | Set-Content $config -Encoding UTF8
    Write-Host "Configuration écrite : $config"
}
finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Installation terminée. Lancez autoriser_poste.cmd (une seule fois : il affiche le code de ce PC), puis demarrer.cmd :"
Write-Host "tapez le code dans « Autoriser ce poste », puis connectez-vous avec titulaire / demo."
