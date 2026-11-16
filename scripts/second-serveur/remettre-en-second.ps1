<#
.SYNOPSIS
    Retour après une bascule : l'ANCIEN principal (réparé) redevient 2e serveur et rattrape le nouveau principal.

.DESCRIPTION
    À lancer sur l'ancien principal, en administrateur Windows, AVANT de redémarrer son API (docs/mise-en-production.md,
    « 2e serveur », « Retour »). Au préalable, sur le NOUVEAU principal : preparer-principal.ps1 -AdresseSecond <ce PC>.
    Étapes :
      1. API de ce PC arrêtée et mise en démarrage manuel (elle refuserait de toute façon de démarrer face à un principal plus récent) ;
      2. contrôle : le nouveau principal répond et sa base est en écriture ;
      3. PostgreSQL de ce PC arrêté ; son dossier de données GARDÉ (renommé « <dossier>_ancien_principal_<date> ») : il peut contenir
         les dernières saisies d'avant la panne, non reçues par l'autre serveur (à examiner avant de le supprimer, jamais automatiquement) ;
      4. copie complète depuis le nouveau principal (pg_basebackup, slot « second_serveur », -R), droits, démarrage, contrôle.
    Ensuite, pour revenir à la répartition d'origine à un moment calme : basculer-second-serveur.ps1 sur ce PC (après arrêt de l'API
    et de PostgreSQL de l'autre), puis remettre-en-second.ps1 sur l'autre.
    -Essai : n'arrête rien, n'écrit rien ; contrôle et affiche ce qui serait fait.

.EXAMPLE
    .\remettre-en-second.ps1 -NouveauPrincipal 192.168.1.21 -Essai
#>
param(
    [Parameter(Mandatory)] [string]$NouveauPrincipal,   # IPv4 du serveur devenu principal à la bascule
    [string]$RoleReplication = "pharma_replication",
    [string]$Slot            = "second_serveur",
    [int]   $Port            = 5432,
    [string]$PgBin           = "",
    [string]$ServicePg       = "",
    [string]$DossierDonnees  = "",
    [string]$ServiceApi      = "PharmaOfficine API",
    [switch]$Essai
)
. (Join-Path $PSScriptRoot "commun.ps1")

if (-not [Net.IPAddress]::TryParse($NouveauPrincipal, [ref]$null)) { throw "Adresse IPv4 invalide : $NouveauPrincipal" }
if ($RoleReplication -notmatch '^[a-z_][a-z0-9_]*$' -or $Slot -notmatch '^[a-z_][a-z0-9_]*$') { throw "Nom de rôle ou de slot invalide." }
$mode = if ($Essai) { "[ESSAI : rien n'est modifié] " } else { "" }
$PgBin = Get-PgBin $PgBin

if (-not (Test-Port $NouveauPrincipal $Port)) { throw "Nouveau principal $NouveauPrincipal injoignable sur le port $Port." }
if (-not $ServicePg) { $ServicePg = (Get-Service "postgresql*" -ErrorAction SilentlyContinue | Select-Object -First 1).Name }
if (-not $DossierDonnees) {
    $chemin = (Get-CimInstance Win32_Service -Filter "Name='$ServicePg'").PathName
    if ($chemin -match '-D\s+"([^"]+)"') { $DossierDonnees = $Matches[1] } elseif ($chemin -match '-D\s+(\S+)') { $DossierDonnees = $Matches[1] }
}
if (-not $DossierDonnees) { throw "Dossier de données introuvable (préciser -DossierDonnees)." }
$ancien = "{0}_ancien_principal_{1:yyyyMMdd_HHmmss}" -f $DossierDonnees.TrimEnd('\'), (Get-Date)

if ($Essai) {
    Write-Etape $mode "Service « $ServiceApi » arrêté, démarrage manuel."
    Write-Etape $mode "Service $ServicePg arrêté ; $DossierDonnees gardé sous $ancien (dernières saisies d'avant la panne à examiner)."
    Write-Etape $mode "pg_basebackup -h $NouveauPrincipal -U $RoleReplication -S $Slot -X stream -R -D $DossierDonnees ; démarrage ; contrôle « lecture seule »."
    Write-Host "$mode Simulation terminée : rien n'a été modifié."
    return
}
if ((Read-Host "Ce PC redevient 2e serveur : sa base actuelle est mise de côté ($ancien) et remplacée par la copie de $NouveauPrincipal. Taper OUI") -ne "OUI") {
    Write-Host "Abandon : rien n'a été modifié."; return
}

if (Get-Service $ServiceApi -ErrorAction SilentlyContinue) { Stop-Service $ServiceApi -ErrorAction SilentlyContinue; Set-Service $ServiceApi -StartupType Manual }
Stop-Service $ServicePg
if (Test-Path $DossierDonnees) { Rename-Item $DossierDonnees $ancien; Write-Host "Ancienne base gardée : $ancien" }

$env:PGPASSWORD = Read-MotDePasse "Mot de passe du rôle $RoleReplication"
try {
    & (Join-Path $PgBin "pg_basebackup.exe") -h $NouveauPrincipal -p $Port -U $RoleReplication -D $DossierDonnees -S $Slot -X stream -R -P -w
    if ($LASTEXITCODE -ne 0) { throw "pg_basebackup a échoué (slot $Slot créé sur le nouveau principal ? preparer-principal.ps1). Ancienne base : $ancien." }
} finally { Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue }
$compte = (Get-CimInstance Win32_Service -Filter "Name='$ServicePg'").StartName
if ($compte -and $compte -ne "LocalSystem") { & icacls $DossierDonnees /grant "${compte}:(OI)(CI)F" /T /Q | Out-Null }
Start-Service $ServicePg
Write-Host "Ce PC est de nouveau 2e serveur (copie en continu de $NouveauPrincipal). Contrôle : verifier-second-serveur.ps1 -Principal $NouveauPrincipal."
Write-Host "Ancienne base ($ancien) : à examiner avec le support avant toute suppression."
