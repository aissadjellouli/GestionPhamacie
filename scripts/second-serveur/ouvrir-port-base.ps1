<#
.SYNOPSIS
    2e serveur : ouvre le port PostgreSQL (5432) du serveur PRINCIPAL à l'adresse du 2e serveur, et à elle seule.

.DESCRIPTION
    À lancer sur le serveur principal, en administrateur Windows, AVANT initialiser-second-serveur.ps1 (docs/mise-en-production.md,
    parcours C). Crée une règle de pare-feu Windows entrante : TCP, port 5432, profil Privé, adresse distante = l'IP donnée.
    Rien n'est deviné : l'adresse est obligatoire, une seule IPv4 (pas de plage, pas de « Any », pas de 0.0.0.0).

    Règle à nom fixe : relancé (même adresse ou nouvelle adresse), le script REMPLACE la règle, sans doublon.
      - Retour d'un serveur réparé (F4) ou changement d'IP du 2e serveur : relancer ce script avec la nouvelle adresse,
        sur le serveur qui est principal à ce moment-là.
      - Plus de 2e serveur : -Retirer supprime la règle.

    Contrôles de sécurité (le port 5432 ne doit JAMAIS être ouvert au reste du réseau) :
      - avertissement si une AUTRE règle entrante active ouvre le port 5432 (le script ne la modifie pas, il la nomme) ;
      - avertissement si le port est ouvert à toutes les adresses distantes par une règle d'un autre nom.

    Ce script ne touche NI postgresql.conf NI pg_hba.conf : voir preparer-principal.ps1, qui règle listen_addresses (adresse du
    principal ajoutée) et écrit les lignes pg_hba.conf limitées à /32 (2e serveur ET principal).
    -WhatIf : n'écrit rien, annonce ce qui serait fait. -Confirm : demande confirmation avant chaque action.

.EXAMPLE
    .\ouvrir-port-base.ps1 -AdresseSecond 192.168.1.21 -WhatIf
.EXAMPLE
    .\ouvrir-port-base.ps1 -AdresseSecond 192.168.1.21
.EXAMPLE
    .\ouvrir-port-base.ps1 -AdresseSecond 192.168.1.21 -Retirer
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = "Medium")]
param(
    [Parameter(Mandatory)] [string]$AdresseSecond,   # IPv4 du 2e serveur : seule adresse autorisée à joindre le port
    [int]   $Port = 5432,
    [switch]$Retirer                                  # supprime la règle (plus de 2e serveur)
)
$ErrorActionPreference = "Stop"

$NomRegle = "PharmaOfficine PostgreSQL 5432 (2e serveur)"   # nom fixe : la règle est retrouvée et remplacée à chaque lancement

# Une seule adresse IPv4, unicast : refuse plage, masque, liste, « Any », 0.0.0.0, boucle locale, diffusion, multicast
$ip = $null
if ($AdresseSecond -notmatch '^\d{1,3}(\.\d{1,3}){3}$' -or -not [Net.IPAddress]::TryParse($AdresseSecond, [ref]$ip)) {
    throw "Adresse IPv4 invalide : « $AdresseSecond ». Une seule adresse du 2e serveur (ex. 192.168.1.21), ni plage, ni masque, ni « Any »."
}
$octets = $ip.GetAddressBytes()
if ($octets[0] -eq 0 -or $octets[0] -eq 127 -or $octets[0] -ge 224) {
    throw "Adresse refusée : $AdresseSecond n'est pas l'adresse d'un poste du réseau local."
}
if ($Port -lt 1 -or $Port -gt 65535) { throw "Port invalide : $Port." }

$simulation = [bool]$WhatIfPreference
$etiquette = if ($simulation) { "[SIMULATION] " } else { "" }

if (-not $simulation) {
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $admin) { throw "À lancer dans PowerShell en administrateur." }
}

# Règles actuelles (lecture seule)
$anciennes = @(Get-NetFirewallRule -DisplayName $NomRegle -ErrorAction SilentlyContinue)

if ($Retirer) {
    if ($anciennes.Count -eq 0) { Write-Host "Aucune règle « $NomRegle » : rien à retirer." }
    elseif ($PSCmdlet.ShouldProcess($NomRegle, "Supprimer la règle de pare-feu")) {
        $anciennes | Remove-NetFirewallRule
        Write-Host "Règle « $NomRegle » supprimée : le port $Port n'est plus ouvert au 2e serveur."
    }
    else { Write-Host "${etiquette}$($anciennes.Count) règle(s) « $NomRegle » seraient supprimées." }
}
else {
    $anciennesAdresses = @($anciennes | ForEach-Object { ($_ | Get-NetFirewallAddressFilter).RemoteAddress }) -join ", "
    if ($anciennes.Count -gt 0) { Write-Host "Règle actuelle : $($anciennes.Count) règle(s) « $NomRegle » (adresse distante : $anciennesAdresses)." }
    else { Write-Host "Règle actuelle : aucune." }

    if ($PSCmdlet.ShouldProcess($NomRegle, "Créer la règle (entrée TCP $Port, profil Privé, adresse distante $AdresseSecond), en remplaçant l'ancienne")) {
        if ($anciennes.Count -gt 0) { $anciennes | Remove-NetFirewallRule }
        New-NetFirewallRule -DisplayName $NomRegle -Direction Inbound -Protocol TCP -LocalPort $Port `
            -RemoteAddress $AdresseSecond -Action Allow -Profile Private `
            -Description "Réplication PostgreSQL vers le 2e serveur : cette adresse seulement (scripts/second-serveur/ouvrir-port-base.ps1)" | Out-Null
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
    Write-Host "Vérifier aussi (preparer-principal.ps1 s'en charge) : listen_addresses contient l'adresse de ce serveur ; pg_hba.conf : lignes « replication » et base limitées à $AdresseSecond/32."
}
