<#
.SYNOPSIS
    Bascule sur le 2e serveur : CE PC (2e serveur) devient le serveur principal. Décidée par une personne, jamais automatique.

.DESCRIPTION
    À lancer sur le 2e serveur, en administrateur Windows, à la demande du titulaire (docs/mise-en-production.md, « 2e serveur »,
    « Bascule »). Pourquoi un script lancé devant le 2e serveur plutôt qu'un bouton à distance : la bascule promeut la base et
    démarre un service Windows (droits administrateur) ; aucun service réseau n'a donc à porter ces droits, et la personne qui
    bascule vérifie sur place que l'ancien principal est vraiment arrêté (deux serveurs qui écrivent = données incohérentes).
    Étapes :
      1. contrôles : base de CE PC en lecture seule (2e serveur) ; l'ancien principal NE RÉPOND PLUS (ni PostgreSQL ni l'API) —
         sinon refus : arrêtez son API et PostgreSQL, ou débranchez-le du réseau ;
      2. dernière transaction reçue (les ventes postérieures, non reçues, seraient à ressaisir) ; confirmation tapée ;
      3. promotion de la base : pg_promote() ;
      4. API de ce PC en démarrage automatique et démarrée ; contrôle de /api/sante ;
      5. consignes : chaque poste bascule par le bouton « Basculer sur le 2e serveur » du bandeau « Serveur injoignable »
         (adresse AdresseApiSecours) ; l'ancien principal ne doit PAS redémarrer son API : à son retour, remettre-en-second.ps1.
    -Essai : ne promeut rien, ne démarre rien ; fait les contrôles et affiche ce qui serait fait.

.EXAMPLE
    .\basculer-second-serveur.ps1 -AncienPrincipal 192.168.1.20 -Essai
#>
param(
    [Parameter(Mandatory)] [string]$AncienPrincipal,    # IPv4 du serveur principal en panne
    [int]   $Port        = 5432,
    [int]   $PortApi     = 5080,
    [string]$PgBin       = "",
    [string]$SuperUser   = "postgres",
    [string]$ServiceApi  = "PharmaOfficine API",
    [string]$Schema      = "http",                      # http ou https, comme l'installation de l'API
    [switch]$Essai
)
. (Join-Path $PSScriptRoot "commun.ps1")

if (-not [Net.IPAddress]::TryParse($AncienPrincipal, [ref]$null)) { throw "Adresse IPv4 invalide : $AncienPrincipal" }
if ($Schema -notin "http", "https") { throw "Schema : http ou https." }
$mode = if ($Essai) { "[ESSAI : rien n'est modifié] " } else { "" }
$PgBin = Get-PgBin $PgBin
$mdpSuper = Read-MotDePasse "Mot de passe du superutilisateur PostgreSQL local ($SuperUser)"
function Local([string]$sql) { Invoke-Psql -PgBin $PgBin -HostPg "localhost" -Port $Port -User $SuperUser -Db "postgres" -Sql $sql -MotDePasse $mdpSuper }

# 1. Contrôles
if ((Local "SELECT pg_is_in_recovery()") -ne "t") { throw "La base de ce PC n'est pas en lecture seule : ce PC est déjà le serveur principal (rien à basculer)." }
$pgRepond = Test-Port $AncienPrincipal $Port
$apiRepond = Test-Port $AncienPrincipal $PortApi
if ($pgRepond -or $apiRepond) {
    throw ("L'ancien principal $AncienPrincipal répond encore ({0}). Bascule refusée : deux serveurs principaux rendraient les données " +
           "incohérentes. Sur l'ancien principal, arrêtez le service de l'API et PostgreSQL (ou débranchez son câble réseau), puis relancez.") -f
          (@($(if ($pgRepond) { "PostgreSQL, port $Port" }), $(if ($apiRepond) { "API, port $PortApi" })) -ne $null -join " ; ")
}
Write-Host "Ancien principal $AncienPrincipal : ne répond plus (PostgreSQL et API)."

# 2. Dernière transaction reçue
$derniere = Local "SELECT COALESCE(to_char(pg_last_xact_replay_timestamp() AT TIME ZONE 'Africa/Algiers', 'DD/MM/YYYY HH24:MI:SS'), 'inconnue')"
Write-Host "Dernière transaction reçue du principal : $derniere. Ce qui a été saisi après (s'il y en a) n'est pas sur ce serveur : à ressaisir."

$adresses = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike "127.*" -and $_.IPAddress -notlike "169.254.*" } | Select-Object -ExpandProperty IPAddress
if ($Essai) {
    Write-Etape $mode "Promotion de la base de ce PC : SELECT pg_promote(true, 60)."
    Write-Etape $mode "Service « $ServiceApi » : démarrage automatique, démarré ; contrôle de /api/sante."
    foreach ($ip in $adresses) { Write-Etape $mode "Adresse de l'API pour les postes (AdresseApiSecours) : ${Schema}://${ip}:$PortApi/" }
    Write-Host "$mode Simulation terminée : rien n'a été modifié."
    return
}
if ((Read-Host "Basculer : ce PC devient le serveur principal. Taper BASCULER pour confirmer") -ne "BASCULER") { Write-Host "Abandon : rien n'a été modifié."; return }

# 3. Promotion
if ((Local "SELECT pg_promote(true, 60)") -ne "t") { throw "Promotion non confirmée dans les 60 s : voir le journal de PostgreSQL." }
if ((Local "SELECT pg_is_in_recovery()") -ne "f") { throw "La base est encore en lecture seule après la promotion." }
Write-Host "Base promue : ce PC est le serveur principal."

# 4. API
Set-Service $ServiceApi -StartupType Automatic
Start-Service $ServiceApi
$ok = $false
foreach ($i in 1..20) {
    Start-Sleep -Seconds 3
    try { $ok = (Invoke-WebRequest "${Schema}://localhost:$PortApi/api/sante" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200 } catch { $ok = $false }
    if ($ok) { break }
}
if (-not $ok) { Write-Warning "L'API ne répond pas encore : voir le journal de l'API (section « Journaux » de son appsettings) et le message de refus éventuel." }
else { Write-Host "API démarrée et joignable." }

# 5. Consignes
foreach ($ip in $adresses) { Write-Host "Postes : bouton « Basculer sur le 2e serveur » du bandeau (adresse ${Schema}://${ip}:$PortApi/), puis rouvrir Pharma Officine." }
Write-Host "Titulaire : Administration › 2e serveur (dossiers CHIFA-Officine et MDN de ce serveur, clé identique), et CHIFA-Officine sur ce PC."
Write-Host "Ancien principal : NE PAS redémarrer son API. À son retour : scripts\second-serveur\remettre-en-second.ps1 (il redevient 2e serveur)."
