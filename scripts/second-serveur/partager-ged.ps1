<#
.SYNOPSIS
    2e serveur : partage la GED du serveur PRINCIPAL en LECTURE SEULE, au seul compte de copie et à la seule adresse du 2e serveur.

.DESCRIPTION
    À lancer sur le serveur qui est principal (ou qui doit être lu : voir F4), en administrateur Windows, AVANT copier-ged.ps1
    (docs/mise-en-production.md, parcours C). Script dédié (et non une option de copier-ged.ps1) : il tourne sur une autre machine,
    avec d'autres droits, et sa simulation (-Essai ou -WhatIf) doit pouvoir être relue seule.
    Crée, chacun sans doublon (relancé, le script REMPLACE) :
      1. le compte local dédié -Compte (« PharmaGedCopie ») : aucun groupe, pas d'administrateur, mot de passe demandé (14 caractères
         au moins, deux saisies, jamais écrit) à saisir à l'identique sur le 2e serveur (copier-ged.ps1 -Installer) ;
      2. un droit NTFS « lecture et exécution » (ajout) de ce compte sur le dossier de la GED : rien n'est retiré ;
      3. le partage SMB masqué (-NomPartage, « PharmaGED$ »), LECTURE SEULE pour ce compte et lui seul : jamais « Tout le monde »,
         pas de contrôle total ni de modification ; accès filtré selon les droits, chiffrement SMB exigé ;
      4. une règle de pare-feu entrante : TCP 445 (SMB), profil Privé, adresse distante = l'IP du 2e serveur, et elle seule.
    Contrôles : avertissement si le partage donne un droit à un autre compte (surtout « Tout le monde ») ; avertissement si une AUTRE
    règle entrante active ouvre le port 445 (le script ne la modifie pas, il la nomme ; elle ne doit pas ouvrir 445 au réseau).
    -DossierOrdonnances : seulement si Ordonnances__Dossier sort de la GED (deuxième partage, -NomPartageOrdonnances).
    -Retirer : supprime les partages et la règle (le compte est gardé : Remove-LocalUser PharmaGedCopie pour le supprimer).
    -Essai (ou -WhatIf) : n'écrit rien, annonce ce qui serait fait. -Confirm : confirmation avant chaque action.

    Changement d'IP du 2e serveur, bascule (F3) ou retour (F4) : relancer ce script avec la nouvelle adresse, sur le serveur qui
    est principal à ce moment-là ; sur l'autre, -Retirer.

.EXAMPLE
    .\partager-ged.ps1 -AdresseSecond 192.168.1.21 -Essai
.EXAMPLE
    .\partager-ged.ps1 -AdresseSecond 192.168.1.21
.EXAMPLE
    .\partager-ged.ps1 -AdresseSecond 192.168.1.21 -Retirer
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = "Medium")]
param(
    [Parameter(Mandatory)] [string]$AdresseSecond,   # IPv4 du serveur qui copie : seule adresse autorisée à joindre le port 445
    [string]$DossierGed                = "",         # vide : Ged__Dossier de la machine, sinon C:\ProgramData\PharmaOfficine\GED
    [string]$DossierOrdonnances        = "",         # seulement si Ordonnances__Dossier sort de la GED
    [string]$NomPartage                = 'PharmaGED$',
    [string]$NomPartageOrdonnances     = 'PharmaOrdonnances$',
    [string]$Compte                    = "PharmaGedCopie",
    [int]   $Port = 445,
    [switch]$Retirer,
    [switch]$Essai
)
. (Join-Path $PSScriptRoot "commun.ps1")
if ($Essai) { $WhatIfPreference = $true }

$NomRegle = "PharmaOfficine SMB 445 GED (2e serveur)"   # nom fixe : la règle est retrouvée et remplacée à chaque lancement

# Une seule adresse IPv4, unicast : refuse plage, masque, liste, « Any », 0.0.0.0, boucle locale, diffusion, multicast
$ip = $null
if ($AdresseSecond -notmatch '^\d{1,3}(\.\d{1,3}){3}$' -or -not [Net.IPAddress]::TryParse($AdresseSecond, [ref]$ip)) {
    throw "Adresse IPv4 invalide : « $AdresseSecond ». Une seule adresse du serveur qui copie (ex. 192.168.1.21), ni plage, ni masque, ni « Any »."
}
$octets = $ip.GetAddressBytes()
if ($octets[0] -eq 0 -or $octets[0] -eq 127 -or $octets[0] -ge 224) {
    throw "Adresse refusée : $AdresseSecond n'est pas l'adresse d'un poste du réseau local."
}
if ($Compte -notmatch '^[A-Za-z][A-Za-z0-9_.-]{0,19}$') { throw "Nom de compte invalide : « $Compte »." }
foreach ($n in @($NomPartage, $NomPartageOrdonnances)) {
    if ($n -notmatch '^[A-Za-z0-9_.-]{1,40}\$?$') { throw "Nom de partage invalide : « $n »." }
}

if (-not $DossierGed) {
    $DossierGed = [Environment]::GetEnvironmentVariable("Ged__Dossier", "Machine")
    if (-not $DossierGed) { $DossierGed = $env:Ged__Dossier }
    if (-not $DossierGed) { $DossierGed = Join-Path $env:ProgramData "PharmaOfficine\GED" }
}
$partages = @([pscustomobject]@{ Nom = $NomPartage; Dossier = [IO.Path]::GetFullPath($DossierGed).TrimEnd('\') })
if ($DossierOrdonnances) { $partages += [pscustomobject]@{ Nom = $NomPartageOrdonnances; Dossier = [IO.Path]::GetFullPath($DossierOrdonnances).TrimEnd('\') } }
foreach ($p in $partages) {
    if ($p.Dossier -match '^[A-Za-z]:?$') { throw "Dossier refusé : $($p.Dossier) est une racine de disque." }
}

$simulation = [bool]$WhatIfPreference
$etiquette = if ($simulation) { "[SIMULATION] " } else { "" }
if (-not $simulation) {
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $admin) { throw "À lancer dans PowerShell en administrateur." }
}

$anciennes = @(Get-NetFirewallRule -DisplayName $NomRegle -ErrorAction SilentlyContinue)

if ($Retirer) {
    foreach ($p in $partages) {
        $existant = Get-SmbShare -Name $p.Nom -ErrorAction SilentlyContinue
        if (-not $existant) { Write-Host "Aucun partage « $($p.Nom) » : rien à retirer." }
        elseif ($PSCmdlet.ShouldProcess($p.Nom, "Supprimer le partage (les documents ne sont pas touchés)")) {
            Remove-SmbShare -Name $p.Nom -Confirm:$false
            Write-Host "Partage « $($p.Nom) » supprimé (les documents ne sont pas touchés)."
        }
        else { Write-Host "${etiquette}Le partage « $($p.Nom) » serait supprimé (les documents ne sont pas touchés)." }
    }
    if ($anciennes.Count -eq 0) { Write-Host "Aucune règle « $NomRegle » : rien à retirer." }
    elseif ($PSCmdlet.ShouldProcess($NomRegle, "Supprimer la règle de pare-feu")) {
        $anciennes | Remove-NetFirewallRule
        Write-Host "Règle « $NomRegle » supprimée : le port $Port n'est plus ouvert au 2e serveur."
    }
    else { Write-Host "${etiquette}$($anciennes.Count) règle(s) « $NomRegle » seraient supprimées." }
}
else {
    foreach ($p in $partages) {
        if (-not (Test-Path -LiteralPath $p.Dossier -PathType Container)) { throw "Dossier de la GED introuvable : $($p.Dossier) (préciser -DossierGed)." }
    }

    # 1. Compte local dédié
    $compteExiste = [bool](Get-LocalUser -Name $Compte -ErrorAction SilentlyContinue)
    if ($compteExiste) { Write-Host "Compte local $Compte : existe déjà (gardé tel quel)." }
    elseif ($PSCmdlet.ShouldProcess($Compte, "Créer le compte local (aucun groupe, mot de passe demandé)")) {
        $mdp = Read-MotDePasse "Mot de passe du nouveau compte $Compte (14 caractères au moins, à saisir à l'identique sur le 2e serveur)"
        if ($mdp.Length -lt 14) { throw "Mot de passe trop court (14 caractères au moins)." }
        if ($mdp -ne (Read-MotDePasse "Confirmer le mot de passe")) { throw "Les deux saisies sont différentes." }
        New-LocalUser -Name $Compte -Password (ConvertTo-SecureString $mdp -AsPlainText -Force) -PasswordNeverExpires -UserMayNotChangePassword `
            -AccountNeverExpires -Description "Lecture de la GED par le 2e serveur (partager-ged.ps1)" | Out-Null
        $mdp = $null
        Write-Host "Compte local $Compte créé (aucun groupe, pas d'administrateur). Noter son mot de passe au coffre."
    }
    else { Write-Host "${etiquette}Le compte local $Compte serait créé (aucun groupe, mot de passe demandé, sans expiration)." }

    foreach ($p in $partages) {
        # 2. Droit NTFS en lecture (ajout : aucun droit existant n'est retiré)
        if ($PSCmdlet.ShouldProcess($p.Dossier, "Ajouter le droit « lecture et exécution » à $Compte (NTFS)")) {
            & icacls.exe $p.Dossier /grant "${Compte}:(OI)(CI)RX" | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Droit de lecture non accordé à $Compte sur $($p.Dossier)." }
            Write-Host "Droit NTFS : $Compte en lecture et exécution sur $($p.Dossier)."
        }
        else { Write-Host "${etiquette}$Compte recevrait le droit « lecture et exécution » (NTFS) sur $($p.Dossier)." }

        # 3. Partage en lecture seule pour ce compte seulement (remplacé sans doublon)
        $existant = Get-SmbShare -Name $p.Nom -ErrorAction SilentlyContinue
        if ($existant) { Write-Host "Partage actuel : « $($p.Nom) » -> $($existant.Path) (sera remplacé)." } else { Write-Host "Partage actuel : aucun « $($p.Nom) »." }
        if ($PSCmdlet.ShouldProcess($p.Nom, "Créer le partage $($p.Dossier) : lecture seule pour $Compte seulement, chiffré, en remplaçant l'ancien")) {
            if ($existant) { Remove-SmbShare -Name $p.Nom -Confirm:$false }
            New-SmbShare -Name $p.Nom -Path $p.Dossier -ReadAccess $Compte -FolderEnumerationMode AccessBased -EncryptData $true -CachingMode None `
                -Description "GED en lecture seule pour le 2e serveur (partager-ged.ps1)" | Out-Null
            Write-Host "Partage « $($p.Nom) » créé : lecture seule, $Compte seulement, chiffrement SMB exigé."
            # Contrôle : aucun autre compte (surtout pas « Tout le monde »), aucun droit autre que Lecture
            $acces = @(Get-SmbShareAccess -Name $p.Nom)
            foreach ($a in $acces) {
                if ($a.AccountName -notlike "*\$Compte" -and $a.AccountName -ne $Compte) {
                    Write-Warning "Le partage « $($p.Nom) » donne un droit à « $($a.AccountName) » ($($a.AccessRight)) : le retirer (Revoke-SmbShareAccess -Name '$($p.Nom)' -AccountName '$($a.AccountName)')."
                }
                elseif ($a.AccessRight -ne "Read") { Write-Warning "Le partage « $($p.Nom) » donne « $($a.AccessRight) » à $Compte : seul « Read » est admis." }
            }
        }
        else { Write-Host "${etiquette}Partage « $($p.Nom) » ($($p.Dossier)) : lecture seule pour $Compte seulement (aucun accès général), chiffrement SMB exigé, remplace l'ancien." }
    }

    # 4. Pare-feu : SMB depuis l'adresse du 2e serveur, et elle seule
    $anciennesAdresses = @($anciennes | ForEach-Object { ($_ | Get-NetFirewallAddressFilter).RemoteAddress }) -join ", "
    if ($anciennes.Count -gt 0) { Write-Host "Règle actuelle : $($anciennes.Count) règle(s) « $NomRegle » (adresse distante : $anciennesAdresses)." }
    else { Write-Host "Règle actuelle : aucune." }
    if ($PSCmdlet.ShouldProcess($NomRegle, "Créer la règle (entrée TCP $Port, profil Privé, adresse distante $AdresseSecond), en remplaçant l'ancienne")) {
        if ($anciennes.Count -gt 0) { $anciennes | Remove-NetFirewallRule }
        New-NetFirewallRule -DisplayName $NomRegle -Direction Inbound -Protocol TCP -LocalPort $Port `
            -RemoteAddress $AdresseSecond -Action Allow -Profile Private `
            -Description "Lecture de la GED par le 2e serveur : cette adresse seulement (scripts/second-serveur/partager-ged.ps1)" | Out-Null
        Write-Host "Pare-feu : port $Port ouvert en entrée, profil Privé, au seul 2e serveur ($AdresseSecond)."
    }
    else {
        Write-Host "${etiquette}Pare-feu : $($anciennes.Count) règle(s) « $NomRegle » remplacée(s) par une règle unique : entrée TCP $Port, profil Privé, adresse distante $AdresseSecond."
    }
}

# Contrôle : aucune autre règle active ne doit ouvrir ce port (au reste du réseau, jamais)
$autres = @(Get-NetFirewallRule -Direction Inbound -Enabled True -Action Allow -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -ne $NomRegle } |
    Where-Object { $pf = $_ | Get-NetFirewallPortFilter; $pf.Protocol -in "TCP", "Any" -and ($pf.LocalPort -contains "$Port") })
foreach ($r in $autres) {
    $distante = ($r | Get-NetFirewallAddressFilter).RemoteAddress -join ", "
    $large = if ($distante -in "Any", "*", "") { " OUVERTE À TOUT LE RÉSEAU" } else { "" }
    Write-Warning ("Une autre règle ouvre le port $Port : « $($r.DisplayName) » (adresse distante : $distante)$large. " +
        "Le port $Port ne doit être joignable que par le 2e serveur : la désactiver (Disable-NetFirewallRule -DisplayName '$($r.DisplayName)') après vérification.")
}
if ($autres.Count -eq 0) { Write-Host "Contrôle : aucune autre règle active n'ouvre le port $Port." }

if (-not $Retirer) {
    Write-Host "Sur le 2e serveur : copier-ged.ps1 -Source \\<IP de ce serveur>\$($partages[0].Nom) -Essai, puis -Installer (même mot de passe pour $Compte)."
}
