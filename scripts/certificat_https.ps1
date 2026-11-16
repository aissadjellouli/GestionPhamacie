#Requires -Version 5.1
<#
.SYNOPSIS
    Met l'API de la pharmacie en HTTPS sur le réseau : HTTPS seul sur le réseau, HTTP seulement sur le PC serveur lui-même.

.DESCRIPTION
    1. Crée une autorité de certification propre à la pharmacie (une seule fois), sans clé exportable.
    2. Crée le certificat du serveur, signé par cette autorité, pour le nom du PC, ses adresses et celles demandées
       (-NomsSupplementaires, -AdressesSupplementaires). Il n'est recréé que si la liste des noms et adresses change.
    3. Écrit appsettings.Https.json à côté de l'API :
         http  -> localhost:5080 (le PC serveur seulement, jamais le réseau)
         https -> 0.0.0.0:5443   (le réseau de la pharmacie)
       Sans « AutoriserHttpReseau » : l'API refuse de démarrer si une adresse HTTP du réseau est configurée.
    4. Exporte le certificat PUBLIC de l'autorité (autorite-pharmacie.crt) et son empreinte SHA-256
       (empreinte-autorite-pharmacie.txt) dans le dossier -DossierExport. Rien n'est publié par l'API : le fichier se
       transmet par clé USB ou câble, jamais par le réseau (voir docs/mise-en-production.md, parcours A, étape 4.6).
    5. Pare-feu Windows (avec -PareFeu) : port https ouvert en réseau privé seulement. Le port http n'est jamais ouvert par ce
       script ; les règles existantes qui l'ouvrent sont signalées pour être fermées.

    A exécuter en administrateur sur le PC serveur, puis redémarrer le service « PharmaOfficine API ».
    Idempotent : relancé, il réutilise l'autorité, ne recrée le certificat du serveur que si les noms/adresses changent,
    remplace sa règle de pare-feu au lieu d'en ajouter une.
    Relancer le script si une adresse change (nouvelle adresse du PC, 2e serveur de bascule, adresse VPN).

    Simulation : -WhatIf n'écrit rien (ni magasin de certificats, ni pare-feu, ni configuration, ni fichier) et affiche ce qui
    serait fait.

.PARAMETER DossierApi
    Dossier d'installation de l'API (là où se trouve Pharma.Api.exe).

.PARAMETER DossierExport
    Dossier où écrire autorite-pharmacie.crt et l'empreinte (ex. la clé USB, E:\). Créé s'il n'existe pas.

.PARAMETER Adresses
    Remplace la liste par défaut (nom du PC et ses adresses IPv4 privées) : noms ou adresses IP du PC serveur.

.PARAMETER NomsSupplementaires
    Noms en plus (ex. le nom du 2e serveur de bascule). A renseigner seulement si ce nom est réellement utilisé pour joindre l'API.

.PARAMETER AdressesSupplementaires
    Adresses IP en plus (ex. l'adresse IP du 2e serveur, l'adresse VPN du serveur). Rien n'est deviné : à saisir telles quelles.

.EXAMPLE
    .\certificat_https.ps1 -DossierApi "C:\Program Files\PharmaOfficine\Api" -DossierExport E:\ -PareFeu

.EXAMPLE
    .\certificat_https.ps1 -DossierApi "C:\Program Files\PharmaOfficine\Api" -DossierExport E:\ -AdressesSupplementaires 10.8.0.1 -NomsSupplementaires SERVEUR2 -PareFeu

.EXAMPLE
    .\certificat_https.ps1 -DossierApi "C:\Program Files\PharmaOfficine\Api" -DossierExport E:\ -PareFeu -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $DossierApi,
    [Parameter(Mandatory)] [string] $DossierExport,
    # Remplace la liste par défaut : nom du PC et ses adresses IPv4 privées
    [string[]] $Adresses,
    [string[]] $NomsSupplementaires = @(),
    [string[]] $AdressesSupplementaires = @(),
    [int] $PortHttps = 5443,
    [int] $PortHttp = 5080,
    [ValidateSet("LocalMachine", "CurrentUser")] [string] $Magasin = "LocalMachine",
    [switch] $PareFeu
)

$ErrorActionPreference = "Stop"
$simulation = [bool]$WhatIfPreference
$sujetAutorite = "CN=PharmaOfficine Autorite locale"
$sujetServeur = "CN=PharmaOfficine Mobile"
$store = "Cert:\$Magasin\My"
$etiquette = if ($simulation) { "[SIMULATION] " } else { "" }

function Test-Ip([string] $texte) { $ip = $null; [Net.IPAddress]::TryParse($texte, [ref]$ip) }

function Get-Empreinte([Security.Cryptography.X509Certificates.X509Certificate2] $certificat) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $octets = $sha.ComputeHash($certificat.RawData) } finally { $sha.Dispose() }
    ($octets | ForEach-Object { $_.ToString("X2") }) -join ":"
}

if (-not (Test-Path $DossierApi)) { throw "Dossier de l'API introuvable : $DossierApi" }
foreach ($a in $AdressesSupplementaires) {
    if (-not (Test-Ip $a)) { throw "Adresse IP supplémentaire invalide : « $a » (pour un nom, utiliser -NomsSupplementaires)." }
}
foreach ($n in $NomsSupplementaires) {
    if ([string]::IsNullOrWhiteSpace($n) -or (Test-Ip $n)) { throw "Nom supplémentaire invalide : « $n » (pour une adresse IP, utiliser -AdressesSupplementaires)." }
}

if (-not $Adresses) {
    $Adresses = @($env:COMPUTERNAME) + @(Get-NetIPAddress -AddressFamily IPv4 |
        Where-Object { $_.IPAddress -match '^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)' } |
        Select-Object -ExpandProperty IPAddress)
}
# Liste finale, sans doublon : le poste lui-même (localhost, 127.0.0.1), la liste par défaut ou demandée, les ajouts
$noms = @("localhost", "127.0.0.1") + @($Adresses) + @($NomsSupplementaires) + @($AdressesSupplementaires) |
    Where-Object { $_ } | ForEach-Object { $_.Trim() } | Sort-Object -Unique { $_.ToLowerInvariant() }
Write-Host "${etiquette}Noms et adresses du certificat : $($noms -join ', ')"

# 1. Autorité locale (réutilisée si elle existe déjà)
$autorite = Get-ChildItem $store | Where-Object { $_.Subject -eq $sujetAutorite -and $_.NotAfter -gt (Get-Date).AddMonths(1) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if ($autorite) {
    Write-Host "${etiquette}Autorité locale existante réutilisée : $($autorite.Thumbprint)"
}
elseif ($simulation) {
    Write-Host "${etiquette}Une autorité locale serait créée (valable 10 ans, clé non exportable)."
}
else {
    $autorite = New-SelfSignedCertificate -Subject $sujetAutorite -CertStoreLocation $store `
        -KeyUsage CertSign, CRLSign, DigitalSignature -KeyExportPolicy NonExportable -KeyLength 3072 `
        -NotAfter (Get-Date).AddYears(10) -TextExtension @("2.5.29.19={critical}{text}ca=1&pathlength=0")
    Write-Host "Autorité locale créée : $($autorite.Thumbprint)"
}

# 2. Certificat du serveur : réutilisé tant que l'autorité et la liste de noms/adresses sont les mêmes (marque dans FriendlyName)
$san = ($noms | ForEach-Object { if (Test-Ip $_) { "IPAddress=$_" } else { "DNS=$_" } }) -join "&"
$marque = if ($autorite) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $empreinteListe = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($autorite.Thumbprint + "|" + (($noms | ForEach-Object { $_.ToLowerInvariant() }) -join ","))))
    } finally { $sha.Dispose() }
    "PharmaOfficine serveur " + (($empreinteListe[0..5] | ForEach-Object { $_.ToString("x2") }) -join "")
} else { $null }
$existants = @(Get-ChildItem $store | Where-Object { $_.Subject -eq $sujetServeur })
$valide = if ($marque) {
    $existants | Where-Object { $_.FriendlyName -eq $marque -and $_.NotAfter -gt (Get-Date).AddMonths(1) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
}
if ($valide) {
    # Les autres certificats du même sujet (anciens) sont retirés : jamais de doublon
    $autres = @($existants | Where-Object { $_.Thumbprint -ne $valide.Thumbprint })
    if ($autres.Count -gt 0 -and $PSCmdlet.ShouldProcess("$($autres.Count) ancien(s) certificat(s) du serveur", "Supprimer")) { $autres | Remove-Item }
    Write-Host "${etiquette}Certificat du serveur existant réutilisé : $($valide.Thumbprint), valable jusqu'au $($valide.NotAfter.ToString('dd/MM/yyyy'))"
}
elseif ($simulation) {
    Write-Host "${etiquette}Un certificat du serveur serait créé ($($existants.Count) ancien(s) remplacé(s)) pour : $san"
}
else {
    $existants | Remove-Item
    $serveur = New-SelfSignedCertificate -Subject $sujetServeur -Signer $autorite -CertStoreLocation $store `
        -KeyExportPolicy NonExportable -KeyLength 2048 -NotAfter (Get-Date).AddDays(825) `
        -TextExtension @("2.5.29.17={text}$san", "2.5.29.37={text}1.3.6.1.5.5.7.3.1")
    $serveur.FriendlyName = $marque
    Write-Host "Certificat du serveur créé : $($serveur.Thumbprint), valable jusqu'au $($serveur.NotAfter.ToString('dd/MM/yyyy'))"
}

# 3. Configuration de l'API (aucun secret : le certificat est lu dans le magasin Windows).
#    http : « localhost » = 127.0.0.1 et [::1], ce PC seulement. https : tout le réseau.
$config = [ordered]@{
    Kestrel = [ordered]@{
        Endpoints = [ordered]@{
            Http  = @{ Url = "http://localhost:$PortHttp" }
            Https = [ordered]@{
                Url         = "https://0.0.0.0:$PortHttps"
                Certificate = [ordered]@{ Subject = "PharmaOfficine Mobile"; Store = "My"; Location = $Magasin; AllowInvalid = $true }
            }
        }
    }
}
$fichier = Join-Path $DossierApi "appsettings.Https.json"
$json = $config | ConvertTo-Json -Depth 6
if ($simulation) { Write-Host "${etiquette}Configuration qui serait écrite dans $fichier :"; Write-Host $json }
else { $json | Set-Content -Path $fichier -Encoding UTF8; Write-Host "Configuration écrite : $fichier" }

# 4. Certificat public de l'autorité + empreinte SHA-256, exportés pour copie par clé USB (rien n'est servi par le réseau)
$ancienneCopie = Join-Path $DossierApi "wwwroot\mobile\autorite-pharmacie.crt"
if (Test-Path $ancienneCopie) {
    if ($simulation) { Write-Host "${etiquette}L'ancienne copie publiée par l'API serait supprimée : $ancienneCopie" }
    else { Remove-Item $ancienneCopie; Write-Host "Ancienne copie publiée par l'API supprimée : $ancienneCopie" }
}
$crt = Join-Path $DossierExport "autorite-pharmacie.crt"
$txt = Join-Path $DossierExport "empreinte-autorite-pharmacie.txt"
if ($autorite) {
    $empreinte = Get-Empreinte $autorite
    if ($simulation) {
        Write-Host "${etiquette}Seraient écrits : $crt et $txt"
    }
    else {
        if (-not (Test-Path $DossierExport)) { New-Item -ItemType Directory -Path $DossierExport | Out-Null }
        [IO.File]::WriteAllBytes($crt, $autorite.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        @("Certificat de l'autorité de la pharmacie : autorite-pharmacie.crt",
          "Empreinte SHA-256 (à comparer avec celle affichée sur l'appareil avant d'installer le certificat) :",
          $empreinte) | Set-Content -Path $txt -Encoding UTF8
        Write-Host "Certificat exporté : $crt"
    }
    Write-Host "${etiquette}Empreinte SHA-256 de l'autorité : $empreinte"
}
else {
    Write-Host "${etiquette}Le certificat de l'autorité et son empreinte seraient exportés dans $DossierExport."
}

# 5. Pare-feu : https ouvert en réseau privé seulement ; http jamais ouvert (règles existantes signalées)
if ($PareFeu) {
    $nom = "PharmaOfficine API (https $PortHttps)"
    $anciennes = @(Get-NetFirewallRule -DisplayName $nom, "PharmaOfficine scanner mobile (https $PortHttps)" -ErrorAction SilentlyContinue)
    if ($simulation) {
        Write-Host "${etiquette}Pare-feu : $($anciennes.Count) règle(s) « $nom » remplacée(s) par une règle unique (port $PortHttps, profil Privé)."
    }
    else {
        $anciennes | Remove-NetFirewallRule
        New-NetFirewallRule -DisplayName $nom -Direction Inbound -Protocol TCP -LocalPort $PortHttps -Action Allow -Profile Private | Out-Null
        Write-Host "Pare-feu : port $PortHttps ouvert (profil Privé)."
    }
}
$ouvertsHttp = @(Get-NetFirewallRule -Direction Inbound -Enabled True -Action Allow -ErrorAction SilentlyContinue |
    Where-Object { ($_ | Get-NetFirewallPortFilter).LocalPort -contains "$PortHttp" })
foreach ($r in $ouvertsHttp) {
    Write-Warning "Le pare-feu ouvre encore le port $PortHttp (règle « $($r.DisplayName) »). Une fois tous les postes en https, la désactiver : Disable-NetFirewallRule -DisplayName '$($r.DisplayName)'"
}

$adresseEssai = ($noms | Where-Object { $_ -ne "localhost" -and $_ -ne "127.0.0.1" } | Select-Object -First 1)
Write-Host ""
if ($simulation) { Write-Host "Simulation terminée : rien n'a été modifié." }
else {
    Write-Host "Redémarrez le service « PharmaOfficine API », puis ouvrez : https://${adresseEssai}:$PortHttps/mobile/"
    Write-Host "Copiez le contenu de $DossierExport sur clé USB (jamais par le réseau) et installez le certificat sur chaque poste et téléphone."
}
