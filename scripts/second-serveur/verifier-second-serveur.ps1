<#
.SYNOPSIS
    2e serveur : contrôle de CE serveur (principal ou 2e serveur) et relevé pour l'écran « 2e serveur ».

.DESCRIPTION
    Lit, sans rien modifier sur ce PC :
      - le rôle de la base (principal, ou 2e serveur en lecture seule) ;
      - la réplication : pg_stat_replication (principal) ou pg_stat_wal_receiver et retard de rejeu (2e serveur) ;
      - l'API : version de Pharma.Api.exe, service (sur le 2e serveur : en démarrage automatique, l'API tourne en veille : role = veille) ;
      - la clé de scellement : empreinte (Pharma.Api.exe --empreinte-cle), jamais la clé ;
      - les imprimantes Windows déclarées, les dossiers CHIFA-Officine et MDN (-DossierChifa, -DossierMdn) joignables.
    Sans -Essai et avec -Principal : le relevé est inscrit dans la table serveur_officine du PRINCIPAL (compte de l'API, mot de passe
    demandé), pour que le titulaire le voie à l'écran ; la ligne de ce serveur est créée au premier relevé (rôle « second »).
    -Essai : n'écrit rien (affiche le relevé et la requête qui serait faite).

.EXAMPLE
    .\verifier-second-serveur.ps1 -Principal 192.168.1.20 -DossierChifa "D:\CHIFA_OFFICINE\CNAS_SIGNED" -Essai
#>
param(
    [string]$Principal      = "",                    # IPv4 du principal ; vide : pas de relevé inscrit
    [string]$Base           = "pharmacie",
    [string]$AppUser        = "pharma_app",
    [int]   $Port           = 5432,
    [string]$PgBin          = "",
    [string]$SuperUser      = "postgres",
    [string]$DossierApi     = "C:\Program Files\PharmaOfficine\Api",
    [string]$ServiceApi     = "PharmaOfficine API",
    [string]$DossierChifa   = "",
    [string]$DossierMdn     = "",
    [string]$EmpreinteJetonPrincipal = "",           # facultatif : empreinte du principal (Pharma.Api.exe --empreinte-jeton), pour contrôler la clé des jetons
    [switch]$Essai
)
. (Join-Path $PSScriptRoot "commun.ps1")

if ($Principal -and -not [Net.IPAddress]::TryParse($Principal, [ref]$null)) { throw "Adresse IPv4 invalide : $Principal" }
if ($Base -notmatch '^[a-z_][a-z0-9_]*$' -or $AppUser -notmatch '^[a-z_][a-z0-9_]*$') { throw "Nom de base ou de compte invalide." }
$mode = if ($Essai) { "[ESSAI : rien n'est modifié] " } else { "" }
$PgBin = Get-PgBin $PgBin
$nom = $env:COMPUTERNAME.Trim().ToUpperInvariant()

# Base locale (lecture seule)
$mdpSuper = Read-MotDePasse "Mot de passe du superutilisateur PostgreSQL local ($SuperUser)"
function Local([string]$sql) { Invoke-Psql -PgBin $PgBin -HostPg "localhost" -Port $Port -User $SuperUser -Db "postgres" -Sql $sql -MotDePasse $mdpSuper }
$second = (Local "SELECT pg_is_in_recovery()") -eq "t"
Write-Host ("Serveur {0} : {1}" -f $nom, $(if ($second) { "2e serveur (base en lecture seule)" } else { "principal (base en écriture)" }))
if ($second) {
    $r = Local "SELECT COALESCE((SELECT status || ' depuis ' || COALESCE(sender_host, '?') FROM pg_stat_wal_receiver), 'AUCUNE RÉCEPTION') || '|' || COALESCE(pg_wal_lsn_diff(pg_last_wal_receive_lsn(), pg_last_wal_replay_lsn())::bigint, 0) || '|' || COALESCE(round(EXTRACT(EPOCH FROM now() - pg_last_xact_replay_timestamp()))::text, '?')"
    $etat, $octets, $secondes = $r -split '\|'
    Write-Host "Réception : $etat · à rejouer : $octets octets · dernière transaction rejouée il y a $secondes s (normal si l'officine est calme)."
} else {
    $lignes = Local "SELECT COALESCE(state, '?') || ' · ' || COALESCE(host(client_addr), '?') || ' · retard ' || COALESCE(pg_wal_lsn_diff(pg_current_wal_lsn(), replay_lsn)::bigint::text, '?') || ' octets' FROM pg_stat_replication"
    if ($lignes) { $lignes | ForEach-Object { Write-Host "Réplication : $_" } } else { Write-Warning "Aucun 2e serveur connecté." }
    Local "SELECT slot_name || ' · actif : ' || active || ' · ' || COALESCE(wal_status, '?') FROM pg_replication_slots" | ForEach-Object { Write-Host "Slot : $_" }
}

# API, clé, imprimantes, dossiers
$exeApi = Join-Path $DossierApi "Pharma.Api.exe"
$version = $null; $empreinte = $null
if (Test-Path $exeApi) {
    $fv = (Get-Item $exeApi).VersionInfo
    $version = if ($fv.FileBuildPart -gt 0) { "{0}.{1:00}.{2}" -f $fv.FileMajorPart, $fv.FileMinorPart, $fv.FileBuildPart } else { "{0}.{1:00}" -f $fv.FileMajorPart, $fv.FileMinorPart }
    $sortie = & $exeApi --empreinte-cle 2>$null
    if ($LASTEXITCODE -eq 0 -and "$sortie" -match '([0-9a-f]{16})') { $empreinte = $Matches[1] }
}
$service = Get-Service $ServiceApi -ErrorAction SilentlyContinue
$demarrage = if ($service) { (Get-CimInstance Win32_Service -Filter "Name='$ServiceApi'").StartMode } else { "absent" }
Write-Host ("API : version {0} · service {1}, démarrage {2}" -f $(if ($version) { $version } else { "introuvable" }), $(if ($service) { $service.Status } else { "absent" }), $demarrage)
if ($second -and $service) {
    # Vague 2 : le 2e serveur fait tourner l'API en veille (démarrage automatique) ; son rôle publié doit être « veille »
    $roleApi = $null
    foreach ($url in "https://localhost:5443/api/healthcheck", "http://localhost:5080/api/healthcheck") { $roleApi = Get-RoleApi $url; if ($roleApi) { break } }
    Write-Host ("API en veille : rôle publié {0}" -f $(if ($roleApi) { $roleApi } else { "aucune réponse (service arrêté, ou SecondServeur:AutreServeur non réglé)" }))
    if ($demarrage -ne "Auto") { Write-Warning "2e serveur : le service de l'API devrait être en démarrage automatique (initialiser-second-serveur.ps1 le règle)." }
    if ($roleApi -and $roleApi -ne "veille") { Write-Warning "2e serveur : le rôle publié devrait être « veille »." }
}
Write-Host ("Clé de scellement : {0}" -f $(if ($empreinte) { "présente, empreinte $empreinte (à comparer à celle du principal, écran « 2e serveur »)" } else { "ABSENTE ou illisible" }))
$cleJetons = [Environment]::GetEnvironmentVariable("Jwt__Cle", "Machine")
if (-not $cleJetons) { Write-Warning "Clé des jetons (Jwt__Cle) absente sur ce PC." }
else {
    $empJetons = Get-EmpreinteJeton $cleJetons
    Write-Host "Clé des jetons : empreinte $empJetons (doit être identique sur les deux serveurs)."
    if ($EmpreinteJetonPrincipal -and $EmpreinteJetonPrincipal -ne $empJetons) { Write-Warning "Clé des jetons DIFFÉRENTE de celle du principal ($EmpreinteJetonPrincipal) : bascule sans reconnexion, retrait et relais impossibles." }
}
$cleJetons = $null
$imprimantes = (Get-Printer -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch 'PDF|XPS|OneNote|Fax' } | Select-Object -ExpandProperty Name) -join ", "
Write-Host ("Imprimantes : {0}" -f $(if ($imprimantes) { $imprimantes } else { "aucune" }))
function Joignable([string]$d) { if (-not $d) { return "" } if (Test-Path $d -PathType Container) { return "true" } return "false" }
$chifaOk = Joignable $DossierChifa; $mdnOk = Joignable $DossierMdn
if ($DossierChifa) { Write-Host "Dossier CHIFA-Officine $DossierChifa : $(if ($chifaOk -eq 'true') { 'joignable' } else { 'INJOIGNABLE' })" }
if ($DossierMdn) { Write-Host "Dossier MDN $DossierMdn : $(if ($mdnOk -eq 'true') { 'joignable' } else { 'INJOIGNABLE' })" }

# Relevé inscrit sur le principal (requête paramétrée : variables psql)
if (-not $Principal) { return }
$sql = @"
INSERT INTO serveur_officine (nom, role, version_api, cle_presente, empreinte_cle, imprimantes, dossier_chifa_joignable, dossier_mdn_joignable, releve_le)
VALUES (:'nom', CASE WHEN :'second' = 'true' THEN 'second' ELSE 'principal' END, NULLIF(:'version', ''), :'cle'::boolean, NULLIF(:'empreinte', ''),
        NULLIF(:'imprimantes', ''), NULLIF(:'chifa', '')::boolean, NULLIF(:'mdn', '')::boolean, now())
ON CONFLICT (nom) DO UPDATE SET version_api = EXCLUDED.version_api, cle_presente = EXCLUDED.cle_presente, empreinte_cle = EXCLUDED.empreinte_cle,
    imprimantes = EXCLUDED.imprimantes, dossier_chifa_joignable = COALESCE(EXCLUDED.dossier_chifa_joignable, serveur_officine.dossier_chifa_joignable),
    dossier_mdn_joignable = COALESCE(EXCLUDED.dossier_mdn_joignable, serveur_officine.dossier_mdn_joignable), releve_le = now(),
    role = CASE WHEN :'second' = 'true' THEN 'second' ELSE serveur_officine.role END;
"@
$variables = @{ nom = $nom; second = "$($second.ToString().ToLowerInvariant())"; version = "$version"; cle = $(if ($empreinte) { "true" } else { "false" });
                empreinte = "$empreinte"; imprimantes = $imprimantes; chifa = $chifaOk; mdn = $mdnOk }
if ($Essai) {
    Write-Etape $mode "Relevé inscrit sur le principal $Principal (base $Base, compte $AppUser) :"
    $variables.GetEnumerator() | Sort-Object Name | ForEach-Object { Write-Host ("    {0} = {1}" -f $_.Name, $_.Value) }
    Write-Host "$mode Simulation terminée : rien n'a été écrit."
    return
}
$mdpApp = Read-MotDePasse "Mot de passe du compte $AppUser (base $Base du principal)"
Invoke-Psql -PgBin $PgBin -HostPg $Principal -Port $Port -User $AppUser -Db $Base -Sql $sql -MotDePasse $mdpApp -Variables $variables | Out-Null
Write-Host "Relevé inscrit sur le principal : visible dans Administration › 2e serveur."
