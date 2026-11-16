<#
.SYNOPSIS
    Rejeu complet de database\migration (001 à NNN) sur une COPIE jetable d'une base à jour : contrôle que toute la suite est rejouable.

.DESCRIPTION
    À lancer sur une copie (pg_dump -Fc de la base, puis pg_restore dans une base nouvelle), jamais sur une base en service.
    Le script refuse toute base dont le nom ne commence pas par « pharmacie_jetable_rejeu_ ».

    Il rejoue la suite exactement comme la restauration de l'application (Sauvegardes/RestaurationPostgres) :
      - scripts 001 à 032 dans une transaction ;
      - script 033 seul (CREATE INDEX CONCURRENTLY interdit en transaction) ;
      - scripts 034 et suivants dans une transaction.
    Les scripts 101b et 111b (administrateur) ne sont jamais rejoués. Le rejeu est fait -Passages fois (2 par défaut).

    Il compare ensuite, avant et après, la structure (pg_dump --schema-only), les lignes de chaque table (hors valeurs de séquences,
    que les INSERT ... ON CONFLICT DO NOTHING font avancer) et le nombre de lignes par table. Tout écart est affiché et donne un code de
    sortie 1. Code de sortie 0 : la suite est rejouable et ne change rien (structure et données).

    Quand le lancer : après tout nouveau script ou toute modification d'un ancien script (règle de docs\scripts-base-de-donnees.md),
    sur une copie de la base de développement ET sur une copie d'une base de recette déjà mise à jour.
    Le test automatique équivalent (base vide, schema.sql + seed) est tests\Pharma.Architecture.Tests\ScriptsRejouablesTests.cs.

    Mot de passe : jamais en paramètre ; variable PGPASSWORD déjà définie ou fichier pgpass.conf.

.PARAMETER Base
    Nom de la copie jetable (obligatoirement « pharmacie_jetable_rejeu_... »).

.PARAMETER Hote
    Serveur PostgreSQL (défaut : localhost).

.PARAMETER Port
    Port PostgreSQL (défaut : 5432).

.PARAMETER Compte
    Compte PostgreSQL propriétaire de la copie (défaut : pharma_app).

.PARAMETER Passages
    Nombre de rejeux complets (défaut : 2).

.PARAMETER DossierMigration
    Dossier des scripts (défaut : database\migration du dépôt).

.PARAMETER SansMarqueurReglages
    Preuve seulement : ne pose PAS le marqueur « pharma.reglage_central = on ». Sur une copie où une table de réglages est imposée par le serveur
    central, le rejeu doit alors ÉCHOUER (garde zz_reglage_garde du script 141) ; avec le marqueur (comportement normal), il réussit.

.PARAMETER PgBin
    Dossier bin de PostgreSQL (défaut : version la plus récente de C:\Program Files\PostgreSQL).

.EXAMPLE
    pg_dump -Fc -h localhost -U pharma_app -f $env:TEMP\copie.dump pharmacie_dev
    createdb -h localhost -U pharma_app pharmacie_jetable_rejeu_dev
    pg_restore -h localhost -U pharma_app -d pharmacie_jetable_rejeu_dev --no-owner --no-privileges $env:TEMP\copie.dump
    .\database\tests\rejeu_complet.ps1 -Base pharmacie_jetable_rejeu_dev
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Base,
    [string]$Hote = "localhost",
    [int]$Port = 5432,
    [string]$Compte = "pharma_app",
    [int]$Passages = 2,
    [string]$DossierMigration = "",
    [string]$PgBin = "",
    [switch]$SansMarqueurReglages
)

$ErrorActionPreference = "Stop"
if ($Base -notlike "pharmacie_jetable_rejeu_*") { throw "Base refusée : le rejeu complet ne s'exécute que sur une copie nommée pharmacie_jetable_rejeu_... (jamais sur une base en service)." }
if (-not $DossierMigration) { $DossierMigration = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) "database\migration" }
if (-not $PgBin) {
    $PgBin = Get-ChildItem "C:\Program Files\PostgreSQL" -Directory | Sort-Object { [int]($_.Name -replace '\D', '') } -Descending |
        ForEach-Object { Join-Path $_.FullName "bin" } | Where-Object { Test-Path (Join-Path $_ "psql.exe") } | Select-Object -First 1
}
if (-not $PgBin) { throw "psql.exe introuvable : renseigner -PgBin." }
$psql = Join-Path $PgBin "psql.exe"
$pgDump = Join-Path $PgBin "pg_dump.exe"
$env:PGCLIENTENCODING = "UTF8"
$env:PGTZ = "UTC"

function Invoke-Sql {
    param([string[]]$Arguments)
    $sortie = & $psql "--no-password" "--host=$Hote" "--port=$Port" "--username=$Compte" "--dbname=$Base" "-X" "-v" "ON_ERROR_STOP=1" @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw ("psql a échoué : " + (($sortie | Out-String).Trim())) }
    return $sortie
}

# Instantané : structure, lignes de chaque table (triées, hors valeurs de séquences), nombre de lignes
function Get-Etat {
    param([string]$Nom)
    $ErrorActionPreference = "Continue"   # pg_dump écrit des avertissements sur la sortie d'erreur (clés étrangères circulaires)
    $structure = & $pgDump "--no-password" "--host=$Hote" "--port=$Port" "--username=$Compte" "--schema-only" "--no-owner" "--no-privileges" $Base 2>$null |
        Where-Object { $_ -notmatch '^\\(un)?restrict ' }
    $donnees = & $pgDump "--no-password" "--host=$Hote" "--port=$Port" "--username=$Compte" "--data-only" "--no-owner" "--no-privileges" $Base 2>$null |
        Where-Object { $_ -notmatch '^\\(un)?restrict ' -and $_ -notmatch 'pg_catalog\.setval' } | Sort-Object
    $requete = "SELECT string_agg('SELECT ' || quote_literal(relname) || ' AS t, count(*) AS n FROM ' || quote_ident(relname), ' UNION ALL ' ORDER BY relname) FROM pg_class WHERE relkind = 'r' AND relnamespace = 'public'::regnamespace"
    $sql = (Invoke-Sql @("-At", "-c", $requete) | Out-String).Trim()
    $comptages = Invoke-Sql @("-At", "-c", "$sql ORDER BY 1") | Out-String
    return [pscustomobject]@{ Nom = $Nom; Structure = ($structure -join "`n"); Donnees = ($donnees -join "`n"); Comptages = $comptages }
}

$scripts = Get-ChildItem $DossierMigration -Filter "*.sql" | Where-Object { $_.Name -match '^\d{3}_' } | Sort-Object Name
if ($scripts.Count -eq 0) { throw "Aucun script dans $DossierMigration." }
$lots = @(
    @{ Scripts = @($scripts | Where-Object { [int]$_.Name.Substring(0, 3) -le 32 }); Transaction = $true },
    @{ Scripts = @($scripts | Where-Object { [int]$_.Name.Substring(0, 3) -eq 33 }); Transaction = $false },
    @{ Scripts = @($scripts | Where-Object { [int]$_.Name.Substring(0, 3) -ge 34 }); Transaction = $true }
) | Where-Object { $_.Scripts.Count -gt 0 }

Write-Host "Base : $Base ($Hote`:$Port), $($scripts.Count) scripts, $Passages passage(s)." -ForegroundColor Cyan
$avant = Get-Etat "avant"
# Marqueur de la mutualisation des réglages (script 141) : une table imposée par le serveur central refuse sinon les scripts rejoués
$env:PGOPTIONS = if ($SansMarqueurReglages) { "-c client_min_messages=warning" } else { "-c client_min_messages=warning -c pharma.reglage_central=on" }
$debut = Get-Date
for ($i = 1; $i -le $Passages; $i++) {
    foreach ($lot in $lots) {
        $lignes = @($lot.Scripts | ForEach-Object { "\i '" + ($_.FullName -replace '\\', '/' -replace "'", "''") + "'" })
        if ($lot.Transaction) { $lignes = @("BEGIN;") + $lignes + @("COMMIT;") }
        $temporaire = Join-Path ([IO.Path]::GetTempPath()) ("pharma_rejeu_" + [guid]::NewGuid().ToString("N") + ".sql")
        try {
            [IO.File]::WriteAllLines($temporaire, $lignes, (New-Object Text.UTF8Encoding($false)))
            [void](Invoke-Sql @("-q", "-f", $temporaire))
        }
        catch { Write-Host "ÉCHEC au passage $i (lot $($lot.Scripts[0].Name) .. $($lot.Scripts[-1].Name)) : $($_.Exception.Message)" -ForegroundColor Red; exit 1 }
        finally { Remove-Item $temporaire -ErrorAction SilentlyContinue }
    }
    Write-Host "Passage $i : tous les scripts rejoués sans erreur." -ForegroundColor Green
}
Remove-Item Env:\PGOPTIONS -ErrorAction SilentlyContinue
$apres = Get-Etat "apres"

$ecarts = @()
if ($avant.Structure -cne $apres.Structure) { $ecarts += "structure différente (pg_dump --schema-only)" }
if ($avant.Donnees -cne $apres.Donnees) { $ecarts += "lignes de tables différentes (hors séquences)" }
if ($avant.Comptages -cne $apres.Comptages) { $ecarts += "nombre de lignes par table différent" }
if ($ecarts.Count -gt 0) {
    Write-Host "ÉCART après rejeu : $($ecarts -join " ; ")." -ForegroundColor Red
    exit 1
}
Write-Host ("OK : structure, données et comptages identiques avant/après ({0:N0} s)." -f ((Get-Date) - $debut).TotalSeconds) -ForegroundColor Green
exit 0
