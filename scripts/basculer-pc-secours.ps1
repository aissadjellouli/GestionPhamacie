<#
.SYNOPSIS
    PC serveur en panne : remet la pharmacie en service sur le PC de secours à partir de la dernière copie de secours.

.DESCRIPTION
    À lancer sur le PC de secours, où PostgreSQL et l'API Pharma Officine sont déjà installés (docs/mise-en-production.md,
    « PC serveur en panne »). Étapes :
      1. dernière copie pharmacie_*.dump du dossier -Copies ;
      2. intégrité : empreinte SHA-256 comparée à celle notée lors de la copie (<copie>.sha256), puis pg_restore --list ;
      3. restauration dans la base -Base (une base existante n'est jamais supprimée : gardée sous « <base>_avant_bascule_<date> ») ;
      4. version du schéma : tous les scripts database\migration du dossier de l'API rejoués dans l'ordre (ils sont idempotents) ;
         le dernier script rejoué est affiché : il doit être le dernier livré avec l'API ;
      5. démarrage du service de l'API et adresse à donner aux postes.
    -Essai : simulation, ne touche à rien (ni base, ni service) ; affiche ce qui serait fait.
    Les copies ne sont pas chiffrées et restent dans l'officine.

.EXAMPLE
    .\basculer-pc-secours.ps1 -Copies "D:\CopieSecours" -Essai
.EXAMPLE
    .\basculer-pc-secours.ps1 -Copies "D:\CopieSecours"
#>
param(
    [Parameter(Mandatory)] [string]$Copies,          # second emplacement (Administration › Sauvegardes › Copie de secours)
    [string]$Fichier     = "",                       # copie précise ; vide : la plus récente
    [string]$Base        = "pharmacie",
    [string]$DossierApi  = "C:\Program Files\PharmaOfficine\Api",
    [string]$Service     = "PharmaOfficine API",
    [string]$PgBin       = "",
    [string]$HostPg      = "localhost",
    [int]   $Port        = 5432,
    [int]   $PortApi     = 5080,
    [string]$SuperUser   = "postgres",
    [string]$AppUser     = "pharma_app",
    [switch]$Essai
)

$ErrorActionPreference = "Stop"
if ($Base -notmatch '^[a-z_][a-z0-9_]*$') { throw "Nom de base invalide : $Base (lettres minuscules, chiffres et _)." }
$mode = if ($Essai) { "[ESSAI : rien n'est modifié] " } else { "" }

# 0. Outils PostgreSQL
if (-not $PgBin) {
    $installation = Get-ChildItem "C:\Program Files\PostgreSQL" -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName "bin\pg_restore.exe") } |
        Sort-Object { [int]($_.Name -replace '\D', '') } -Descending | Select-Object -First 1
    if (-not $installation) { throw "PostgreSQL introuvable dans C:\Program Files\PostgreSQL (préciser -PgBin)." }
    $PgBin = Join-Path $installation.FullName "bin"
}
$psql = Join-Path $PgBin "psql.exe"
$pgRestore = Join-Path $PgBin "pg_restore.exe"

# 1. Dernière copie
if (-not $Fichier) {
    $dernier = Get-ChildItem $Copies -Filter "pharmacie_*.dump" -File -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
    if (-not $dernier) { throw "Aucune copie pharmacie_*.dump dans $Copies." }
    $Fichier = $dernier.FullName
}
if (-not (Test-Path $Fichier)) { throw "Copie introuvable : $Fichier" }
$info = Get-Item $Fichier
Write-Host ("{0}Copie : {1} ({2:N1} Mo, du {3:dd/MM/yyyy à HH:mm})" -f $mode, $info.FullName, ($info.Length / 1MB), $info.LastWriteTime)

# 2. Intégrité : empreinte SHA-256 notée lors de la copie, puis lecture complète par pg_restore
$fichierEmpreinte = "$Fichier.sha256"
if (Test-Path $fichierEmpreinte) {
    $notee = ((Get-Content $fichierEmpreinte -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
    $calculee = (Get-FileHash $Fichier -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($notee -ne $calculee) { throw "Copie abîmée : empreinte SHA-256 différente de celle notée ($fichierEmpreinte). Prendre une copie plus ancienne (-Fichier)." }
    Write-Host "Empreinte SHA-256 vérifiée."
} else {
    Write-Warning "Pas d'empreinte notée ($fichierEmpreinte) : seule la lecture par pg_restore est contrôlée."
}
& $pgRestore --list $Fichier | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Copie illisible ou incomplète : $Fichier. Prendre une copie plus ancienne (-Fichier)." }
Write-Host "Copie lisible et complète (pg_restore --list)."

# Scripts de la base livrés avec l'API
$dossierScripts = Join-Path $DossierApi "database"
$scripts = Get-ChildItem $dossierScripts -Filter "*.sql" -File -ErrorAction SilentlyContinue | Sort-Object Name
if (-not $scripts) { throw "Scripts de la base introuvables dans $dossierScripts : installer l'API sur le PC de secours (même version que le PC serveur)." }
Write-Host ("Scripts à rejouer : {0} (dernier : {1})" -f $scripts.Count, $scripts[-1].Name)

$adresses = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike "127.*" -and $_.IPAddress -notlike "169.254.*" } | Select-Object -ExpandProperty IPAddress

if ($Essai) {
    Write-Host "$mode Restauration dans la base $Base (une base $Base existante serait gardée sous ${Base}_avant_bascule_<date>)."
    Write-Host "$mode Démarrage du service « $Service »."
    foreach ($ip in $adresses) { Write-Host "$mode Adresse à donner aux postes : http://${ip}:$PortApi/ (ou https selon l'installation)" }
    Write-Host "$mode Simulation terminée : aucune base ni aucun service n'a été modifié."
    return
}

# 3. Restauration
$env:PGCLIENTENCODING = "UTF8"
$motDePasse = Read-Host "Mot de passe de $SuperUser" -AsSecureString
$superPwd = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($motDePasse))
function Invoke-Super([string]$sql, [string]$db = "postgres") {
    $env:PGPASSWORD = $superPwd
    $r = & $psql -h $HostPg -p $Port -U $SuperUser -d $db -v ON_ERROR_STOP=1 -qtA -c $sql
    if ($LASTEXITCODE -ne 0) { throw "Échec psql : $sql" }
    return $r
}
try {
    & sc.exe stop $Service 2>$null | Out-Null   # service éventuellement démarré : arrêté pendant la bascule
    $horodatage = "{0:yyyyMMdd_HHmmss}" -f (Get-Date)
    $neuve = "{0}_bascule_{1}" -f $Base, $horodatage
    Invoke-Super "CREATE DATABASE $neuve OWNER $AppUser ENCODING 'UTF8' TEMPLATE template0" | Out-Null
    Write-Host "Restauration dans $neuve..."
    $env:PGPASSWORD = $superPwd
    & $pgRestore -h $HostPg -p $Port -U $SuperUser -d $neuve --role=$AppUser --no-owner --no-privileges --exit-on-error $Fichier
    if ($LASTEXITCODE -ne 0) {
        Invoke-Super "DROP DATABASE IF EXISTS $neuve WITH (FORCE)" | Out-Null
        throw "Échec de pg_restore (code $LASTEXITCODE) : rien n'a changé. Recommencer avec une copie plus ancienne (-Fichier)."
    }

    # 4. Version du schéma : scripts rejoués dans l'ordre, arrêt au premier échec
    foreach ($s in $scripts) {
        $env:PGPASSWORD = $superPwd
        & $psql -h $HostPg -p $Port -U $SuperUser -d $neuve -v ON_ERROR_STOP=1 -q -f $s.FullName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Script $($s.Name) en échec sur $neuve : base laissée telle quelle pour examen, $Base n'a pas été touchée." }
    }
    Write-Host "Schéma à jour : dernier script rejoué $($scripts[-1].Name)."

    # Échange : la base existante est gardée, jamais supprimée
    if (Invoke-Super "SELECT 1 FROM pg_database WHERE datname = '$Base'") {
        $ancienne = "{0}_avant_bascule_{1}" -f $Base, $horodatage
        Invoke-Super "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$Base' AND pid <> pg_backend_pid()" | Out-Null
        Invoke-Super "ALTER DATABASE $Base RENAME TO $ancienne" | Out-Null
        Write-Host "Base précédente gardée sous le nom $ancienne"
    }
    Invoke-Super "ALTER DATABASE $neuve RENAME TO $Base" | Out-Null
    $resume = Invoke-Super "SELECT 'Ventes : ' || (SELECT count(*) FROM vente) || ', dernière le ' || COALESCE(to_char((SELECT max(date_vente) FROM vente), 'DD/MM/YYYY HH24:MI'), '—') || ' · employés : ' || (SELECT count(*) FROM utilisateur)" $Base
    Write-Host "Base $Base prête. $resume"
}
finally {
    $env:PGPASSWORD = $null
    $superPwd = $null
}

# 5. API et adresse pour les postes
& sc.exe start $Service | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Warning "Le service « $Service » n'a pas démarré : voir les journaux de l'API (clé de scellement des journaux, procédure « PC serveur en panne »)." }
else { Write-Host "Service « $Service » démarré." }
foreach ($ip in $adresses) { Write-Host "Adresse à donner aux postes : http://${ip}:$PortApi/ (ou https selon l'installation)" }
Write-Host "Sur chaque poste : bouton « Utiliser le PC de secours » du bandeau, ou Administration › Imprimantes › Serveur et PC de secours, puis redémarrer le poste."
Write-Host "Les opérations faites après cette copie sont à ressaisir."
