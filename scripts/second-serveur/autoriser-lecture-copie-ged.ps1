<#
.SYNOPSIS
    2e serveur : permet à la tâche de copie de la GED de lire, sans mot de passe, la fréquence réglée dans l'application.

.DESCRIPTION
    La fréquence de la copie de la GED (« toutes les N minutes », Administration > 2e serveur > Copie de la GED) est stockée dans la base
    (surveillance_replication.ged_copie_minutes). La tâche de copie (copier-ged.ps1), qui tourne sur le 2e serveur sous un compte Windows
    sans droit, la lit dans la copie répliquée de la base de CE serveur. Aucun mot de passe de base n'est stocké pour cela :
      - un rôle PostgreSQL dédié « pharma_ged_lecture » : connexion seulement, ni superutilisateur, ni création de base ou de rôle,
        ni réplication, sessions en lecture seule, droit de lire CETTE SEULE COLONNE (un nombre de minutes, sans caractère sensible) ;
      - authentification Windows (SSPI, décision du titulaire du 2026-11-17) : PostgreSQL vérifie le compte Windows qui se connecte et
        n'accepte comme pharma_ged_lecture QUE le compte de la tâche de copie (-CompteWindows, « PharmaGedCopie » par défaut, celui
        que crée copier-ged.ps1 -Installer) sur CE PC :
          * pg_ident.conf : une ligne de correspondance « pharma_ged_copie  <compte>@<NOM DU PC>  pharma_ged_lecture » ;
          * pg_hba.conf : deux lignes « sspi map=pharma_ged_copie » pour ce rôle SEUL, depuis la boucle locale seulement (127.0.0.1, ::1).
        Un autre programme du PC, lancé sous un autre compte Windows, est refusé ; aucun autre rôle (pharma_app, postgres...) n'est
        touché ; aucune ouverture réseau.
      - les anciennes lignes « trust » de ce rôle (première version de ce script) sont retirées du pg_hba.conf.
    Alternatives écartées : « trust » (tout programme de ce PC pouvait se présenter comme ce rôle) ; un fichier de mot de passe
    (pgpass.conf) du compte de la tâche, qui serait un secret en clair sur le disque.
    À vérifier sur le premier vrai serveur : pour un compte local, Windows présente « compte@NOM DU PC » (réglages par défaut de
    PostgreSQL : include_realm=1, compat_realm=1) ; si la lecture est refusée, le journal de PostgreSQL donne le nom reçu.

    À lancer en administrateur Windows, avec le superutilisateur PostgreSQL de ce serveur (mot de passe demandé, jamais écrit) :
      1. sur le serveur PRINCIPAL : crée le rôle et lui donne la lecture de la colonne (le script SQL 143 doit être passé) ; le rôle et son
         droit sont ensuite répliqués vers le 2e serveur ;
      2. sur le 2e serveur : vérifie que le rôle est arrivé (sinon : le créer d'abord sur le principal, attendre la réplication), puis
         ajoute la correspondance au pg_ident.conf et les deux lignes au pg_hba.conf de ce serveur (le principal les a aussi, pour le
         retour de bascule F3/F4 : relancer ce script sur lui) ; copie datée de chaque fichier gardée avant modification ; les lignes
         du pg_hba.conf sont placées AVANT les autres règles (PostgreSQL applique la première règle qui correspond) ; rechargement
         de la configuration, sans redémarrage.
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
    [string]$CompteWindows = "PharmaGedCopie",   # compte local de la tâche de copie (copier-ged.ps1 -Compte)
    [int]   $Port      = 5432,
    [string]$PgBin     = "",
    [switch]$Essai
)
. (Join-Path $PSScriptRoot "commun.ps1")

foreach ($nom in $Base, $Role) {
    if ($nom -notmatch '^[a-z_][a-z0-9_]*$') { throw "Nom invalide : $nom (lettres minuscules, chiffres et _)." }
}
if ($CompteWindows -notmatch '^[A-Za-z0-9_.-]{1,20}$') { throw "Compte Windows invalide : $CompteWindows (lettres, chiffres, _ . -, 20 caractères au plus)." }
$Carte = "pharma_ged_copie"
$identite = "{0}@{1}" -f $CompteWindows, $env:COMPUTERNAME
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

# 2. Authentification Windows (SSPI) : ce rôle seul, boucle locale seulement, et seulement pour le compte Windows de la tâche
$modifie = $false
function Copier-Avant([string]$fichier) {
    $copie = "$fichier.avant-lecture-copie-ged-{0:yyyyMMdd-HHmmss}" -f (Get-Date)
    Copy-Item -LiteralPath $fichier -Destination $copie
    return $copie
}
function Normaliser-Ligne([string]$l) { (($l -split '#')[0] -replace '\s+', ' ').Trim() }

# 2a. pg_ident.conf : correspondance « compte Windows de la tâche » -> pharma_ged_lecture
$ident = Sql "SHOW ident_file"
$ligneIdent = "$Carte    $identite    $Role    # compte Windows de la tâche de copie de la GED (copier-ged.ps1), ce PC seulement"
$contenuIdent = if (Test-Path -LiteralPath $ident) { @(Get-Content -LiteralPath $ident -Encoding UTF8) } else { @() }
if ($contenuIdent | Where-Object { (Normaliser-Ligne $_) -eq (Normaliser-Ligne $ligneIdent) }) { Write-Host "pg_ident.conf : correspondance déjà présente ($identite)." }
elseif ($Essai) { Write-Etape $mode "pg_ident.conf, ligne ajoutée : $ligneIdent" }
else {
    $copie = if (Test-Path -LiteralPath $ident) { Copier-Avant $ident } else { "aucune (fichier absent)" }
    $bloc = @(("# ---- Lecture de la fréquence de copie de la GED ({0:dd/MM/yyyy}) ----" -f (Get-Date)), $ligneIdent)
    Set-Content -LiteralPath $ident -Value ($contenuIdent + $bloc) -Encoding UTF8
    Write-Host "pg_ident.conf complété : $identite -> $Role (copie gardée : $copie)."
    $modifie = $true
}

# 2b. pg_hba.conf : « sspi » pour ce rôle seul, boucle locale seulement, avant toute autre règle ; anciennes lignes « trust » retirées
$hba = Sql "SHOW hba_file"
$lignes = @(
    "host    $Base    $Role    127.0.0.1/32    sspi    map=$Carte    # lecture de la fréquence de copie de la GED (copier-ged.ps1), ce PC seulement",
    "host    $Base    $Role    ::1/128         sspi    map=$Carte    # idem, IPv6 local"
)
$contenu = if (Test-Path -LiteralPath $hba) { @(Get-Content -LiteralPath $hba -Encoding UTF8) } else { @() }
$ancienTrust = "^\s*host\s+$Base\s+$Role\s+\S+\s+trust\b"
$anciennes = @($contenu | Where-Object { $_ -match $ancienTrust })
$manquantes = @($lignes | Where-Object { $l = Normaliser-Ligne $_; -not ($contenu | Where-Object { (Normaliser-Ligne $_) -eq $l }) })
if (-not $manquantes -and -not $anciennes) { Write-Host "pg_hba.conf : lignes de lecture (sspi) déjà présentes." }
elseif ($Essai) {
    $anciennes | ForEach-Object { Write-Etape $mode "pg_hba.conf, ancienne ligne « trust » retirée : $_" }
    $manquantes | ForEach-Object { Write-Etape $mode "pg_hba.conf, ligne ajoutée avant les autres règles : $_" }
}
else {
    $copie = Copier-Avant $hba
    $contenu = @($contenu | Where-Object { $_ -notmatch $ancienTrust })
    if ($manquantes) {
        $premiere = 0
        for ($i = 0; $i -lt $contenu.Count; $i++) { if ($contenu[$i].Trim() -and -not $contenu[$i].TrimStart().StartsWith('#')) { $premiere = $i; break } }
        $bloc = @("# ---- Lecture de la fréquence de copie de la GED, authentification Windows ({0:dd/MM/yyyy}) ----" -f (Get-Date)) + $manquantes + @("")
        $avant = if ($premiere -gt 0) { @($contenu[0..($premiere - 1)]) } else { @() }
        $apres = if ($contenu.Count -gt $premiere) { @($contenu[$premiere..($contenu.Count - 1)]) } else { @() }
        $contenu = $avant + $bloc + $apres
    }
    Set-Content -LiteralPath $hba -Value $contenu -Encoding UTF8
    if ($anciennes) { Write-Host ("pg_hba.conf : {0} ancienne(s) ligne(s) « trust » retirée(s)." -f $anciennes.Count) }
    Write-Host "pg_hba.conf complété (copie gardée : $copie)."
    $modifie = $true
}
if ($modifie) {
    Sql "SELECT pg_reload_conf()" | Out-Null
    $erreurs = Sql "SELECT count(*) FROM pg_hba_file_rules WHERE error IS NOT NULL"
    if ($erreurs -ne "0") { Write-Warning "pg_hba.conf : $erreurs ligne(s) en erreur (vue pg_hba_file_rules) : remettre la copie gardée si besoin." }
    $erreursIdent = Sql "SELECT count(*) FROM pg_ident_file_mappings WHERE error IS NOT NULL"
    if ($erreursIdent -ne "0") { Write-Warning "pg_ident.conf : $erreursIdent ligne(s) en erreur (vue pg_ident_file_mappings) : remettre la copie gardée si besoin." }
    Write-Host "Configuration rechargée (aucun redémarrage)."
}
if ($Essai) { Write-Host "$mode Simulation terminée : rien n'a été modifié." }
else { Write-Host "Contrôle : copier-ged.ps1 -Source <partage> -Installer -Essai affiche « Réglage de l'application : toutes les N minute(s) »." }
