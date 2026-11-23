<#
.SYNOPSIS
    2e serveur, étape 2 : fait de CE PC le 2e serveur (copie en continu de la base du principal, en lecture seule).

.DESCRIPTION
    À lancer sur le 2e serveur, en administrateur Windows, après preparer-principal.ps1 sur le principal (docs/mise-en-production.md,
    « 2e serveur »). PostgreSQL (même version majeure que le principal) et l'API Tracodex Officine (même version, même installateur)
    sont déjà installés sur ce PC. Étapes :
      1. contrôles : principal joignable (port PostgreSQL), version de PostgreSQL ;
      2. API de secours : service arrêté, mis en démarrage AUTOMATIQUE avec relance sur erreur (vague 2 : l'API démarre en « veille »
         sur la base en lecture seule dès que « SecondServeur:AutreServeur » est réglé ; sans ce réglage elle refuse de démarrer) ;
      3. PostgreSQL de ce PC arrêté ; son dossier de données gardé à côté (renommé « <dossier>_avant_second_<date> », jamais supprimé) ;
      4. pg_basebackup depuis le principal avec le slot « second_serveur » et -R (standby.signal et primary_conninfo écrits) :
         la base arrive complète, puis se met à jour en continu ; au démarrage de l'un ou l'autre PC, le retard se rattrape seul ;
      5. droits du compte du service PostgreSQL sur le nouveau dossier, démarrage, contrôle « en lecture seule » et « réception » ;
      6. clé de scellement : import du fichier exporté par le titulaire (-FichierCle ; le titulaire tape son mot de passe).
    Clé des jetons (Jwt__Cle, même valeur que le principal : sans elle, bascule sans reconnexion, retrait et relais impossibles) : saisie
    MASQUÉE avant toute modification, contrôlée par son empreinte (-EmpreinteJetonPrincipal) ; différente : l'installation s'arrête sans rien
    toucher. Identique : enregistrée comme variable machine Jwt__Cle (jamais écrite dans un fichier).
    -Essai : n'arrête rien, n'écrit rien ; contrôle et affiche ce qui serait fait.

.EXAMPLE
    .\initialiser-second-serveur.ps1 -Principal 192.168.1.20 -EmpreinteJetonPrincipal 0123456789abcdef -FichierCle E:\cle-scellement-20261116-0123456789abcdef.pocle -Essai
#>
param(
    [Parameter(Mandatory)] [string]$Principal,          # IPv4 du serveur principal
    [string]$RoleReplication = "pharma_replication",
    [string]$Slot            = "second_serveur",
    [int]   $Port            = 5432,
    [string]$PgBin           = "",
    [string]$ServicePg       = "",                     # vide : le service postgresql* de ce PC
    [string]$DossierDonnees  = "",                     # vide : data_directory du PostgreSQL de ce PC (lu dans le registre du service)
    [string]$DossierApi      = "C:\Program Files\PharmaOfficine\Api",
    [string]$ServiceApi      = "PharmaOfficine API",
    [string]$FichierCle      = "",                     # export .pocle de la clé de scellement (clé USB du coffre)
    [Parameter(Mandatory)] [string]$EmpreinteJetonPrincipal,   # 16 caractères, affichés sur le principal par « Pharma.Api.exe --empreinte-jeton »
    [switch]$Essai
)
. (Join-Path $PSScriptRoot "commun.ps1")

if (-not [Net.IPAddress]::TryParse($Principal, [ref]$null)) { throw "Adresse IPv4 invalide : $Principal" }
if ($RoleReplication -notmatch '^[a-z_][a-z0-9_]*$' -or $Slot -notmatch '^[a-z_][a-z0-9_]*$') { throw "Nom de rôle ou de slot invalide." }
$mode = if ($Essai) { "[ESSAI : rien n'est modifié] " } else { "" }
$PgBin = Get-PgBin $PgBin

# 1. Contrôles
if (-not (Test-Port $Principal $Port)) { throw "Principal $Principal injoignable sur le port $Port (pare-feu, listen_addresses, PC éteint ?)." }
Write-Host "Principal $Principal joignable (port $Port)."
if (-not $ServicePg) { $ServicePg = (Get-Service "postgresql*" -ErrorAction SilentlyContinue | Select-Object -First 1).Name }
if (-not $ServicePg) { throw "Service PostgreSQL introuvable sur ce PC : installer PostgreSQL (même version majeure que le principal)." }
if (-not $DossierDonnees) {
    $chemin = (Get-CimInstance Win32_Service -Filter "Name='$ServicePg'").PathName
    if ($chemin -match '-D\s+"([^"]+)"') { $DossierDonnees = $Matches[1] } elseif ($chemin -match '-D\s+(\S+)') { $DossierDonnees = $Matches[1] }
}
if (-not $DossierDonnees) { throw "Dossier de données introuvable (préciser -DossierDonnees)." }
$versionLocale = (& (Join-Path $PgBin "pg_basebackup.exe") --version) -replace '\D+(\d+).*', '$1'
Write-Host "PostgreSQL local : service $ServicePg, données $DossierDonnees, pg_basebackup $versionLocale (pg_basebackup refuse une version majeure différente)."
$exeApi = Join-Path $DossierApi "Pharma.Api.exe"
if (-not (Test-Path $exeApi)) { Write-Warning "API introuvable ($exeApi) : installer l'API sur ce PC avec le même installateur que le principal." }
else { Write-Host ("API de secours : version {0}" -f (Get-Item $exeApi).VersionInfo.FileVersion) }
if ($FichierCle -and -not (Test-Path $FichierCle)) { throw "Fichier de clé introuvable : $FichierCle" }

# Clé des jetons : contrôle bloquant AVANT toute modification (aussi en -Essai : lecture seule, la clé reste en mémoire)
$cleJetons = Read-CleJetonsControlee $EmpreinteJetonPrincipal

$horodatage = "{0:yyyyMMdd_HHmmss}" -f (Get-Date)
$ancien = "{0}_avant_second_{1}" -f $DossierDonnees.TrimEnd('\'), $horodatage
if ($Essai) {
    Write-Etape $mode "Clé des jetons contrôlée (empreinte identique) puis enregistrée en variable machine Jwt__Cle."
    Write-Etape $mode "Service « $ServiceApi » arrêté, démarrage automatique et relance sur erreur (démarré à la fin si SecondServeur:AutreServeur est déjà réglé : voir le message final)."
    Write-Etape $mode "Service $ServicePg arrêté ; $DossierDonnees renommé en $ancien (gardé)."
    Write-Etape $mode "pg_basebackup -h $Principal -U $RoleReplication -S $Slot -X stream -R -D $DossierDonnees (mot de passe de $RoleReplication demandé)."
    Write-Etape $mode "Droits du service PostgreSQL sur $DossierDonnees, démarrage de $ServicePg, contrôle pg_is_in_recovery() = vrai."
    if ($FichierCle) { Write-Etape $mode "Import de la clé : `"$exeApi`" --importer-cle `"$FichierCle`" (mot de passe tapé par le titulaire)." }
    Write-Host "$mode Simulation terminée : rien n'a été modifié."
    return
}

$confirmation = Read-Host "La base PostgreSQL de CE PC va être remplacée par la copie du principal (l'ancienne est gardée sous $ancien). Taper OUI pour continuer"
if ($confirmation -ne "OUI") { Write-Host "Abandon : rien n'a été modifié."; return }

# Clé des jetons : variable machine (comme sur le principal), lue par le service au prochain démarrage de l'API
[Environment]::SetEnvironmentVariable("Jwt__Cle", $cleJetons, "Machine")
$cleJetons = $null
Write-Host "Variable machine Jwt__Cle enregistrée (empreinte contrôlée)."

# 2. API de secours en veille
if (Get-Service $ServiceApi -ErrorAction SilentlyContinue) {
    Stop-Service $ServiceApi -ErrorAction SilentlyContinue
    Set-ServiceApiRelance $ServiceApi
    Write-Host "Service « $ServiceApi » arrêté, démarrage automatique avec relance sur erreur (il sera démarré après le réglage de SecondServeur:AutreServeur)."
}

# 3. Ancien dossier de données gardé
Stop-Service $ServicePg
if (Test-Path $DossierDonnees) { Rename-Item $DossierDonnees $ancien; Write-Host "Ancien dossier gardé : $ancien" }

# 4. Copie de base et réplication continue
$mdpRepl = Read-MotDePasse "Mot de passe du rôle $RoleReplication (donné à preparer-principal.ps1)"
$env:PGPASSWORD = $mdpRepl
try {
    & (Join-Path $PgBin "pg_basebackup.exe") -h $Principal -p $Port -U $RoleReplication -D $DossierDonnees -S $Slot -X stream -R -P -w
    if ($LASTEXITCODE -ne 0) { throw "pg_basebackup a échoué : l'ancien dossier reste disponible sous $ancien (le renommer pour revenir en arrière)." }
} finally { Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue }

# 5. Droits du compte du service, démarrage, contrôle
$compte = (Get-CimInstance Win32_Service -Filter "Name='$ServicePg'").StartName
if ($compte -and $compte -ne "LocalSystem") { & icacls $DossierDonnees /grant "${compte}:(OI)(CI)F" /T /Q | Out-Null }
Start-Service $ServicePg
Start-Sleep -Seconds 5
$mdpSuper = Read-MotDePasse "Mot de passe du superutilisateur PostgreSQL (postgres, le même que sur le principal : la copie est complète)"
$etat = Invoke-Psql -PgBin $PgBin -HostPg "localhost" -Port $Port -User "postgres" -Db "postgres" -MotDePasse $mdpSuper `
    -Sql "SELECT pg_is_in_recovery() || '|' || COALESCE((SELECT status FROM pg_stat_wal_receiver), 'aucune réception')"
Write-Host "Base en lecture seule | réception : $etat"
if ($etat -notlike "t|*") { Write-Warning "La base n'est pas en lecture seule : vérifiez standby.signal dans $DossierDonnees." }

# 6. Clé de scellement
if ($FichierCle -and (Test-Path $exeApi)) {
    Write-Host "Import de la clé de scellement : le titulaire tape le mot de passe du fichier."
    & $exeApi --importer-cle $FichierCle
    if ($LASTEXITCODE -ne 0) { Write-Warning "Clé non importée : relancer « `"$exeApi`" --importer-cle `"$FichierCle`" »." }
}
Write-Host "2e serveur prêt. Contrôle : verifier-second-serveur.ps1 -Principal $Principal (relève aussi la version et la clé pour l'écran « 2e serveur »)."
Write-Host "Dans l'appsettings de l'API de CE serveur : « SecondServeur:AutreServeur » = $Principal, puis démarrer le service « $ServiceApi » : l'API démarre en veille (healthcheck : role = veille)."
Write-Host "Sans ce réglage l'API refuse de démarrer sur une base en lecture seule (le service relancé en boucle : ne pas le démarrer avant)."
