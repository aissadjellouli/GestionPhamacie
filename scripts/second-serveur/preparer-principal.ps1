<#
.SYNOPSIS
    2e serveur, étape 1 : prépare le serveur PRINCIPAL à envoyer sa base en continu au 2e serveur (réplication physique native).

.DESCRIPTION
    À lancer sur le serveur principal, en administrateur Windows, avec le superutilisateur PostgreSQL (docs/mise-en-production.md,
    « 2e serveur »). Étapes :
      1. rôle de réplication (LOGIN REPLICATION, mot de passe demandé, jamais écrit dans un fichier) ;
      2. postgresql.conf par ALTER SYSTEM : wal_level = replica, max_wal_senders, max_replication_slots, hot_standby = on,
         max_slot_wal_keep_size (plafond des modifications gardées pour un 2e serveur éteint), listen_addresses ;
      3. pg_hba.conf : copie datée gardée, puis deux lignes pour l'adresse du 2e serveur seulement (réplication ; compte de l'API
         pour le contrôle « jamais deux serveurs principaux » et le relevé du script de vérification) ;
      4. slot de réplication « second_serveur » (le principal garde les modifications tant que le 2e serveur ne les a pas reçues) ;
      5. rôle pg_monitor donné au compte de l'API (écran « 2e serveur » : retard de la réplication) ;
      6. rechargement de la configuration ; wal_level demande un REDÉMARRAGE de PostgreSQL (option -Redemarrer, à un moment calme).
    -Essai : n'écrit rien (ni rôle, ni fichier, ni slot, ni service) ; lit l'état actuel et affiche ce qui serait fait.
    Idempotent : relancé, il ne refait que ce qui manque.
    Le même script sert après une bascule, sur le NOUVEAU principal, pour y raccrocher l'ancien (remettre-en-second.ps1).

.EXAMPLE
    .\preparer-principal.ps1 -AdresseSecond 192.168.1.21 -AdressePrincipal 192.168.1.20 -Essai
.EXAMPLE
    .\preparer-principal.ps1 -AdresseSecond 192.168.1.21 -AdressePrincipal 192.168.1.20 -Redemarrer
#>
param(
    [Parameter(Mandatory)] [string]$AdresseSecond,      # IPv4 du 2e serveur (réseau local de l'officine)
    [Parameter(Mandatory)] [string]$AdressePrincipal,   # IPv4 de ce serveur, ajoutée à listen_addresses si besoin
    [string]$Base            = "pharmacie",
    [string]$RoleReplication = "pharma_replication",
    [string]$Slot            = "second_serveur",
    [string]$AppUser         = "pharma_app",
    [string]$SuperUser       = "postgres",
    [int]   $Port            = 5432,
    [string]$PgBin           = "",
    [string]$ServicePg       = "",                     # vide : le service postgresql-x64-<version> trouvé
    [string]$PlafondWal      = "20GB",                 # max_slot_wal_keep_size : au-delà, le 2e serveur est à réinitialiser
    [switch]$Redemarrer,
    [switch]$Essai
)
. (Join-Path $PSScriptRoot "commun.ps1")

foreach ($ip in $AdresseSecond, $AdressePrincipal) {
    if (-not [Net.IPAddress]::TryParse($ip, [ref]$null)) { throw "Adresse IPv4 invalide : $ip" }
}
foreach ($nom in $Base, $RoleReplication, $Slot, $AppUser) {
    if ($nom -notmatch '^[a-z_][a-z0-9_]*$') { throw "Nom invalide : $nom (lettres minuscules, chiffres et _)." }
}
if ($PlafondWal -notmatch '^\d+(MB|GB)$') { throw "PlafondWal invalide : $PlafondWal (ex. 20GB)." }
$mode = if ($Essai) { "[ESSAI : rien n'est modifié] " } else { "" }
$PgBin = Get-PgBin $PgBin
$mdpSuper = Read-MotDePasse "Mot de passe du superutilisateur PostgreSQL $SuperUser"
function Sql([string]$requete, [hashtable]$v = @{}) {
    Invoke-Psql -PgBin $PgBin -HostPg "localhost" -Port $Port -User $SuperUser -Db $Base -Sql $requete -MotDePasse $mdpSuper -Variables $v
}

# 0. État actuel (lecture seule)
if ((Sql "SELECT rolsuper FROM pg_roles WHERE rolname = current_user") -ne "t") { throw "$SuperUser n'est pas superutilisateur." }
if ((Sql "SELECT pg_is_in_recovery()") -eq "t") { throw "Ce serveur est en lecture seule (c'est un 2e serveur) : lancez ce script sur le serveur principal." }
$version = Sql "SHOW server_version"
$hba = Sql "SHOW hba_file"
$reglages = @{}
foreach ($l in (Sql "SELECT name || '=' || setting FROM pg_settings WHERE name IN ('wal_level','max_wal_senders','max_replication_slots','hot_standby','listen_addresses','max_slot_wal_keep_size')")) {
    $k, $v = $l -split '=', 2; $reglages[$k] = $v
}
Write-Host "PostgreSQL $version · pg_hba.conf : $hba"
$reglages.GetEnumerator() | Sort-Object Name | ForEach-Object { Write-Host ("  {0} = {1}" -f $_.Name, $_.Value) }

# 1. Rôle de réplication
$roleExiste = (Sql "SELECT count(*) FROM pg_roles WHERE rolname = :'r'" @{ r = $RoleReplication }) -eq "1"
if ($roleExiste) { Write-Host "Rôle $RoleReplication : existe." }
elseif ($Essai) { Write-Etape $mode "Création du rôle $RoleReplication (LOGIN REPLICATION, mot de passe demandé)." }
else {
    $mdpRepl = Read-MotDePasse "Mot de passe à donner au rôle $RoleReplication (à noter pour initialiser-second-serveur.ps1)"
    if ($mdpRepl.Length -lt 12) { throw "Mot de passe de réplication : 12 caractères au moins." }
    Sql "SELECT format('CREATE ROLE %I LOGIN REPLICATION PASSWORD %L', :'r', :'p') \gexec" @{ r = $RoleReplication; p = $mdpRepl } | Out-Null
    Write-Host "Rôle $RoleReplication créé."
}

# 2. postgresql.conf (ALTER SYSTEM : postgresql.auto.conf)
$ecoute = $reglages["listen_addresses"]
$nouvelleEcoute = if ($ecoute -eq "*" -or ($ecoute -split ',' | ForEach-Object { $_.Trim() }) -contains $AdressePrincipal) { $null } else { "$ecoute,$AdressePrincipal" }
$voulus = [ordered]@{ wal_level = "replica"; max_wal_senders = "10"; max_replication_slots = "10"; hot_standby = "on"; max_slot_wal_keep_size = $PlafondWal }
if ($nouvelleEcoute) { $voulus["listen_addresses"] = $nouvelleEcoute }
$redemarrageNecessaire = $false
foreach ($k in $voulus.Keys) {
    $actuel = $reglages[$k]
    $deja = switch ($k) {
        "max_slot_wal_keep_size" { $actuel -ne "-1" }   # déjà plafonné : on garde le plafond choisi
        "max_wal_senders"        { [int]$actuel -ge 10 }
        "max_replication_slots"  { [int]$actuel -ge 10 }
        default                  { $actuel -eq $voulus[$k] }
    }
    if ($deja) { continue }
    if ($k -in "wal_level", "max_wal_senders", "max_replication_slots", "listen_addresses") { $redemarrageNecessaire = $true }
    if ($Essai) { Write-Etape $mode "ALTER SYSTEM SET $k = '$($voulus[$k])' (actuel : $actuel)"; continue }
    Sql "SELECT format('ALTER SYSTEM SET %s = %L', :'k', :'v') \gexec" @{ k = $k; v = $voulus[$k] } | Out-Null
    Write-Host "$k = $($voulus[$k]) (était $actuel)"
}

# 3. pg_hba.conf : lignes pour le 2e serveur seulement
# Les deux adresses : pg_hba.conf est recopié sur le 2e serveur par pg_basebackup (s'il est dans le dossier de données, cas de
# l'installateur Windows) ; chaque serveur accepte ainsi l'autre, avant comme après une bascule (contrôle « deux principaux », retour)
$lignes = @(
    "host    replication     $RoleReplication    $AdresseSecond/32    scram-sha-256    # 2e serveur : réplication",
    "host    replication     $RoleReplication    $AdressePrincipal/32    scram-sha-256    # principal : réplication après une bascule",
    "host    $Base    $AppUser    $AdresseSecond/32    scram-sha-256    # 2e serveur : contrôle et relevé",
    "host    $Base    $AppUser    $AdressePrincipal/32    scram-sha-256    # principal : contrôle « deux principaux »"
)
$contenu = if (Test-Path $hba) { Get-Content $hba -Encoding UTF8 } else { @() }
$manquantes = $lignes | Where-Object { $l = ($_ -split '#')[0].Trim() -replace '\s+', ' '; -not ($contenu | Where-Object { ($_ -replace '\s+', ' ').Trim().StartsWith($l) }) }
if (-not $manquantes) { Write-Host "pg_hba.conf : lignes du 2e serveur déjà présentes." }
elseif ($Essai) { $manquantes | ForEach-Object { Write-Etape $mode "pg_hba.conf, ligne ajoutée : $_" } }
else {
    $copie = "$hba.avant-second-serveur-{0:yyyyMMdd-HHmmss}" -f (Get-Date)
    Copy-Item $hba $copie
    Add-Content -Path $hba -Value (@("", "# ---- 2e serveur de l'officine ({0:dd/MM/yyyy}) ----" -f (Get-Date)) + $manquantes) -Encoding UTF8
    Write-Host "pg_hba.conf complété (copie gardée : $copie)."
}

# 4. Slot de réplication
if ((Sql "SELECT count(*) FROM pg_replication_slots WHERE slot_name = :'s'" @{ s = $Slot }) -eq "1") { Write-Host "Slot $Slot : existe." }
elseif ($Essai) { Write-Etape $mode "Création du slot physique $Slot." }
else {
    if ($reglages["wal_level"] -eq "minimal") { Write-Warning "Slot créé après le redémarrage seulement (wal_level = minimal) : relancez ce script après -Redemarrer." }
    else { Sql "SELECT pg_create_physical_replication_slot(:'s', true)" @{ s = $Slot } | Out-Null; Write-Host "Slot $Slot créé." }
}

# 5. Lecture du retard par l'API (écran « 2e serveur »)
if ((Sql "SELECT pg_has_role(:'u', 'pg_monitor', 'MEMBER')" @{ u = $AppUser }) -eq "t") { Write-Host "$AppUser : membre de pg_monitor." }
elseif ($Essai) { Write-Etape $mode "GRANT pg_monitor TO $AppUser." }
else { Sql "SELECT format('GRANT pg_monitor TO %I', :'u') \gexec" @{ u = $AppUser } | Out-Null; Write-Host "pg_monitor donné à $AppUser." }

# 6. Prise en compte
if ($Essai) {
    Write-Etape $mode ("Rechargement de la configuration" + $(if ($redemarrageNecessaire) { " ; REDÉMARRAGE de PostgreSQL nécessaire (-Redemarrer)" } else { "" }) + ".")
    Write-Host "$mode Simulation terminée : rien n'a été modifié."
    return
}
Sql "SELECT pg_reload_conf()" | Out-Null
if ($redemarrageNecessaire) {
    if (-not $ServicePg) { $ServicePg = (Get-Service "postgresql*" | Select-Object -First 1).Name }
    if ($Redemarrer) {
        Write-Host "Redémarrage de $ServicePg (les postes attendent quelques secondes)..."
        Restart-Service $ServicePg
        if ((Sql "SELECT count(*) FROM pg_replication_slots WHERE slot_name = :'s'" @{ s = $Slot }) -ne "1") {
            Sql "SELECT pg_create_physical_replication_slot(:'s', true)" @{ s = $Slot } | Out-Null
        }
        Write-Host "PostgreSQL redémarré."
    } else {
        Write-Warning "Redémarrage de PostgreSQL nécessaire (wal_level, listen_addresses…) : à un moment calme, relancez avec -Redemarrer ou redémarrez le service $ServicePg."
    }
}
Write-Host "Principal prêt. Ensuite, sur le 2e serveur : initialiser-second-serveur.ps1 -Principal $AdressePrincipal."
Write-Host "Dans l'appsettings de l'API de ce serveur : « SecondServeur:AutreServeur » = $AdresseSecond (contrôle « jamais deux principaux »)."
