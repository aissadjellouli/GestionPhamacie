<#
.SYNOPSIS
    Fonctions communes aux scripts du 2e serveur (chargées par « . $PSScriptRoot\commun.ps1 »). Ne fait rien seul.
#>

$ErrorActionPreference = "Stop"
# SQL envoyé à psql par l'entrée standard : en UTF-8 (Windows PowerShell 5.1 envoie de l'ASCII par défaut)
$OutputEncoding = New-Object System.Text.UTF8Encoding $false

function Get-PgBin([string]$PgBin) {
    if ($PgBin) { return $PgBin }
    $installation = Get-ChildItem "C:\Program Files\PostgreSQL" -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName "bin\psql.exe") } |
        Sort-Object { [int]($_.Name -replace '\D', '') } -Descending | Select-Object -First 1
    if (-not $installation) { throw "PostgreSQL introuvable dans C:\Program Files\PostgreSQL (préciser -PgBin)." }
    return (Join-Path $installation.FullName "bin")
}

function Read-MotDePasse([string]$invite) {
    $s = Read-Host $invite -AsSecureString
    return [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($s))
}

# Requête psql (résultat brut, une ligne par enregistrement, colonnes séparées par « | »). Variables passées par -v (jamais
# concaténées dans le SQL) : dans le SQL, :'nom' pour un texte.
function Invoke-Psql {
    param([string]$PgBin, [string]$HostPg, [int]$Port, [string]$User, [string]$Db, [string]$Sql, [string]$MotDePasse,
          [hashtable]$Variables = @{})
    $env:PGPASSWORD = $MotDePasse
    $env:PGCLIENTENCODING = "UTF8"
    $arguments = @("-h", $HostPg, "-p", $Port, "-U", $User, "-d", $Db, "-v", "ON_ERROR_STOP=1", "-qtA", "-w")
    foreach ($k in $Variables.Keys) { $arguments += @("-v", "$k=$($Variables[$k])") }
    try {
        # -c n'interprète pas les variables psql : le SQL passe par l'entrée standard
        $r = $Sql | & (Join-Path $PgBin "psql.exe") @arguments -f -
        if ($LASTEXITCODE -ne 0) { throw "Échec de psql sur $HostPg ($Db)." }
        return $r
    } finally { Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue }
}

function Test-Port([string]$Hote, [int]$Port, [int]$DelaiMs = 3000) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $tache = $client.ConnectAsync($Hote, $Port)
        return ($tache.Wait($DelaiMs) -and $client.Connected)
    } catch { return $false } finally { $client.Dispose() }
}

function Write-Etape([string]$Mode, [string]$Texte) { Write-Host "$Mode$Texte" }
