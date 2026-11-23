# Fonctions communes aux scripts de mise en production (scripts\mep). Ce fichier n'est pas lancé seul :
# il est chargé par creer_base_officine.ps1, mettre_a_jour_base_officine.ps1 et creer_base_centrale.ps1.
#
# Règles : aucun mot de passe en paramètre ni écrit sur disque. Un mot de passe saisi (Read-Host -AsSecureString)
# reste en mémoire du processus ; il est transmis à psql / pg_dump par la variable PGPASSWORD du processus enfant seulement.
# Si PGPASSWORD est déjà défini ou si un fichier pgpass.conf existe, rien n'est demandé.

$script:Simulation = $false
$script:MotsDePasse = @{}

function Write-Etape([string]$Texte)  { Write-Host ""; Write-Host "== $Texte" -ForegroundColor Cyan }
function Write-Ok([string]$Texte)     { Write-Host "   [OK] $Texte" -ForegroundColor Green }
function Write-Info([string]$Texte)   { Write-Host "   $Texte" }
function Write-Simul([string]$Texte)  { Write-Host "   [simulation] $Texte" -ForegroundColor Yellow }
function Write-Avert([string]$Texte)  { Write-Host "   [ATTENTION] $Texte" -ForegroundColor Yellow }

function Initialize-OutilsMep {
    param([string]$PgBin, [string]$Hote, [int]$Port, [bool]$Simulation)
    $script:Simulation = $Simulation
    $script:HotePg = $Hote
    $script:PortPg = $Port
    if (-not $PgBin) {
        $installation = Get-ChildItem "C:\Program Files\PostgreSQL" -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName "bin\psql.exe") } |
            Sort-Object { [int]($_.Name -replace '\D', '0') } -Descending | Select-Object -First 1
        if ($installation) { $PgBin = Join-Path $installation.FullName "bin" }
        else {
            $cmd = Get-Command psql.exe -ErrorAction SilentlyContinue
            if ($cmd) { $PgBin = Split-Path $cmd.Source -Parent }
        }
    }
    if (-not $PgBin) { throw "PostgreSQL introuvable (C:\Program Files\PostgreSQL ou variable PATH). Préciser -PgBin." }
    $script:Psql = Join-Path $PgBin "psql.exe"
    $script:PgDump = Join-Path $PgBin "pg_dump.exe"
    $script:PgRestore = Join-Path $PgBin "pg_restore.exe"
    foreach ($o in $script:Psql, $script:PgDump, $script:PgRestore) {
        if (-not (Test-Path -LiteralPath $o)) { throw "Outil introuvable : $o (préciser -PgBin, dossier bin de PostgreSQL)." }
    }
    # Les scripts SQL contiennent des accents ; l'entrée standard (mot de passe d'un nouveau rôle) est envoyée en UTF-8 sans BOM
    $env:PGCLIENTENCODING = "UTF8"
    try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
    $global:OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    Write-Ok "PostgreSQL : $PgBin"
}

function Test-ConnexionSansSaisie {
    if ($env:PGPASSWORD) { return $true }
    $fichier = if ($env:PGPASSFILE) { $env:PGPASSFILE } else { Join-Path $env:APPDATA "postgresql\pgpass.conf" }
    return (Test-Path -LiteralPath $fichier)
}

function ConvertFrom-SecureStringEnClair([System.Security.SecureString]$Secure) {
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

# Demande (une fois) le mot de passe d'un compte PostgreSQL, sauf si pgpass.conf / PGPASSWORD le fournissent.
# En simulation (-WhatIf) rien n'est demandé : les contrôles qui ont besoin de la base sont alors ignorés.
# Retourne $true si une connexion à ce compte est possible sans autre saisie.
function Request-MotDePasse {
    param([Parameter(Mandatory)][string]$Compte, [string]$Invite)
    if ($script:MotsDePasse.ContainsKey($Compte)) { return $true }
    if (Test-ConnexionSansSaisie) { return $true }
    if ($script:Simulation) { return $false }
    if (-not $Invite) { $Invite = "Mot de passe PostgreSQL du compte $Compte" }
    $secure = Read-Host $Invite -AsSecureString
    $clair = ConvertFrom-SecureStringEnClair $secure
    if (-not $clair) { throw "Mot de passe vide pour le compte $Compte." }
    $script:MotsDePasse[$Compte] = $clair
    return $true
}

function Set-MotDePasseMemoire([string]$Compte, [string]$Clair) { $script:MotsDePasse[$Compte] = $Clair }

# Lance un outil PostgreSQL (psql, pg_dump, pg_restore). Le mot de passe passe par PGPASSWORD, dans ce processus seulement.
function Invoke-OutilPg {
    param([Parameter(Mandatory)][string]$Outil, [string]$Compte, [string[]]$Arguments = @(), [string]$Entree)
    $ancienMdp = $env:PGPASSWORD
    $ancienOpts = $env:PGOPTIONS
    $ea = $ErrorActionPreference
    try {
        if ($Compte -and $script:MotsDePasse.ContainsKey($Compte)) { $env:PGPASSWORD = $script:MotsDePasse[$Compte] }
        # Marqueur de la mutualisation des réglages (script 141) : sans lui, une table imposée par le serveur central refuserait les scripts rejoués
        $env:PGOPTIONS = "-c client_min_messages=warning -c pharma.reglage_central=on"
        $ErrorActionPreference = "Continue"      # stderr de l'outil ne doit pas lever d'exception (Windows PowerShell 5.1)
        if ($PSBoundParameters.ContainsKey('Entree')) { $sortie = $Entree | & $Outil @Arguments 2>&1 }
        else { $sortie = & $Outil @Arguments 2>&1 }
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $ea
        $env:PGPASSWORD = $ancienMdp
        $env:PGOPTIONS = $ancienOpts
        if ($null -eq $ancienMdp) { Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue }
        if ($null -eq $ancienOpts) { Remove-Item Env:\PGOPTIONS -ErrorAction SilentlyContinue }
    }
    $lignes = @($sortie | ForEach-Object { "$_" })
    return [pscustomobject]@{ Code = $code; Lignes = $lignes; Texte = ($lignes -join "`n") }
}

# psql avec ON_ERROR_STOP. -Role : « SET ROLE » exécuté dans la même session, avant -c / -f.
function Invoke-Psql {
    param([Parameter(Mandatory)][string]$Compte, [Parameter(Mandatory)][string]$Base,
          [string[]]$Arguments = @(), [string]$Role, [string]$Entree, [switch]$Tolerant)
    $a = @("-h", $script:HotePg, "-p", "$($script:PortPg)", "-U", $Compte, "-d", $Base, "-w", "-X", "-q", "-v", "ON_ERROR_STOP=1")
    if ($Role) { $a += @("-c", "SET ROLE $Role") }
    $a += $Arguments
    if ($PSBoundParameters.ContainsKey('Entree')) { $r = Invoke-OutilPg -Outil $script:Psql -Compte $Compte -Arguments $a -Entree $Entree }
    else { $r = Invoke-OutilPg -Outil $script:Psql -Compte $Compte -Arguments $a }
    if ($r.Code -ne 0 -and -not $Tolerant) {
        throw "psql a échoué (compte $Compte, base $Base) :`n$($r.Texte)"
    }
    return $r
}

# Valeur unique renvoyée par une requête (psql -tA), nettoyée.
function Get-Scalaire {
    param([string]$Compte, [string]$Base, [string]$Sql, [string]$Role)
    $r = Invoke-Psql -Compte $Compte -Base $Base -Role $Role -Arguments @("-tAc", $Sql)
    return (($r.Lignes | Where-Object { $_ -ne "" }) -join "`n").Trim()
}

function Test-NomBase([string]$Nom) {
    if ($Nom -cnotmatch '^[a-z][a-z0-9_]{0,62}$') {
        throw "Nom de base invalide « $Nom » : minuscules, chiffres et _ seulement, 63 caractères au plus, première lettre alphabétique."
    }
}

# Scripts numérotés « NNN_*.sql » (jamais les scripts « 101b », « 111b », « 163b » : ils se lancent à part), dans l'ordre des numéros.
function Get-ScriptsNumerotes {
    param([Parameter(Mandatory)][string]$Dossier)
    if (-not (Test-Path -LiteralPath $Dossier)) { throw "Dossier de scripts introuvable : $Dossier" }
    $liste = @(Get-ChildItem -LiteralPath $Dossier -Filter "*.sql" -File | Where-Object { $_.Name -match '^\d{3}_' } |
        Sort-Object { [int]$_.Name.Substring(0, 3) })
    if ($liste.Count -eq 0) { throw "Aucun script numéroté (NNN_*.sql) dans $Dossier" }
    return $liste
}

# Numéros de database\migration jamais attribués (docs/base-de-donnees.md, « Numérotation ») : tolérés par Test-SuiteSansTrou.
# Les scripts « b » (101b, 111b, 163b) ne sont pas dans la liste numérotée (Get-ScriptsNumerotes les ignore).
$script:NumerosNonAttribues = @(113, 119)

# La suite numérotée de l'officine doit être sans trou ni doublon (comme celle du central, creer_base_centrale.ps1), hors numéros non attribués.
function Test-SuiteSansTrou {
    param([Parameter(Mandatory)][object[]]$Scripts, [int[]]$NonAttribues = $script:NumerosNonAttribues)
    $numeros = @($Scripts | ForEach-Object { [int]$_.Name.Substring(0, 3) })
    $doublons = @($numeros | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name })
    if ($doublons.Count -gt 0) { throw "Suite des scripts : numéro(s) en double : $($doublons -join ', ')." }
    $presentsInterdits = @($numeros | Where-Object { $NonAttribues -contains $_ })
    if ($presentsInterdits.Count -gt 0) { throw "Suite des scripts : le(s) numéro(s) $($presentsInterdits -join ', ') figurent dans la liste des numéros non attribués (outils_mep.ps1) mais un script existe : mettre la liste à jour." }
    $attendus = 1..($numeros | Measure-Object -Maximum).Maximum | Where-Object { $NonAttribues -notcontains $_ }
    $manquants = @($attendus | Where-Object { $numeros -notcontains $_ } | ForEach-Object { '{0:000}' -f $_ })
    if ($manquants.Count -gt 0) { throw "Suite des scripts interrompue : numéro(s) manquant(s) $($manquants -join ', ') (ni script, ni numéro non attribué)." }
}

# L'en-tête d'un fichier d'init doit citer « 001 à NNN » avec NNN = dernier script numéroté du dépôt.
function Test-EnteteInit {
    param([Parameter(Mandatory)][string]$Fichier, [Parameter(Mandatory)][string]$Dernier)
    if (-not (Test-Path -LiteralPath $Fichier)) { throw "Fichier d'init introuvable : $Fichier" }
    $debut = (Get-Content -LiteralPath $Fichier -Encoding UTF8 -TotalCount 80) -join "`n"
    if ($debut -notmatch ("001 \u00e0 " + $Dernier + "\b")) {
        throw "L'en-tête de $(Split-Path $Fichier -Leaf) ne cite pas « 001 à $Dernier » : l'init est en retard sur les scripts de database\migration. Rien n'a été fait. Voir le développeur."
    }
}

function Invoke-Principal {
    param([Parameter(Mandatory)][scriptblock]$Corps)
    $chrono = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $Corps
        Write-Host ""
        Write-Host ("Terminé en {0:N0} s." -f $chrono.Elapsed.TotalSeconds)
        exit 0
    }
    catch {
        Write-Host ""
        Write-Host "ÉCHEC : $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "Aucun mot de passe n'est conservé. Voir docs\scripts-base-de-donnees.md, section « En cas d'échec »." -ForegroundColor Red
        exit 1
    }
}
