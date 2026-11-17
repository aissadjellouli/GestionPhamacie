<#
.SYNOPSIS
    2e serveur : recopie la GED (documents) du serveur principal vers ce serveur, sans jamais rien supprimer, à la fréquence réglée dans l'application.

.DESCRIPTION
    La GED ne va JAMAIS au serveur central (décision du titulaire) : le 2e serveur la reçoit du principal pour pouvoir reprendre
    l'activité après une bascule (docs/mise-en-production.md, parcours C et procédures F3 et F4).

    À lancer SUR LE SERVEUR QUI REÇOIT (le 2e serveur, ou le serveur réparé en retour de bascule) : il TIRE les fichiers depuis le
    partage en lecture seule du serveur qui est principal à ce moment-là (partager-ged.ps1, lancé sur ce principal).
      - copie les fichiers NOUVEAUX et MODIFIÉS (robocopy /E /XO), même règle que la copie vers le disque externe ;
      - ne supprime JAMAIS rien : ni /MIR, ni /PURGE ; un fichier supprimé à la source reste à la destination ;
      - ne copie jamais un fichier plus ancien par-dessus un plus récent (/XO) ;
      - source injoignable : refus clair, rien n'est touché (code 1) ; nouvelles tentatives limitées (-Tentatives, -AttenteSecondes) ;
      - journal court : un fichier par mois dans -DossierJournaux (une ligne par copie, le détail seulement si quelque chose a été
        copié ou a échoué) ; jamais de mot de passe dans le journal.

    FRÉQUENCE (décision du titulaire, 2026-11-17) : « Copier la GED toutes les N minutes », N de 1 à 60, 5 par défaut, réglé dans
    l'application (Administration > 2e serveur > Copie de la GED). Ce serveur a une copie répliquée de la base : le script lit N
    dans SA base locale (table surveillance_replication, colonne ged_copie_minutes), EN LECTURE SEULE, avec le rôle « pharma_ged_lecture »
    (droit de lire cette seule colonne, sans mot de passe : voir autoriser-lecture-copie-ged.ps1 et docs/mise-en-production.md).
    La tâche planifiée se déclenche CHAQUE MINUTE ; le script ne copie que si N minutes se sont écoulées depuis le début de la dernière
    copie RÉUSSIE (repère : fichier derniere-copie-ged.txt dans -DossierJournaux ; tolérance de 20 secondes). Sinon il sort aussitôt
    (code 0, sans journal). Une copie refusée (principal éteint) ou en échec n'est retentée qu'après N minutes (journal court). Si la lecture du réglage échoue (base arrêtée, rôle absent...), il prend 5 minutes, le journalise (une fois,
    puis au retour à la normale) et ne bloque jamais la copie. Un réglage modifié est pris en compte au passage suivant (une minute).
    Priorité : -Maintenant (copie tout de suite, repère ignoré) > -ToutesLesMinutes (impose N, pour les essais) > réglage de
    l'application > 5 minutes par défaut. -Maintenant et -ToutesLesMinutes ne servent qu'en ligne de commande (copie immédiate, essais) :
    la tâche installée n'en porte aucun.

    Code de sortie : 0 = terminé (copie faite, rien à copier, ou pas encore l'heure) ; 1 = refus avant toute copie (source injoignable,
    paramètre invalide, mauvais usage) ; 2 = copie faite mais des fichiers n'ont pas pu être copiés (voir le journal).

    -Essai : simulation (robocopy /L), n'écrit rien (ni fichier, ni journal, ni repère, ni tâche) ; ignore le repère de dernière copie.
    -Installer : crée la tâche planifiée Windows « copie de la GED » (déclenchée chaque minute ; la fréquence réelle est le réglage de
      l'application, 5 minutes par défaut), exécutée par un COMPTE LOCAL DÉDIÉ (-Compte, « PharmaGedCopie » par défaut), sans droit
      d'administrateur : lecture seule sur le partage du principal (même nom et même mot de passe que sur le principal, créé par
      partager-ged.ps1), modification de la seule GED locale et du dossier de journaux de la copie. Relancé, il REMPLACE la tâche
      (pas de doublon), y compris l'ancienne tâche de 5 minutes. Le mot de passe est demandé (jamais écrit dans un fichier ni dans
      le journal ; il n'est gardé que par le Planificateur de tâches de Windows).
    -Desinstaller : supprime la tâche (le compte et les fichiers copiés sont gardés).

    Une seule direction à la fois : la tâche n'existe que sur le serveur qui est SECOND. Après une bascule (F3) : -Desinstaller sur le
    2e serveur devenu principal ; après le retour (F4) : -Installer sur le serveur réparé, qui tire depuis le nouveau principal.

.EXAMPLE
    .\copier-ged.ps1 -Source \\192.168.1.20\PharmaGED$ -Essai
.EXAMPLE
    .\copier-ged.ps1 -Source \\192.168.1.20\PharmaGED$ -Maintenant
.EXAMPLE
    .\copier-ged.ps1 -Source \\192.168.1.20\PharmaGED$ -Installer -Essai
.EXAMPLE
    .\copier-ged.ps1 -Source \\192.168.1.20\PharmaGED$ -Installer
.EXAMPLE
    .\copier-ged.ps1 -Source \\192.168.1.20\PharmaGED$ -Desinstaller
#>
param(
    [Parameter(Mandatory)] [string]$Source,            # \\<principal>\<partage> de la GED (lecture seule) ; un chemin local sert aux essais
    [string]$Destination            = "",              # GED locale ; vide : Ged__Dossier de la machine, sinon C:\ProgramData\PharmaOfficine\GED
    [string]$SourceOrdonnances      = "",              # seulement si Ordonnances__Dossier sort de la GED sur le principal
    [string]$DestinationOrdonnances = "",              # idem, dossier local correspondant
    [string]$DossierJournaux        = "",              # vide : C:\ProgramData\PharmaOfficine\Journaux\Copie-GED
    [int]   $Tentatives             = 3,               # nouvelles tentatives par fichier (robocopy /R)
    [int]   $AttenteSecondes        = 10,              # attente entre deux tentatives (robocopy /W)
    [string]$Compte                 = "PharmaGedCopie",
    [string]$NomTache               = "PharmaOfficine copie de la GED (2e serveur)",
    [int]   $ToutesLesMinutes       = 0,               # 0 : réglage de l'application ; 1 à 60 : impose N (essais seulement)
    [string]$HoteBase               = "localhost",     # base de CE serveur (copie répliquée du principal), lue en lecture seule
    [int]   $PortBase               = 5432,
    [string]$Base                   = "pharmacie",
    [string]$CompteLecture          = "pharma_ged_lecture",
    [string]$PgBin                  = "",
    [switch]$Maintenant,                               # copie tout de suite, sans regarder la dernière copie ni le réglage
    [switch]$Installer,
    [switch]$Desinstaller,
    [switch]$Essai
)
. (Join-Path $PSScriptRoot "commun.ps1")

$mode = if ($Essai) { "[ESSAI : rien n'est modifié] " } else { "" }
if (-not $DossierJournaux) { $DossierJournaux = Join-Path $env:ProgramData "PharmaOfficine\Journaux\Copie-GED" }
$MinutesParDefaut = 5
$ToleranceSecondes = 20
$fichierEtat = Join-Path $DossierJournaux "derniere-copie-ged.txt"

function Ecrire-Journal([string]$ligne, [string[]]$details = @()) {
    if ($Essai) { return }
    try {
        New-Item -ItemType Directory -Force -Path $DossierJournaux | Out-Null
        $fichier = Join-Path $DossierJournaux ("copie-ged-{0:yyyyMM}.log" -f (Get-Date))
        Add-Content -LiteralPath $fichier -Value (@("{0:yyyy-MM-dd HH:mm:ss} | {1}" -f (Get-Date), $ligne) + $details) -Encoding UTF8
    } catch { Write-Warning "Journal non écrit : $($_.Exception.Message)" }
}

# Refus : message clair, journal, code 1, rien n'a été copié
function Refuser([string]$message) {
    Write-Host "REFUS : $message" -ForegroundColor Red
    Ecrire-Journal "REFUS : $message"
    exit 1
}

# Dossier joignable ? (Test-Path lève une erreur quand le réseau ou le serveur est introuvable : c'est un « non »)
function Test-Dossier([string]$chemin) {
    try { return [bool](Test-Path -LiteralPath $chemin -PathType Container -ErrorAction Stop) } catch { return $false }
}

# Chemin complet, sans « \ » final (qui casserait la ligne de commande de robocopy) ; jamais une racine de disque
function Normaliser([string]$chemin, [string]$nom) {
    if (-not $chemin) { Refuser "$nom : chemin vide." }
    try { $complet = [IO.Path]::GetFullPath($chemin) } catch { Refuser "$nom : chemin invalide « $chemin » (attendu : \\serveur\partage ou C:\dossier)." }
    if ($complet.TrimEnd('\') -match '^[A-Za-z]:?$' -or $complet -match '^\\\\[^\\]+\\?$') { Refuser "$nom : « $chemin » est une racine (disque ou serveur) : indiquer le dossier de la GED." }
    return $complet.TrimEnd('\')
}

# Fréquence réglée dans l'application, lue dans la base LOCALE (copie répliquée : lecture seule par nature, et session en lecture
# seule par précaution). Rôle sans mot de passe, limité à la lecture de cette colonne. Retourne Minutes (1 à 60, sinon 5), Erreur.
function Lire-Reglage {
    $retour = [pscustomobject]@{ Minutes = $MinutesParDefaut; Erreur = $null }
    $anciens = @{ PGCONNECT_TIMEOUT = $env:PGCONNECT_TIMEOUT; PGOPTIONS = $env:PGOPTIONS; PGCLIENTENCODING = $env:PGCLIENTENCODING }
    $preference = $ErrorActionPreference
    try {
        $psql = Join-Path (Get-PgBin $PgBin) "psql.exe"
        $env:PGCONNECT_TIMEOUT = "5"
        $env:PGCLIENTENCODING = "UTF8"
        $env:PGOPTIONS = "-c default_transaction_read_only=on -c statement_timeout=5000"
        $ErrorActionPreference = "Continue"
        $sortie = @(& $psql -h $HoteBase -p $PortBase -U $CompteLecture -d $Base -w -X -qtA -v ON_ERROR_STOP=1 `
            -c "SELECT ged_copie_minutes FROM surveillance_replication WHERE id = 1" 2>&1)
        $codePsql = $LASTEXITCODE
        $ErrorActionPreference = $preference
        $texte = (@($sortie | ForEach-Object { "$_".Trim() } | Where-Object { $_ })) -join " "
        if ($codePsql -ne 0) { throw "psql code $codePsql : $texte" }
        $valeur = 0
        if (-not [int]::TryParse($texte, [ref]$valeur) -or $valeur -lt 1 -or $valeur -gt 60) { throw "valeur illisible ou hors de 1 à 60 : « $texte »" }
        $retour.Minutes = $valeur
    } catch {
        $retour.Erreur = ("$($_.Exception.Message)" -replace '\s+', ' ')
        if ($retour.Erreur.Length -gt 300) { $retour.Erreur = $retour.Erreur.Substring(0, 300) }
    } finally {
        $ErrorActionPreference = $preference
        $env:PGCONNECT_TIMEOUT = $anciens.PGCONNECT_TIMEOUT; $env:PGOPTIONS = $anciens.PGOPTIONS; $env:PGCLIENTENCODING = $anciens.PGCLIENTENCODING
    }
    return $retour
}

# Repère : début de la dernière copie RÉUSSIE (UTC), dernière tentative en échec ou refusée (UTC : elle n'est retentée qu'après N
# minutes, pour ne pas remplir le journal toutes les minutes quand le principal est éteint) et état de la lecture du réglage ;
# absent ou illisible : copie due
function Lire-Etat {
    $etat = @{ Copie = $null; Echec = $null; Lecture = "ok" }
    try {
        foreach ($l in (Get-Content -LiteralPath $fichierEtat -Encoding UTF8 -ErrorAction Stop)) {
            $k, $v = $l -split '=', 2
            if ($k -eq "copie" -or $k -eq "echec") {
                $d = [datetime]::MinValue
                if ([datetime]::TryParse($v, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind, [ref]$d)) { $etat[$(if ($k -eq "copie") { "Copie" } else { "Echec" })] = $d.ToUniversalTime() }
            }
            if ($k -eq "lecture" -and $v) { $etat.Lecture = $v.Trim() }
        }
    } catch { Write-Verbose "Repère de dernière copie absent ou illisible : copie due." }
    return $etat
}

function Ecrire-Etat($copieUtc, $echecUtc, [string]$lecture) {
    if ($Essai) { return }
    $lignes = @("lecture=$lecture")
    if ($copieUtc) { $lignes += "copie=" + $copieUtc.ToString("o") }
    if ($echecUtc) { $lignes += "echec=" + $echecUtc.ToString("o") }
    try {
        New-Item -ItemType Directory -Force -Path $DossierJournaux | Out-Null
        Set-Content -LiteralPath $fichierEtat -Value $lignes -Encoding UTF8
    } catch { Write-Warning "Repère de dernière copie non écrit : $($_.Exception.Message)" }
}

# --- Réglages locaux -----------------------------------------------------------------------------------------------------------
if ($Installer -and $Desinstaller) { Refuser "-Installer et -Desinstaller ne se combinent pas." }
if ($Tentatives -lt 0 -or $Tentatives -gt 10) { Refuser "-Tentatives doit être compris entre 0 et 10." }
if ($AttenteSecondes -lt 1 -or $AttenteSecondes -gt 120) { Refuser "-AttenteSecondes doit être compris entre 1 et 120." }
if ($ToutesLesMinutes -ne 0 -and ($ToutesLesMinutes -lt 1 -or $ToutesLesMinutes -gt 60)) { Refuser "-ToutesLesMinutes doit être compris entre 1 et 60 (ou absent : réglage de l'application)." }
if (($Installer -or $Desinstaller) -and ($ToutesLesMinutes -ne 0 -or $Maintenant)) { Refuser "-ToutesLesMinutes et -Maintenant ne servent qu'à copier en ligne de commande : la fréquence de la tâche est le réglage de l'application." }
foreach ($nom in $Base, $CompteLecture) { if ($nom -notmatch '^[A-Za-z_][A-Za-z0-9_]{0,62}$') { Refuser "Nom invalide : « $nom »." } }
if ($PortBase -lt 1 -or $PortBase -gt 65535) { Refuser "-PortBase invalide." }
if ($Compte -notmatch '^[A-Za-z][A-Za-z0-9_.-]{0,19}$') { Refuser "Nom de compte invalide : « $Compte »." }
if ([bool]$SourceOrdonnances -ne [bool]$DestinationOrdonnances) { Refuser "-SourceOrdonnances et -DestinationOrdonnances vont ensemble." }

if (-not $Destination) {
    $Destination = [Environment]::GetEnvironmentVariable("Ged__Dossier", "Machine")
    if (-not $Destination) { $Destination = $env:Ged__Dossier }
    if (-not $Destination) { $Destination = Join-Path $env:ProgramData "PharmaOfficine\GED" }
}
$Source = Normaliser $Source "Source"
$Destination = Normaliser $Destination "Destination"
$paires = @([pscustomobject]@{ Nom = "GED"; Source = $Source; Destination = $Destination })
if ($SourceOrdonnances) {
    $paires += [pscustomobject]@{ Nom = "Ordonnances"; Source = (Normaliser $SourceOrdonnances "SourceOrdonnances"); Destination = (Normaliser $DestinationOrdonnances "DestinationOrdonnances") }
}
foreach ($p in $paires) {
    $a = $p.Source + '\'; $b = $p.Destination + '\'
    if ($a.StartsWith($b, [StringComparison]::OrdinalIgnoreCase) -or $b.StartsWith($a, [StringComparison]::OrdinalIgnoreCase)) {
        Refuser "$($p.Nom) : la source et la destination sont le même dossier (ou l'une est dans l'autre) : $($p.Source) / $($p.Destination)."
    }
}

# --- Tâche planifiée (-Installer / -Desinstaller) --------------------------------------------------------------------------------
if ($Installer -or $Desinstaller) {
    if (-not $Essai) {
        $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        if (-not $admin) { throw "À lancer dans PowerShell en administrateur." }
    }
    $existante = Get-ScheduledTask -TaskName $NomTache -ErrorAction SilentlyContinue

    if ($Desinstaller) {
        if (-not $existante) { Write-Host "Aucune tâche « $NomTache » : rien à supprimer." }
        elseif ($Essai) { Write-Etape $mode "La tâche « $NomTache » serait supprimée (le compte $Compte et les fichiers copiés sont gardés)." }
        else {
            Unregister-ScheduledTask -TaskName $NomTache -Confirm:$false
            Write-Host "Tâche « $NomTache » supprimée. Le compte $Compte et les fichiers copiés sont gardés."
        }
        exit 0
    }

    Write-Host ("Tâche actuelle : {0}" -f $(if ($existante) { "« $NomTache » existe (elle sera remplacée, pas de doublon)" } else { "aucune" }))
    if (-not (Test-Dossier $Source)) {
        Write-Warning "La source $Source n'est pas joignable depuis cette session (vérifier : partager-ged.ps1 lancé sur le principal, pare-feu 445, compte $Compte)."
    }
    $compteExiste = [bool](Get-LocalUser -Name $Compte -ErrorAction SilentlyContinue)
    # La tâche ne porte ni -Maintenant ni -ToutesLesMinutes : la fréquence est celle de l'application
    $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -Source "{1}" -Destination "{2}" -DossierJournaux "{3}" -Tentatives {4} -AttenteSecondes {5}' -f `
        $PSCommandPath, $Source, $Destination, $DossierJournaux, $Tentatives, $AttenteSecondes
    $arguments += ' -HoteBase "{0}" -PortBase {1} -Base "{2}" -CompteLecture "{3}"' -f $HoteBase, $PortBase, $Base, $CompteLecture
    if ($PgBin) { $arguments += ' -PgBin "{0}"' -f $PgBin }
    if ($SourceOrdonnances) { $arguments += ' -SourceOrdonnances "{0}" -DestinationOrdonnances "{1}"' -f $paires[1].Source, $paires[1].Destination }
    $lu = Lire-Reglage

    if ($Essai) {
        Write-Etape $mode ("Compte local « {0} » : {1}." -f $Compte, $(if ($compteExiste) { "existe déjà" } else { "serait créé (aucun groupe, mot de passe demandé, sans expiration)" }))
        Write-Etape $mode "Droit « modification » donné à $Compte sur $Destination et sur $DossierJournaux (et sur $($paires[-1].Destination) si les ordonnances sont à part)."
        Write-Etape $mode ("Réglage de l'application : {0}." -f $(if ($lu.Erreur) { "illisible ($($lu.Erreur)) : 5 minutes par défaut ; voir autoriser-lecture-copie-ged.ps1" } else { "toutes les $($lu.Minutes) minute(s)" }))
        Write-Etape $mode "Tâche « $NomTache » : déclenchée chaque minute (copie seulement si la fréquence de l'application, 5 minutes par défaut, est écoulée), compte $env:COMPUTERNAME\$Compte (mot de passe demandé, gardé par Windows), sans droit d'administrateur, une seule copie à la fois."
        Write-Etape $mode "Commande : powershell.exe $arguments"
        Write-Host "$mode Simulation terminée : aucune tâche, aucun compte, aucun droit n'a été créé."
        exit 0
    }

    Write-Host "Mot de passe du compte local $Compte : le MÊME que sur le serveur principal (partager-ged.ps1). Il n'est écrit nulle part."
    $mdp = Read-MotDePasse "Mot de passe du compte $Compte"
    if (-not $compteExiste) {
        if ($mdp.Length -lt 14) { throw "Mot de passe trop court (14 caractères au moins)." }
        if ($mdp -ne (Read-MotDePasse "Confirmer le mot de passe")) { throw "Les deux saisies sont différentes." }
        New-LocalUser -Name $Compte -Password (ConvertTo-SecureString $mdp -AsPlainText -Force) -PasswordNeverExpires -UserMayNotChangePassword `
            -AccountNeverExpires -Description "Copie de la GED vers le 2e serveur (copier-ged.ps1)" | Out-Null
        Write-Host "Compte local $Compte créé (aucun groupe, pas d'administrateur)."
    }
    # Droits NTFS : modification de la GED locale et des journaux de la copie seulement (ajout, rien n'est retiré)
    $dossiersDroits = @($paires | ForEach-Object { $_.Destination }) + $DossierJournaux
    foreach ($d in $dossiersDroits) {
        New-Item -ItemType Directory -Force -Path $d | Out-Null
        & icacls.exe $d /grant "${Compte}:(OI)(CI)M" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Droit de modification non accordé à $Compte sur $d." }
    }
    $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $arguments -WorkingDirectory $PSScriptRoot
    $declencheur = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1) -RepetitionDuration (New-TimeSpan -Days 3650)
    $reglages = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 4)
    Register-ScheduledTask -TaskName $NomTache -Action $action -Trigger $declencheur -Settings $reglages -User "$env:COMPUTERNAME\$Compte" -Password $mdp `
        -RunLevel Limited -Description "Copie de la GED du principal vers ce serveur, sans suppression, à la fréquence réglée dans l'application (scripts/second-serveur/copier-ged.ps1)" -Force | Out-Null
    $mdp = $null
    Write-Host "Tâche « $NomTache » créée : déclenchée chaque minute, sous le compte $Compte ; la copie suit le réglage de l'application (5 minutes par défaut), première copie dans 1 minute."
    if ($lu.Erreur) { Write-Warning "Réglage de l'application illisible depuis ce serveur ($($lu.Erreur)) : la copie se fera toutes les 5 minutes tant que la lecture échoue. Voir autoriser-lecture-copie-ged.ps1." }
    else { Write-Host "Réglage actuel de l'application : toutes les $($lu.Minutes) minute(s)." }
    Write-Host "Contrôle : Get-ScheduledTaskInfo -TaskName '$NomTache' (LastTaskResult 0 = bon) et le journal $DossierJournaux."
    exit 0
}

# --- Copie ---------------------------------------------------------------------------------------------------------------------
# Fréquence : copie seulement si N minutes se sont écoulées depuis le début de la dernière copie réussie
# (priorité : -Maintenant, puis -ToutesLesMinutes, puis le réglage de l'application, puis 5 minutes)
$etat = Lire-Etat
$lecture = "ok"
if ($Maintenant) { $reglage = "copie immédiate (-Maintenant)" }
else {
    if ($ToutesLesMinutes -gt 0) { $minutes = $ToutesLesMinutes; $reglage = "$minutes min (ligne de commande)" }
    else {
        $lu = Lire-Reglage
        $minutes = $lu.Minutes
        if ($lu.Erreur) {
            $lecture = "echec"; $reglage = "$minutes min (défaut : réglage illisible)"
            Write-Warning "Réglage de l'application illisible ($($lu.Erreur)) : $minutes minutes par défaut."
            if ($etat.Lecture -ne "echec") { Ecrire-Journal "Réglage de la fréquence illisible : $($lu.Erreur) ; 5 minutes par défaut jusqu'au retour de la lecture." }
        } else {
            $reglage = "$minutes min (application)"
            if ($etat.Lecture -eq "echec") { Ecrire-Journal "Réglage de la fréquence de nouveau lisible : $minutes minute(s)." }
        }
    }
    $maintenantUtc = (Get-Date).ToUniversalTime()
    $ecoule = if ($etat.Copie) { ($maintenantUtc - $etat.Copie).TotalSeconds } else { [double]::PositiveInfinity }
    $ecouleEchec = if ($etat.Echec) { ($maintenantUtc - $etat.Echec).TotalSeconds } else { [double]::PositiveInfinity }
    $limite = $minutes * 60 - $ToleranceSecondes
    # Une date dans le futur (horloge changée) n'empêche jamais la copie
    if (-not $Essai -and (($ecoule -ge 0 -and $ecoule -lt $limite) -or ($ecouleEchec -ge 0 -and $ecouleEchec -lt $limite))) {
        Write-Host ("Pas encore l'heure : dernière copie réussie il y a {0:N0} s, fréquence {1}." -f $ecoule, $reglage)
        if ($lecture -ne $etat.Lecture) { Ecrire-Etat $etat.Copie $etat.Echec $lecture }
        exit 0
    }
}

# Contrôles AVANT toute copie : source joignable (sinon refus, rien n'est touché ; retenté après N minutes seulement)
$debutPassage = (Get-Date).ToUniversalTime()
foreach ($p in $paires) {
    if (-not (Test-Dossier $p.Source)) {
        Ecrire-Etat $etat.Copie $debutPassage $lecture
        Refuser "$($p.Nom) : source injoignable ($($p.Source)). Rien n'a été copié ni modifié. Vérifier le serveur principal, le partage (partager-ged.ps1), le pare-feu (445) et le compte."
    }
}

$serviceApi = Get-Service "PharmaOfficine API" -ErrorAction SilentlyContinue
if ($serviceApi -and $serviceApi.Status -eq "Running") {
    Write-Warning "Le service de l'API tourne sur ce serveur : ce serveur est-il bien celui qui doit RECEVOIR la GED ? (sans suppression : rien n'est perdu)."
}

$pire = 0
foreach ($p in $paires) {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("copier-ged-{0}.log" -f [guid]::NewGuid().ToString("N"))
    # Jamais /MIR ni /PURGE : seulement ajout et mise à jour. /XO : jamais un fichier plus ancien par-dessus un plus récent.
    $robocopy = @($p.Source, $p.Destination, "/E", "/XO", "/COPY:DAT", "/DCOPY:DAT", "/XJ", "/R:$Tentatives", "/W:$AttenteSecondes",
                  "/XD", '$RECYCLE.BIN', "System Volume Information", "/NP", "/NDL", "/NJH", "/LOG:$tmp")
    if ($Essai) { $robocopy += "/L" }
    $debut = Get-Date
    & robocopy.exe @robocopy | Out-Null
    $code = $LASTEXITCODE
    $detail = if (Test-Path -LiteralPath $tmp) { @(Get-Content -LiteralPath $tmp -Encoding Default | Where-Object { $_.Trim() }) } else { @() }
    Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue

    $copie = ($code -band 1) -ne 0
    $echec = $code -ge 8
    $resume = if ($echec) { "ECHEC (code robocopy $code)" } elseif ($copie) { if ($Essai) { "fichiers à copier (simulation)" } else { "fichiers copiés" } } else { "rien à copier" }
    Write-Etape $mode ("{0} : {1} ({2:N0} s)." -f $p.Nom, $resume, ((Get-Date) - $debut).TotalSeconds)
    if ($copie -or $echec) { $detail | Select-Object -First 40 | ForEach-Object { Write-Host "    $_" } }
    if ($echec) { Write-Host "ERREUR : des fichiers n'ont pas été copiés ; relancer, ou lire le journal $DossierJournaux." -ForegroundColor Red }
    # Journal : une ligne par copie ; le détail (200 lignes au plus) seulement si copie ou échec
    Ecrire-Journal ("{0} | {1} -> {2} | {3} (code robocopy {4}) | fréquence : {5}" -f $p.Nom, $p.Source, $p.Destination, $resume, $code, $reglage) $(if ($copie -or $echec) { $detail | Select-Object -First 200 | ForEach-Object { "    $_" } } else { @() })
    if ($echec) { $pire = 2 }
}
# Repère : seulement si TOUT a été copié (une copie en échec est retentée au passage suivant)
if ($pire -eq 0) { Ecrire-Etat $debutPassage $null $lecture }
else { Ecrire-Etat $etat.Copie $debutPassage $lecture }
if ($Essai) { Write-Host "$mode Simulation terminée : rien n'a été écrit." }
exit $pire
