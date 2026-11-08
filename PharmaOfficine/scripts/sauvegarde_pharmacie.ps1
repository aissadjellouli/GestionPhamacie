# =====================================================================
# Sauvegarde automatique de la base PostgreSQL de l'officine
# A planifier chaque soir via le Planificateur de taches Windows
# =====================================================================

# --- Configuration (a adapter) ---------------------------------------
$PgBin         = "C:\Program Files\PostgreSQL\17\bin"
$DbName        = "pharmacie"
$DbUser        = "sauvegarde"
$DbHost        = "localhost"
$DbPort        = 5432
$DossierLocal  = "D:\Sauvegardes\Pharmacie"     # second disque interne
$DossierExterne = "E:\Sauvegardes\Pharmacie"    # disque USB externe
$RetentionJours = 30
$Journal       = "C:\ProgramData\Pharmacie\sauvegarde.log"

# Fichier de mot de passe protege (voir instructions)
$env:PGPASSFILE = "C:\ProgramData\Pharmacie\pgpass.conf"

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
