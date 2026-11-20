<#
.SYNOPSIS
    OUTIL DE DEVELOPPEMENT, JAMAIS DE PRODUCTION. Prépare ce PC pour lancer, depuis Visual Studio et en débogage, l'API locale,
    le poste ET le serveur central (profil « API + Poste + Central » de PharmaOfficine.slnLaunch).
    Voir docs\poste-de-developpement.md, « Lancer API + Poste + Central dans Visual Studio ».

.DESCRIPTION
    A NE JAMAIS UTILISER EN PRODUCTION. Bases fixes, jamais en paramètre : officine pharmacie_dev, serveur central pharmacie_cloud_dev.
    Toute autre base est refusée. Aucune base n'est créée, supprimée ni restaurée ; aucun compte utilisateur de l'application n'est créé.

    -Preparer  (idempotent : le relancer reprend ce qui manque et réutilise ce qui existe)
        1. contrôles : psql, dotnet, droits de pharma_app (pas superutilisateur, CREATEROLE, option ADMIN sur pharma_cloud_api),
           base pharmacie_cloud_dev présente et à jour (témoin du script central 028), port 5090 libre ;
        2. dossier %LOCALAPPDATA%\PharmaOfficine\central-dev (hors dépôt) ; sous-dossier « autorite » et dossier de synchronisation
           de l'officine (sync-pharmacie_dev) réservés au compte courant, à SYSTEM et aux Administrateurs (icacls) ;
        3. mots de passe tirés au sort (autorité : 32 caractères ; rôle : 32 caractères), RÉUTILISÉS s'ils existent déjà dans les
           variables d'utilisateur ; jamais écrits ailleurs que dans ces variables, jamais affichés ;
        4. rôle de connexion pharma_cloud_dev_vs (LOGIN, membre de pharma_cloud_api : non propriétaire, cloisonnement appliqué) ;
        5. variables d'environnement de l'UTILISATEUR Windows (lues par Visual Studio après son redémarrage) :
              PHARMA_CLOUD_DOSSIER, PHARMA_CLOUD_MOT_DE_PASSE, PHARMA_CLOUD_CONNEXION (rôle pharma_cloud_dev_vs, base pharmacie_cloud_dev),
              ConnectionStrings__Pharmacie (pharmacie_dev), Synchronisation__Dossier (dossier propre à pharmacie_dev).
           PHARMA_CLOUD_ADMIN_CONNEXION (propriétaire) n'est JAMAIS posée au niveau utilisateur : seulement passée au processus de l'outil ;
        6. outil d'administration du central compilé HORS du dépôt (%LOCALAPPDATA%\PharmaOfficine\central-dev\outil) : Visual Studio
           verrouille les dossiers bin du dépôt ;
        7. autorité et certificat HTTPS du central (localhost, 127.0.0.1) s'ils manquent ;
        8. officine « Développement (pharmacie_dev) » créée au central si elle manque (son code provisoire va dans etat.json, sans
           secret), sinon nouveau code. Le code d'activation (usage unique, 24 h) est AFFICHÉ, jamais écrit.

    -Activer   Visual Studio lancé (profil « API + Poste + Central ») : vérifie l'API (5080, /api/healthcheck) et le central (5090),
               affiche la marche à suivre à l'écran Administration > Synchronisation et un code neuf si besoin, puis l'état.
    -Etat      lecture seule : variables (noms seulement), dossiers, rôle, officine au central, synchronisation de pharmacie_dev, ports.
    -Retirer   retire les variables d'utilisateur, le rôle pharma_cloud_dev_vs et le dossier %LOCALAPPDATA%\PharmaOfficine\central-dev.
               Rien n'est supprimé dans les bases (l'officine reste au central, la synchronisation de pharmacie_dev reste réglée).
               A lancer d'abord avec -WhatIf ; refusé tant que le central écoute sur 5090.

    Mot de passe de pharma_app : variable PGPASSWORD déjà définie, ou pgpass.conf, ou saisie masquée (docs\poste-de-developpement.md).
    Il entre dans ConnectionStrings__Pharmacie (variable d'utilisateur, hors dépôt), comme dans appsettings.Development.json.

.PARAMETER Preparer
    Prépare (ou complète) le poste.

.PARAMETER Activer
    Aide à l'activation par l'écran, Visual Studio lancé.

.PARAMETER Etat
    Affiche l'état, sans rien modifier.

.PARAMETER Retirer
    Retire variables, rôle et dossier (à lancer d'abord avec -WhatIf).

.PARAMETER Recompiler
    Avec -Preparer : recompile l'outil d'administration même s'il existe.

.EXAMPLE
    .\scripts\dev\preparer_central_vs.ps1 -Preparer -WhatIf
    Montre ce qui serait fait, sans rien créer.

.EXAMPLE
    .\scripts\dev\preparer_central_vs.ps1 -Preparer
    (redémarrer Visual Studio, profil « API + Poste + Central », F5)
    .\scripts\dev\preparer_central_vs.ps1 -Activer
    .\scripts\dev\preparer_central_vs.ps1 -Etat

.NOTES
    Si PowerShell refuse le script : powershell -ExecutionPolicy Bypass -File .\scripts\dev\preparer_central_vs.ps1 -Preparer
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium', PositionalBinding = $false)]
param(
    [switch]$Preparer,
    [switch]$Activer,
    [switch]$Etat,
    [switch]$Retirer,
    [switch]$Recompiler
)

$ErrorActionPreference = "Stop"
. (Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) "mep") "outils_mep.ps1")
$Depot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

# --- Noms fixes : jamais en paramètre ---
$CompteBase = "pharma_app"
$BaseMaintenance = "postgres"                 # base de connexion pour lire pg_roles et gérer le rôle (ce n'est pas le compte postgres)
$BaseCentrale = "pharmacie_cloud_dev"
$BaseOfficine = "pharmacie_dev"
$RoleCentral = "pharma_cloud_dev_vs"
$RoleGroupe = "pharma_cloud_api"
$LibelleOfficine = "Développement (pharmacie_dev)"
$PortApi = 5080                               # lu seulement (santé), jamais pris
$PortCentral = 5090
$AdresseLocale = "127.0.0.1"
$PortPostgres = 5432
$Racine = Join-Path $env:LOCALAPPDATA "PharmaOfficine\central-dev"
$DossierAutorite = Join-Path $Racine "autorite"
$DossierSync = Join-Path $Racine "sync-pharmacie_dev"
$Outil = Join-Path $Racine "outil"
$ExeCentral = Join-Path $Outil "bin\Pharma.Cloud.Api\debug\Pharma.Cloud.Api.exe"
$FichierEtat = Join-Path $Racine "etat.json"
$VariablesUtilisateur = @("PHARMA_CLOUD_DOSSIER", "PHARMA_CLOUD_MOT_DE_PASSE", "PHARMA_CLOUD_CONNEXION", "ConnectionStrings__Pharmacie", "Synchronisation__Dossier")
$VariableInterdite = "PHARMA_CLOUD_ADMIN_CONNEXION"

function Write-Etape2([string]$t) { Write-Host ""; Write-Host "== $t" -ForegroundColor Cyan }

# ------------------------------------------------------------------ garde-fous
function Assert-BaseAutorisee([string]$Base, [ValidateSet("central", "officine")][string]$Cote) {
    if ($Cote -eq "central" -and $Base -cne "pharmacie_cloud_dev") { throw "Refusé : côté serveur central, seule la base pharmacie_cloud_dev est permise (demandé : « $Base »)." }
    if ($Cote -eq "officine" -and $Base -cne "pharmacie_dev") { throw "Refusé : côté officine, seule la base pharmacie_dev est permise (demandé : « $Base »)." }
}

function Assert-DossierRacine {
    $attendu = Join-Path $env:LOCALAPPDATA "PharmaOfficine\central-dev"
    if (-not $env:LOCALAPPDATA -or $Racine -ne $attendu -or (Split-Path $Racine -Leaf) -ne "central-dev") { throw "Refusé : dossier inattendu ($Racine)." }
    if ($Racine.StartsWith($Depot, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusé : le dossier de travail ne doit pas être dans le dépôt." }
}

# ------------------------------------------------------------------ outils
function Test-PortOuvert([int]$Port) {
    $c = New-Object Net.Sockets.TcpClient
    try { $a = $c.BeginConnect($AdresseLocale, $Port, $null, $null); return ($a.AsyncWaitHandle.WaitOne(500) -and $c.Connected) }
    catch { return $false }
    finally { $c.Close() }
}

function Test-PortLibre([int]$Port) {
    $ecoutes = [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
    return -not ($ecoutes | Where-Object { $_.Port -eq $Port })
}

function New-MotDePasseAleatoire([int]$Longueur = 32) {
    $alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray()
    $rng = New-Object Security.Cryptography.RNGCryptoServiceProvider
    $o = New-Object 'byte[]' $Longueur
    $rng.GetBytes($o)
    return -join ($o | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
}

function Get-VariableUtilisateur([string]$Nom) { return [Environment]::GetEnvironmentVariable($Nom, "User") }

# Pose une variable d'utilisateur. Déjà la même valeur : rien. Autre valeur : arrêt (jamais d'écrasement d'un réglage inconnu).
function Set-VariableUtilisateur([string]$Nom, [string]$Valeur) {
    if ($VariablesUtilisateur -notcontains $Nom) { throw "Refusé : variable « $Nom » hors de la liste de ce script." }
    $actuelle = Get-VariableUtilisateur $Nom
    if ($actuelle -ceq $Valeur) { Write-Ok "Variable d'utilisateur $Nom déjà en place."; return }
    if ($actuelle) { throw "La variable d'utilisateur $Nom existe déjà avec une autre valeur : le script ne l'écrase pas. La corriger ou la retirer soi-même (ou -Retirer), puis relancer -Preparer." }
    [Environment]::SetEnvironmentVariable($Nom, $Valeur, "User")
    Write-Ok "Variable d'utilisateur $Nom posée."
}

function Lire-Etat {
    if (-not (Test-Path -LiteralPath $FichierEtat)) { return [pscustomobject]@{ OfficineProvisoire = ""; Libelle = $LibelleOfficine; Base = $BaseOfficine } }
    return (Get-Content -LiteralPath $FichierEtat -Raw -Encoding UTF8 | ConvertFrom-Json)
}

# etat.json : code provisoire de l'officine au central, libellé, base. Aucun secret.
function Enregistrer-Etat($e) { $e | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $FichierEtat -Encoding UTF8 }

function Get-MotDePassePg {
    if ($script:MotsDePasse.ContainsKey($CompteBase)) { return $script:MotsDePasse[$CompteBase] }
    return $env:PGPASSWORD
}

function Get-ChaineConnexion([string]$Base, [string]$Compte, [string]$Mdp) {
    $c = "Host=localhost;Port=$PortPostgres;Database=$Base;Username=$Compte"
    if ($Mdp) { $c += ";Password=$Mdp" }
    return $c
}

function Get-MotDePasseDeChaine([string]$Chaine) {
    if (-not $Chaine) { return $null }
    $m = [regex]::Match($Chaine, '(?i)(?:^|;)\s*Password=([^;]*)')
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

function Get-BaseDeChaine([string]$Chaine) {
    if (-not $Chaine) { return $null }
    $m = [regex]::Match($Chaine, '(?i)(?:^|;)\s*Database=([^;]*)')
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

# Outil d'administration du central, lancé avec des variables valables pour ce processus enfant seulement
function Invoke-Outil {
    param([string[]]$Arguments, [hashtable]$Variables)
    $anciennes = @{}
    foreach ($k in $Variables.Keys) { $anciennes[$k] = [Environment]::GetEnvironmentVariable($k, "Process"); [Environment]::SetEnvironmentVariable($k, $Variables[$k], "Process") }
    $ea = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        Push-Location (Split-Path $ExeCentral -Parent)
        $sortie = & $ExeCentral @Arguments 2>&1
        $code = $LASTEXITCODE
        Pop-Location
    }
    finally { $ErrorActionPreference = $ea; foreach ($k in $anciennes.Keys) { [Environment]::SetEnvironmentVariable($k, $anciennes[$k], "Process") } }
    $lignes = @($sortie | ForEach-Object { "$_" })
    return [pscustomobject]@{ Code = $code; Lignes = $lignes; Texte = ($lignes -join "`n") }
}

function Get-VariablesOutil([string]$MotDePasseAutorite) {
    Assert-BaseAutorisee $BaseCentrale "central"
    return @{
        PHARMA_CLOUD_DOSSIER = $DossierAutorite
        PHARMA_CLOUD_MOT_DE_PASSE = $MotDePasseAutorite
        PHARMA_CLOUD_ADMIN_CONNEXION = (Get-ChaineConnexion $BaseCentrale $CompteBase (Get-MotDePassePg))
    }
}

function Get-ScalaireCentral([string]$Sql) { Assert-BaseAutorisee $BaseCentrale "central"; return Get-Scalaire -Compte $CompteBase -Base $BaseCentrale -Sql $Sql }
function Get-ScalaireOfficine([string]$Sql) { Assert-BaseAutorisee $BaseOfficine "officine"; return Get-Scalaire -Compte $CompteBase -Base $BaseOfficine -Sql $Sql }

function Test-Guid([string]$Texte) { $g = [guid]::Empty; return [guid]::TryParse($Texte, [ref]$g) }

# Réservé au compte courant, à SYSTEM (S-1-5-18) et aux Administrateurs (S-1-5-32-544) ; héritage coupé
function Set-DossierRestreint([string]$Dossier) {
    if (-not (Test-Path -LiteralPath $Dossier)) { [void](New-Item -ItemType Directory -Path $Dossier) }
    $moi = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $sortie = & icacls.exe $Dossier /inheritance:r /grant:r "*${moi}:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "icacls en échec sur $Dossier :`n$(@($sortie) -join "`n")" }
    Write-Ok "Dossier $Dossier réservé au compte courant, à SYSTEM et aux Administrateurs."
}

function Initialize-Pg([bool]$Simulation) {
    # Lectures seulement ici : -WhatIf ne doit pas transformer en simulation la remise en ordre des variables du processus (PGOPTIONS)
    $WhatIfPreference = $false
    Initialize-OutilsMep -PgBin "" -Hote "localhost" -Port $PortPostgres -Simulation $Simulation
    $possible = Request-MotDePasse -Compte $CompteBase -Invite "Mot de passe PostgreSQL de pharma_app (compte de développement, voir docs\poste-de-developpement.md)"
    if (-not $possible) { return $false }
    $r = Invoke-Psql -Compte $CompteBase -Base $BaseMaintenance -Arguments @("-tAc", "SELECT rolsuper::text || '|' || rolcreaterole::text FROM pg_roles WHERE rolname = current_user") -Tolerant
    if ($r.Code -ne 0) { throw "Connexion à PostgreSQL impossible avec pharma_app (localhost:$PortPostgres). Vérifier le mot de passe et que le service tourne.`n$($r.Texte)" }
    $d = ($r.Lignes | Where-Object { $_ }) -join ""
    if ($d -match "^true") { throw "Le compte de connexion est un superutilisateur : ce script refuse de s'en servir (utiliser pharma_app)." }
    if ($d -notmatch "\|true$") { throw "Le compte pharma_app n'a pas le droit CREATEROLE. Le script ne s'élève pas lui-même." }
    $admin = Get-Scalaire -Compte $CompteBase -Base $BaseMaintenance -Sql "SELECT count(*) FROM pg_auth_members am JOIN pg_roles r ON r.oid = am.roleid JOIN pg_roles m ON m.oid = am.member WHERE r.rolname = '$RoleGroupe' AND m.rolname = current_user AND am.admin_option"
    if ($admin -eq "0") { throw "pharma_app n'a pas l'option ADMIN sur $RoleGroupe (GRANT $RoleGroupe TO pharma_app WITH ADMIN OPTION, par l'administrateur PostgreSQL). Le script ne s'élève pas lui-même." }
    Write-Ok "pharma_app : pas superutilisateur, CREATEROLE, option ADMIN sur $RoleGroupe."
    $existe = Get-Scalaire -Compte $CompteBase -Base $BaseMaintenance -Sql "SELECT count(*) FROM pg_database WHERE datname = '$BaseCentrale'"
    if ($existe -eq "0") { throw "La base $BaseCentrale n'existe pas : ce script ne la crée pas (voir database\cloud\README.md)." }
    $temoin = Get-ScalaireCentral "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'ligne_remplacee' AND column_name = 'colonnes_effacees'"
    if ($temoin -ne "1") { throw "La base $BaseCentrale n'est pas à jour (témoin du script central 028 absent) : passer les scripts database\cloud d'abord." }
    Write-Ok "Base $BaseCentrale présente et à jour (témoin 028)."
    $existe = Get-Scalaire -Compte $CompteBase -Base $BaseMaintenance -Sql "SELECT count(*) FROM pg_database WHERE datname = '$BaseOfficine'"
    if ($existe -eq "0") { throw "La base $BaseOfficine n'existe pas." }
    Write-Ok "Base $BaseOfficine présente."
    return $true
}

# ------------------------------------------------------------------ -Preparer
function Invoke-Preparer {
    $simul = [bool]$WhatIfPreference
    Assert-DossierRacine
    Assert-BaseAutorisee $BaseCentrale "central"
    Assert-BaseAutorisee $BaseOfficine "officine"
    Write-Etape2 "Contrôles"
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "dotnet introuvable (installer le SDK .NET)." }
    Write-Ok "dotnet $(& dotnet --version)"
    if (Get-VariableUtilisateur $VariableInterdite) { throw "La variable d'utilisateur $VariableInterdite existe : elle ne doit jamais être posée au niveau utilisateur (connexion du propriétaire). La retirer soi-même, puis relancer." }
    if (-not (Test-PortLibre $PortCentral)) { throw "Le port $PortCentral est déjà occupé (serveur central lancé ?). Arrêter le débogage dans Visual Studio, puis relancer -Preparer." }
    Write-Ok "Port $PortCentral libre."
    $pg = Initialize-Pg $simul
    if (-not $pg) { Write-Avert "Simulation sans mot de passe de pharma_app (PGPASSWORD absent) : contrôles PostgreSQL non faits." }
    foreach ($n in $VariablesUtilisateur) {
        Write-Info ("Variable d'utilisateur {0} : {1}" -f $n, $(if (Get-VariableUtilisateur $n) { "présente (réutilisée si identique)" } else { "absente" }))
    }

    if ($simul) {
        Write-Etape2 "Plan (simulation, rien n'est créé)"
        Write-Simul "dossier $Racine ; $DossierAutorite et $DossierSync réservés (icacls : compte courant, SYSTEM, Administrateurs)"
        Write-Simul "mots de passe de l'autorité et du rôle tirés au sort, ou réutilisés depuis les variables d'utilisateur ; jamais affichés"
        Write-Simul "rôle $RoleCentral créé ou mis à jour (connexion, mot de passe), puis rendu membre de $RoleGroupe"
        Write-Simul "variables d'utilisateur : $($VariablesUtilisateur -join ', ') ($VariableInterdite jamais au niveau utilisateur)"
        Write-Simul "   ConnectionStrings__Pharmacie -> base $BaseOfficine (compte $CompteBase) ; Synchronisation__Dossier -> $DossierSync"
        Write-Simul "   PHARMA_CLOUD_CONNEXION -> base $BaseCentrale (rôle $RoleCentral) ; PHARMA_CLOUD_DOSSIER -> $DossierAutorite"
        Write-Simul "outil d'administration compilé dans $Outil (hors des dossiers bin du dépôt) s'il manque"
        Write-Simul "autorité et certificat HTTPS (localhost, $AdresseLocale) dans $DossierAutorite s'ils manquent"
        Write-Simul "officine « $LibelleOfficine » créée au central si elle manque (code provisoire dans $FichierEtat), sinon nouveau code ; code affiché, jamais écrit"
        Write-Simul "rien n'est créé, supprimé ni restauré dans $BaseOfficine ni $BaseCentrale (hors l'officine et son code au central)"
        return
    }

    Write-Etape2 "Dossiers (hors dépôt)"
    foreach ($d in @($Racine, $Outil)) { if (-not (Test-Path -LiteralPath $d)) { [void](New-Item -ItemType Directory -Path $d) } }
    Set-DossierRestreint $DossierAutorite
    Set-DossierRestreint $DossierSync

    Write-Etape2 "Mots de passe (tirés au sort ou réutilisés)"
    $mdpAutorite = Get-VariableUtilisateur "PHARMA_CLOUD_MOT_DE_PASSE"
    $autoriteExiste = Test-Path -LiteralPath (Join-Path $DossierAutorite "autorite.crt")
    if ($mdpAutorite) { Write-Ok "Mot de passe de l'autorité réutilisé (variable d'utilisateur)." }
    elseif ($autoriteExiste) { throw "L'autorité existe dans $DossierAutorite mais son mot de passe (PHARMA_CLOUD_MOT_DE_PASSE) a disparu : impossible de la réutiliser. Lancer -Retirer, puis -Preparer." }
    else { $mdpAutorite = New-MotDePasseAleatoire 32; Write-Ok "Mot de passe de l'autorité tiré au sort (32 caractères)." }
    if ($mdpAutorite.Length -lt 24) { throw "Mot de passe de l'autorité trop court (24 caractères au moins)." }
    $cxActuelle = Get-VariableUtilisateur "PHARMA_CLOUD_CONNEXION"
    $mdpRole = Get-MotDePasseDeChaine $cxActuelle
    if ($mdpRole) { Write-Ok "Mot de passe du rôle réutilisé (variable d'utilisateur)." }
    else { $mdpRole = New-MotDePasseAleatoire 32; Write-Ok "Mot de passe du rôle tiré au sort (32 caractères)." }

    Write-Etape2 "Rôle $RoleCentral (membre de $RoleGroupe)"
    $existe = (Get-Scalaire -Compte $CompteBase -Base $BaseMaintenance -Sql "SELECT count(*) FROM pg_roles WHERE rolname = '$RoleCentral'") -ne "0"
    $sql = if ($existe) { "ALTER ROLE $RoleCentral LOGIN PASSWORD '$mdpRole';" } else { "CREATE ROLE $RoleCentral LOGIN PASSWORD '$mdpRole';" }
    $r = Invoke-Psql -Compte $CompteBase -Base $BaseMaintenance -Arguments @("-f", "-") -Entree $sql -Tolerant
    if ($r.Code -ne 0) { throw "Création du rôle $RoleCentral impossible.`n$($r.Texte -replace [regex]::Escape($mdpRole), '***')" }
    $r = Invoke-Psql -Compte $CompteBase -Base $BaseMaintenance -Arguments @("-c", "GRANT $RoleGroupe TO $RoleCentral") -Tolerant
    if ($r.Code -ne 0) { throw "GRANT $RoleGroupe TO $RoleCentral refusé.`n$($r.Texte)" }
    # Contrôle : le rôle se connecte à la base centrale (mot de passe passé à psql par PGPASSWORD, ce processus seulement)
    Set-MotDePasseMemoire $RoleCentral $mdpRole
    $r = Invoke-Psql -Compte $RoleCentral -Base $BaseCentrale -Arguments @("-tAc", "SELECT current_user") -Tolerant
    if ($r.Code -ne 0) { throw "Le rôle $RoleCentral ne se connecte pas à $BaseCentrale.`n$($r.Texte)" }
    Write-Ok "Rôle $RoleCentral prêt ($(if ($existe) { 'mis à jour' } else { 'créé' })), connexion à $BaseCentrale vérifiée."

    Write-Etape2 "Variables d'utilisateur Windows"
    Set-VariableUtilisateur "PHARMA_CLOUD_DOSSIER" $DossierAutorite
    Set-VariableUtilisateur "PHARMA_CLOUD_MOT_DE_PASSE" $mdpAutorite
    Set-VariableUtilisateur "PHARMA_CLOUD_CONNEXION" (Get-ChaineConnexion $BaseCentrale $RoleCentral $mdpRole)
    $cxOfficine = Get-ChaineConnexion $BaseOfficine $CompteBase (Get-MotDePassePg)
    if ((Get-BaseDeChaine $cxOfficine) -cne "pharmacie_dev") { throw "Refusé : ConnectionStrings__Pharmacie doit viser pharmacie_dev." }
    Set-VariableUtilisateur "ConnectionStrings__Pharmacie" $cxOfficine
    Set-VariableUtilisateur "Synchronisation__Dossier" $DossierSync

    Write-Etape2 "Outil d'administration du central (dans $Outil)"
    if ((Test-Path -LiteralPath $ExeCentral) -and -not $Recompiler) { Write-Ok "Déjà compilé (-Recompiler pour refaire)." }
    else {
        Write-Info "dotnet build Pharma.Cloud.Api (hors des dossiers bin du dépôt) ..."
        $ea = $ErrorActionPreference; $ErrorActionPreference = "Continue"
        $sortie = & dotnet build (Join-Path $Depot "src\Pharma.Cloud.Api\Pharma.Cloud.Api.csproj") -c Debug --artifacts-path $Outil -v q -nologo 2>&1
        $code = $LASTEXITCODE; $ErrorActionPreference = $ea
        if ($code -ne 0 -or -not (Test-Path -LiteralPath $ExeCentral)) { throw "Compilation de l'outil en échec :`n$((@($sortie | ForEach-Object { "$_" }) | Select-Object -Last 15) -join "`n")" }
        Write-Ok "Outil compilé."
    }

    Write-Etape2 "Autorité et certificat HTTPS du central (localhost, $AdresseLocale)"
    $vars = Get-VariablesOutil $mdpAutorite
    if (-not $autoriteExiste) {
        $r = Invoke-Outil -Arguments @("--initialiser-autorite") -Variables $vars
        if ($r.Code -ne 0) { throw "--initialiser-autorite en échec :`n$($r.Texte)" }
        Write-Ok ($r.Lignes | Select-Object -First 1)
    }
    else { Write-Ok "Autorité déjà présente." }
    if (-not (Get-ChildItem -LiteralPath $DossierAutorite -Filter "*.pfx" -ErrorAction SilentlyContinue)) {
        $r = Invoke-Outil -Arguments @("--certificat-serveur", "--noms", "localhost,$AdresseLocale") -Variables $vars
        if ($r.Code -ne 0) { throw "--certificat-serveur en échec :`n$($r.Texte)" }
        Write-Ok ($r.Lignes | Select-Object -First 1)
    }
    else { Write-Ok "Certificat HTTPS déjà présent." }

    Write-Etape2 "Officine « $LibelleOfficine » au central"
    Invoke-CodeActivation $mdpAutorite
    Write-Etape2 "Suite"
    Write-Info "1. Fermer TOUTES les fenêtres de Visual Studio, puis le rouvrir (il lit les variables d'utilisateur à son démarrage)."
    Write-Info "2. Profil « API + Poste + Central », F5."
    Write-Info "3. .\scripts\dev\preparer_central_vs.ps1 -Activer (marche à suivre à l'écran, code neuf si besoin)."
}

# Code d'activation : officine créée si elle manque, sinon code neuf ; rien si pharmacie_dev est déjà activée au central
function Invoke-CodeActivation([string]$MotDePasseAutorite, [switch]$SiBesoin) {
    $etat = Lire-Etat
    $idOfficine = Get-ScalaireOfficine "SELECT COALESCE(officine_id::text, '') FROM sync_parametre WHERE id = 1"
    if ($idOfficine -and (Test-Guid $idOfficine)) {
        $activee = Get-ScalaireCentral "SELECT count(*) FROM code_activation WHERE officine_id = '$idOfficine' AND utilise_le IS NOT NULL"
        if ($activee -ne "0") { Write-Ok "pharmacie_dev est déjà activée au central (officine $idOfficine) : aucun code."; return }
    }
    $provisoire = [string]$etat.OfficineProvisoire
    $presente = $provisoire -and (Test-Guid $provisoire) -and ((Get-ScalaireCentral "SELECT count(*) FROM officine WHERE id = '$provisoire'") -ne "0")
    if ($presente -and $SiBesoin) {
        $valides = Get-ScalaireCentral "SELECT count(*) FROM code_activation WHERE officine_id = '$provisoire' AND utilise_le IS NULL AND expire_le > now() + interval '1 hour'"
        if ($valides -ne "0") { Write-Info "Un code d'activation non utilisé et encore valable existe (affiché par -Preparer). Un code neuf est donné quand même ci-dessous ; l'un ou l'autre convient." }
    }
    $vars = Get-VariablesOutil $MotDePasseAutorite
    if (-not $presente) {
        $r = Invoke-Outil -Arguments @("--creer-officine", $LibelleOfficine) -Variables $vars
        if ($r.Code -ne 0) { throw "--creer-officine en échec :`n$($r.Texte)" }
        $guid = [regex]::Match($r.Texte, '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}').Value
        if (-not $guid) { throw "Code provisoire de l'officine introuvable dans la sortie de --creer-officine." }
        $etat = [pscustomobject]@{ OfficineProvisoire = $guid; Libelle = $LibelleOfficine; Base = $BaseOfficine; CreeeLe = (Get-Date).ToString("yyyy-MM-dd HH:mm") }
        Enregistrer-Etat $etat
        Write-Ok "Officine créée au central (code provisoire $guid, dans $FichierEtat)."
    }
    else {
        $r = Invoke-Outil -Arguments @("--code-activation", $provisoire, "--serveur", "principal") -Variables $vars
        if ($r.Code -ne 0) { throw "--code-activation en échec :`n$($r.Texte)" }
        Write-Ok "Officine déjà créée au central ($provisoire) : nouveau code."
    }
    $code = ($r.Lignes | Where-Object { $_.Trim() } | Select-Object -Last 1).Trim()
    Write-Host ""
    Write-Host "   Code d'activation (usage unique, valable 24 h, à coller dans l'écran ; il n'est écrit nulle part) :" -ForegroundColor Cyan
    Write-Host "   $code" -ForegroundColor White
}

# ------------------------------------------------------------------ -Activer
function Invoke-Activer {
    Assert-DossierRacine
    if (-not (Test-Path -LiteralPath $ExeCentral)) { throw "Poste non préparé : lancer -Preparer." }
    $mdpAutorite = Get-VariableUtilisateur "PHARMA_CLOUD_MOT_DE_PASSE"
    if (-not $mdpAutorite) { throw "Variable d'utilisateur PHARMA_CLOUD_MOT_DE_PASSE absente : lancer -Preparer." }
    Write-Etape2 "Serveurs lancés par Visual Studio"
    $sante = "sans réponse"
    try { $sante = (Invoke-RestMethod -Uri "http://localhost:$PortApi/api/healthcheck" -TimeoutSec 5).statut } catch { $sante = "sans réponse" }
    $central = Test-PortOuvert $PortCentral
    Write-Info "API de l'officine (http://localhost:$PortApi) : $sante"
    Write-Info ("Serveur central (https://${AdresseLocale}:$PortCentral) : {0}" -f $(if ($central) { "à l'écoute" } else { "n'écoute pas" }))
    if ($sante -eq "sans réponse" -or -not $central) {
        throw "Lancer d'abord Visual Studio (redémarré après -Preparer), profil « API + Poste + Central », F5 ; puis relancer -Activer."
    }
    [void](Initialize-Pg $false)
    Write-Etape2 "Code d'activation"
    Invoke-CodeActivation $mdpAutorite -SiBesoin
    Write-Etape2 "Marche à suivre dans le poste (compte titulaire)"
    Write-Info "1. Administration > Synchronisation > onglet « Réglages »."
    Write-Info "2. Adresse du serveur central : https://${AdresseLocale}:$PortCentral/"
    Write-Info "3. Cocher « Synchronisation activée », puis « Enregistrer »."
    Write-Info "4. Onglet « Activation » : coller le code ci-dessus, puis « Activer »."
    Write-Info "5. Le chargement complet part en arrière-plan ; suivre avec -Etat (onglet « État » du poste)."
    Write-Info "   Maj+F5 dans Visual Studio coupe l'envoi : il reprend au prochain F5."
    Invoke-Etat
}

# ------------------------------------------------------------------ -Etat (lecture seule)
function Invoke-Lecture([string]$Base, [string]$Sql, [string]$Officine) {
    $a = @("-c", "SET default_transaction_read_only = on")
    if ($Officine) {
        if (-not (Test-Guid $Officine)) { throw "Code d'officine invalide." }
        $a += @("-c", "SET app.officine_id = '$Officine'")
    }
    $a += @("-c", $Sql)
    $r = Invoke-Psql -Compte $CompteBase -Base $Base -Arguments $a -Tolerant
    return $r.Texte
}

function Invoke-Etat {
    Assert-DossierRacine
    Write-Etape2 "Variables d'utilisateur (noms seulement, aucune valeur secrète affichée)"
    foreach ($n in $VariablesUtilisateur) { Write-Info ("{0,-30} {1}" -f $n, $(if (Get-VariableUtilisateur $n) { "présente" } else { "absente" })) }
    Write-Info ("{0,-30} {1}" -f "  base de ConnectionStrings", $(if ($b = Get-BaseDeChaine (Get-VariableUtilisateur "ConnectionStrings__Pharmacie")) { $b } else { "-" }))
    Write-Info ("{0,-30} {1}" -f "  Synchronisation__Dossier", $(if ($s = Get-VariableUtilisateur "Synchronisation__Dossier") { $s } else { "-" }))
    if (Get-VariableUtilisateur $VariableInterdite) { Write-Avert "$VariableInterdite est posée au niveau utilisateur : la retirer (jamais au niveau utilisateur)." }
    else { Write-Ok "$VariableInterdite absente du niveau utilisateur." }
    Write-Etape2 "Dossiers"
    foreach ($d in @($Racine, $DossierAutorite, $DossierSync, $ExeCentral)) { Write-Info ("{0} : {1}" -f $d, $(if (Test-Path -LiteralPath $d) { "présent" } else { "absent" })) }
    $etat = Lire-Etat
    Write-Info "Officine provisoire (etat.json) : $(if ($etat.OfficineProvisoire) { $etat.OfficineProvisoire } else { '-' })"
    Write-Etape2 "Ports"
    $sante = "sans réponse"
    try { $sante = (Invoke-RestMethod -Uri "http://localhost:$PortApi/api/healthcheck" -TimeoutSec 5).statut } catch { $sante = "sans réponse" }
    Write-Info "API officine $PortApi : $sante"
    Write-Info ("Central $PortCentral : {0}" -f $(if (Test-PortOuvert $PortCentral) { "à l'écoute" } else { "n'écoute pas" }))
    if (-not (Initialize-Pg $true)) { Write-Avert "Sans mot de passe de pharma_app (PGPASSWORD) : état des bases non lu."; return }
    Write-Etape2 "Rôle $RoleCentral"
    $role = Get-Scalaire -Compte $CompteBase -Base $BaseMaintenance -Sql "SELECT count(*) FROM pg_roles WHERE rolname = '$RoleCentral' AND rolcanlogin"
    $membre = Get-Scalaire -Compte $CompteBase -Base $BaseMaintenance -Sql "SELECT count(*) FROM pg_auth_members am JOIN pg_roles r ON r.oid = am.roleid JOIN pg_roles m ON m.oid = am.member WHERE r.rolname = '$RoleGroupe' AND m.rolname = '$RoleCentral'"
    Write-Info ("Rôle : {0} ; membre de {1} : {2}" -f $(if ($role -ne "0") { "présent (LOGIN)" } else { "absent" }), $RoleGroupe, $(if ($membre -ne "0") { "oui" } else { "non" }))
    Write-Etape2 "Central ($BaseCentrale) : officines"
    Assert-BaseAutorisee $BaseCentrale "central"
    Write-Host (Invoke-Lecture $BaseCentrale "SELECT id, libelle FROM officine ORDER BY 2" "")
    Write-Etape2 "Officine ($BaseOfficine) : synchronisation"
    Assert-BaseAutorisee $BaseOfficine "officine"
    Write-Host (Invoke-Lecture $BaseOfficine "SELECT active AS activee, adresse_serveur, officine_id, inscrite_le, dernier_envoi_le, dernier_accuse_le, derniere_erreur, derniere_erreur_le FROM sync_parametre" "")
    Write-Host (Invoke-Lecture $BaseOfficine "SELECT count(*) FILTER (WHERE lot_numero IS NULL) AS lignes_hors_lot, count(*) FILTER (WHERE lot_numero IS NOT NULL) AS lignes_en_lot FROM sync_sortie" "")
    $off = Get-ScalaireOfficine "SELECT COALESCE(officine_id::text, '') FROM sync_parametre WHERE id = 1"
    if ($off -and (Test-Guid $off) -and ((Get-ScalaireCentral "SELECT count(*) FROM code_activation WHERE officine_id = '$off' AND utilise_le IS NOT NULL") -ne "0")) {
        Write-Etape2 "Central : lots reçus de pharmacie_dev"
        Write-Host (Invoke-Lecture $BaseCentrale "SELECT count(*) AS lots, sum(nb_lignes) AS lignes, max(numero) AS dernier_lot, max(recu_le) AS dernier_recu FROM lot_recu" $off)
        Write-Host (Invoke-Lecture $BaseCentrale "SELECT numero, motif, etat, debut_le, fin_le FROM chargement ORDER BY numero DESC LIMIT 3" $off)
    }
    else { Write-Info "pharmacie_dev n'est pas encore activée au central." }
}

# ------------------------------------------------------------------ -Retirer
function Invoke-Retirer {
    $simul = [bool]$WhatIfPreference
    Assert-DossierRacine
    if (Test-PortOuvert $PortCentral) { throw "Le central écoute sur $PortCentral : arrêter le débogage dans Visual Studio (Maj+F5), puis relancer -Retirer." }
    # Seules les variables posées par ce script : valeurs reconnues (chemins de $Racine, base pharmacie_dev, rôle pharma_cloud_dev_vs)
    $aRetirer = @()
    foreach ($n in $VariablesUtilisateur) {
        $v = Get-VariableUtilisateur $n
        if (-not $v) { continue }
        $notre = switch ($n) {
            "PHARMA_CLOUD_DOSSIER" { $v -eq $DossierAutorite }
            "Synchronisation__Dossier" { $v -eq $DossierSync }
            "PHARMA_CLOUD_CONNEXION" { $v -match "(?i)Database=pharmacie_cloud_dev;Username=$RoleCentral(;|$)" }
            "ConnectionStrings__Pharmacie" { (Get-BaseDeChaine $v) -ceq "pharmacie_dev" }
            "PHARMA_CLOUD_MOT_DE_PASSE" { (Get-VariableUtilisateur "PHARMA_CLOUD_DOSSIER") -eq $DossierAutorite }
        }
        if ($notre) { $aRetirer += $n } else { Write-Avert "Variable $n non reconnue comme posée par ce script : conservée." }
    }
    if ($simul) {
        Write-Etape2 "Plan (simulation, rien n'est retiré)"
        foreach ($n in $aRetirer) { Write-Simul "retrait de la variable d'utilisateur $n" }
        Write-Simul "retrait du rôle $RoleCentral (aucun objet ni donnée en base)"
        if (Test-Path -LiteralPath $Racine) { Write-Simul "suppression du dossier $Racine (autorité, certificat, clé de synchronisation de pharmacie_dev, outil)" }
        Write-Simul "rien n'est supprimé dans $BaseOfficine ni $BaseCentrale"
        Write-Avert "Après retrait, pharmacie_dev garde ses réglages de synchronisation : décocher « Synchronisation activée » à l'écran avant, sur pharmacie_dev."
        return
    }
    foreach ($n in $aRetirer) { [Environment]::SetEnvironmentVariable($n, $null, "User"); Write-Ok "Variable d'utilisateur $n retirée." }
    [void](Initialize-Pg $false)
    [void](Invoke-Psql -Compte $CompteBase -Base $BaseMaintenance -Arguments @("-c", "DROP ROLE IF EXISTS $RoleCentral"))
    Write-Ok "Rôle $RoleCentral retiré."
    if (Test-Path -LiteralPath $Racine) {
        Assert-DossierRacine
        Remove-Item -LiteralPath $Racine -Recurse -Force
        Write-Ok "Dossier $Racine supprimé."
    }
    Write-Info "Redémarrer Visual Studio pour qu'il oublie les variables. Rien n'a été supprimé en base."
}

# ------------------------------------------------------------------ principal
try {
    $actions = @(@([bool]$Preparer, [bool]$Activer, [bool]$Etat, [bool]$Retirer) | Where-Object { $_ })
    if ($actions.Count -ne 1) {
        Write-Host "OUTIL DE DEVELOPPEMENT, JAMAIS DE PRODUCTION. Une seule action : -Preparer [-Recompiler], -Activer, -Etat, -Retirer. Option -WhatIf. Aide : Get-Help $PSCommandPath -Full"
        exit 1
    }
    Write-Host "OUTIL DE DEVELOPPEMENT, JAMAIS DE PRODUCTION : central de développement (pharmacie_cloud_dev) et officine pharmacie_dev, 127.0.0.1 seulement." -ForegroundColor Yellow
    $script:Simulation = [bool]$WhatIfPreference
    if ($Preparer) { Invoke-Preparer }
    elseif ($Activer) { Invoke-Activer }
    elseif ($Etat) { Invoke-Etat }
    elseif ($Retirer) { Invoke-Retirer }
    exit 0
}
catch {
    Write-Host ""
    Write-Host "ECHEC : $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Le poste reste en l'état : -Etat pour voir, -Preparer pour reprendre." -ForegroundColor Red
    exit 1
}
