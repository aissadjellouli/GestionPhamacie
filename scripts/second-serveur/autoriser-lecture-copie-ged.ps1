<#
.SYNOPSIS
    2e serveur : permet à la tâche de copie de la GED de lire, sans mot de passe, la fréquence réglée dans l'application.

.DESCRIPTION
    La fréquence de la copie de la GED (« toutes les N minutes », Administration > 2e serveur > Copie de la GED) est stockée dans la base
    (surveillance_replication.ged_copie_minutes). La tâche de copie (copier-ged.ps1), qui tourne sur le 2e serveur sous un compte Windows
    sans droit, la lit dans la copie répliquée de la base de CE serveur. Aucun mot de passe de base n'est stocké pour cela :
      - un rôle PostgreSQL dédié « pharma_ged_lecture » : connexion seulement, ni superutilisateur, ni création de base ou de rôle,
        ni réplication, sessions en lecture seule, droit de lire CETTE SEULE COLONNE (un nombre de minutes, sans caractère sensible) ;
      - deux lignes dans le pg_hba.conf de ce serveur, « trust » pour ce rôle SEUL, depuis la boucle locale seulement (127.0.0.1 et ::1) :
        le réglage ne se lit que depuis ce PC ; aucun autre rôle (pharma_app, postgres...) n'est touché ; aucune ouverture réseau.
    Alternative écartée : un fichier de mot de passe (pgpass.conf) du compte de la tâche, qui serait un secret en clair sur le disque.

    À lancer en administrateur Windows, avec le superutilisateur PostgreSQL de ce serveur (mot de passe demandé, jamais écrit) :
      1. sur le serveur PRINCIPAL : crée le rôle et lui donne la lecture de la colonne (le script SQL 143 doit être passé) ; le rôle et son
         droit sont ensuite répliqués vers le 2e serveur ;
      2. sur le 2e serveur : vérifie que le rôle est arrivé (sinon : le créer d'abord sur le principal, attendre la réplication), puis
         ajoute les deux lignes au pg_hba.conf de ce serveur (le principal l'a aussi, pour le retour de bascule F3/F4 : relancer ce
         script sur lui) ; copie datée du pg_hba.conf gardée ; les lignes sont placées AVANT les autres règles (PostgreSQL applique la
         première règle qui correspond) ; rechargement de la configuration, sans redémarrage.
    Idempotent : relancé, il ne refait que ce qui manque. -Essai : n'écrit rien, affiche ce qui serait fait.
    Sans ce script, la copie fonctionne quand même : le réglage est illisible, la copie se fait toutes les 5 minutes (valeur par défaut)
    et le journal de la copie le dit.

.EXAMPLE
    .\autoriser-lecture-copie-ged.ps1 -Essai
.EXAMPLE
    .\autoriser-lecture-copie-ged.ps1
#>
param(
    [string]$Base      = "pharmacie",
    [string]$Role      = "pharma_ged_lecture",
    [string]$SuperUser = "postgres",
    [int]   $Port      = 5432,
    [string]$PgBin     = "",
    [switch]$Essai
)
. (Join-Path $PSScriptRoot "commun.ps1")

foreach ($nom in $Base, $Role) {
    if ($nom -notmatch '^[a-z_][a-z0-9_]*$') { throw "Nom invalide : $nom (lettres minuscules, chiffres et _)." }
}
$mode = if ($Essai) { "[ESSAI : rien n'est modifié] " } else { "" }
if (-not $Essai) {
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $admin) { throw "À lancer dans PowerShell en administrateur." }
}
$PgBin = Get-PgBin $PgBin
$mdpSuper = Read-MotDePasse "Mot de passe du superutilisateur PostgreSQL $SuperUser"
function Sql([string]$requete, [hashtable]$v = @{}) {
    Invoke-Psql -PgBin $PgBin -HostPg "localhost" -Port $Port -User $SuperUser -Db $Base -Sql $requete -MotDePasse $mdpSuper -Variables $v
}

if ((Sql "SELECT rolsuper FROM pg_roles WHERE rolname = current_user") -ne "t") { throw "$SuperUser n'est pas superutilisateur." }
$secondServeur = (Sql "SELECT pg_is_in_recovery()") -eq "t"
Write-Host ("Ce serveur est {0}." -f $(if ($secondServeur) { "un 2e serveur (base en lecture seule)" } else { "le serveur principal" }))

# 1. Rôle et droit de lecture (sur le principal ; le 2e serveur les reçoit par la réplication)
$roleExiste = (Sql "SELECT count(*) FROM pg_roles WHERE rolname = :'r'" @{ r = $Role }) -eq "1"
if ($secondServeur) {
    if ($roleExiste) { Write-Host "Rôle ${Role} : présent (reçu du principal)." }
    else { Write-Warning "Rôle ${Role} absent : lancer d'abord ce script sur le serveur principal, attendre la réplication, puis relancer ici." }
}
else {
    if ($roleExiste) { Write-Host "Rôle ${Role} : existe." }
    elseif ($Essai) { Write-Etape $mode "Création du rôle ${Role} (LOGIN, sans droit, sans mot de passe, sessions en lecture seule)." }
    else {
        Sql "SELECT format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION CONNECTION LIMIT 5', :'r') \gexec" @{ r = $Role } | Out-Null
        Sql "SELECT format('ALTER ROLE %I SET default_transaction_read_only = on', :'r') \gexec" @{ r = $Role } | Out-Null
        Write-Host "Rôle ${Role} créé."
        $roleExiste = $true
    }
    $colonne = (Sql "SELECT count(*) FROM information_schema.columns WHERE table_name = 'surveillance_replication' AND column_name = 'ged_copie_minutes'") -eq "1"
    if (-not $colonne) { Write-Warning "La colonne surveillance_replication.ged_copie_minutes n'existe pas : passer d'abord la mise à jour de la base (script 143)." }
    elseif ($Essai) { Write-Etape $mode "GRANT SELECT (ged_copie_minutes) ON surveillance_replication TO ${Role} (cette seule colonne)." }
    elseif ($roleExiste) {
        Sql "SELECT format('GRANT SELECT (ged_copie_minutes) ON public.surveillance_replication TO %I', :'r') \gexec" @{ r = $Role } | Out-Null
        Write-Host "Droit de lecture de la seule colonne ged_copie_minutes donné à ${Role}."
    }
}

# 2. pg_hba.conf de CE serveur : « trust » pour ce rôle seul, depuis la boucle locale seulement, avant toute autre règle
$hba = Sql "SHOW hba_file"
$lignes = @(
    "host    $Base    $Role    127.0.0.1/32    trust    # lecture de la fréquence de copie de la GED (copier-ged.ps1), ce PC seulement",
    "host    $Base    $Role    ::1/128         trust    # idem, IPv6 local"
)
$contenu = if (Test-Path -LiteralPath $hba) { @(Get-Content -LiteralPath $hba -Encoding UTF8) } else { @() }
$manquantes = @($lignes | Where-Object { $l = ($_ -split '#')[0].Trim() -replace '\s+', ' '; -not ($contenu | Where-Object { ($_ -replace '\s+', ' ').Trim().StartsWith($l) }) })
if (-not $manquantes) { Write-Host "pg_hba.conf : lignes de lecture déjà présentes." }
elseif ($Essai) { $manquantes | ForEach-Object { Write-Etape $mode "pg_hba.conf, ligne ajoutée avant les autres règles : $_" } }
else {
    $copie = "$hba.avant-lecture-copie-ged-{0:yyyyMMdd-HHmmss}" -f (Get-Date)
    Copy-Item -LiteralPath $hba -Destination $copie
    $premiere = 0
    for ($i = 0; $i -lt $contenu.Count; $i++) { if ($contenu[$i].Trim() -and -not $contenu[$i].TrimStart().StartsWith('#')) { $premiere = $i; break } }
    $bloc = @("# ---- Lecture de la fréquence de copie de la GED ({0:dd/MM/yyyy}) ----" -f (Get-Date)) + $manquantes + @("")
    $avant = if ($premiere -gt 0) { @($contenu[0..($premiere - 1)]) } else { @() }
    $apres = if ($contenu.Count -gt $premiere) { @($contenu[$premiere..($contenu.Count - 1)]) } else { @() }
    $nouveau = $avant + $bloc + $apres
    Set-Content -LiteralPath $hba -Value $nouveau -Encoding UTF8
    Write-Host "pg_hba.conf complété (copie gardée : $copie)."
    Sql "SELECT pg_reload_conf()" | Out-Null
    Write-Host "Configuration rechargée (aucun redémarrage)."
}
if ($Essai) { Write-Host "$mode Simulation terminée : rien n'a été modifié." }
else { Write-Host "Contrôle : copier-ged.ps1 -Source <partage> -Installer -Essai affiche « Réglage de l'application : toutes les N minute(s) »." }
