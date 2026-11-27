<#
.SYNOPSIS
    Prépare le dossier de livraison d'une officine : publie l'API et le poste, puis nettoie la copie livrée.

.DESCRIPTION
    Remplace les deux commandes « dotnet publish » du manuel de MEP (A18, et la publication du poste), avec le nettoyage du lot P1 de la
    protection du code (décision du titulaire du 2026-11-27 : « nettoyer à la livraison, pas dans git ») :
      - <Sortie>\Api   : Pharma.Api publié (Release, win-x64, autonome), scripts SQL dans database\ et database\init\ ;
      - <Sortie>\Poste : Pharma.Desktop publié (Release, win-x64, autonome) ;
      - publication sans symboles de débogage, puis scripts\mep\nettoyer_livraison.ps1 (plus de .pdb ni de configuration de
        développement, scripts SQL sans commentaires, contrôles) ;
      - contrôle qu'aucun mot de passe n'est écrit dans Api\appsettings.json.
    À lancer sur le PC des sources (SDK .NET 10), depuis le dossier du dépôt. Le dépôt n'est jamais modifié.
    Voir docs\manuel-mep.md (A18) et docs\protection-du-code.md.

.PARAMETER Sortie
    Dossier de livraison à créer (ex. D:\Livraison). Refusé s'il existe et n'est pas vide, sauf avec -Remplacer.

.PARAMETER Remplacer
    Vide le dossier de livraison s'il existe déjà.

.EXAMPLE
    .\scripts\mep\creer_livraison.ps1 -Sortie D:\Livraison
#>
param(
    [Parameter(Mandatory)][string]$Sortie,
    [switch]$Remplacer
)

$ErrorActionPreference = "Stop"
$racine = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

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

& (Join-Path $PSScriptRoot "nettoyer_livraison.ps1") -Dossier $Sortie

$dernier = @(Get-ChildItem -LiteralPath (Join-Path $Sortie "Api\database") -File -Filter "*.sql" | Where-Object { $_.Name -match '^\d{3}_' } |
    Sort-Object Name)[-1].Name
Write-Host ""
Write-Host "Livraison prête dans $Sortie (dernier script : $dernier)." -ForegroundColor Green
