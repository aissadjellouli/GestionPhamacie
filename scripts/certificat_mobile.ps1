#Requires -Version 5.1
<#
.SYNOPSIS
    Prépare le HTTPS du scanner mobile sur le PC serveur (la caméra du téléphone n'est autorisée qu'en https).

.DESCRIPTION
    1. Crée une autorité de certification propre à la pharmacie (une seule fois), sans clé exportable.
    2. Crée le certificat du serveur, signé par cette autorité, pour les adresses du PC sur le Wi-Fi.
    3. Écrit appsettings.Https.json à côté de l'API : http sur le port habituel, https sur le port mobile.
    4. Publie le certificat PUBLIC de l'autorité dans la page mobile (autorite-pharmacie.crt),
       à installer une fois sur chaque téléphone (voir docs/scanner-mobile.md).
    5. Ouvre le port https dans le pare-feu Windows (réseau privé uniquement), si demandé.

    A exécuter en administrateur sur le PC serveur, puis redémarrer le service « PharmaOfficine API ».
    Relancer le script si l'adresse IP du PC change (une adresse fixe dans la box est conseillée).

.EXAMPLE
    .\certificat_mobile.ps1 -DossierApi "C:\Program Files\PharmaOfficine\Api" -Adresses 192.168.1.10 -PareFeu
#>
param(
    [Parameter(Mandatory)] [string] $DossierApi,
    # Adresses IP ou noms du PC serveur sur le Wi-Fi ; par défaut : nom du PC et ses adresses IPv4 privées
    [string[]] $Adresses,
    [int] $PortHttps = 5443,
    [int] $PortHttp = 5080,
    [ValidateSet("LocalMachine", "CurrentUser")] [string] $Magasin = "LocalMachine",
    [switch] $PareFeu
)

$ErrorActionPreference = "Stop"
$sujetAutorite = "CN=PharmaOfficine Autorite locale"
$sujetServeur = "CN=PharmaOfficine Mobile"
$store = "Cert:\$Magasin\My"

if (-not (Test-Path $DossierApi)) { throw "Dossier de l'API introuvable : $DossierApi" }

if (-not $Adresses) {
    $Adresses = @($env:COMPUTERNAME) + @(Get-NetIPAddress -AddressFamily IPv4 |
        Where-Object { $_.IPAddress -match '^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)' } |
        Select-Object -ExpandProperty IPAddress)
}
Write-Host "Adresses du certificat : $($Adresses -join ', ')"

# 1. Autorité locale (réutilisée si elle existe déjà)
$autorite = Get-ChildItem $store | Where-Object { $_.Subject -eq $sujetAutorite -and $_.NotAfter -gt (Get-Date).AddMonths(1) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $autorite) {
    $autorite = New-SelfSignedCertificate -Subject $sujetAutorite -CertStoreLocation $store `
        -KeyUsage CertSign, CRLSign, DigitalSignature -KeyExportPolicy NonExportable -KeyLength 3072 `
        -NotAfter (Get-Date).AddYears(10) -TextExtension @("2.5.29.19={critical}{text}ca=1&pathlength=0")
    Write-Host "Autorité locale créée : $($autorite.Thumbprint)"
}

# 2. Certificat du serveur (remplace l'ancien)
Get-ChildItem $store | Where-Object { $_.Subject -eq $sujetServeur } | Remove-Item
$san = ($Adresses | ForEach-Object { if ($_ -match '^\d{1,3}(\.\d{1,3}){3}$') { "IPAddress=$_" } else { "DNS=$_" } }) -join "&"
$serveur = New-SelfSignedCertificate -Subject $sujetServeur -Signer $autorite -CertStoreLocation $store `
    -KeyExportPolicy NonExportable -KeyLength 2048 -NotAfter (Get-Date).AddDays(825) `
    -TextExtension @("2.5.29.17={text}$san", "2.5.29.37={text}1.3.6.1.5.5.7.3.1")
Write-Host "Certificat du serveur créé : $($serveur.Thumbprint), valable jusqu'au $($serveur.NotAfter.ToString('dd/MM/yyyy'))"

# 3. Configuration de l'API (aucun secret : le certificat est lu dans le magasin Windows)
$config = [ordered]@{
    Kestrel = [ordered]@{
        Endpoints = [ordered]@{
            Http  = @{ Url = "http://0.0.0.0:$PortHttp" }
            Https = [ordered]@{
                Url         = "https://0.0.0.0:$PortHttps"
                Certificate = [ordered]@{ Subject = "PharmaOfficine Mobile"; Store = "My"; Location = $Magasin; AllowInvalid = $true }
            }
        }
    }
}
$fichier = Join-Path $DossierApi "appsettings.Https.json"
$config | ConvertTo-Json -Depth 6 | Set-Content -Path $fichier -Encoding UTF8
Write-Host "Configuration écrite : $fichier"

# 4. Certificat public de l'autorité, téléchargeable depuis le téléphone
$dossierMobile = Join-Path $DossierApi "wwwroot\mobile"
if (Test-Path $dossierMobile) {
    $crt = Join-Path $dossierMobile "autorite-pharmacie.crt"
    [IO.File]::WriteAllBytes($crt, $autorite.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    Write-Host "Certificat à installer sur les téléphones : http://<adresse du PC>:$PortHttp/mobile/autorite-pharmacie.crt"
}

# 5. Pare-feu : port https ouvert sur le réseau privé de la pharmacie seulement
if ($PareFeu) {
    $nom = "PharmaOfficine scanner mobile (https $PortHttps)"
    Get-NetFirewallRule -DisplayName $nom -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName $nom -Direction Inbound -Protocol TCP -LocalPort $PortHttps -Action Allow -Profile Private | Out-Null
    Write-Host "Pare-feu : port $PortHttps ouvert (profil Privé)."
}

Write-Host ""
Write-Host "Redémarrez le service « PharmaOfficine API », puis ouvrez sur le téléphone : https://$($Adresses | Select-Object -Last 1):$PortHttps/mobile/"
