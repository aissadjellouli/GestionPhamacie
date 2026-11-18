<#
.SYNOPSIS
    OUTIL DE DEVELOPPEMENT, JAMAIS DE PRODUCTION. Lance sur ce PC, en une commande, une officine d'essai ET un serveur central d'essai
    reliés entre eux, sur des bases jetables. Voir docs\essai-cloud-officine.md (guide pas à pas).

.DESCRIPTION
    A NE JAMAIS UTILISER EN PRODUCTION : ce script crée des bases jetables, un mot de passe d'essai, une autorité de certification
    d'essai et lance deux serveurs en arrière-plan sur 127.0.0.1. La mise en production se fait avec scripts\mep et
    docs\mise-en-production.md (parcours E).

    -Demarrer  (idempotent : le relancer ne casse rien, il reprend ce qui manque)
        1. contrôles : psql, pg_dump, pg_restore, dotnet ; ports libres (refus de 5080, 5180, 9333, 5186, 9446, 5432 et de tout port occupé) ;
        2. dossier d'essai %TEMP%\pharma-essai-cloud (hors dépôt) : autorité, certificat HTTPS, journaux, PID, mots de passe d'essai
           tirés au hasard (jamais écrits ailleurs) ;
        3. compilation des deux serveurs dans ce dossier (le dépôt et les exécutables en service ne sont pas touchés) ;
        4. base centrale jetable pharmacie_cloud_essai (scripts database\cloud 001 à 015 par scripts\mep\creer_base_centrale.ps1)
           et copie jetable pharmacie_essai_officine de pharmacie_dev (pg_dump en lecture seule, puis restauration) ;
        5. autorité d'essai + certificat HTTPS de 127.0.0.1 ; serveur central (port -PortCentral) et API officine (port -PortApi) lancés ;
        6. officine créée au central puis ACTIVÉE par l'API (réglages, code d'activation, certificat), comme le fait l'écran ;
        7. résumé : adresses, poste, commandes utiles.

    -Etat       processus, file d'attente de l'officine (sync_sortie, sync_lot), lots reçus au central, dernier envoi, dernière erreur.
    -Arreter    arrête les deux processus lancés par ce script (seulement eux, par PID), supprime les deux bases jetables, le rôle
                d'essai et le dossier d'essai. Avec -GarderBases : bases et dossier conservés (pause), -Demarrer reprend ensuite.
    -ArreterCentral  arrête seulement le serveur central d'essai (pour voir l'officine en attente) ; -Demarrer le relance.
    -Central    lance une commande d'administration du serveur central d'essai (-Central --reglages, -Central --regler-famille banque oui, -Central --lister…).
    -Requete    requête SQL en LECTURE sur -Sur officine (défaut) ou -Sur central.

    Garde-fous : adresses 127.0.0.1 / localhost seulement ; rien n'est envoyé sur Internet ; aucun magasin de certificats Windows
    touché ; aucun secret dans le dépôt ; le compte postgres n'est jamais utilisé (compte pharma_app, sans droit de superutilisateur) ;
    pharmacie_dev n'est lue que par pg_dump ; pharmacie_test, pharmacie_cloud_dev, le port 5080 et C:\ProgramData\PharmaOfficine
    ne sont jamais touchés ; les seules bases supprimées sont pharmacie_cloud_essai et pharmacie_essai_officine.

    Mots de passe : jamais en paramètre. Le seul mot de passe demandé est celui de PostgreSQL pour pharma_app : variable PGPASSWORD
    déjà définie, ou pgpass.conf, ou saisie masquée.
    Activation de l'officine par l'API : sans rien demander, le script crée dans la copie jetable pharmacie_essai_officine (et nulle
    part ailleurs) un compte titulaire temporaire « essai_activation », au mot de passe aléatoire haché en bcrypt comme le fait
    l'application et gardé en mémoire seulement, l'utilise pour activer l'officine, puis le désactive. Le compte titulaire de
    démonstration n'est jamais modifié. Compatibilité : si la variable PHARMA_ESSAI_MDP_TITULAIRE est définie, c'est l'ancien chemin
    (connexion avec le compte « titulaire » et ce mot de passe, voir docs\poste-de-developpement.md, « Comptes de démonstration »).

    Si une étape demande un droit que pharma_app n'a pas, le script s'arrête avec un message qui l'explique.

.PARAMETER Demarrer
    Démarre (ou reprend) l'essai.

.PARAMETER Etat
    Affiche l'état de l'essai.

.PARAMETER Arreter
    Arrête et nettoie l'essai.

.PARAMETER GarderBases
    Avec -Arreter : garde les bases, le rôle et le dossier d'essai.

.PARAMETER ArreterCentral
    Arrête seulement le serveur central d'essai (par son PID). L'officine continue et met ses lots en attente.

.PARAMETER Central
    Commande d'administration du serveur central d'essai : tout ce qui suit -Central lui est transmis, par exemple
    -Central --regler-famille banque oui

.PARAMETER Requete
    Requête SQL à lancer en lecture seule.

.PARAMETER Sur
    Avec -Requete : officine (défaut) ou central.

.PARAMETER PortCentral
    Port HTTPS du serveur central d'essai (défaut 5190).

.PARAMETER PortApi
    Port HTTP de l'API de l'officine d'essai (défaut 5191).

.PARAMETER PortPostgres
    Port de PostgreSQL (défaut 5432). Le serveur est toujours « localhost ».

.PARAMETER Recompiler
    Force la compilation même si les exécutables d'essai existent.

.EXAMPLE
    .\scripts\dev\essai_cloud_officine.ps1 -Demarrer -WhatIf
    Montre ce qui serait fait, sans rien créer.

.EXAMPLE
    .\scripts\dev\essai_cloud_officine.ps1 -Demarrer
    .\scripts\dev\essai_cloud_officine.ps1 -Etat
    .\scripts\dev\essai_cloud_officine.ps1 -Arreter

.NOTES
    Si PowerShell refuse le script : powershell -ExecutionPolicy Bypass -File .\scripts\dev\essai_cloud_officine.ps1 -Demarrer
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium', PositionalBinding = $false)]
param(
    [switch]$Demarrer,
    [switch]$Etat,
    [switch]$Arreter,
    [switch]$GarderBases,
    [switch]$ArreterCentral,
    [switch]$Central,
    [string]$Requete,
    [ValidateSet("officine", "central")][string]$Sur = "officine",
    [int]$PortCentral = 5190,
    [int]$PortApi = 5191,
    [int]$PortPostgres = 5432,
    [switch]$Recompiler,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$ArgumentsCentral
)

$ErrorActionPreference = "Stop"
. (Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) "mep") "outils_mep.ps1")
$Depot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

# --- Noms réservés à l'essai : jamais en paramètre, seules ces bases et ce rôle peuvent être créés ou supprimés ---
$CompteBase = "pharma_app"
$BaseMaintenance = "postgres"                 # base de connexion pour créer et supprimer les bases (ce n'est pas le compte postgres)
$BaseSource = "pharmacie_dev"                 # lue par pg_dump seulement
$BaseCentrale = "pharmacie_cloud_essai"
$BaseOfficine = "pharmacie_essai_officine"
$BasesJetables = @($BaseCentrale, $BaseOfficine)
$RoleCentral = "pharma_cloud_essai"
$IdentifiantEssai = "essai_activation"      # compte titulaire temporaire, créé seulement dans la copie jetable $BaseOfficine
$PortsInterdits = @(5080, 5180, 9333, 5186, 9446, 5432)
$DossierEssai = Join-Path $env:TEMP "pharma-essai-cloud"
$FichierEtat = Join-Path $DossierEssai "etat.json"
$FichierSecrets = Join-Path $DossierEssai "secrets.json"
$Artefacts = Join-Path $DossierEssai "artefacts"
$DossierAutorite = Join-Path $DossierEssai "autorite"
$ExeCentral = Join-Path $Artefacts "bin\Pharma.Cloud.Api\debug\Pharma.Cloud.Api.exe"
$ExeApi = Join-Path $Artefacts "bin\Pharma.Api\debug\Pharma.Api.exe"
$AdresseLocale = "127.0.0.1"

function Write-Etape2([string]$t) { Write-Host ""; Write-Host "== $t" -ForegroundColor Cyan }

# ------------------------------------------------------------------ outils
function Test-PortInterdit([int]$Port) {
    if ($Port -lt 1024 -or $Port -gt 65535) { return "port $Port hors de 1024 à 65535" }
    if ($PortsInterdits -contains $Port) { return "port $Port interdit (réservé : 5080, 5180, 9333, 5186, 9446 et PostgreSQL)" }
    return $null
}

function Test-PortLibre([int]$Port) {
    $ecoutes = [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
    return -not ($ecoutes | Where-Object { $_.Port -eq $Port })
}

function Test-PortOuvert([int]$Port) {
    $c = New-Object Net.Sockets.TcpClient
    try { $a = $c.BeginConnect($AdresseLocale, $Port, $null, $null); return ($a.AsyncWaitHandle.WaitOne(500) -and $c.Connected) }
    catch { return $false }
    finally { $c.Close() }
}

function New-MotDePasseEssai([int]$Longueur = 24) {
    $alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray()
    $rng = New-Object Security.Cryptography.RNGCryptoServiceProvider
    $o = New-Object 'byte[]' $Longueur
    $rng.GetBytes($o)
    return -join ($o | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
}

function Lire-Json([string]$Fichier) {
    if (-not (Test-Path -LiteralPath $Fichier)) { return $null }
    return (Get-Content -LiteralPath $Fichier -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Ecrire-Json($Objet, [string]$Fichier) {
    $Objet | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Fichier -Encoding UTF8
}

function Lire-Etat {
    $e = Lire-Json $FichierEtat
    if (-not $e) { return [pscustomobject]@{ PortCentral = $PortCentral; PortApi = $PortApi; PidCentral = 0; PidApi = 0; OfficineProvisoire = ""; OfficineActivee = $false } }
    return $e
}

function Enregistrer-Etat($e) { Ecrire-Json $e $FichierEtat }

function Get-ProcessusEssai([int]$ProcessId, [string]$NomAttendu) {
    if ($ProcessId -le 0) { return $null }
    $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($p -and $p.ProcessName -eq $NomAttendu) { return $p }
    return $null
}

function Get-MotDePassePg {
    if ($script:MotsDePasse.ContainsKey($CompteBase)) { return $script:MotsDePasse[$CompteBase] }
    return $env:PGPASSWORD
}

function Get-ChaineConnexion([string]$Base, [string]$Compte, [string]$Mdp) {
    $c = "Host=localhost;Port=$PortPostgres;Database=$Base;Username=$Compte"
    if ($Mdp) { $c += ";Password=$Mdp" }
    return $c
}

# Lance un exécutable avec des variables d'environnement valables pour ce processus enfant seulement.
function Start-Enfant {
    param([string]$Exe, [hashtable]$Variables, [string]$Journal)
    $anciennes = @{}
    foreach ($k in $Variables.Keys) { $anciennes[$k] = [Environment]::GetEnvironmentVariable($k, "Process"); [Environment]::SetEnvironmentVariable($k, $Variables[$k], "Process") }
    try {
        return Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe -Parent) -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput "$Journal.out.log" -RedirectStandardError "$Journal.err.log"
    }
    finally { foreach ($k in $anciennes.Keys) { [Environment]::SetEnvironmentVariable($k, $anciennes[$k], "Process") } }
}

function Invoke-Enfant {
    param([string]$Exe, [string[]]$Arguments, [hashtable]$Variables)
    $anciennes = @{}
    foreach ($k in $Variables.Keys) { $anciennes[$k] = [Environment]::GetEnvironmentVariable($k, "Process"); [Environment]::SetEnvironmentVariable($k, $Variables[$k], "Process") }
    $ea = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        Push-Location (Split-Path $Exe -Parent)
        $sortie = & $Exe @Arguments 2>&1
        $code = $LASTEXITCODE
        Pop-Location
    }
    finally { $ErrorActionPreference = $ea; foreach ($k in $anciennes.Keys) { [Environment]::SetEnvironmentVariable($k, $anciennes[$k], "Process") } }
    $lignes = @($sortie | ForEach-Object { "$_" })
    return [pscustomobject]@{ Code = $code; Lignes = $lignes; Texte = ($lignes -join "`n") }
}

function Get-VariablesAdmin($secrets) {
    return @{
        PHARMA_CLOUD_DOSSIER = $DossierAutorite
        PHARMA_CLOUD_MOT_DE_PASSE = $secrets.MotDePasseAutorite
        PHARMA_CLOUD_ADMIN_CONNEXION = (Get-ChaineConnexion $BaseCentrale $CompteBase (Get-MotDePassePg))
        PHARMA_CLOUD_CONNEXION = (Get-ChaineConnexion $BaseCentrale $RoleCentral $secrets.MotDePasseRole)
    }
}

function Remove-BaseJetable([string]$Nom) {
    # Seule porte de suppression d'une base : les deux bases d'essai de ce script
    if ($BasesJetables -notcontains $Nom) { throw "Suppression refusée : « $Nom » n'est pas une base d'essai de ce script." }
    [void](Invoke-Psql -Compte $CompteBase -Base $BaseMaintenance -Arguments @("-c", "DROP DATABASE IF EXISTS `"$Nom`" WITH (FORCE)"))
}

function Test-BaseExiste([string]$Nom) {
    return (Get-Scalaire -Compte $CompteBase -Base $BaseMaintenance -Sql "SELECT count(*) FROM pg_database WHERE datname = '$Nom'") -ne "0"
}

function Initialize-Pg {
    Initialize-OutilsMep -PgBin "" -Hote "localhost" -Port $PortPostgres -Simulation $false
    [void](Request-MotDePasse -Compte $CompteBase -Invite "Mot de passe PostgreSQL de pharma_app (compte de développement, voir docs\poste-de-developpement.md)")
    $r = Invoke-Psql -Compte $CompteBase -Base $BaseMaintenance -Arguments @("-tAc", "SELECT rolsuper::text || '|' || rolcreatedb::text FROM pg_roles WHERE rolname = current_user") -Tolerant
    if ($r.Code -ne 0) { throw "Connexion à PostgreSQL impossible avec pharma_app (localhost:$PortPostgres). Vérifier le mot de passe et que le service tourne.`n$($r.Texte)" }
    $d = ($r.Lignes | Where-Object { $_ }) -join ""
    if ($d -match "^true") { throw "Le compte de connexion est un superutilisateur : ce script refuse de s'en servir (utiliser pharma_app)." }
    if ($d -notmatch "\|true$") { throw "Le compte pharma_app n'a pas le droit CREATEDB : il faut que l'administrateur PostgreSQL le lui donne (ALTER ROLE pharma_app CREATEDB). Le script ne s'élève pas lui-même." }
}

# Compte titulaire temporaire de l'essai : refus dans toute autre base que la copie jetable pharmacie_essai_officine
function Assert-BaseEssaiSeulement([string]$Base) {
    if ($Base -cne "pharmacie_essai_officine" -or $BaseOfficine -cne "pharmacie_essai_officine") {
        throw "Refusé : le compte temporaire d'essai n'est créé que dans la base pharmacie_essai_officine (demandé : « $Base »)."
    }
    if ($Base -ceq $BaseSource) { throw "Refusé : jamais de compte temporaire dans la base source $BaseSource." }
    if ($BasesJetables -notcontains $Base) { throw "Refusé : « $Base » n'est pas une base d'essai de ce script." }
    $courante = Get-Scalaire -Compte $CompteBase -Base $Base -Sql "SELECT current_database()"
    if ($courante -cne "pharmacie_essai_officine") { throw "Refusé : la connexion ne pointe pas sur pharmacie_essai_officine (base courante : « $courante »)." }
}

# Bibliothèque BCrypt.Net-Next de l'application (même version que Directory.Packages.props). Windows PowerShell 5.1 (.NET Framework) ne charge pas
# la version compilée pour .NET 10 : il prend la version « net35 » du même paquet, sans dépendance, qui produit le même format ($2a$11$...).
function Get-BibliothequeBcrypt {
    if ($PSVersionTable.PSEdition -eq "Core") {
        $dll = Join-Path $Artefacts "bin\Pharma.Api\debug\BCrypt.Net-Next.dll"
        if (-not (Test-Path -LiteralPath $dll)) { throw "BCrypt.Net-Next.dll introuvable dans $Artefacts : relancer -Demarrer -Recompiler." }
        return $dll
    }
    $version = ([regex]::Match((Get-Content -LiteralPath (Join-Path $Depot "Directory.Packages.props") -Raw), 'Include="BCrypt\.Net-Next"\s+Version="([^"]+)"')).Groups[1].Value
    if (-not $version) { throw "Version de BCrypt.Net-Next introuvable dans Directory.Packages.props." }
    $racine = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget\packages" }
    $dll = Join-Path $racine "bcrypt.net-next\$version\lib\net35\BCrypt.Net-Next.dll"
    if (-not (Test-Path -LiteralPath $dll)) { throw "BCrypt.Net-Next $version (lib\net35) introuvable dans $racine : lancer un dotnet restore du dépôt, ou définir PHARMA_ESSAI_MDP_TITULAIRE (ancien chemin)." }
    return $dll
}

# Crée (ou réactive) le compte « essai_activation » avec le mot de passe donné, haché en bcrypt comme l'application
# (BCrypt.Net, facteur de coût par défaut). Le hachage se fait en mémoire ; le mot de passe n'est écrit nulle part.
function New-CompteEssaiActivation([string]$MotDePasse) {
    Assert-BaseEssaiSeulement $BaseOfficine
    $dll = Get-BibliothequeBcrypt
    Add-Type -Path $dll
    $hash = [BCrypt.Net.BCrypt]::HashPassword($MotDePasse)
    if ($hash -notmatch '^\$2[abxy]\$\d\d\$[./A-Za-z0-9]{53}$') { throw "Hachage bcrypt inattendu : compte d'essai non créé." }
    $id = $IdentifiantEssai
    $sql = @"
UPDATE utilisateur SET mot_de_passe_hash = '$hash', actif = true, doit_changer_mot_de_passe = false
 WHERE identifiant = '$id';
INSERT INTO utilisateur (nom_complet, identifiant, mot_de_passe_hash, actif, role_id, doit_changer_mot_de_passe)
SELECT 'Compte temporaire d''essai (activation)', '$id', '$hash', true, r.id, false FROM role r
 WHERE r.code = 'titulaire' AND NOT EXISTS (SELECT 1 FROM utilisateur WHERE identifiant = '$id');
"@
    [void](Invoke-Psql -Compte $CompteBase -Base $BaseOfficine -Arguments @("-f", "-") -Entree $sql)
    $n = Get-Scalaire -Compte $CompteBase -Base $BaseOfficine -Sql "SELECT count(*) FROM utilisateur u JOIN role r ON r.id = u.role_id WHERE u.identifiant = '$id' AND u.actif AND r.code = 'titulaire'"
    if ($n -ne "1") { throw "Le compte temporaire d'essai n'a pas pu être créé dans $BaseOfficine." }
}

# Désactive le compte temporaire (seul compte touché) ; ne masque jamais une autre erreur
function Disable-CompteEssaiActivation {
    try {
        Assert-BaseEssaiSeulement $BaseOfficine
        [void](Invoke-Psql -Compte $CompteBase -Base $BaseOfficine -Arguments @("-c", "UPDATE utilisateur SET actif = false WHERE identifiant = '$IdentifiantEssai'"))
        Write-Ok "Compte temporaire $IdentifiantEssai désactivé."
    }
    catch { Write-Avert "Compte temporaire $IdentifiantEssai non désactivé : $($_.Exception.Message) (il disparaît avec -Arreter)." }
}

# ------------------------------------------------------------------ -Demarrer
function Invoke-Demarrer {
    $simul = [bool]$WhatIfPreference
    Write-Etape2 "Contrôles"
    foreach ($p in @($PortCentral, $PortApi)) { if ($m = Test-PortInterdit $p) { throw "Refusé : $m." } }
    if ($PortCentral -eq $PortApi) { throw "Refusé : les deux ports doivent être différents." }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "dotnet introuvable (installer le SDK .NET)." }
    Write-Ok "dotnet $(& dotnet --version)"

    $etat = Lire-Etat
    $enCours = (Test-Path -LiteralPath $FichierEtat)
    if ($enCours) { $PortCentralUtilise = [int]$etat.PortCentral; $PortApiUtilise = [int]$etat.PortApi }
    else { $PortCentralUtilise = $PortCentral; $PortApiUtilise = $PortApi }
    $centralVivant = [bool](Get-ProcessusEssai ([int]$etat.PidCentral) "Pharma.Cloud.Api")
    $apiVivante = [bool](Get-ProcessusEssai ([int]$etat.PidApi) "Pharma.Api")
    if (-not $centralVivant -and -not (Test-PortLibre $PortCentralUtilise)) { throw "Le port $PortCentralUtilise est déjà occupé : choisir un autre -PortCentral." }
    if (-not $apiVivante -and -not (Test-PortLibre $PortApiUtilise)) { throw "Le port $PortApiUtilise est déjà occupé : choisir un autre -PortApi." }
    Write-Ok "Ports d'essai : central $PortCentralUtilise (HTTPS), API officine $PortApiUtilise (HTTP), tous deux sur $AdresseLocale."
    if ($enCours) { Write-Info "Un essai existe déjà ($DossierEssai) : reprise de ce qui manque." }

    if ($simul) {
        Write-Etape2 "Plan (simulation, rien n'est créé)"
        Write-Simul "dossier d'essai $DossierEssai (autorité, journaux, PID, mots de passe d'essai tirés au hasard)"
        Write-Simul "compilation de Pharma.Cloud.Api et Pharma.Api dans $Artefacts si les exécutables manquent"
        Write-Simul "base centrale $BaseCentrale : scripts\mep\creer_base_centrale.ps1 (database\cloud 001 à 011), rôle $RoleCentral"
        Write-Simul "copie jetable $BaseOfficine de $BaseSource (pg_dump en lecture seule puis pg_restore)"
        Write-Simul "autorité d'essai et certificat HTTPS de $AdresseLocale,localhost dans $DossierAutorite"
        Write-Simul "lancement du serveur central (port $PortCentralUtilise) et de l'API officine (port $PortApiUtilise), journaux dans $DossierEssai"
        Write-Simul "création de l'officine au central, réglages et activation par l'API (compte temporaire $IdentifiantEssai créé dans $BaseOfficine seulement, puis désactivé ; ou, si PHARMA_ESSAI_MDP_TITULAIRE est défini, compte titulaire de démonstration)"
        return
    }

    Initialize-Pg
    Write-Ok "PostgreSQL : pharma_app, droit CREATEDB présent, pas superutilisateur."

    Write-Etape2 "Dossier d'essai et mots de passe d'essai"
    foreach ($d in @($DossierEssai, $DossierAutorite, (Join-Path $DossierEssai "officine"), (Join-Path $DossierEssai "journaux-officine"))) {
        if (-not (Test-Path -LiteralPath $d)) { [void](New-Item -ItemType Directory -Path $d) }
    }
    $secrets = Lire-Json $FichierSecrets
    if (-not $secrets) {
        $secrets = [pscustomobject]@{ MotDePasseAutorite = (New-MotDePasseEssai 24); MotDePasseRole = (New-MotDePasseEssai 24) }
        Ecrire-Json $secrets $FichierSecrets
        Write-Ok "Mots de passe d'essai tirés au hasard, gardés seulement dans $FichierSecrets."
    }
    else { Write-Ok "Mots de passe d'essai déjà présents." }
    $etat | Add-Member -NotePropertyName PortCentral -NotePropertyValue $PortCentralUtilise -Force
    $etat | Add-Member -NotePropertyName PortApi -NotePropertyValue $PortApiUtilise -Force
    Enregistrer-Etat $etat

    Write-Etape2 "Compilation (dans $Artefacts)"
    foreach ($p in @(@{ Nom = "Pharma.Cloud.Api"; Exe = $ExeCentral }, @{ Nom = "Pharma.Api"; Exe = $ExeApi })) {
        if ((Test-Path -LiteralPath $p.Exe) -and -not $Recompiler) { Write-Ok "$($p.Nom) déjà compilé (-Recompiler pour refaire)."; continue }
        Write-Info "dotnet build $($p.Nom) ..."
        $ea = $ErrorActionPreference; $ErrorActionPreference = "Continue"
        $sortie = & dotnet build (Join-Path $Depot "src\$($p.Nom)\$($p.Nom).csproj") -c Debug --artifacts-path $Artefacts -v q -nologo 2>&1
        $code = $LASTEXITCODE; $ErrorActionPreference = $ea
        if ($code -ne 0 -or -not (Test-Path -LiteralPath $p.Exe)) { throw "Compilation de $($p.Nom) en échec :`n$((@($sortie | ForEach-Object { "$_" }) | Select-Object -Last 15) -join "`n")" }
        Write-Ok "$($p.Nom) compilé."
    }

    Write-Etape2 "Base centrale jetable $BaseCentrale"
    $centraleExiste = Test-BaseExiste $BaseCentrale
    $avecTables = $false
    if ($centraleExiste) { $avecTables = (Get-Scalaire -Compte $CompteBase -Base $BaseCentrale -Sql "SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND tablename = 'officine'") -ne "0" }
    if ($avecTables) {
        if (-not (Test-Path -LiteralPath (Join-Path $DossierAutorite "autorite.crt"))) {
            throw "La base $BaseCentrale existe mais l'autorité d'essai a disparu : reste d'un ancien essai. Lancer -Arreter (supprime les bases d'essai), puis -Demarrer."
        }
        Write-Ok "Base centrale déjà prête."
    }
    else {
        # Le script de production crée la base et passe database\cloud 001 à 011 ; pharma_app est propriétaire (aucun compte postgres)
        $mep = Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) "mep") "creer_base_centrale.ps1"
        $mdp = Get-MotDePassePg
        $ancien = $env:PGPASSWORD
        try {
            if ($mdp) { $env:PGPASSWORD = $mdp }
            $r = & powershell -NoProfile -ExecutionPolicy Bypass -File $mep -Base $BaseCentrale -Proprietaire $CompteBase -CompteAdmin $CompteBase -Hote localhost -Port $PortPostgres -DossierDepot $Depot 2>&1
            if ($LASTEXITCODE -ne 0) { throw "creer_base_centrale.ps1 en échec :`n$((@($r | ForEach-Object { "$_" }) | Select-Object -Last 15) -join "`n")" }
        }
        finally { if ($null -eq $ancien) { Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue } else { $env:PGPASSWORD = $ancien } }
        Write-Ok "Base centrale créée (scripts database\cloud 001 à 011)."
    }
    # Rôle de connexion du serveur central d'essai : membre de pharma_cloud_api (non propriétaire, sécurité au niveau des lignes appliquée)
    $existe = (Get-Scalaire -Compte $CompteBase -Base $BaseMaintenance -Sql "SELECT count(*) FROM pg_roles WHERE rolname = '$RoleCentral'") -ne "0"
    $sql = if ($existe) { "ALTER ROLE $RoleCentral LOGIN PASSWORD '$($secrets.MotDePasseRole)';" } else { "CREATE ROLE $RoleCentral LOGIN PASSWORD '$($secrets.MotDePasseRole)';" }
    $r = Invoke-Psql -Compte $CompteBase -Base $BaseMaintenance -Arguments @("-f", "-") -Entree $sql -Tolerant
    if ($r.Code -ne 0) { throw "Création du rôle $RoleCentral impossible : pharma_app doit avoir le droit CREATEROLE. Le script ne s'élève pas lui-même.`n$($r.Texte)" }
    $r = Invoke-Psql -Compte $CompteBase -Base $BaseMaintenance -Arguments @("-c", "GRANT pharma_cloud_api TO $RoleCentral") -Tolerant
    if ($r.Code -ne 0) { throw "pharma_app n'a pas le droit d'accorder pharma_cloud_api (option ADMIN) à $RoleCentral. Il faut que l'administrateur PostgreSQL exécute : GRANT pharma_cloud_api TO pharma_app WITH ADMIN OPTION. Le script s'arrête.`n$($r.Texte)" }
    Write-Ok "Rôle $RoleCentral prêt (membre de pharma_cloud_api)."

    Write-Etape2 "Copie jetable $BaseOfficine (de $BaseSource, en lecture seule)"
    if (Test-BaseExiste $BaseOfficine) { Write-Ok "Copie déjà présente." }
    else {
        $dump = Join-Path $DossierEssai "source.dump"
        $r = Invoke-OutilPg -Outil $script:PgDump -Compte $CompteBase -Arguments @("-h", "localhost", "-p", "$PortPostgres", "-U", $CompteBase, "-w", "-Fc", "--no-owner", "--no-privileges", "-f", $dump, $BaseSource)
        if ($r.Code -ne 0) { throw "pg_dump de $BaseSource en échec :`n$($r.Texte)" }
        [void](Invoke-Psql -Compte $CompteBase -Base $BaseMaintenance -Arguments @("-c", "CREATE DATABASE `"$BaseOfficine`" OWNER `"$CompteBase`" ENCODING 'UTF8' TEMPLATE template0"))
        $r = Invoke-OutilPg -Outil $script:PgRestore -Compte $CompteBase -Arguments @("-h", "localhost", "-p", "$PortPostgres", "-U", $CompteBase, "-w", "--no-owner", "--no-privileges", "-d", $BaseOfficine, $dump)
        Remove-Item -LiteralPath $dump -Force -ErrorAction SilentlyContinue
        if ($r.Code -ne 0) { throw "pg_restore en échec (la copie reste, -Arreter la supprime) :`n$($r.Texte)" }
        # Réglages de la base (fuseau horaire) : pg_dump ne les copie pas
        $fuseau = Get-Scalaire -Compte $CompteBase -Base $BaseMaintenance -Sql "SELECT s FROM pg_db_role_setting r JOIN pg_database d ON d.oid = r.setdatabase, unnest(r.setconfig) s WHERE d.datname = '$BaseSource' AND r.setrole = 0 AND s LIKE 'TimeZone=%' LIMIT 1"
        if ($fuseau -match '^TimeZone=([A-Za-z_/+\-0-9]+)$') { [void](Invoke-Psql -Compte $CompteBase -Base $BaseMaintenance -Arguments @("-c", "ALTER DATABASE `"$BaseOfficine`" SET TimeZone = '$($Matches[1])'")) }
        # La copie part sans synchronisation active et sans adresse
        [void](Invoke-Psql -Compte $CompteBase -Base $BaseOfficine -Arguments @("-c", "UPDATE sync_parametre SET active = false, adresse_serveur = NULL WHERE inscrite_le IS NULL"))
        Write-Ok "Copie créée ($((Get-Scalaire -Compte $CompteBase -Base $BaseOfficine -Sql "SELECT count(*) FROM pg_tables WHERE schemaname = 'public'")) tables)."
    }

    Write-Etape2 "Autorité d'essai et certificat HTTPS ($AdresseLocale, localhost)"
    $vars = Get-VariablesAdmin $secrets
    if (-not (Test-Path -LiteralPath (Join-Path $DossierAutorite "autorite.crt"))) {
        $r = Invoke-Enfant -Exe $ExeCentral -Arguments @("--initialiser-autorite") -Variables $vars
        if ($r.Code -ne 0) { throw "--initialiser-autorite en échec :`n$($r.Texte)" }
        Write-Ok ($r.Lignes | Select-Object -First 1)
    }
    if (-not (Get-ChildItem -LiteralPath $DossierAutorite -Filter "*.pfx" -ErrorAction SilentlyContinue)) {
        $r = Invoke-Enfant -Exe $ExeCentral -Arguments @("--certificat-serveur", "--noms", "localhost,$AdresseLocale") -Variables $vars
        if ($r.Code -ne 0) { throw "--certificat-serveur en échec :`n$($r.Texte)" }
        Write-Ok ($r.Lignes | Select-Object -First 1)
    }
    else { Write-Ok "Autorité et certificat déjà présents." }

    Write-Etape2 "Serveur central d'essai"
    $etat = Lire-Etat
    if (Get-ProcessusEssai ([int]$etat.PidCentral) "Pharma.Cloud.Api") { Write-Ok "Déjà en marche (PID $($etat.PidCentral))." }
    else {
        $v = Get-VariablesAdmin $secrets
        [void]$v.Remove("PHARMA_CLOUD_ADMIN_CONNEXION")        # le service n'a jamais la connexion d'administration
        $v["Cloud__Adresse"] = $AdresseLocale
        $v["Cloud__Port"] = "$PortCentralUtilise"
        $p = Start-Enfant -Exe $ExeCentral -Variables $v -Journal (Join-Path $DossierEssai "central")
        $etat | Add-Member -NotePropertyName PidCentral -NotePropertyValue $p.Id -Force
        Enregistrer-Etat $etat
        Wait-Ecoute $PortCentralUtilise $p "central" (Join-Path $DossierEssai "central.err.log")
        Write-Ok "Serveur central en marche (PID $($p.Id)), https://${AdresseLocale}:$PortCentralUtilise"
    }

    Write-Etape2 "API de l'officine d'essai"
    $etat = Lire-Etat
    if (Get-ProcessusEssai ([int]$etat.PidApi) "Pharma.Api") { Write-Ok "Déjà en marche (PID $($etat.PidApi))." }
    else {
        $racine = Join-Path $DossierEssai "officine"
        $v = @{
            Urls = "http://${AdresseLocale}:$PortApiUtilise"
            ConnectionStrings__Pharmacie = (Get-ChaineConnexion $BaseOfficine $CompteBase (Get-MotDePassePg))
            Jwt__Cle = (New-MotDePasseEssai 96)
            Synchronisation__Dossier = (Join-Path $racine "synchronisation")
            Ged__Dossier = (Join-Path $racine "GED")
            Ordonnances__Dossier = (Join-Path $racine "Ordonnances")
            Chifa__DossierExport = (Join-Path $racine "Chifa")
            SuperAdmin__Fichier = (Join-Path $racine "super-admin.dat")
            Integrite__FichierCle = (Join-Path $racine "cle-integrite.dat")
            Journaux__Dossier = (Join-Path $DossierEssai "journaux-officine")
        }
        $p = Start-Enfant -Exe $ExeApi -Variables $v -Journal (Join-Path $DossierEssai "api")
        $etat | Add-Member -NotePropertyName PidApi -NotePropertyValue $p.Id -Force
        Enregistrer-Etat $etat
        Wait-Ecoute $PortApiUtilise $p "API officine" (Join-Path $DossierEssai "api.err.log")
        Write-Ok "API de l'officine en marche (PID $($p.Id)), http://${AdresseLocale}:$PortApiUtilise"
    }

    Write-Etape2 "Officine créée au central et activée"
    $etat = Lire-Etat
    if ($etat.OfficineActivee) { Write-Ok "Officine déjà activée (aucune connexion à demander)." }
    else {
        $base = "http://${AdresseLocale}:$PortApiUtilise"
        $mdpTit = $env:PHARMA_ESSAI_MDP_TITULAIRE
        $compteEssai = -not $mdpTit      # sans mot de passe donné : compte temporaire dédié à l'essai (jamais le compte titulaire de démonstration)
        $jeton = $null
        $etatSync = $null
        try {
            try {
                if ($compteEssai) {
                    $mdpTit = New-MotDePasseEssai 24      # aléatoire, en mémoire seulement
                    try { New-CompteEssaiActivation $mdpTit }
                    catch { throw "Création du compte temporaire $IdentifiantEssai impossible : $($_.Exception.Message)" }
                    Write-Ok "Compte temporaire $IdentifiantEssai créé dans $BaseOfficine (mot de passe aléatoire, en mémoire seulement)."
                }
                $corps = @{ identifiant = $(if ($compteEssai) { $IdentifiantEssai } else { "titulaire" }); motDePasse = $mdpTit } | ConvertTo-Json
                $jeton = (Invoke-RestMethod -Method Post -Uri "$base/api/auth/connexion" -ContentType "application/json" -Body $corps).jeton
            }
            catch {
                if ($compteEssai -and $_.Exception.Message -like "Création du compte temporaire*") { throw $_.Exception.Message }
                if ($compteEssai) { throw "Connexion avec le compte temporaire $IdentifiantEssai impossible : $($_.Exception.Message) Le reste de l'essai tourne ; relancer -Demarrer." }
                throw "Connexion du compte titulaire de démonstration refusée : vérifier le mot de passe de PHARMA_ESSAI_MDP_TITULAIRE (docs\poste-de-developpement.md). Le reste de l'essai tourne ; relancer -Demarrer, sans la variable pour utiliser le compte temporaire."
            }
            $entetes = @{ Authorization = "Bearer $jeton" }
            $etatSync = Invoke-RestMethod -Method Get -Uri "$base/api/admin/synchronisation" -Headers $entetes
            if ($etatSync.activee) { Write-Ok "Officine déjà activée (certificat en place)." }
            else {
                $etat = Lire-Etat
                $adresseCentral = "https://${AdresseLocale}:$PortCentralUtilise"
                if (-not $etat.OfficineProvisoire) {
                    $r = Invoke-Enfant -Exe $ExeCentral -Arguments @("--creer-officine", "Officine d'essai") -Variables (Get-VariablesAdmin $secrets)
                    if ($r.Code -ne 0) { throw "--creer-officine en échec :`n$($r.Texte)" }
                    $guid = [regex]::Match($r.Texte, '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}').Value
                    $code = ($r.Lignes | Where-Object { $_.Trim() } | Select-Object -Last 1).Trim()
                    $etat | Add-Member -NotePropertyName OfficineProvisoire -NotePropertyValue $guid -Force
                    Enregistrer-Etat $etat
                    Write-Ok "Officine créée au central ($guid, code provisoire)."
                }
                else {
                    # Reprise : le code précédent a pu être consommé ou périmé, on en demande un neuf pour la même officine
                    $r = Invoke-Enfant -Exe $ExeCentral -Arguments @("--code-activation", $etat.OfficineProvisoire, "--serveur", "principal") -Variables (Get-VariablesAdmin $secrets)
                    if ($r.Code -ne 0) { throw "--code-activation en échec :`n$($r.Texte)" }
                    $code = ($r.Lignes | Where-Object { $_.Trim() } | Select-Object -Last 1).Trim()
                }
                $reglages = @{ active = $true; adresse = $adresseCentral; textesLibres = "filtres"; envoyerMedecins = $false } | ConvertTo-Json
                [void](Invoke-RestMethod -Method Put -Uri "$base/api/admin/synchronisation/reglages" -Headers $entetes -ContentType "application/json" -Body $reglages)
                try { [void](Invoke-RestMethod -Method Post -Uri "$base/api/admin/synchronisation/activation" -Headers $entetes -ContentType "application/json" -Body (@{ code = $code } | ConvertTo-Json)) }
                catch { throw "Activation refusée : $($_.ErrorDetails.Message)" }
                Write-Ok "Réglages enregistrés, code d'activation utilisé, certificat de l'officine reçu."
            }
        }
        finally { if ($compteEssai) { Disable-CompteEssaiActivation } }
        Write-Info "Le chargement complet des données part en arrière-plan (60 lots par minute au plus : environ 7 minutes pour la copie de démonstration). Suivre avec -Etat."
        $etat = Lire-Etat
        $etat | Add-Member -NotePropertyName OfficineActivee -NotePropertyValue $true -Force
        Enregistrer-Etat $etat
    }

    Write-Etape2 "Résumé"
    Write-Info "Serveur central d'essai : https://${AdresseLocale}:$PortCentralUtilise   base $BaseCentrale"
    Write-Info "API de l'officine d'essai : http://${AdresseLocale}:$PortApiUtilise   base $BaseOfficine (copie de $BaseSource)"
    Write-Info "Dossier d'essai (journaux, autorité, PID) : $DossierEssai"
    Write-Info ""
    Write-Info "Brancher un poste sur l'officine d'essai (dans une fenêtre PowerShell) :"
    Write-Info "   `$env:PHARMA_API = `"http://${AdresseLocale}:$PortApiUtilise/`" ; dotnet run --project src\Pharma.Desktop"
    Write-Info "Identifiants de démonstration : docs\poste-de-developpement.md, « Comptes de démonstration »."
    Write-Info ""
    Write-Info "Commandes utiles :"
    Write-Info "   .\scripts\dev\essai_cloud_officine.ps1 -Etat"
    Write-Info "   .\scripts\dev\essai_cloud_officine.ps1 -Central --reglages"
    Write-Info "   .\scripts\dev\essai_cloud_officine.ps1 -Requete `"SELECT count(*) FROM lot_recu`" -Sur central"
    Write-Info "   .\scripts\dev\essai_cloud_officine.ps1 -Arreter"
}

function Wait-Ecoute([int]$Port, $Processus, [string]$Nom, [string]$JournalErreur) {
    $limite = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $limite) {
        if ($Processus.HasExited) { throw "Le serveur $Nom s'est arrêté au démarrage. Dernières lignes de $JournalErreur :`n$((Get-Content -LiteralPath $JournalErreur -Tail 12 -ErrorAction SilentlyContinue) -join "`n")" }
        if (Test-PortOuvert $Port) { return }
        Start-Sleep -Milliseconds 700
    }
    throw "Le serveur $Nom n'écoute pas sur le port $Port après 90 s (voir $JournalErreur)."
}

# ------------------------------------------------------------------ -Etat
function Invoke-Lecture([string]$Base, [string]$Sql, [string]$Officine) {
    $a = @("-c", "SET default_transaction_read_only = on")
    if ($Officine) {
        $g = [guid]::Empty
        if (-not [guid]::TryParse($Officine, [ref]$g)) { throw "Code d'officine invalide." }
        $a += @("-c", "SET app.officine_id = '$g'")
    }
    $a += @("-c", $Sql)
    $r = Invoke-Psql -Compte $CompteBase -Base $Base -Arguments $a -Tolerant
    return $r.Texte
}

function Get-OfficineActive {
    $id = Get-Scalaire -Compte $CompteBase -Base $BaseOfficine -Sql "SELECT COALESCE(officine_id::text, '') FROM sync_parametre WHERE id = 1"
    if ($id) { return $id }
    return ""
}

function Invoke-Etat {
    if (-not (Test-Path -LiteralPath $FichierEtat)) { Write-Info "Aucun essai en cours (dossier $DossierEssai absent). Lancer -Demarrer."; return }
    $etat = Lire-Etat
    Write-Etape2 "Processus"
    $c = Get-ProcessusEssai ([int]$etat.PidCentral) "Pharma.Cloud.Api"
    $a = Get-ProcessusEssai ([int]$etat.PidApi) "Pharma.Api"
    Write-Info ("Serveur central : {0}   https://127.0.0.1:{1}   {2}" -f $(if ($c) { "EN MARCHE (PID $($c.Id))" } else { "ARRETE" }), $etat.PortCentral, $(if ($c -and (Test-PortOuvert ([int]$etat.PortCentral))) { "port ouvert" } else { "port fermé" }))
    $sante = "sans réponse"
    if ($a) { try { $sante = (Invoke-RestMethod -Uri "http://${AdresseLocale}:$($etat.PortApi)/api/sante" -TimeoutSec 5).statut } catch { $sante = "sans réponse" } }
    Write-Info ("API officine   : {0}   http://127.0.0.1:{1}   santé : {2}" -f $(if ($a) { "EN MARCHE (PID $($a.Id))" } else { "ARRETEE" }), $etat.PortApi, $sante)
    Initialize-Pg
    if (-not (Test-BaseExiste $BaseOfficine)) { Write-Avert "La base $BaseOfficine n'existe pas."; return }
    Write-Etape2 "Officine : synchronisation"
    Write-Host (Invoke-Lecture $BaseOfficine "SELECT active AS activee, officine_id, inscrite_le, dernier_envoi_le, dernier_accuse_le, dernier_lot, dernier_accuse, derniere_erreur, derniere_erreur_le FROM sync_parametre" "")
    Write-Etape2 "Officine : file d'attente (sync_sortie) et lots (sync_lot)"
    Write-Host (Invoke-Lecture $BaseOfficine "SELECT count(*) FILTER (WHERE lot_numero IS NULL) AS lignes_hors_lot, count(*) FILTER (WHERE lot_numero IS NOT NULL) AS lignes_en_lot, count(*) AS total FROM sync_sortie" "")
    Write-Host (Invoke-Lecture $BaseOfficine "SELECT count(*) AS lots, count(*) FILTER (WHERE accuse_le IS NULL) AS non_accuses, max(numero) AS dernier_numero, max(accuse_le) AS dernier_accuse FROM sync_lot" "")
    if (-not (Test-BaseExiste $BaseCentrale)) { Write-Avert "La base $BaseCentrale n'existe pas."; return }
    $off = Get-OfficineActive
    if (-not $off) { $off = [string]$etat.OfficineProvisoire }
    Write-Etape2 "Central : lots reçus par officine (lot_recu)"
    if ($off) {
        Write-Host (Invoke-Lecture $BaseCentrale "SELECT officine_id, count(*) AS lots, sum(nb_lignes) AS lignes, max(numero) AS dernier_lot, max(recu_le) AS dernier_recu FROM lot_recu GROUP BY officine_id" $off)
        Write-Host (Invoke-Lecture $BaseCentrale "SELECT numero, motif, etat, debut_le, fin_le, octets_recus FROM chargement ORDER BY numero DESC LIMIT 3" $off)
    }
    else { Write-Info "Officine pas encore activée." }
    Write-Etape2 "Central : dernières lignes du journal de sécurité"
    Write-Host (Invoke-Lecture $BaseCentrale "SELECT le, evenement, left(detail, 80) AS detail FROM journal_securite ORDER BY id DESC LIMIT 5" "")
}

# ------------------------------------------------------------------ -Arreter
function Invoke-Arreter {
    $simul = [bool]$WhatIfPreference
    $dossierPresent = Test-Path -LiteralPath $DossierEssai
    $etat = if ($dossierPresent) { Lire-Etat } else { $null }
    $pids = @()
    if ($etat) {
        if (Get-ProcessusEssai ([int]$etat.PidCentral) "Pharma.Cloud.Api") { $pids += @{ Id = [int]$etat.PidCentral; Nom = "serveur central" } }
        if (Get-ProcessusEssai ([int]$etat.PidApi) "Pharma.Api") { $pids += @{ Id = [int]$etat.PidApi; Nom = "API officine" } }
    }
    if ($simul) {
        Write-Etape2 "Plan (simulation, rien n'est supprimé)"
        foreach ($p in $pids) { Write-Simul "arrêt du $($p.Nom) (PID $($p.Id))" }
        if ($GarderBases) { Write-Simul "bases, rôle et dossier d'essai conservés" }
        else { foreach ($b in $BasesJetables) { Write-Simul "suppression de la base $b" }; Write-Simul "suppression du rôle $RoleCentral ; suppression du dossier $DossierEssai" }
        if (-not $dossierPresent -and $pids.Count -eq 0) { Write-Info "Aucun essai en cours." }
        return
    }
    foreach ($p in $pids) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $p.Id -Timeout 15 -ErrorAction SilentlyContinue
        # Rien n'est supprimé tant qu'un serveur d'essai tourne encore (processus lancé avec d'autres droits : l'arrêter soi-même)
        if (Get-Process -Id $p.Id -ErrorAction SilentlyContinue) { throw "Le $($p.Nom) (PID $($p.Id)) n'a pas pu être arrêté (droits insuffisants ?). Arrêter ce processus soi-même (Stop-Process -Id $($p.Id) -Force, dans une fenêtre PowerShell lancée avec les mêmes droits que lui), puis relancer -Arreter. Aucune base n'a été supprimée." }
        Write-Ok "$($p.Nom) arrêté (PID $($p.Id))."
    }
    if ($etat) {
        $etat.PidCentral = 0; $etat.PidApi = 0
        Enregistrer-Etat $etat
    }
    if ($GarderBases) {
        if ($dossierPresent) { Write-Ok "Bases, rôle et dossier d'essai conservés ($DossierEssai). -Demarrer reprend l'essai." }
        else { Write-Info "Aucun essai en cours." }
        return
    }
    Initialize-Pg
    $restes = @($BasesJetables | Where-Object { Test-BaseExiste $_ })
    $roleExiste = (Get-Scalaire -Compte $CompteBase -Base $BaseMaintenance -Sql "SELECT count(*) FROM pg_roles WHERE rolname = '$RoleCentral'") -ne "0"
    if (-not $dossierPresent -and $restes.Count -eq 0 -and -not $roleExiste) { Write-Info "Aucun essai en cours : rien à faire."; return }
    foreach ($b in $restes) { Remove-BaseJetable $b; Write-Ok "Base $b supprimée." }
    if ($roleExiste) {
        [void](Invoke-Psql -Compte $CompteBase -Base $BaseMaintenance -Arguments @("-c", "DROP ROLE IF EXISTS $RoleCentral"))
        Write-Ok "Rôle $RoleCentral supprimé."
    }
    if ($dossierPresent) {
        # Seul le dossier d'essai exact peut être supprimé
        if ((Split-Path $DossierEssai -Leaf) -ne "pharma-essai-cloud" -or (Split-Path $DossierEssai -Parent) -ne $env:TEMP) { throw "Suppression refusée : dossier inattendu." }
        Remove-Item -LiteralPath $DossierEssai -Recurse -Force
        Write-Ok "Dossier d'essai supprimé."
    }
    Write-Info "Il ne reste rien de l'essai."
}

function Invoke-ArreterCentral {
    if (-not (Test-Path -LiteralPath $FichierEtat)) { Write-Info "Aucun essai en cours : rien à faire."; return }
    $etat = Lire-Etat
    $p = Get-ProcessusEssai ([int]$etat.PidCentral) "Pharma.Cloud.Api"
    if (-not $p) { Write-Info "Le serveur central d'essai est déjà arrêté."; return }
    if ($WhatIfPreference) { Write-Simul "arrêt du serveur central (PID $($p.Id))"; return }
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Wait-Process -Id $p.Id -Timeout 15 -ErrorAction SilentlyContinue
    $etat.PidCentral = 0
    Enregistrer-Etat $etat
    Write-Ok "Serveur central arrêté (PID $($p.Id)). L'officine continue ; -Demarrer le relance."
}

# ------------------------------------------------------------------ -Central / -Requete
function Invoke-CentralAdmin {
    if (-not (Test-Path -LiteralPath $FichierSecrets)) { throw "Aucun essai en cours : lancer -Demarrer." }
    if (-not (Test-Path -LiteralPath $ExeCentral)) { throw "Serveur central d'essai non compilé : lancer -Demarrer." }
    Initialize-Pg
    $r = Invoke-Enfant -Exe $ExeCentral -Arguments $ArgumentsCentral -Variables (Get-VariablesAdmin (Lire-Json $FichierSecrets))
    $r.Lignes | ForEach-Object { Write-Host $_ }
    if ($r.Code -ne 0) { throw "Commande d'administration en échec (code $($r.Code))." }
}

function Invoke-Requete {
    if (-not (Test-Path -LiteralPath $FichierEtat)) { throw "Aucun essai en cours : lancer -Demarrer." }
    Initialize-Pg
    if ($Sur -eq "officine") { Write-Host (Invoke-Lecture $BaseOfficine $Requete "") }
    else {
        $off = Get-OfficineActive
        if (-not $off) { $off = [string](Lire-Etat).OfficineProvisoire }
        Write-Host (Invoke-Lecture $BaseCentrale $Requete $off)
    }
}

# ------------------------------------------------------------------ principal
try {
    $actions = @(@([bool]$Demarrer, [bool]$Etat, [bool]$Arreter, [bool]$ArreterCentral, [bool]$Central, [bool]$Requete) | Where-Object { $_ })
    if ($Central -and (-not $ArgumentsCentral)) { throw "-Central attend une commande, par exemple : -Central --reglages" }
    if ($actions.Count -ne 1) {
        Write-Host "OUTIL DE DEVELOPPEMENT, JAMAIS DE PRODUCTION. Une seule action : -Demarrer, -Etat, -Arreter [-GarderBases], -ArreterCentral, -Central <arguments>, -Requete <sql> [-Sur officine|central]. Option -WhatIf. Aide : Get-Help $PSCommandPath -Full"
        exit 1
    }
    Write-Host "OUTIL DE DEVELOPPEMENT, JAMAIS DE PRODUCTION : essai local, bases jetables, adresses 127.0.0.1 seulement." -ForegroundColor Yellow
    $script:Simulation = [bool]$WhatIfPreference
    if ($Demarrer) { Invoke-Demarrer }
    elseif ($Arreter) { Invoke-Arreter }
    elseif ($ArreterCentral) { Invoke-ArreterCentral }
    elseif ($Etat) { Invoke-Etat }
    elseif ($Central) { Invoke-CentralAdmin }
    elseif ($Requete) { Invoke-Requete }
    exit 0
}
catch {
    Write-Host ""
    Write-Host "ECHEC : $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "L'essai reste en l'état : -Etat pour voir, -Demarrer pour reprendre, -Arreter pour tout supprimer." -ForegroundColor Red
    exit 1
}
