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
#
# Chaque passage est inscrit au journal des sauvegardes de l'application
# (table journal_sauvegarde, ecran Administration, Sauvegardes, rubrique
# Journal) : reussie (copie faite sur le disque externe), disque_absent ou
# echouee. L'alerte "aucune sauvegarde sur le disque externe depuis N jours"
# lit ce journal. Un echec d'inscription n'arrete pas la sauvegarde.
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

# Inscription au journal des sauvegardes de l'application (valeurs passees en variables psql, jamais collees dans le SQL)
function Inscrire-Journal($statut, $fichierSauvegarde, $tailleOctets, $message) {
    try {
        $message = "$message" -replace '"', "'"   # guillemets doubles mal transmis aux programmes par PowerShell 5.1
        $sql = "INSERT INTO journal_sauvegarde (debut, fin, statut, declenchement, fichier, taille_octets, message) " +
               "VALUES (:'debut'::timestamptz, now(), :'statut', 'automatique', NULLIF(:'fichier', ''), NULLIF(:'taille', '')::bigint, :'message');"
        $env:PGCLIENTENCODING = "UTF8"
        $sql | & "$PgBin\psql.exe" -h $DbHost -p $DbPort -U $DbUser -w -d $DbName -q -X -v ON_ERROR_STOP=1 `
            -v "debut=$script:DebutUtc" -v "statut=$statut" -v "fichier=$fichierSauvegarde" -v "taille=$tailleOctets" `
            -v "message=$message (script sauvegarde_pharmacie.ps1)" | Out-Null
        if ($LASTEXITCODE -ne 0) { Ecrire-Journal "ATTENTION : inscription au journal des sauvegardes impossible (code $LASTEXITCODE)" }
    } catch {
        Ecrire-Journal "ATTENTION : inscription au journal des sauvegardes impossible : $($_.Exception.Message)"
    }
}

# --- 1. Sauvegarde ----------------------------------------------------
$script:DebutUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
New-Item -ItemType Directory -Force -Path $DossierLocal | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path $Journal) | Out-Null

$horodatage = Get-Date -Format "yyyy-MM-dd_HHmm"
$fichier    = Join-Path $DossierLocal "${DbName}_$horodatage.dump"

Ecrire-Journal "Debut de la sauvegarde vers $fichier"

& "$PgBin\pg_dump.exe" -h $DbHost -p $DbPort -U $DbUser -w `
    --format=custom --compress=6 --file="$fichier" $DbName

if ($LASTEXITCODE -ne 0 -or -not (Test-Path $fichier)) {
    $code = $LASTEXITCODE
    Ecrire-Journal "ERREUR : pg_dump a echoue (code $code)"
    Inscrire-Journal "echouee" "" "" "pg_dump a echoue (code $code)"
    exit 1
}

# --- 2. Verification : le fichier doit etre lisible -------------------
& "$PgBin\pg_restore.exe" --list "$fichier" | Out-Null
if ($LASTEXITCODE -ne 0) {
    Ecrire-Journal "ERREUR : fichier de sauvegarde illisible"
    Inscrire-Journal "echouee" $fichier "" "Fichier de sauvegarde illisible (pg_restore --list)"
    exit 2
}

$tailleOctets = (Get-Item $fichier).Length
$tailleMo = [math]::Round($tailleOctets / 1MB, 2)
Ecrire-Journal "Sauvegarde OK ($tailleMo Mo)"

# --- 3. Copie sur le disque externe -----------------------------------
if (Test-Path (Split-Path $DossierExterne -Qualifier)) {
    New-Item -ItemType Directory -Force -Path $DossierExterne | Out-Null
    try {
        Copy-Item $fichier -Destination $DossierExterne -Force -ErrorAction Stop
        Ecrire-Journal "Copie sur le disque externe OK"
        Inscrire-Journal "reussie" (Join-Path $DossierExterne (Split-Path $fichier -Leaf)) $tailleOctets "Sauvegarde de $tailleMo Mo, copiee sur le disque externe"
    } catch {
        Ecrire-Journal "ERREUR : copie sur le disque externe impossible : $($_.Exception.Message)"
        Inscrire-Journal "echouee" $fichier $tailleOctets "Copie sur le disque externe impossible : $($_.Exception.Message)"
    }
    Purger-Anciennes $DossierExterne
} else {
    Ecrire-Journal "ATTENTION : disque externe absent, copie non faite"
    Inscrire-Journal "disque_absent" $fichier $tailleOctets "Disque externe absent : sauvegarde gardee seulement sur $DossierLocal"
}

# --- 4. Rotation ------------------------------------------------------
Purger-Anciennes $DossierLocal

Ecrire-Journal "Fin de la sauvegarde"
exit 0
