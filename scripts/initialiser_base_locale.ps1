<#
.SYNOPSIS
    Crée ou met à jour les bases PostgreSQL locales de développement et de test : applique tous les
    scripts numérotés du dossier database/migration dans l’ordre, puis les données de démonstration.

.DESCRIPTION
    Les scripts SQL sont idempotents : les rejouer sur une base à jour ne change rien.
    Une base existante est donc simplement mise à jour (nouveaux scripts, nouvelles colonnes...),
    sans perte de données. -Recreer repart d'une base vide.

.EXAMPLE
    .\scripts\initialiser_base_locale.ps1
    Crée les bases manquantes et met à jour les autres (demande le mot de passe de postgres).

.EXAMPLE
    .\scripts\initialiser_base_locale.ps1 -MiseAJour
    Met à jour les bases existantes avec le compte applicatif, sans mot de passe postgres.

.EXAMPLE
    .\scripts\initialiser_base_locale.ps1 -Bases pharmacie_test -Recreer
    Supprime puis recrée la base de test, après confirmation : le nom de chaque base à supprimer est à retaper.

.EXAMPLE
    .\scripts\initialiser_base_locale.ps1 -Bases pharmacie_test -Recreer -Force
    Même chose sans confirmation (automatisation).
#>
param(
    [string]$PgBin       = "",          # vide : dernière version de PostgreSQL installée
    [string]$HostPg      = "localhost",
    [int]   $Port        = 5432,
    [string]$SuperUser   = "postgres",
    [string]$AppUser     = "pharma_app",
    [string]$AppPassword = "pharma_dev",
    [string[]]$Bases     = @("pharmacie_dev", "pharmacie_test"),
    [switch]$Recreer,
    [switch]$Force,                     # avec -Recreer : pas de confirmation (automatisation)
    [switch]$MiseAJour,
    [switch]$SansDemo
)

$ErrorActionPreference = "Stop"
$racine = Split-Path $PSScriptRoot -Parent

if (-not $PgBin) {
    $installation = Get-ChildItem "C:\Program Files\PostgreSQL" -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName "bin\psql.exe") } |
        Sort-Object { [int]($_.Name -replace '\D', '') } -Descending | Select-Object -First 1
    if (-not $installation) { throw "PostgreSQL introuvable dans C:\Program Files\PostgreSQL (préciser -PgBin)" }
    $PgBin = Join-Path $installation.FullName "bin"
}
$psql = Join-Path $PgBin "psql.exe"
if (-not (Test-Path $psql)) { throw "psql introuvable : $psql (adapter -PgBin)" }
Write-Host "PostgreSQL : $PgBin"

if ($MiseAJour -and $Recreer) { throw "-MiseAJour et -Recreer ne vont pas ensemble." }
if ($Force -and -not $Recreer) { throw "-Force ne sert qu'avec -Recreer." }

# -Recreer supprime les bases (données comprises) : chaque nom est retapé avant que rien ne soit fait, sauf avec -Force
if ($Recreer -and -not $Force) {
    Write-Warning "-Recreer SUPPRIME les bases suivantes et toutes leurs données : $($Bases -join ', ')"
    foreach ($base in $Bases) {
        $saisie = Read-Host "Pour confirmer la suppression, retapez le nom de la base « $base »"
        if ($saisie -cne $base) { throw "Confirmation incorrecte pour $base : rien n'a été fait." }
    }
}

# Les scripts contiennent des accents
$env:PGCLIENTENCODING = "UTF8"
# Rejouer un script idempotent produit un « existe déjà, poursuite du traitement » par objet : on ne garde que les avertissements
# Marqueur de la mutualisation des réglages (script 141) : les scripts rejoués ne sont jamais refusés par la garde d'une table imposée
$env:PGOPTIONS = "-c client_min_messages=warning -c pharma.reglage_central=on"

function Invoke-Psql([string]$utilisateur, [string]$motDePasse, [string]$base, [string[]]$arguments) {
    $env:PGPASSWORD = $motDePasse
    & $psql -h $HostPg -p $Port -U $utilisateur -d $base -v ON_ERROR_STOP=1 -q @arguments
    if ($LASTEXITCODE -ne 0) { throw "Échec psql ($base) : $($arguments -join ' ')" }
}

# Migrations seulement (NNN_*.sql de database\migration, 001 à 999) : la création à la MEP (database\init) ne passe pas par ce script.
# Les scripts « b » (101b, 111b) sont exclus : ils se lancent à part, sur décision (docs\scripts-base-de-donnees.md).
$scripts = Get-ChildItem (Join-Path $racine "database\migration") -Filter "*.sql" | Where-Object { $_.Name -match "^\d{3}_" } | Sort-Object { [int]$_.Name.Substring(0, 3) }
# Fichiers qui remplissent la base (données fictives) : database\data
$demo    = Get-ChildItem (Join-Path $racine "database\data") -Filter "*.sql" | Sort-Object Name

try {
    if (-not $MiseAJour) {
        $motDePasseSuper = Read-Host "Mot de passe de $SuperUser" -AsSecureString
        $superPwd = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR($motDePasseSuper))

        # 1. Rôle applicatif
        $existe = Invoke-Psql $SuperUser $superPwd "postgres" @("-tAc", "SELECT 1 FROM pg_roles WHERE rolname = '$AppUser'")
        if (-not $existe) {
            Invoke-Psql $SuperUser $superPwd "postgres" @("-c", "CREATE ROLE $AppUser LOGIN PASSWORD '$AppPassword'")
            Write-Host "Rôle $AppUser créé"
        }
        # Restauration par le super admin depuis l'application : base neuve créée puis renommée (droit CREATEDB, sans être super-utilisateur)
        Invoke-Psql $SuperUser $superPwd "postgres" @("-c", "ALTER ROLE $AppUser CREATEDB")
    }

    foreach ($base in $Bases) {
        # 2. Base (propriétaire = rôle applicatif, pour créer pg_trgm et pgcrypto)
        if (-not $MiseAJour) {
            if ($Recreer) {
                Invoke-Psql $SuperUser $superPwd "postgres" @("-c", "DROP DATABASE IF EXISTS $base WITH (FORCE)")
            }
            $baseExiste = Invoke-Psql $SuperUser $superPwd "postgres" @("-tAc", "SELECT 1 FROM pg_database WHERE datname = '$base'")
            if ($baseExiste) {
                Write-Host "Base $base : mise à jour"
            } else {
                Invoke-Psql $SuperUser $superPwd "postgres" @("-c", "CREATE DATABASE $base OWNER $AppUser ENCODING 'UTF8' TEMPLATE template0")
                Write-Host "Base $base créée"
            }
        } else {
            Write-Host "Base $base : mise à jour"
        }

        # 3. Schéma, dans l'ordre des numéros (scripts idempotents)
        foreach ($s in $scripts) {
            Write-Host "  $($s.Name)"
            Invoke-Psql $AppUser $AppPassword $base @("-f", $s.FullName)
        }

        # 4. Données fictives (le script ne fait rien si elles sont déjà présentes)
        if (-not $SansDemo) {
            foreach ($s in $demo) {
                Write-Host "  data\$($s.Name)"
                Invoke-Psql $AppUser $AppPassword $base @("-f", $s.FullName)
            }
        }
    }
}
finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}

Write-Host "Terminé. Connexion de l'API : Host=$HostPg;Port=$Port;Database=pharmacie_dev;Username=$AppUser;Password=$AppPassword"
