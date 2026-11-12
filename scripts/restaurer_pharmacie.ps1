<#
.SYNOPSIS
    Restaure la base de la pharmacie et/ou les documents de la GED à partir des sauvegardes du disque externe.

.DESCRIPTION
    Base : le fichier .dump choisi (par défaut la sauvegarde pharmacie_*.dump la plus récente du dossier -Sauvegardes)
    est vérifié, puis restauré dans une base neuve créée par le compte postgres (propriétaire pharma_app).
    Une base existante du même nom n'est jamais supprimée : sans -Remplacer, rien n'est fait ; avec -Remplacer, elle est
    renommée « <base>_avant_restauration_AAAAMMJJ_HHMM » et reste disponible.
    GED : le dossier GED du disque externe est recopié dans la GED du serveur. Rien n'est supprimé, et un document déjà
    présent et plus récent sur le serveur est gardé.

    Avant : arrêter le service de l'API (sc.exe stop "PharmaOfficine API"). Après : appliquer les scripts database/migration/0xx
    plus récents que la sauvegarde, s'il y en a (voir docs/mise-en-production.md, « Restauration »), puis redémarrer le service.

.EXAMPLE
    .\scripts\restaurer_pharmacie.ps1 -Sauvegardes "E:\Sauvegardes\Pharmacie"
    Restaure la base « pharmacie » depuis la dernière sauvegarde, puis la GED depuis E:\Sauvegardes\Pharmacie\GED.

.EXAMPLE
    .\scripts\restaurer_pharmacie.ps1 -Fichier "E:\Sauvegardes\Pharmacie\pharmacie_20261110_220000.dump" -Remplacer -SansGed
    Restaure une sauvegarde précise ; la base actuelle est gardée sous un autre nom.

.EXAMPLE
    .\scripts\restaurer_pharmacie.ps1 -Sauvegardes "E:\Sauvegardes\Pharmacie" -SansBase
    Recopie seulement les documents de la GED.

.EXAMPLE
    .\scripts\restaurer_pharmacie.ps1 -Sauvegardes "E:\Sauvegardes\Pharmacie" -Base pharmacie_essai -SansGed
    Test mensuel de restauration (étape 8.3) dans une base d'essai, sans toucher à la base en service.
#>
param(
    [string]$Sauvegardes = "",           # dossier des sauvegardes sur le disque externe (réglé dans Administration › Sauvegardes)
    [string]$Fichier     = "",           # sauvegarde précise (.dump) ; vide : la plus récente de -Sauvegardes
    [string]$Base        = "pharmacie",
    [string]$SourceGed   = "",           # vide : <Sauvegardes>\GED
    [string]$DossierGed  = "C:\ProgramData\PharmaOfficine\GED",
    [string]$PgBin       = "",           # vide : dernière version de PostgreSQL installée
    [string]$HostPg      = "localhost",
    [int]   $Port        = 5432,
    [string]$SuperUser   = "postgres",
    [string]$AppUser     = "pharma_app",
    [switch]$Remplacer,
    [switch]$SansBase,
    [switch]$SansGed
)

$ErrorActionPreference = "Stop"
if ($SansBase -and $SansGed) { throw "-SansBase et -SansGed ensemble : rien à restaurer." }
if ($Base -notmatch '^[a-z_][a-z0-9_]*$') { throw "Nom de base invalide : $Base (lettres minuscules, chiffres et _)." }

# ---------------------------------------------------------------------
# Base de données
# ---------------------------------------------------------------------
if (-not $SansBase) {
    if (-not $PgBin) {
        $installation = Get-ChildItem "C:\Program Files\PostgreSQL" -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName "bin\pg_restore.exe") } |
            Sort-Object { [int]($_.Name -replace '\D', '') } -Descending | Select-Object -First 1
        if (-not $installation) { throw "PostgreSQL introuvable dans C:\Program Files\PostgreSQL (préciser -PgBin)" }
        $PgBin = Join-Path $installation.FullName "bin"
    }
    $psql = Join-Path $PgBin "psql.exe"
    $pgRestore = Join-Path $PgBin "pg_restore.exe"

    # 1. Sauvegarde à restaurer
    if (-not $Fichier) {
        if (-not $Sauvegardes) { throw "Préciser -Sauvegardes (dossier du disque externe) ou -Fichier." }
        # Nom pharmacie_AAAAMMJJ_HHMMSS.dump (module de l'API) ou pharmacie_AAAA-MM-JJ_HHMM.dump (script planifié) :
        # la date de modification départage les deux formats
        $dernier = Get-ChildItem $Sauvegardes -Filter "pharmacie_*.dump" -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $dernier) { throw "Aucune sauvegarde pharmacie_*.dump dans $Sauvegardes" }
        $Fichier = $dernier.FullName
    }
    if (-not (Test-Path $Fichier)) { throw "Sauvegarde introuvable : $Fichier" }
    $info = Get-Item $Fichier
    Write-Host ("Sauvegarde : {0} ({1:N1} Mo, du {2:dd/MM/yyyy à HH:mm})" -f $info.FullName, ($info.Length / 1MB), $info.LastWriteTime)

    # 2. Fichier lisible et complet
    & $pgRestore --list $Fichier | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Sauvegarde illisible ou incomplète : $Fichier. Choisir une sauvegarde plus ancienne (-Fichier)." }

    $env:PGCLIENTENCODING = "UTF8"
    $motDePasseSuper = Read-Host "Mot de passe de $SuperUser" -AsSecureString
    $superPwd = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($motDePasseSuper))
    function Invoke-Super([string]$sql) {
        $env:PGPASSWORD = $superPwd
        $r = & $psql -h $HostPg -p $Port -U $SuperUser -d postgres -v ON_ERROR_STOP=1 -qtA -c $sql
        if ($LASTEXITCODE -ne 0) { throw "Échec psql : $sql" }
        return $r
    }

    try {
        # 3. Base existante : jamais supprimée, remplacée seulement si demandé, et seulement après une restauration réussie
        $existe = [bool](Invoke-Super "SELECT 1 FROM pg_database WHERE datname = '$Base'")
        if ($existe -and -not $Remplacer) {
            throw "La base $Base existe déjà : rien n'a été fait. Relancer avec -Remplacer (elle sera gardée sous un autre nom), ou choisir un autre nom avec -Base."
        }
        $horodatage = "{0:yyyyMMdd_HHmmss}" -f (Get-Date)
        $cible = if ($existe) { "{0}_restauration_{1}" -f $Base, $horodatage } else { $Base }

        # 4. Base neuve (nom provisoire si la base existe), restaurée par son propriétaire (extensions pg_trgm et pgcrypto permises)
        Invoke-Super "CREATE DATABASE $cible OWNER $AppUser ENCODING 'UTF8' TEMPLATE template0" | Out-Null
        Write-Host "Restauration dans $cible..."
        & $pgRestore -h $HostPg -p $Port -U $SuperUser -d $cible --role=$AppUser --no-owner --no-privileges --exit-on-error $Fichier
        if ($LASTEXITCODE -ne 0) {
            # Restauration ratée : la base incomplète est supprimée, la base en service n'a pas été touchée
            Invoke-Super "DROP DATABASE IF EXISTS $cible WITH (FORCE)" | Out-Null
            throw "Échec de pg_restore (code $LASTEXITCODE) : rien n'a changé$(if ($existe) { " (la base $Base en service est intacte)" }). Recommencer avec une autre sauvegarde."
        }

        # 5. Restauration réussie : la base en service est gardée sous un autre nom, la restaurée prend son nom
        if ($existe) {
            $ancienne = "{0}_avant_restauration_{1}" -f $Base, $horodatage
            Invoke-Super "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$Base' AND pid <> pg_backend_pid()" | Out-Null
            Invoke-Super "ALTER DATABASE $Base RENAME TO $ancienne" | Out-Null
            Invoke-Super "ALTER DATABASE $cible RENAME TO $Base" | Out-Null
            Write-Host "Base précédente gardée sous le nom $ancienne"
        }

        # 6. Contrôle : contenu restauré
        $env:PGPASSWORD = $superPwd
        $resume = & $psql -h $HostPg -p $Port -U $SuperUser -d $Base -qtA -c @"
SELECT 'Ventes : ' || (SELECT count(*) FROM vente) || ', dernière le ' || COALESCE(to_char((SELECT max(date_vente) FROM vente), 'DD/MM/YYYY HH24:MI'), '—')
    || ' · produits : ' || (SELECT count(*) FROM produit) || ' · employés : ' || (SELECT count(*) FROM utilisateur)
"@
        Write-Host "Base $Base restaurée. $resume"
        Write-Host "Les opérations faites après cette sauvegarde sont à ressaisir. Appliquer ensuite les scripts database/migration/0xx plus récents que la sauvegarde s'il y en a."
    }
    finally {
        $env:PGPASSWORD = $null
        $superPwd = $null
    }
}

# ---------------------------------------------------------------------
# GED
# ---------------------------------------------------------------------
if (-not $SansGed) {
    if (-not $SourceGed) {
        if (-not $Sauvegardes) { throw "Préciser -Sauvegardes (dossier du disque externe) ou -SourceGed." }
        $SourceGed = Join-Path $Sauvegardes "GED"
    }
    if (-not (Test-Path $SourceGed)) { throw "Copie de la GED introuvable : $SourceGed" }
    New-Item -ItemType Directory -Force -Path $DossierGed | Out-Null
    # /E : sous-dossiers ; /XO : un document plus récent sur le serveur est gardé ; jamais /MIR (il supprimerait des documents)
    & robocopy $SourceGed $DossierGed /E /XO /COPY:DAT /DCOPY:T /R:1 /W:1 /NP /NDL /NJH
    # robocopy : 0 à 7 = réussite (1 = fichiers copiés), 8 et plus = échec
    if ($LASTEXITCODE -ge 8) { throw "Échec de la copie de la GED (robocopy code $LASTEXITCODE)." }
    Write-Host "GED restaurée dans $DossierGed depuis $SourceGed"
    $global:LASTEXITCODE = 0
}
