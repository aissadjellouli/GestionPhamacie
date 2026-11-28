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
      3 bis. retire les commentaires de tous les scripts .ps1 (lot P1 bis), par l'analyse du langage PowerShell, en gardant « #Requires »
         et le bloc d'aide (.SYNOPSIS, lu par Get-Help) ; refuse le script si ses instructions changent ;
      4. contrôle le résultat : plus aucun .pdb, scripts SQL et PowerShell stables (un second nettoyage ne change rien), en-tête des
         fichiers d'init présent, aucun fichier de clé ni de certificat (.pem, .pfx, .snk, .key) dans le dossier.
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
# Seule la copie livrée se nettoie : jamais un dossier du dépôt des sources (ni le dépôt, ni l'un de ses sous-dossiers, ni un dossier qui le contient)
. (Join-Path $PSScriptRoot "garde_livraison.ps1")
$Dossier = Assert-DossierHorsDepot $Dossier (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)
if (-not ('PharmaOfficine.Livraison.NettoyageSql' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot "NettoyageSql.cs")
}

# Lecture d'un script PowerShell en UTF-8 strict ; retient s'il porte la signature UTF-8 (exigée par Windows PowerShell 5.1 pour les accents)
function Read-ScriptPowerShell([string]$Chemin) {
    $octets = [System.IO.File]::ReadAllBytes($Chemin)
    $signature = ($octets.Length -ge 3 -and $octets[0] -eq 0xEF -and $octets[1] -eq 0xBB -and $octets[2] -eq 0xBF)
    $debut = if ($signature) { 3 } else { 0 }
    $texte = (New-Object System.Text.UTF8Encoding($false, $true)).GetString($octets, $debut, $octets.Length - $debut)
    return @{ Texte = $texte; Signature = $signature }
}

function Get-JetonsPowerShell([string]$Texte, [string]$Nom) {
    $jetons = $null; $erreurs = $null
    [void][System.Management.Automation.Language.Parser]::ParseInput($Texte, [ref]$jetons, [ref]$erreurs)
    if ($erreurs.Count -gt 0) { throw "Script PowerShell mal formé ($Nom) : $($erreurs[0].Message)" }
    return , $jetons
}

# Suite des instructions d'un script, sans les commentaires ni les lignes vides : sert à prouver que le nettoyage ne change rien d'autre
function Get-InstructionsPowerShell([string]$Texte, [string]$Nom) {
    $suite = New-Object System.Text.StringBuilder
    $dernier = "NewLine"
    foreach ($j in (Get-JetonsPowerShell $Texte $Nom)) {
        $genre = [string]$j.Kind
        if ($genre -eq "Comment") { continue }
        if ($genre -eq "NewLine" -and $dernier -eq "NewLine") { continue }
        $dernier = $genre
        $contenu = if ($genre -eq "NewLine") { "" } else { $j.Text }
        [void]$suite.Append($genre).Append([char]1).Append($contenu).Append([char]2)
    }
    return $suite.ToString()
}

# Retire les commentaires d'un script PowerShell à partir de l'analyse du langage (un « # » dans un texte n'est pas un commentaire).
# Gardés : « #Requires » (instruction) et le bloc d'aide « <# .SYNOPSIS … #> » (lu par Get-Help sur le PC serveur).
function Remove-CommentairesPowerShell([string]$Texte, [string]$Nom) {
    $jetons = Get-JetonsPowerShell $Texte $Nom
    $retirer = @($jetons | Where-Object {
            [string]$_.Kind -eq "Comment" -and $_.Text -notmatch '^#requires\b' -and
            -not ($_.Text.StartsWith("<#") -and $_.Text -match '(?m)^\s*\.(SYNOPSIS|DESCRIPTION)\b')
        })
    $sortie = New-Object System.Text.StringBuilder $Texte
    $n = $Texte.Length
    for ($i = $retirer.Count - 1; $i -ge 0; $i--) {
        $debut = $retirer[$i].Extent.StartOffset; $fin = $retirer[$i].Extent.EndOffset
        $g = $debut; while ($g -gt 0 -and ($Texte[$g - 1] -eq ' ' -or $Texte[$g - 1] -eq "`t")) { $g-- }
        $d = $fin; while ($d -lt $n -and ($Texte[$d] -eq ' ' -or $Texte[$d] -eq "`t")) { $d++ }
        $seul = ($g -eq 0 -or $Texte[$g - 1] -eq "`n")
        $finDeLigne = ($d -ge $n -or $Texte[$d] -eq "`r" -or $Texte[$d] -eq "`n")
        if ($seul -and $finDeLigne) {
            # commentaire seul sur sa ou ses lignes : la ligne entière part
            if ($d -lt $n -and $Texte[$d] -eq "`r") { $d++ }
            if ($d -lt $n -and $Texte[$d] -eq "`n") { $d++ }
            [void]$sortie.Remove($g, $d - $g)
        }
        elseif ($finDeLigne) {
            # commentaire en fin de ligne : il part avec les espaces qui le précèdent
            [void]$sortie.Remove($g, $d - $g)
        }
        else {
            # commentaire au milieu d'une ligne : remplacé par un espace
            [void]$sortie.Remove($debut, $fin - $debut).Insert($debut, " ")
        }
    }
    return $sortie.ToString()
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
# Une configuration d'environnement réel (appsettings.Production.json, appsettings.Https.json…) restée à côté des sources porterait son mot
# de passe dans la livraison : refus (elle s'écrit sur le PC serveur, jamais sur le PC des sources)
$reelles = @(Get-ChildItem -LiteralPath $Dossier -Recurse -File -Filter "appsettings.*.json")
if ($reelles.Count -gt 0) {
    throw "Configuration d'un environnement réel présente dans la livraison : $(($reelles | ForEach-Object { $_.FullName }) -join ', '). Retirez-la des sources publiées, puis refaites la livraison."
}

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

# 3 bis. Commentaires des scripts PowerShell (lot P1 bis) : même encodage en sortie (signature UTF-8 gardée si elle existe)
$ps1 = @(Get-ChildItem -LiteralPath $Dossier -Recurse -File -Filter "*.ps1")
$avantPs = 0; $apresPs = 0
foreach ($f in $ps1) {
    $lu = Read-ScriptPowerShell $f.FullName
    $propre = Remove-CommentairesPowerShell $lu.Texte $f.Name
    if ((Get-InstructionsPowerShell $propre $f.Name) -cne (Get-InstructionsPowerShell $lu.Texte $f.Name)) {
        throw "Le nettoyage de $($f.Name) change ses instructions : livraison refusée."
    }
    if ((Remove-CommentairesPowerShell $propre $f.Name) -cne $propre) { throw "Nettoyage instable pour $($f.Name) : livraison refusée." }
    $avantPs += $lu.Texte.Length; $apresPs += $propre.Length
    [System.IO.File]::WriteAllText($f.FullName, $propre, (New-Object System.Text.UTF8Encoding($lu.Signature)))
}
$gainPs = if ($avantPs -gt 0) { [math]::Round(100 * ($avantPs - $apresPs) / $avantPs) } else { 0 }
Write-Host "   Scripts PowerShell sans commentaires : $($ps1.Count) (taille réduite de $gainPs %)"

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
# Fichiers de l'obfuscation qui ne partent jamais : correspondances (tous les noms réels) et licences de l'outil
$outil = @(Get-ChildItem -LiteralPath $Dossier -Recurse -File | Where-Object { $_.Extension -in @(".nrmap", ".license", ".v3lic", ".nrproj") })
if ($outil.Count -gt 0) { throw "Fichier de l'outil d'obfuscation dans la livraison ($($outil[0].FullName)) : livraison refusée." }
Write-Host "   [OK] Contrôles : aucun .pdb, scripts SQL et PowerShell stables, instructions PowerShell inchangées, en-têtes d'init présents, aucun fichier de clé." -ForegroundColor Green
