<#
.SYNOPSIS
    Gardes communes des scripts de fabrication (creer_livraison.ps1, creer_paquet_essai.ps1, nettoyer_livraison.ps1) : ils ne vident et ne
    réécrivent jamais un dossier du dépôt des sources.

.DESCRIPTION
    À charger par « . garde_livraison.ps1 » (ce script ne fait rien seul). Il reste sur le PC des sources, il n'est pas livré.
      Get-CheminPlein          chemin complet d'un dossier, existant ou non, sans préfixe de fournisseur (un chemin réseau reste « \\serveur\partage »).
      Assert-DossierHorsDepot  refuse un dossier situé dans un dépôt de sources (dossier .git ou fichier PharmaOfficine.slnx dans ce dossier
                               ou l'un de ses parents), ou qui contient le dépôt de ces scripts.
      Assert-DossierLivraison  avant de vider un dossier existant : refuse tout dossier non vide qui n'est pas une livraison déjà fabriquée
                               (on y reconnaît Api\Pharma.Api.dll).
#>

function Get-CheminPlein([string]$Chemin) {
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Chemin).TrimEnd('\')
}

function Assert-DossierHorsDepot([string]$Chemin, [string]$RacineDepot) {
    $plein = Get-CheminPlein $Chemin
    $racine = Get-CheminPlein $RacineDepot
    if ($plein -ieq $racine -or $racine.StartsWith($plein + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Le dossier $plein contient le dépôt des sources ($racine) : refusé, rien n'a été modifié."
    }
    $d = $plein
    while ($d) {
        if ((Test-Path -LiteralPath (Join-Path $d ".git")) -or (Test-Path -LiteralPath (Join-Path $d "PharmaOfficine.slnx"))) {
            throw "Le dossier $plein est dans un dépôt de sources ($d) : refusé, rien n'a été modifié. Choisissez un dossier hors du dépôt."
        }
        $parent = [System.IO.Path]::GetDirectoryName($d)   # null à la racine d'un lecteur ou d'un partage
        if (-not $parent -or $parent -ieq $d) { break }
        $d = $parent
    }
    return $plein
}

function Assert-DossierLivraison([string]$Chemin) {
    if (-not (Test-Path -LiteralPath $Chemin -PathType Container)) { return }
    if (@(Get-ChildItem -LiteralPath $Chemin -Force).Count -eq 0) { return }
    if (-not (Test-Path -LiteralPath (Join-Path $Chemin "Api\Pharma.Api.dll") -PathType Leaf)) {
        throw "Le dossier $Chemin existe, n'est pas vide et n'est pas une livraison déjà fabriquée (Api\Pharma.Api.dll absent) : il n'est pas vidé. Choisissez un autre dossier, ou supprimez-le vous-même."
    }
}
