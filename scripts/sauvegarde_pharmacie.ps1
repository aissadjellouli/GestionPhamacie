# =====================================================================
# Sauvegarde automatique de la base PostgreSQL de l'officine
# A planifier chaque soir via le Planificateur de taches Windows.
# Choisir ce script OU la sauvegarde automatique de l'application
# (Administration, Sauvegardes) : pas les deux.
#
# Mot de passe : jamais dans ce script. Creer une fois le fichier
#   C:\ProgramData\PharmaOfficine\pgpass.conf
# avec une seule ligne (hote:port:base:compte:mot de passe) :
#   localhost:5432:pharmacie:pharma_app:<mot de passe de pharma_app>
# puis en limiter l'acces au compte Windows qui lance la tache :
#   icacls C:\ProgramData\PharmaOfficine\pgpass.conf /inheritance:r /grant:r "<compte>:R"
# =====================================================================

# --- Configuration (a adapter) ---------------------------------------
$DbName        = "pharmacie"
$DbUser        = "pharma_app"                   # compte de l'application (proprietaire de la base)
$DbHost        = "localhost"
$DbPort        = 5432
$DossierLocal  = "D:\Sauvegardes\Pharmacie"     # second disque interne
$DossierExterne = "E:\Sauvegardes\Pharmacie"    # disque USB externe
$RetentionJours = 30
$Journal       = "C:\ProgramData\PharmaOfficine\sauvegarde.log"

# PostgreSQL : la version la plus recente installee (pg_dump doit etre de la meme version que le serveur, ou plus recent)
$installation = Get-ChildItem "C:\Program Files\PostgreSQL" -Directory -ErrorAction SilentlyContinue |
    Where-Object { Test-Path (Join-Path $_.FullName "bin\pg_dump.exe") } |
    Sort-Object { [int]($_.Name -replace '\D', '') } -Descending | Select-Object -First 1
if (-not $installation) { Write-Output "ERREUR : PostgreSQL introuvable dans C:\Program Files\PostgreSQL"; exit 3 }
$PgBin = Join-Path $installation.FullName "bin"

# Fichier de mot de passe protege (voir l'en-tete)
$env:PGPASSFILE = "C:\ProgramData\PharmaOfficine\pgpass.conf"
if (-not (Test-Path $env:PGPASSFILE)) { Write-Output "ERREUR : $($env:PGPASSFILE) absent (voir l'en-tete du script)"; exit 4 }

# --- Fonctions --------------------------------------------------------
function Ecrire-Journal($message) {
    $ligne = "{0}  {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $message
    Add-Content -Path $Journal -Value $ligne
    Write-Output $ligne
}

function Purger-Anciennes($dossier) {
    Get-ChildItem -Path $dossier -Filter "$DbName*.dump" -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-$RetentionJours) } |
        ForEach-Object {
            Remove-Item $_.FullName -Force
            Ecrire-Journal "Supprime (ancien) : $($_.Name)"
        }
}

# --- 1. Sauvegarde ----------------------------------------------------
New-Item -ItemType Directory -Force -Path $DossierLocal | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path $Journal) | Out-Null

$horodatage = Get-Date -Format "yyyy-MM-dd_HHmm"
$fichier    = Join-Path $DossierLocal "${DbName}_$horodatage.dump"

Ecrire-Journal "Debut de la sauvegarde vers $fichier"

& "$PgBin\pg_dump.exe" -h $DbHost -p $DbPort -U $DbUser -w `
    --format=custom --compress=6 --file="$fichier" $DbName

if ($LASTEXITCODE -ne 0 -or -not (Test-Path $fichier)) {
    Ecrire-Journal "ERREUR : pg_dump a echoue (code $LASTEXITCODE)"
    exit 1
}

# --- 2. Verification : le fichier doit etre lisible -------------------
& "$PgBin\pg_restore.exe" --list "$fichier" | Out-Null
if ($LASTEXITCODE -ne 0) {
    Ecrire-Journal "ERREUR : fichier de sauvegarde illisible"
    exit 2
}

$tailleMo = [math]::Round((Get-Item $fichier).Length / 1MB, 2)
Ecrire-Journal "Sauvegarde OK ($tailleMo Mo)"

# --- 3. Copie sur le disque externe -----------------------------------
if (Test-Path (Split-Path $DossierExterne -Qualifier)) {
    New-Item -ItemType Directory -Force -Path $DossierExterne | Out-Null
    Copy-Item $fichier -Destination $DossierExterne -Force
    Ecrire-Journal "Copie sur le disque externe OK"
    Purger-Anciennes $DossierExterne
} else {
    Ecrire-Journal "ATTENTION : disque externe absent, copie non faite"
}

# --- 4. Rotation ------------------------------------------------------
Purger-Anciennes $DossierLocal

Ecrire-Journal "Fin de la sauvegarde"
exit 0
