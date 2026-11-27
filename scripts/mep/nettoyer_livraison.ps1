<#
.SYNOPSIS
    Nettoie un dossier de livraison avant qu'il parte en officine (lot P1 de la protection du code, 2026-11-27).

.DESCRIPTION
    Décision du titulaire du 2026-11-27 : « nettoyer à la livraison, pas dans git ». Le dépôt garde tout (scripts SQL commentés,
    symboles de débogage) ; seule la COPIE livrée est nettoyée :
      1. supprime les fichiers .pdb (symboles de débogage : inutiles en officine, ils aident à décompiler le programme) ;
      2. supprime appsettings.Development.json (configuration de développement) ;
      3. retire les commentaires de tous les scripts .sql (« -- … », « /* … */ ») sans toucher aux textes, aux commandes psql, aux corps
         de fonctions (hors leurs commentaires) ni à l'en-tête « 001 à NNN » des fichiers d'init (NettoyageSql.cs) ;
      4. contrôle le résultat : plus aucun .pdb, scripts SQL stables (un second nettoyage ne change rien), en-tête des fichiers d'init
         présent, aucun fichier de clé ni de certificat (.pem, .pfx, .snk, .key) dans le dossier.
    Le script refuse un dossier qui ressemble au dépôt (présence de .git ou de src\) : il ne nettoie jamais les sources.
    Voir docs\protection-du-code.md et docs\scripts-mep.md.

.PARAMETER Dossier
    Dossier de livraison (ex. « D:\Livraison » contenant Api\ et Poste\), ou dossier du paquet d'essai.

.EXAMPLE
    .\scripts\mep\nettoyer_livraison.ps1 -Dossier D:\Livraison
#>
param([Parameter(Mandatory)][string]$Dossier)

$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $Dossier -PathType Container)) { throw "Dossier de livraison introuvable : $Dossier" }
$Dossier = (Resolve-Path -LiteralPath $Dossier).Path
if ((Test-Path -LiteralPath (Join-Path $Dossier ".git")) -or (Test-Path -LiteralPath (Join-Path $Dossier "src"))) {
    throw "Le dossier $Dossier ressemble au dépôt des sources (.git ou src\) : nettoyage refusé (seule la copie livrée se nettoie)."
}
if (-not ('PharmaOfficine.Livraison.NettoyageSql' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot "NettoyageSql.cs")
}

Write-Host ""
Write-Host "== Nettoyage de la livraison : $Dossier" -ForegroundColor Cyan

# 1. Symboles de débogage
$pdb = @(Get-ChildItem -LiteralPath $Dossier -Recurse -File -Filter "*.pdb")
$pdb | Remove-Item -Force
Write-Host "   Symboles de débogage (.pdb) supprimés : $($pdb.Count)"

# 2. Configuration de développement
$dev = @(Get-ChildItem -LiteralPath $Dossier -Recurse -File -Filter "appsettings.Development.json")
$dev | Remove-Item -Force
Write-Host "   Configuration de développement supprimée : $($dev.Count)"

# 3. Commentaires des scripts SQL (UTF-8 sans BOM, comme dans le dépôt)
$utf8 = New-Object System.Text.UTF8Encoding($false)
$sql = @(Get-ChildItem -LiteralPath $Dossier -Recurse -File -Filter "*.sql")
$avant = 0; $apres = 0
foreach ($f in $sql) {
    $texte = [System.IO.File]::ReadAllText($f.FullName, $utf8)
    $propre = [PharmaOfficine.Livraison.NettoyageSql]::Nettoyer($texte)
    $avant += $texte.Length; $apres += $propre.Length
    [System.IO.File]::WriteAllText($f.FullName, $propre, $utf8)
}
$gain = if ($avant -gt 0) { [math]::Round(100 * ($avant - $apres) / $avant) } else { 0 }
Write-Host "   Scripts SQL sans commentaires : $($sql.Count) (taille réduite de $gain %)"

# 4. Contrôles
$reste = @(Get-ChildItem -LiteralPath $Dossier -Recurse -File -Filter "*.pdb")
if ($reste.Count -gt 0) { throw "Il reste $($reste.Count) fichier(s) .pdb : livraison refusée." }
foreach ($f in $sql) {
    $texte = [System.IO.File]::ReadAllText($f.FullName, $utf8)
    if ([PharmaOfficine.Livraison.NettoyageSql]::Nettoyer($texte) -cne $texte) { throw "Nettoyage instable pour $($f.Name) : livraison refusée." }
}
foreach ($init in @(Get-ChildItem -LiteralPath $Dossier -Recurse -File | Where-Object { $_.Name -in @("schema.sql", "seed_referentiel.sql") })) {
    if ([System.IO.File]::ReadAllText($init.FullName, $utf8) -notmatch "001 \u00e0 \d{3}") {
        throw "En-tête « 001 à NNN » absent de $($init.FullName) après nettoyage : livraison refusée."
    }
}
$secrets = @(Get-ChildItem -LiteralPath $Dossier -Recurse -File | Where-Object { $_.Extension -in @(".pem", ".pfx", ".snk", ".key") })
if ($secrets.Count -gt 0) { throw "Fichier de clé ou de certificat dans la livraison ($($secrets[0].FullName)) : livraison refusée." }
Write-Host "   [OK] Contrôles : aucun .pdb, scripts SQL stables, en-têtes d'init présents, aucun fichier de clé." -ForegroundColor Green
