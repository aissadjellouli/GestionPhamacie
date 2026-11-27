<#
.SYNOPSIS
    Prépare le dossier de livraison d'une officine : publie l'API et le poste, puis nettoie la copie livrée.

.DESCRIPTION
    Remplace les deux commandes « dotnet publish » du manuel de MEP (A18, et la publication du poste), avec le nettoyage du lot P1 de la
    protection du code (décision du titulaire du 2026-11-27 : « nettoyer à la livraison, pas dans git ») :
      - <Sortie>\Api   : Pharma.Api publié (Release, win-x64, autonome), scripts SQL dans database\ et database\init\ ;
      - <Sortie>\Poste : Pharma.Desktop publié (Release, win-x64, autonome) ;
      - <Sortie>\scripts : les seuls scripts utiles en officine (lot P1 bis : installation et mise à jour de la base, certificat HTTPS,
        sauvegarde, restauration, PC de secours, 2e serveur), à copier sur le PC serveur à la place du dossier scripts\ du dépôt ;
      - publication sans symboles de débogage, puis scripts\mep\nettoyer_livraison.ps1 (plus de .pdb ni de configuration de
        développement, scripts SQL et PowerShell sans commentaires, contrôles) ;
      - contrôle qu'aucun mot de passe n'est écrit dans Api\appsettings.json ;
      - obfuscation de nos assemblys (lot P2 de la protection du code) par .NET Reactor en version complète (option -licensed : aucun
        fichier de la version d'essai ne peut partir), fichiers de correspondance rangés HORS de la livraison.
    À lancer sur le PC des sources (SDK .NET 10, .NET Reactor installé et activé), depuis le dossier du dépôt. Le dépôt n'est jamais modifié.
    Voir docs\manuel-mep.md (A18) et docs\protection-du-code.md (§ 9).

.PARAMETER Sortie
    Dossier de livraison à créer (ex. D:\Livraison). Refusé s'il existe et n'est pas vide, sauf avec -Remplacer.

.PARAMETER Remplacer
    Vide le dossier de livraison s'il existe déjà.

.PARAMETER Obfuscateur
    Chemin de dotNET_Reactor.Console.exe. Vide : cherché dans %LOCALAPPDATA%\Programs\dotNET_Reactor puis dans Program Files.

.PARAMETER Correspondances
    Dossier d'archive des fichiers de correspondance (.nrmap : nom brouillé vers nom réel, pour décoder une trace d'erreur). Vide : dossier
    « <Sortie>-correspondances » à côté de la livraison. Un sous-dossier daté est créé à chaque fabrication. Ces fichiers contiennent tous
    les noms réels : à archiver avec la version livrée, jamais en officine.

.PARAMETER SansObfuscation
    Fabrique une livraison NON obfusquée (essai interne, comparaison avec une livraison obfusquée). Ne se livre pas en officine.

.EXAMPLE
    .\scripts\mep\creer_livraison.ps1 -Sortie D:\Livraison
#>
param(
    [Parameter(Mandatory)][string]$Sortie,
    [switch]$Remplacer,
    [string]$Obfuscateur = "",
    [string]$Correspondances = "",
    [switch]$SansObfuscation
)

$ErrorActionPreference = "Stop"
$racine = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

# L'outil d'obfuscation est cherché avant toute publication : pas de livraison fabriquée à moitié
if (-not $SansObfuscation) { $Obfuscateur = & (Join-Path $PSScriptRoot "obfusquer_livraison.ps1") -ChercherOutil -Obfuscateur $Obfuscateur }

if (Test-Path -LiteralPath $Sortie) {
    if (@(Get-ChildItem -LiteralPath $Sortie -Force).Count -gt 0) {
        if (-not $Remplacer) { throw "Le dossier $Sortie existe et n'est pas vide : choisissez un autre dossier ou ajoutez -Remplacer." }
        $plein = (Resolve-Path -LiteralPath $Sortie).Path
        if ($plein.TrimEnd('\') -ieq $racine.TrimEnd('\') -or $racine.StartsWith($plein.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Le dossier $Sortie contient le dépôt des sources : refusé."
        }
        Remove-Item -LiteralPath $Sortie -Recurse -Force
    }
}
New-Item -ItemType Directory -Force $Sortie | Out-Null

function Publier([string]$Projet, [string]$Dossier) {
    Write-Host "== Publication de $Projet vers $Dossier" -ForegroundColor Cyan
    & dotnet publish (Join-Path $racine "src\$Projet\$Projet.csproj") -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false -o (Join-Path $Sortie $Dossier) -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Échec de la publication de $Projet" }
}
Publier "Pharma.Api" "Api"
Publier "Pharma.Desktop" "Poste"

# Aucun mot de passe dans la configuration livrée (il est saisi sur le PC serveur, voir le manuel de MEP)
$livree = Get-Content -LiteralPath (Join-Path $Sortie "Api\appsettings.json") -Raw -Encoding UTF8
if ($livree -match "Password=[^;`"]+") { throw "Mot de passe présent dans Api\appsettings.json : livraison refusée." }

# Obfuscation de nos assemblys (lot P2) : scripts\mep\obfusquer_livraison.ps1 (liste des assemblys, réglage, contrôles, correspondances hors livraison)
if ($SansObfuscation) {
    Write-Warning "Livraison NON obfusquée (-SansObfuscation) : essai interne seulement, ne pas la livrer en officine."
}
else {
    & (Join-Path $PSScriptRoot "obfusquer_livraison.ps1") -Dossier $Sortie -Obfuscateur $Obfuscateur -Correspondances $Correspondances
}

# Scripts livrés en officine (lot P1 bis) : liste fermée, rien d'autre du dossier scripts\ du dépôt ne part (ni dev\, ni paquet\, ni les
# scripts du PC des sources, ni ceux du serveur central). Un nouveau script se classe ici ou dans ScriptsLivraisonTests (« non livrés »).
$scriptsLivres = @(
    "certificat_https.ps1"
    "sauvegarde_pharmacie.ps1"
    "restaurer_pharmacie.ps1"
    "basculer-pc-secours.ps1"
    "mep\creer_base_officine.ps1"
    "mep\creer_dossier_journaux.ps1"
    "mep\mettre_a_jour_base_officine.ps1"
    "mep\outils_mep.ps1"
    "second-serveur\autoriser-lecture-copie-ged.ps1"
    "second-serveur\basculer-second-serveur.ps1"
    "second-serveur\commun.ps1"
    "second-serveur\copier-ged.ps1"
    "second-serveur\initialiser-second-serveur.ps1"
    "second-serveur\ouvrir-port-base.ps1"
    "second-serveur\partager-ged.ps1"
    "second-serveur\preparer-principal.ps1"
    "second-serveur\remettre-en-second.ps1"
    "second-serveur\verifier-second-serveur.ps1"
)
Write-Host "== Scripts d'officine vers scripts ($($scriptsLivres.Count))" -ForegroundColor Cyan
$sortieScripts = Join-Path $Sortie "scripts"
foreach ($s in $scriptsLivres) {
    $cible = Join-Path $sortieScripts $s
    New-Item -ItemType Directory -Force (Split-Path $cible -Parent) | Out-Null
    Copy-Item -LiteralPath (Join-Path $racine "scripts\$s") -Destination $cible
}

& (Join-Path $PSScriptRoot "nettoyer_livraison.ps1") -Dossier $Sortie

$racineScripts = (Get-Item -LiteralPath $sortieScripts).FullName.TrimEnd('\')
$presents = @(Get-ChildItem -LiteralPath $sortieScripts -Recurse -File | ForEach-Object { $_.FullName.Substring($racineScripts.Length + 1) })
$enTrop = @($presents | Where-Object { $scriptsLivres -notcontains $_ })
if ($enTrop.Count -gt 0 -or $presents.Count -ne $scriptsLivres.Count) { throw "Le dossier scripts de la livraison ne correspond pas à la liste des scripts livrés ($($enTrop -join ', ')) : livraison refusée." }

$dernier = @(Get-ChildItem -LiteralPath (Join-Path $Sortie "Api\database") -File -Filter "*.sql" | Where-Object { $_.Name -match '^\d{3}_' } |
    Sort-Object Name)[-1].Name
Write-Host ""
Write-Host "Livraison prête dans $Sortie (dernier script : $dernier)." -ForegroundColor Green
