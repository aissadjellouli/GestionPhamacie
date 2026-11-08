<#
.SYNOPSIS
    Crée les bases PostgreSQL locales de développement et de test, applique tous les
    scripts du dossier database/ dans l'ordre, puis charge les données de démonstration.

.EXAMPLE
    .\scripts\initialiser_base_locale.ps1 -Recreer
    (demande le mot de passe du superutilisateur postgres)
#>
param(
    [string]$PgBin       = "C:\Program Files\PostgreSQL\17\bin",
    [string]$HostPg      = "localhost",
    [int]   $Port        = 5432,
    [string]$SuperUser   = "postgres",
    [string]$AppUser     = "pharma_app",
    [string]$AppPassword = "pharma_dev",
    [string[]]$Bases     = @("pharmacie_dev", "pharmacie_test"),
    [switch]$Recreer,
    [switch]$SansDemo
)

$ErrorActionPreference = "Stop"
$racine = Split-Path $PSScriptRoot -Parent
$psql   = Join-Path $PgBin "psql.exe"
if (-not (Test-Path $psql)) { throw "psql introuvable : $psql (adapter -PgBin)" }

$motDePasseSuper = Read-Host "Mot de passe de $SuperUser" -AsSecureString
$superPwd = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($motDePasseSuper))

function Invoke-Psql([string]$utilisateur, [string]$motDePasse, [string]$base, [string[]]$arguments) {
    $env:PGPASSWORD = $motDePasse
    & $psql -h $HostPg -p $Port -U $utilisateur -d $base -v ON_ERROR_STOP=1 -q @arguments
    if ($LASTEXITCODE -ne 0) { throw "Échec psql ($base) : $($arguments -join ' ')" }
}

# 1. Rôle applicatif
$existe = Invoke-Psql $SuperUser $superPwd "postgres" @("-tAc", "SELECT 1 FROM pg_roles WHERE rolname = '$AppUser'")
if (-not $existe) {
    Invoke-Psql $SuperUser $superPwd "postgres" @("-c", "CREATE ROLE $AppUser LOGIN PASSWORD '$AppPassword'")
    Write-Host "Rôle $AppUser créé"
}

$scripts = Get-ChildItem (Join-Path $racine "database") -Filter "*.sql" | Sort-Object Name
$demo    = Get-ChildItem (Join-Path $racine "database\demo") -Filter "*.sql" | Sort-Object Name

foreach ($base in $Bases) {
    # 2. Base (propriétaire = rôle applicatif, pour créer pg_trgm et pgcrypto)
    if ($Recreer) {
        Invoke-Psql $SuperUser $superPwd "postgres" @("-c", "DROP DATABASE IF EXISTS $base WITH (FORCE)")
    }
    $baseExiste = Invoke-Psql $SuperUser $superPwd "postgres" @("-tAc", "SELECT 1 FROM pg_database WHERE datname = '$base'")
    if ($baseExiste) { Write-Host "$base existe déjà (utiliser -Recreer pour repartir de zéro)"; continue }

    Invoke-Psql $SuperUser $superPwd "postgres" @("-c", "CREATE DATABASE $base OWNER $AppUser ENCODING 'UTF8' TEMPLATE template0")
    Write-Host "Base $base créée"

    # 3. Schéma, dans l'ordre des numéros
    foreach ($s in $scripts) {
        Write-Host "  $($s.Name)"
        Invoke-Psql $AppUser $AppPassword $base @("-f", $s.FullName)
    }

    # 4. Données fictives
    if (-not $SansDemo) {
        foreach ($s in $demo) {
            Write-Host "  demo\$($s.Name)"
            Invoke-Psql $AppUser $AppPassword $base @("-f", $s.FullName)
        }
    }
}

Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
Write-Host "Terminé. Connexion de l'API : Host=$HostPg;Port=$Port;Database=pharmacie_dev;Username=$AppUser;Password=$AppPassword"
