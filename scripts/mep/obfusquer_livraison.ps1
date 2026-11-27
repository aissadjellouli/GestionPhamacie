<#
.SYNOPSIS
    Obfusque nos assemblys dans un dossier publié (livraison d'officine ou paquet d'essai). Lancé par creer_livraison.ps1 et creer_paquet_essai.ps1.

.DESCRIPTION
    Lot P2 de la protection du code. Outil : .NET Reactor en version complète, toujours appelé avec -licensed (la version d'essai produit des
    fichiers qui cessent de fonctionner après 14 jours : code 101, refus). Réglage validé par l'essai du 2026-11-27
    (docs\protection-du-code.md § 9.5 à 9.8) : le public garde son nom (objets échangés en JSON, composants d'écran), aucune propriété
    renommée, flux de contrôle brouillé, textes chiffrés.
    Refuse le dossier si : un Pharma.*.dll publié n'est pas dans la liste (projet ajouté), un assembly de la liste manque, l'outil échoue,
    un fichier ne change pas ou sa correspondance manque.
    Les fichiers de correspondance (.nrmap : nom brouillé vers nom réel, pour décoder une trace d'erreur) sont rangés dans un sous-dossier
    daté de -Correspondances, HORS du dossier publié : à archiver avec la version, jamais en officine (ils contiennent tous les noms réels).

.PARAMETER Dossier
    Dossier publié, contenant Api\ et Poste\.

.PARAMETER Obfuscateur
    Chemin de dotNET_Reactor.Console.exe. Vide : cherché dans %LOCALAPPDATA%\Programs\dotNET_Reactor puis dans Program Files.

.PARAMETER Correspondances
    Dossier d'archive des fichiers de correspondance. Vide : « <Dossier>-correspondances », à côté du dossier publié.

.PARAMETER ChercherOutil
    Ne fait que chercher l'outil : rend son chemin, ou refuse s'il est introuvable (appelé avant toute publication).

.EXAMPLE
    .\scripts\mep\obfusquer_livraison.ps1 -Dossier D:\Livraison
#>
param(
    [string]$Dossier = "",
    [string]$Obfuscateur = "",
    [string]$Correspondances = "",
    [switch]$ChercherOutil
)

$ErrorActionPreference = "Stop"

# Nos assemblys, les seuls à obfusquer (jamais ceux de .NET ni des bibliothèques). Tout Pharma.*.dll publié doit figurer ici (contrôlé plus bas
# et par ScriptsLivraisonTests : liste = tous les projets référencés par l'API et par le poste).
$assemblysObfusques = @(
    "Api\Pharma.Api.dll"
    "Api\Pharma.Application.dll"
    "Api\Pharma.Domain.dll"
    "Api\Pharma.Infrastructure.dll"
    "Api\Pharma.Shared.dll"
    "Poste\Pharma.Desktop.dll"
    "Poste\Pharma.Shared.dll"
)

if (-not $Obfuscateur) {
    $Obfuscateur = @(
        (Join-Path $env:LOCALAPPDATA "Programs\dotNET_Reactor\dotNET_Reactor.Console.exe"),
        (Join-Path $env:ProgramFiles "Eziriz\.NET Reactor\dotNET_Reactor.Console.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Eziriz\.NET Reactor\dotNET_Reactor.Console.exe")
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $Obfuscateur -or -not (Test-Path -LiteralPath $Obfuscateur)) {
    throw "dotNET_Reactor.Console.exe introuvable : installez et activez .NET Reactor (docs\protection-du-code.md § 9), précisez -Obfuscateur, ou fabriquez avec -SansObfuscation pour un essai interne (jamais pour une officine)."
}
if ($ChercherOutil) { return $Obfuscateur }

if (-not $Dossier -or -not (Test-Path -LiteralPath $Dossier -PathType Container)) { throw "Dossier publié introuvable : $Dossier" }
$Dossier = (Get-Item -LiteralPath $Dossier).FullName.TrimEnd('\')

$publies = @("Api", "Poste" | ForEach-Object { $sous = $_; Get-ChildItem -LiteralPath (Join-Path $Dossier $sous) -File -Filter "Pharma.*.dll" | ForEach-Object { "$sous\$($_.Name)" } })
$horsListe = @($publies | Where-Object { $assemblysObfusques -notcontains $_ })
$absents = @($assemblysObfusques | Where-Object { $publies -notcontains $_ })
if ($horsListe.Count -gt 0 -or $absents.Count -gt 0) {
    throw "La liste des assemblys à obfusquer ne correspond pas à la publication (hors liste : $($horsListe -join ', ') ; absents : $($absents -join ', ')) : mettez `$assemblysObfusques à jour (obfusquer_livraison.ps1). Refusé."
}

Write-Host "== Obfuscation de nos assemblys ($($assemblysObfusques.Count))" -ForegroundColor Cyan
if (-not $Correspondances) { $Correspondances = Join-Path (Split-Path $Dossier -Parent) ((Split-Path $Dossier -Leaf) + "-correspondances") }
$archive = Join-Path $Correspondances (Get-Date -Format "yyyyMMdd-HHmmss")
New-Item -ItemType Directory -Force $archive | Out-Null
$travail = Join-Path ([System.IO.Path]::GetTempPath()) ("pharma_obfuscation_" + [Guid]::NewGuid().ToString("N"))
try {
    foreach ($a in $assemblysObfusques) {
        $source = Join-Path $Dossier $a
        $nom = Split-Path $a -Leaf
        if (Test-Path -LiteralPath $travail) { Remove-Item -LiteralPath $travail -Recurse -Force }
        New-Item -ItemType Directory -Force $travail | Out-Null
        $avant = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        # Sortie d'erreur de l'outil : sous Windows PowerShell 5.1, « 2>&1 » avec « Stop » lèverait avant la lecture du code de sortie
        $avantErreurs = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            # -licensed : code 101 si l'outil n'est pas en version complète
            $messages = & $Obfuscateur -licensed -file $source -targetfile (Join-Path $travail $nom) `
                -obfuscation 1 -obfuscate_public_types 0 -exclude_properties 1 -control_flow_obfuscation 1 -stringencryption 1 `
                -mapping_file 1 -necrobit 0 -antitamp 0 -suppressildasm 1 -q 2>&1 | ForEach-Object { "$_" }
        }
        finally { $ErrorActionPreference = $avantErreurs }
        if ($LASTEXITCODE -eq 101) { throw "Obfuscation refusée (code 101) : .NET Reactor n'est pas activé en version complète sur ce PC (docs\protection-du-code.md § 9.3). Refusé." }
        if ($LASTEXITCODE -ne 0) { throw "Échec de l'obfuscation de $a (code $LASTEXITCODE) : $(($messages | Select-Object -Last 3) -join ' ')" }
        $produit = Join-Path $travail $nom
        $carte = @(Get-ChildItem -LiteralPath $travail -File -Filter "*.nrmap")
        if (-not (Test-Path -LiteralPath $produit) -or $carte.Count -ne 1 -or $carte[0].Length -eq 0) { throw "Obfuscation de $a : fichier ou correspondance manquant. Refusé." }
        if ((Get-FileHash -LiteralPath $produit -Algorithm SHA256).Hash -eq $avant) { throw "Obfuscation de $a : le fichier n'a pas changé. Refusé." }
        # Seul le .dll est repris : l'outil recopie aussi .exe, .deps.json et .runtimeconfig.json, déjà dans le dossier publié
        Copy-Item -LiteralPath $produit -Destination $source -Force
        Copy-Item -LiteralPath $carte[0].FullName -Destination (Join-Path $archive (($a -replace '\\', '_') + ".nrmap"))
        Write-Host "   $a"
    }
}
finally {
    if (Test-Path -LiteralPath $travail) { Remove-Item -LiteralPath $travail -Recurse -Force }
}
Write-Host "   Correspondances (pour décoder une trace de CETTE fabrication) : $archive" -ForegroundColor Yellow
Write-Host "   À archiver avec la version, hors du dossier publié : jamais en officine." -ForegroundColor Yellow
