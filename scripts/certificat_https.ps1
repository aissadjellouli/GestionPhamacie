#Requires -Version 5.1

<#
.SYNOPSIS
    Met l'API de la pharmacie en HTTPS sur le réseau, avec UNE seule autorité de certification pour toute la pharmacie
    (PC serveur principal, 2e serveur, PC de secours) : HTTPS seul sur le réseau, HTTP seulement sur le PC lui-même.

.DESCRIPTION
    Principe : les postes et les téléphones font confiance UNE fois à l'autorité de la pharmacie (autorite-pharmacie.crt).
    Cette autorité signe le certificat de chaque PC qui sert l'API. Une bascule vers le 2e serveur ou le PC de secours ne
    demande donc rien de plus sur les postes, à condition que le nom ou l'adresse utilisé figure dans le certificat de ce PC.

    QUATRE MODES (jeux de paramètres exclusifs) :

    1. PRINCIPAL (par défaut) : sur le PC serveur principal. Administrateur.
         - Première exécution : crée l'autorité (valable 10 ans) et sauvegarde sa clé UNE fois dans un fichier PFX
           (-FichierSauvegardeAutorite), protégé par une phrase de passe saisie à l'écran (12 caractères au moins, jamais
           en paramètre, jamais écrite ni journalisée). Ce PFX est destiné au COFFRE, hors ligne (clé USB rangée), à part
           du certificat public. Dans le magasin Windows de ce PC, la clé de l'autorité est ensuite NON exportable : le
           PFX est le seul exemplaire de sauvegarde. Il ne peut pas être refait plus tard.
         - Crée le certificat de ce PC, signé par l'autorité (825 jours), pour son nom, ses adresses et celles demandées.
         - Écrit appsettings.Https.json (http -> localhost:5080, https -> 0.0.0.0:5443), exporte le certificat PUBLIC de
           l'autorité et son empreinte SHA-256 dans -DossierExport (clé USB des postes), ouvre le pare-feu (-PareFeu).
    2. -CertificatPourAutrePc : sur le principal. Génère le certificat du 2e serveur ou du PC de secours, signé par
       l'autorité, pour les noms (-NomsAutrePc) et adresses (-AdressesAutrePc) donnés (rien n'est deviné), exporté en PFX
       protégé par une phrase de passe saisie à l'écran (-FichierCertificat), à copier sur clé USB. Ne modifie ni la
       configuration ni le pare-feu.
    3. -Importer : sur le 2e serveur ou le PC de secours. Importe ce PFX dans le magasin Windows (clé NON exportable),
       écrit appsettings.Https.json et ouvre le pare-feu (-PareFeu), exactement comme sur le principal. Aucune autorité
       n'est créée sur ces PC.
    4. -RestaurerAutorite : si le principal est détruit ou remplacé. Importe le PFX de l'autorité (celui du coffre) dans le
       magasin du nouveau PC pour continuer à signer, sans rien réinstaller sur les postes. Puis relancer le mode
       PRINCIPAL (sans -FichierSauvegardeAutorite) pour créer le certificat de ce PC.

    Idempotent : relancé, il réutilise l'autorité, ne recrée le certificat que si les noms/adresses changent ou s'il expire
    dans moins d'un mois (mode PRINCIPAL), remplace sa règle de pare-feu au lieu d'en ajouter une. Un certificat serveur
    ancien du même sujet n'est retiré qu'après la mise en place du nouveau.

    Renouvellement : le certificat d'un PC dure 825 jours. Principal : relancer le mode PRINCIPAL avant l'échéance.
    2e serveur / PC de secours : relancer -CertificatPourAutrePc sur le principal, puis -Importer sur l'autre PC.
    Voir docs/mise-en-production.md.

    ANCIENNE AUTORITÉ « PAR PC » : une version précédente du script créait une autorité « PharmaOfficine Autorite locale »
    propre à chaque PC, à clé non exportable. Si elle est trouvée, un avertissement donne la procédure. Elle n'est
    JAMAIS supprimée automatiquement.

    Simulation : -WhatIf n'écrit rien (ni magasin de certificats, ni pare-feu, ni configuration, ni fichier), ne demande
    aucune phrase de passe, et affiche ce qui serait fait.

.PARAMETER DossierApi
    Dossier d'installation de l'API (là où se trouve Pharma.Api.exe). Modes PRINCIPAL et -Importer.

.PARAMETER DossierExport
    Dossier où écrire autorite-pharmacie.crt (public) et l'empreinte, par exemple la clé USB des postes (E:\). Créé s'il
    n'existe pas. Mode PRINCIPAL.

.PARAMETER FichierSauvegardeAutorite
    Chemin du PFX de l'autorité (extension .pfx). Mode PRINCIPAL : fichier à créer à la première exécution (clé USB du
    coffre, dossier existant, fichier inexistant, différent de -DossierExport). Mode -RestaurerAutorite : fichier à lire.

.PARAMETER Adresses
    Remplace la liste par défaut (nom du PC et ses adresses IPv4 privées) : noms ou adresses IP de ce PC. Mode PRINCIPAL.

.PARAMETER NomsSupplementaires
    Noms en plus (ex. nom de ce PC dans le VPN). A renseigner seulement si ce nom est réellement utilisé pour joindre l'API.

.PARAMETER AdressesSupplementaires
    Adresses IP en plus (ex. adresse VPN du serveur). Rien n'est deviné : à saisir telles quelles.

.PARAMETER CertificatPourAutrePc
    Mode : générer le certificat du 2e serveur ou du PC de secours (à lancer sur le principal).

.PARAMETER NomsAutrePc
    Noms sous lesquels les postes joignent l'autre PC (ex. SERVEUR2). Au moins un nom ou une adresse (-AdressesAutrePc).

.PARAMETER AdressesAutrePc
    Adresses IP sous lesquelles les postes joignent l'autre PC. Ce sont celles à saisir dans « PC de secours » des postes.

.PARAMETER FichierCertificat
    Mode -CertificatPourAutrePc : PFX à créer. Mode -Importer : PFX à importer.

.PARAMETER Importer
    Mode : importer le certificat généré pour ce PC (à lancer sur le 2e serveur ou le PC de secours).

.PARAMETER RestaurerAutorite
    Mode : importer l'autorité depuis le PFX du coffre (-FichierSauvegardeAutorite).

.EXAMPLE
    .\certificat_https.ps1 -DossierApi "C:\Program Files\PharmaOfficine\Api" -DossierExport E:\ -FichierSauvegardeAutorite F:\autorite-pharmacie.pfx -PareFeu

.EXAMPLE
    .\certificat_https.ps1 -CertificatPourAutrePc -NomsAutrePc SECOURS -AdressesAutrePc 192.168.1.30 -FichierCertificat E:\certificat-secours.pfx

.EXAMPLE
    .\certificat_https.ps1 -Importer -FichierCertificat E:\certificat-secours.pfx -DossierApi "C:\Program Files\PharmaOfficine\Api" -PareFeu

.EXAMPLE
    .\certificat_https.ps1 -RestaurerAutorite -FichierSauvegardeAutorite F:\autorite-pharmacie.pfx

.EXAMPLE
    .\certificat_https.ps1 -DossierApi "C:\Program Files\PharmaOfficine\Api" -DossierExport E:\ -PareFeu -WhatIf
#>
[CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = "Principal")]
param(
    [Parameter(Mandatory, ParameterSetName = "Principal")]
    [Parameter(Mandatory, ParameterSetName = "Importer")]
    [string] $DossierApi,

    [Parameter(Mandatory, ParameterSetName = "Principal")]
    [string] $DossierExport,

    # Principal : PFX de l'autorité à créer (première exécution) ; RestaurerAutorite : PFX du coffre à lire
    [Parameter(ParameterSetName = "Principal")]
    [Parameter(Mandatory, ParameterSetName = "Restaurer")]
    [string] $FichierSauvegardeAutorite,

    # Remplace la liste par défaut : nom du PC et ses adresses IPv4 privées
    [Parameter(ParameterSetName = "Principal")] [string[]] $Adresses,
    [Parameter(ParameterSetName = "Principal")] [string[]] $NomsSupplementaires = @(),
    [Parameter(ParameterSetName = "Principal")] [string[]] $AdressesSupplementaires = @(),

    [Parameter(Mandatory, ParameterSetName = "AutrePc")] [switch] $CertificatPourAutrePc,
    [Parameter(ParameterSetName = "AutrePc")] [string[]] $NomsAutrePc = @(),
    [Parameter(ParameterSetName = "AutrePc")] [string[]] $AdressesAutrePc = @(),

    [Parameter(Mandatory, ParameterSetName = "AutrePc")]
    [Parameter(Mandatory, ParameterSetName = "Importer")]
    [string] $FichierCertificat,

    [Parameter(Mandatory, ParameterSetName = "Importer")] [switch] $Importer,
    [Parameter(Mandatory, ParameterSetName = "Restaurer")] [switch] $RestaurerAutorite,

    [Parameter(ParameterSetName = "Principal")]
    [Parameter(ParameterSetName = "Importer")]
    [int] $PortHttps = 5443,
    [Parameter(ParameterSetName = "Principal")]
    [Parameter(ParameterSetName = "Importer")]
    [int] $PortHttp = 5080,

    [ValidateSet("LocalMachine", "CurrentUser")] [string] $Magasin = "LocalMachine",

    [Parameter(ParameterSetName = "Principal")]
    [Parameter(ParameterSetName = "Importer")]
    [switch] $PareFeu
)

$ErrorActionPreference = "Stop"
$simulation = [bool]$WhatIfPreference
$sujetAutorite = "CN=PharmaOfficine Autorite pharmacie"
$sujetAutoriteAncienne = "CN=PharmaOfficine Autorite locale"   # version « une autorité par PC » : détectée, jamais supprimée
$sujetServeur = "CN=PharmaOfficine Mobile"                      # le sujet lu par Kestrel (appsettings.Https.json)
$store = "Cert:\$Magasin\My"
$etiquette = if ($simulation) { "[SIMULATION] " } else { "" }
$nbCaracPhraseMin = 12
$joursServeur = 825

# ---------------------------------------------------------------------------------------------------------------------
# Fonctions de fabrication des certificats : en mémoire seulement (aucun magasin, aucun fichier)
# ---------------------------------------------------------------------------------------------------------------------

function Test-Ip([string] $texte) { $ip = $null; [Net.IPAddress]::TryParse($texte, [ref]$ip) }

function Get-Empreinte([System.Security.Cryptography.X509Certificates.X509Certificate2] $certificat) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $octets = $sha.ComputeHash($certificat.RawData) } finally { $sha.Dispose() }
    ($octets | ForEach-Object { $_.ToString("X2") }) -join ":"
}

function New-NumeroSerie {
    $serie = New-Object byte[] 16
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($serie) } finally { $rng.Dispose() }
    $serie[0] = ($serie[0] -band 0x7F) -bor 0x01   # positif, jamais nul
    , $serie
}

# Autorité : RSA 3072, CA racine limitée à un niveau (elle ne signe que des certificats de serveur), 10 ans
function New-CertificatAutorite([string] $sujet) {
    $espace = "System.Security.Cryptography.X509Certificates"
    $rsa = New-Object System.Security.Cryptography.RSACng -ArgumentList 3072
    $req = New-Object "$espace.CertificateRequest" -ArgumentList $sujet, $rsa, ([System.Security.Cryptography.HashAlgorithmName]::SHA256), ([System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    [void]$req.CertificateExtensions.Add((New-Object "$espace.X509BasicConstraintsExtension" -ArgumentList $true, $true, 0, $true))
    $usages = [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign -bor [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::CrlSign -bor [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature
    [void]$req.CertificateExtensions.Add((New-Object "$espace.X509KeyUsageExtension" -ArgumentList $usages, $true))
    [void]$req.CertificateExtensions.Add((New-Object "$espace.X509SubjectKeyIdentifierExtension" -ArgumentList $req.PublicKey, $false))
    $req.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddYears(10))
}

# Certificat de serveur signé par l'autorité : RSA 2048, usage « authentification du serveur », noms et adresses en SAN
function New-CertificatServeur($autorite, [string[]] $noms, [string] $sujet, [int] $jours) {
    $espace = "System.Security.Cryptography.X509Certificates"
    $rsa = New-Object System.Security.Cryptography.RSACng -ArgumentList 2048
    $req = New-Object "$espace.CertificateRequest" -ArgumentList $sujet, $rsa, ([System.Security.Cryptography.HashAlgorithmName]::SHA256), ([System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    [void]$req.CertificateExtensions.Add((New-Object "$espace.X509BasicConstraintsExtension" -ArgumentList $false, $false, 0, $true))
    $usages = [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment
    [void]$req.CertificateExtensions.Add((New-Object "$espace.X509KeyUsageExtension" -ArgumentList $usages, $true))
    $oids = New-Object System.Security.Cryptography.OidCollection
    [void]$oids.Add((New-Object System.Security.Cryptography.Oid -ArgumentList "1.3.6.1.5.5.7.3.1"))
    [void]$req.CertificateExtensions.Add((New-Object "$espace.X509EnhancedKeyUsageExtension" -ArgumentList $oids, $false))
    $san = New-Object "$espace.SubjectAlternativeNameBuilder"
    foreach ($n in $noms) {
        if (Test-Ip $n) { $san.AddIpAddress([Net.IPAddress]::Parse($n)) } else { $san.AddDnsName($n) }
    }
    [void]$req.CertificateExtensions.Add($san.Build($false))
    [void]$req.CertificateExtensions.Add((New-Object "$espace.X509SubjectKeyIdentifierExtension" -ArgumentList $req.PublicKey, $false))
    $fin = [DateTimeOffset]::UtcNow.AddDays($jours)
    if ($fin -gt [DateTimeOffset]$autorite.NotAfter) { throw "L'autorité expire avant la fin de validité demandée du certificat ($($autorite.NotAfter.ToString('dd/MM/yyyy'))) : renouveler l'autorité (docs/mise-en-production.md)." }
    $signe = $req.Create($autorite, [DateTimeOffset]::UtcNow.AddMinutes(-5), $fin, (New-NumeroSerie))
    [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey($signe, $rsa)
}

# ---------------------------------------------------------------------------------------------------------------------
# Phrases de passe : toujours saisies à l'écran (SecureString), jamais en paramètre, jamais écrites
# ---------------------------------------------------------------------------------------------------------------------

function Test-PhrasesEgales([securestring] $a, [securestring] $b) {
    $pa = [IntPtr]::Zero; $pb = [IntPtr]::Zero
    try {
        $pa = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($a)
        $pb = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($b)
        [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pa) -ceq [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pb)
    }
    finally {
        if ($pa -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pa) }
        if ($pb -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pb) }
    }
}

# Création d'un PFX : phrase saisie deux fois
function Read-PhraseNouvelle([string] $invite) {
    $p1 = Read-Host $invite -AsSecureString
    if ($p1.Length -lt $nbCaracPhraseMin) { throw "La phrase de passe doit comporter $nbCaracPhraseMin caractères au moins : rien n'a été écrit." }
    $p2 = Read-Host "Saisir de nouveau la phrase de passe" -AsSecureString
    if (-not (Test-PhrasesEgales $p1 $p2)) { throw "Les deux saisies sont différentes : rien n'a été écrit." }
    $p1
}

# Phrase interne, éphémère, pour passer un certificat de la mémoire au magasin : jamais affichée ni écrite
function New-PhraseAleatoire {
    $octets = New-Object byte[] 48
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($octets) } finally { $rng.Dispose() }
    $s = New-Object System.Security.SecureString
    foreach ($o in $octets) { $s.AppendChar([char](33 + ($o % 90))) }
    $s.MakeReadOnly()
    $s
}

# ---------------------------------------------------------------------------------------------------------------------
# Magasin Windows : la clé est importée NON exportable (aucun drapeau Exportable)
# ---------------------------------------------------------------------------------------------------------------------

function Get-DrapeauxMagasin {
    $cle = if ($Magasin -eq "LocalMachine") { [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::MachineKeySet } else { [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet }
    $cle -bor [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet
}

function Add-AuMagasin([byte[]] $pfx, [securestring] $phrase, [string] $nomConvivial) {
    $certificat = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList $pfx, $phrase, (Get-DrapeauxMagasin)
    $magasinNet = New-Object System.Security.Cryptography.X509Certificates.X509Store -ArgumentList "My", $Magasin
    $magasinNet.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try { $magasinNet.Add($certificat) } finally { $magasinNet.Close() }
    if ($nomConvivial) {
        $dansMagasin = Get-ChildItem $store | Where-Object { $_.Thumbprint -eq $certificat.Thumbprint } | Select-Object -First 1
        if ($dansMagasin) { $dansMagasin.FriendlyName = $nomConvivial }
    }
    $certificat
}

# Certificat issu d'un PFX, sans rien écrire dans le magasin (clé gardée en mémoire seulement)
function Read-Pfx([string] $chemin, [securestring] $phrase) {
    try {
        New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList $chemin, $phrase, ([System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    }
    catch {
        throw "Fichier illisible ou phrase de passe incorrecte : $chemin"
    }
}

function Test-EstAutorite($certificat) {
    $bc = $certificat.Extensions | Where-Object { $_.Oid.Value -eq "2.5.29.19" } | Select-Object -First 1
    [bool]($bc -and $bc.CertificateAuthority)
}

function Get-NomsDuCertificat($certificat) {
    $ext = $certificat.Extensions | Where-Object { $_.Oid.Value -eq "2.5.29.17" } | Select-Object -First 1
    if ($ext) { ($ext.Format($false)) } else { "(aucun)" }
}

function Test-DossierDeFichier([string] $chemin, [string] $nomParametre) {
    $dossier = Split-Path -Path $chemin -Parent
    if (-not $dossier) { $dossier = (Get-Location).Path }
    if (-not (Test-Path -LiteralPath $dossier -PathType Container)) { throw "Dossier introuvable pour $nomParametre : $dossier (clé USB branchée ?)." }
    if ([IO.Path]::GetExtension($chemin) -ne ".pfx") { throw "$nomParametre doit se terminer par .pfx : $chemin" }
}

# ---------------------------------------------------------------------------------------------------------------------
# Ancienne autorité « par PC » : détectée et signalée, jamais supprimée
# ---------------------------------------------------------------------------------------------------------------------

function Show-AncienneAutorite {
    $anciennes = @()
    foreach ($emplacement in "My", "Root") {
        $anciennes += @(Get-ChildItem "Cert:\$Magasin\$emplacement" -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -eq $sujetAutoriteAncienne } | ForEach-Object { [pscustomobject]@{ Magasin = $emplacement; Empreinte = $_.Thumbprint } })
    }
    if ($anciennes.Count -eq 0) { return }
    Write-Warning ("Ancienne autorité « par PC » trouvée sur ce PC ($($anciennes.Count) certificat(s) « $($sujetAutoriteAncienne.Substring(3)) ») : empreintes " +
        (($anciennes | ForEach-Object { "$($_.Magasin) $($_.Empreinte)" }) -join " ; ") + ". Elle n'est PAS supprimée et ne sert plus : le certificat du serveur est désormais signé par l'autorité unique « $($sujetAutorite.Substring(3)) ». Procédure : " +
        "(1) installer la nouvelle autorité (autorite-pharmacie.crt) sur les postes et téléphones ; " +
        "(2) quand tous fonctionnent, retirer l'ancienne à la main, sur ce PC : Get-ChildItem Cert:\$Magasin\My | Where-Object Subject -eq '$sujetAutoriteAncienne' | Remove-Item, " +
        "et sur chaque poste : certutil -delstore Root `"PharmaOfficine Autorite locale`" (docs/mise-en-production.md, « Ancienne autorité par PC »).")
}

# ---------------------------------------------------------------------------------------------------------------------
# Configuration de l'API et pare-feu (communs au mode PRINCIPAL et au mode -Importer)
# ---------------------------------------------------------------------------------------------------------------------

function Write-ConfigurationApi {
    # Aucun secret : le certificat est lu dans le magasin Windows.
    # http : « localhost » = 127.0.0.1 et [::1], ce PC seulement. https : tout le réseau.
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
}

function Set-ReglePareFeu {
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
}

# Retire les anciens certificats de serveur du même sujet, APRÈS la mise en place du nouveau (jamais deux certificats pour Kestrel)
function Remove-AnciensCertificatsServeur([string] $empreinteAGarder) {
    $autres = @(Get-ChildItem $store | Where-Object { $_.Subject -eq $sujetServeur -and $_.Thumbprint -ne $empreinteAGarder })
    if ($autres.Count -gt 0 -and $PSCmdlet.ShouldProcess("$($autres.Count) ancien(s) certificat(s) du serveur", "Supprimer")) {
        $autres | Remove-Item
        Write-Host "$($autres.Count) ancien(s) certificat(s) du serveur retiré(s) du magasin."
    }
}

# =====================================================================================================================
# Mode -RestaurerAutorite : importer l'autorité depuis le PFX du coffre
# =====================================================================================================================
if ($PSCmdlet.ParameterSetName -eq "Restaurer") {
    if (-not (Test-Path -LiteralPath $FichierSauvegardeAutorite -PathType Leaf)) { throw "Fichier de l'autorité introuvable : $FichierSauvegardeAutorite" }
    Show-AncienneAutorite
    $presentes = @(Get-ChildItem $store | Where-Object { $_.Subject -eq $sujetAutorite })
    if ($simulation) {
        Write-Host "${etiquette}L'autorité serait importée depuis $FichierSauvegardeAutorite dans $store (clé non exportable), après saisie de la phrase de passe ($($presentes.Count) autorité(s) déjà présente(s))."
        Write-Host "Simulation terminée : rien n'a été modifié."
        return
    }
    $phrase = Read-Host "Phrase de passe du fichier de l'autorité (coffre)" -AsSecureString
    $lue = Read-Pfx $FichierSauvegardeAutorite $phrase
    if ($lue.Subject -ne $sujetAutorite -or -not (Test-EstAutorite $lue)) { throw "Ce fichier n'est pas l'autorité de la pharmacie ($sujetAutorite) : sujet lu « $($lue.Subject) »." }
    if (-not $lue.HasPrivateKey) { throw "Le fichier ne contient pas la clé privée de l'autorité : il ne permettrait pas de signer." }
    if ($lue.NotAfter -le (Get-Date)) { throw "L'autorité de ce fichier a expiré le $($lue.NotAfter.ToString('dd/MM/yyyy'))." }
    $empreinte = Get-Empreinte $lue
    if ($presentes | Where-Object { $_.Thumbprint -eq $lue.Thumbprint }) {
        Write-Host "Cette autorité est déjà présente dans le magasin de ce PC : rien à faire."
    }
    elseif ($presentes.Count -gt 0) {
        throw "Une autre autorité « $($sujetAutorite.Substring(3)) » (empreinte $($presentes[0].Thumbprint)) est déjà dans le magasin de ce PC. Rien n'est écrasé : vérifier laquelle est la bonne, puis retirer l'autre à la main."
    }
    else {
        [void](Add-AuMagasin ([IO.File]::ReadAllBytes($FichierSauvegardeAutorite)) $phrase $null)
        Write-Host "Autorité importée dans $store (clé non exportable) : $($lue.Thumbprint), valable jusqu'au $($lue.NotAfter.ToString('dd/MM/yyyy'))."
    }
    Write-Host "Empreinte SHA-256 de l'autorité : $empreinte"
    Write-Host "Elle doit être identique à celle notée à la création et à celle des postes. Aucune réinstallation n'est nécessaire sur les postes."
    Write-Host "Suite : relancer ce script en mode principal (sans -FichierSauvegardeAutorite) pour créer le certificat de ce PC."
    return
}

# =====================================================================================================================
# Mode -CertificatPourAutrePc : certificat du 2e serveur ou du PC de secours, signé par l'autorité
# =====================================================================================================================
if ($PSCmdlet.ParameterSetName -eq "AutrePc") {
    foreach ($a in $AdressesAutrePc) { if (-not (Test-Ip $a)) { throw "Adresse IP invalide : « $a » (pour un nom, utiliser -NomsAutrePc)." } }
    foreach ($n in $NomsAutrePc) { if ([string]::IsNullOrWhiteSpace($n) -or (Test-Ip $n)) { throw "Nom invalide : « $n » (pour une adresse IP, utiliser -AdressesAutrePc)." } }
    if (@($NomsAutrePc).Count + @($AdressesAutrePc).Count -eq 0) { throw "Rien n'est deviné : indiquer au moins un nom (-NomsAutrePc) ou une adresse (-AdressesAutrePc) de l'autre PC." }
    Test-DossierDeFichier $FichierCertificat "-FichierCertificat"
    if (Test-Path -LiteralPath $FichierCertificat) { throw "Le fichier existe déjà : $FichierCertificat (rien n'est écrasé ; choisir un autre nom, par exemple avec la date)." }
    $nomsAutre = @("localhost", "127.0.0.1") + @($NomsAutrePc) + @($AdressesAutrePc) |
        Where-Object { $_ } | ForEach-Object { $_.Trim() } | Sort-Object -Unique { $_.ToLowerInvariant() }
    Write-Host "${etiquette}Noms et adresses du certificat de l'autre PC : $($nomsAutre -join ', ')"
    Show-AncienneAutorite
    $autorite = Get-ChildItem $store | Where-Object { $_.Subject -eq $sujetAutorite -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $autorite) {
        if ($simulation) { Write-Warning "Aucune autorité « $($sujetAutorite.Substring(3)) » dans $store : ce mode se lance sur le PC principal (ou après -RestaurerAutorite)." }
        else { throw "Aucune autorité « $($sujetAutorite.Substring(3)) » dans $store : lancer ce mode sur le PC principal (après le mode principal), ou après -RestaurerAutorite." }
    }
    elseif (-not $autorite.HasPrivateKey) { throw "L'autorité de $store n'a pas de clé privée : elle ne peut pas signer. Utiliser -RestaurerAutorite avec le PFX du coffre." }
    if ($simulation) {
        Write-Host "${etiquette}Un certificat serait créé (825 jours), signé par l'autorité, puis exporté dans $FichierCertificat, protégé par une phrase de passe saisie à l'écran."
        Write-Host "Simulation terminée : rien n'a été modifié."
        return
    }
    $phrase = Read-PhraseNouvelle "Phrase de passe du fichier PFX de l'autre PC (12 caractères au moins)"
    $certificat = New-CertificatServeur $autorite $nomsAutre $sujetServeur $joursServeur
    [IO.File]::WriteAllBytes($FichierCertificat, $certificat.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $phrase))
    $relu = Read-Pfx $FichierCertificat $phrase
    if ($relu.Thumbprint -ne $certificat.Thumbprint -or -not $relu.HasPrivateKey) { throw "Le fichier écrit n'est pas relisible : $FichierCertificat" }
    Write-Host "Certificat de l'autre PC écrit : $FichierCertificat (valable jusqu'au $($certificat.NotAfter.ToString('dd/MM/yyyy')))."
    Write-Host "Noms et adresses du certificat : $(Get-NomsDuCertificat $certificat)"
    Write-Host "Copier ce fichier par clé USB (jamais par le réseau, jamais par courriel), puis sur l'autre PC : .\certificat_https.ps1 -Importer -FichierCertificat <fichier> -DossierApi <dossier de l'API> -PareFeu"
    Write-Host "La phrase de passe se transmet à part, oralement ou sur papier."
    return
}

# =====================================================================================================================
# Mode -Importer : sur le 2e serveur ou le PC de secours
# =====================================================================================================================
if ($PSCmdlet.ParameterSetName -eq "Importer") {
    if (-not (Test-Path $DossierApi)) { throw "Dossier de l'API introuvable : $DossierApi" }
    if (-not (Test-Path -LiteralPath $FichierCertificat -PathType Leaf)) { throw "Fichier du certificat introuvable : $FichierCertificat" }
    Show-AncienneAutorite
    if ($simulation) {
        Write-Host "${etiquette}Le certificat de $FichierCertificat serait importé dans $store (clé non exportable), après saisie de la phrase de passe ; les anciens certificats « $($sujetServeur.Substring(3)) » seraient retirés ensuite."
        Write-Host "${etiquette}Aucune autorité ne serait créée sur ce PC."
        Write-ConfigurationApi
        Set-ReglePareFeu
        Write-Host ""
        Write-Host "Simulation terminée : rien n'a été modifié."
        return
    }
    $phrase = Read-Host "Phrase de passe du fichier du certificat" -AsSecureString
    $lu = Read-Pfx $FichierCertificat $phrase
    if ($lu.Subject -ne $sujetServeur) { throw "Ce fichier n'est pas un certificat de serveur de la pharmacie ($sujetServeur) : sujet lu « $($lu.Subject) »." }
    if (Test-EstAutorite $lu) { throw "Ce fichier est une autorité : pour l'importer, utiliser -RestaurerAutorite." }
    if (-not $lu.HasPrivateKey) { throw "Le fichier ne contient pas la clé privée du certificat." }
    if ($lu.NotAfter -le (Get-Date)) { throw "Ce certificat a expiré le $($lu.NotAfter.ToString('dd/MM/yyyy')) : en générer un nouveau sur le principal (-CertificatPourAutrePc)." }
    if ($lu.Issuer -ne $sujetAutorite) { Write-Warning "Ce certificat n'est pas signé par « $($sujetAutorite.Substring(3)) » (émetteur lu : $($lu.Issuer)) : les postes risquent de le refuser." }
    $sanTexte = Get-NomsDuCertificat $lu
    $localNoms = @($env:COMPUTERNAME) + @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty IPAddress)
    if (-not ($localNoms | Where-Object { $_ -and $sanTexte -match [regex]::Escape($_) })) {
        Write-Warning "Ni le nom ni aucune adresse de ce PC ne figure dans le certificat ($sanTexte). Les postes ne pourront le joindre que par un nom ou une adresse listé(e)."
    }
    Write-Host "Noms et adresses du certificat : $sanTexte"
    $dejaLa = Get-ChildItem $store | Where-Object { $_.Thumbprint -eq $lu.Thumbprint } | Select-Object -First 1
    if ($dejaLa) { Write-Host "Ce certificat est déjà dans le magasin de ce PC : réutilisé ($($lu.Thumbprint))." }
    else {
        [void](Add-AuMagasin ([IO.File]::ReadAllBytes($FichierCertificat)) $phrase "PharmaOfficine serveur importé")
        Write-Host "Certificat importé dans $store (clé non exportable) : $($lu.Thumbprint), valable jusqu'au $($lu.NotAfter.ToString('dd/MM/yyyy'))."
    }
    Remove-AnciensCertificatsServeur $lu.Thumbprint
    Write-ConfigurationApi
    Set-ReglePareFeu
    Write-Host ""
    Write-Host "Redémarrez le service « PharmaOfficine API » s'il tourne (sur le 2e serveur et le PC de secours il est arrêté tant qu'il n'y a pas de bascule), puis, après une bascule, ouvrez : https://<nom ou adresse du certificat>:$PortHttps/api/healthcheck"
    Write-Host "Aucune autorité n'a été créée sur ce PC : les postes lui font confiance par l'autorité de la pharmacie déjà installée."
    return
}

# =====================================================================================================================
# Mode principal (par défaut)
# =====================================================================================================================
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

Show-AncienneAutorite

# 1. Autorité unique de la pharmacie (réutilisée si elle existe déjà)
$autorite = Get-ChildItem $store | Where-Object { $_.Subject -eq $sujetAutorite -and $_.NotAfter -gt (Get-Date).AddMonths(1) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if ($autorite) {
    if (-not $autorite.HasPrivateKey) { throw "L'autorité de $store n'a pas de clé privée : elle ne peut pas signer. Utiliser -RestaurerAutorite avec le PFX du coffre." }
    Write-Host "${etiquette}Autorité de la pharmacie existante réutilisée : $($autorite.Thumbprint)"
}
else {
    if (-not $FichierSauvegardeAutorite) {
        throw ("Première exécution : l'autorité de la pharmacie doit être créée et sa clé sauvegardée. Préciser -FichierSauvegardeAutorite <chemin>.pfx " +
               "(clé USB du coffre, différente de -DossierExport). Si le PC principal a été remplacé : -RestaurerAutorite avec le PFX du coffre.")
    }
    Test-DossierDeFichier $FichierSauvegardeAutorite "-FichierSauvegardeAutorite"
    if (Test-Path -LiteralPath $FichierSauvegardeAutorite) { throw "Le fichier existe déjà : $FichierSauvegardeAutorite (rien n'est écrasé ; pour utiliser ce fichier : -RestaurerAutorite)." }
    $dossierPfx = [IO.Path]::GetFullPath((Split-Path -Path $FichierSauvegardeAutorite -Parent)).TrimEnd('\')
    if ($dossierPfx -eq [IO.Path]::GetFullPath($DossierExport).TrimEnd('\')) {
        throw "Le PFX de l'autorité (clé privée, pour le coffre) ne doit pas être rangé avec le certificat public destiné aux postes : choisir un autre dossier que -DossierExport."
    }
    if ($simulation) {
        Write-Host "${etiquette}L'autorité serait créée (valable 10 ans), sa clé sauvegardée dans $FichierSauvegardeAutorite (phrase de passe saisie à l'écran), puis importée dans $store (clé non exportable)."
    }
    else {
        Write-Host "Création de l'autorité de la pharmacie. Le fichier $FichierSauvegardeAutorite est LE SEUL exemplaire de sauvegarde de sa clé : le ranger au coffre."
        $phrase = Read-PhraseNouvelle "Phrase de passe du fichier de l'autorité (12 caractères au moins, à conserver à part du fichier)"
        $nouvelle = New-CertificatAutorite $sujetAutorite
        $octets = $nouvelle.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $phrase)
        [IO.File]::WriteAllBytes($FichierSauvegardeAutorite, $octets)
        $relu = Read-Pfx $FichierSauvegardeAutorite $phrase
        if ($relu.Thumbprint -ne $nouvelle.Thumbprint -or -not $relu.HasPrivateKey) { throw "Le fichier de sauvegarde écrit n'est pas relisible : $FichierSauvegardeAutorite (l'autorité n'a PAS été importée ; supprimer ce fichier et recommencer)." }
        [void](Add-AuMagasin $octets $phrase $null)
        $autorite = Get-ChildItem $store | Where-Object { $_.Thumbprint -eq $nouvelle.Thumbprint } | Select-Object -First 1
        Write-Host "Autorité créée et sauvegardée : $($autorite.Thumbprint) ; clé non exportable dans $store."
    }
}

# 2. Certificat de ce PC : réutilisé tant que l'autorité et la liste de noms/adresses sont les mêmes (marque dans FriendlyName)
$san = $noms -join ", "
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
    Remove-AnciensCertificatsServeur $valide.Thumbprint
    Write-Host "${etiquette}Certificat du serveur existant réutilisé : $($valide.Thumbprint), valable jusqu'au $($valide.NotAfter.ToString('dd/MM/yyyy'))"
}
elseif ($simulation) {
    Write-Host "${etiquette}Un certificat du serveur serait créé, signé par l'autorité ($($existants.Count) ancien(s) remplacé(s) ensuite) pour : $san"
}
else {
    $serveur = New-CertificatServeur $autorite $noms $sujetServeur $joursServeur
    $phraseInterne = New-PhraseAleatoire
    [void](Add-AuMagasin $serveur.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $phraseInterne) $phraseInterne $marque)
    Remove-AnciensCertificatsServeur $serveur.Thumbprint
    Write-Host "Certificat du serveur créé : $($serveur.Thumbprint), valable jusqu'au $($serveur.NotAfter.ToString('dd/MM/yyyy'))"
}

# 3. Configuration de l'API
Write-ConfigurationApi

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
        [IO.File]::WriteAllBytes($crt, $autorite.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
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
Set-ReglePareFeu

$adresseEssai = ($noms | Where-Object { $_ -ne "localhost" -and $_ -ne "127.0.0.1" } | Select-Object -First 1)
Write-Host ""
if ($simulation) { Write-Host "Simulation terminée : rien n'a été modifié." }
else {
    Write-Host "Redémarrez le service « PharmaOfficine API », puis ouvrez : https://${adresseEssai}:$PortHttps/mobile/"
    Write-Host "Copiez le contenu de $DossierExport sur clé USB (jamais par le réseau) et installez le certificat sur chaque poste et téléphone."
    Write-Host "Le PFX de l'autorité ($FichierSauvegardeAutorite, si créé à l'instant) va au coffre, avec sa phrase de passe rangée à part."
}
