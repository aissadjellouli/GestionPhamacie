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

# Requête psql (résultat brut, une ligne par enregistrement, colonnes séparées par « | »). Variables jamais concaténées dans le SQL :
# dans le SQL, :'nom' pour un texte. Elles sont posées par « \set » sur l'entrée standard, avant le SQL (correction du 2026-11-28) :
# passées par « -v » sur la ligne de commande, un guillemet double dans une valeur la modifiait sous Windows PowerShell 5.1 (mot de
# passe de réplication faux), et la valeur était visible dans la liste des processus.
function Invoke-Psql {
    param([string]$PgBin, [string]$HostPg, [int]$Port, [string]$User, [string]$Db, [string]$Sql, [string]$MotDePasse,
          [hashtable]$Variables = @{})
    $ancienMdp = $env:PGPASSWORD
    $ancienEncodage = $env:PGCLIENTENCODING
    $env:PGPASSWORD = $MotDePasse
    $env:PGCLIENTENCODING = "UTF8"
    $arguments = @("-h", $HostPg, "-p", $Port, "-U", $User, "-d", $Db, "-v", "ON_ERROR_STOP=1", "-qtA", "-w")
    # « \set nom 'valeur' » : apostrophe doublée, barre oblique inverse doublée (psql interprète les barres entre apostrophes)
    $poses = @(foreach ($k in $Variables.Keys) { "\set $k '" + ([string]$Variables[$k]).Replace('\', '\\').Replace("'", "''") + "'" })
    $OutputEncoding = New-Object System.Text.UTF8Encoding $false   # entrée standard de psql en UTF-8 (accents des valeurs et du SQL)
    try {
        # -c n'interprète pas les variables psql : le SQL passe par l'entrée standard
        $r = ((@($poses) + $Sql) -join "`n") | & (Join-Path $PgBin "psql.exe") @arguments -f -
        if ($LASTEXITCODE -ne 0) { throw "Échec de psql sur $HostPg ($Db)." }
        return $r
    } finally {
        if ($null -eq $ancienMdp) { Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue } else { $env:PGPASSWORD = $ancienMdp }
        if ($null -eq $ancienEncodage) { Remove-Item Env:\PGCLIENTENCODING -ErrorAction SilentlyContinue } else { $env:PGCLIENTENCODING = $ancienEncodage }
    }
}

function Test-Port([string]$Hote, [int]$Port, [int]$DelaiMs = 3000) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $tache = $client.ConnectAsync($Hote, $Port)
        return ($tache.Wait($DelaiMs) -and $client.Connected)
    } catch { return $false } finally { $client.Dispose() }
}

function Write-Etape([string]$Mode, [string]$Texte) { Write-Host "$Mode$Texte" }

# Service de l'API d'un serveur de l'officine (vague 2, lot V2-1) : démarrage automatique et relance en cas d'arrêt avec erreur.
# L'API en veille s'arrête avec le code 10 une fois la base promue : « failureflag 1 » fait compter ce code comme un échec,
# donc le service est relancé (après 5 s) et démarre en principal. Sans effet destructeur : réglages du service seulement.
function Set-ServiceApiRelance([string]$Nom) {
    & sc.exe config $Nom start= auto | Out-Null
    & sc.exe failure $Nom reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
    & sc.exe failureflag $Nom 1 | Out-Null
}

# Nom de la copie pour la réplication synchrone (2026-11-25, essai réel) : « pg_basebackup -R » n'écrit pas application_name dans
# primary_conninfo ; sans lui, le principal (synchronous_standby_names = 'second_serveur') ne voit jamais la copie comme synchrone
# (aucune attente, aucune garantie). Ajouté à la ligne primary_conninfo de postgresql.auto.conf du dossier de données de la copie,
# AVANT son démarrage, sans BOM ; déjà présent : rien n'est modifié. Rend « ajouté » ou « déjà présent ».
function Set-NomCopieSynchrone([string]$DossierDonnees, [string]$Nom = "second_serveur") {
    $fichier = Join-Path $DossierDonnees "postgresql.auto.conf"
    if (-not (Test-Path $fichier)) { throw "postgresql.auto.conf introuvable dans $DossierDonnees (copie créée avec pg_basebackup -R attendue)." }
    $lignes = [IO.File]::ReadAllLines($fichier)
    $trouve = $false
    for ($i = 0; $i -lt $lignes.Count; $i++) {
        if ($lignes[$i] -match "^\s*primary_conninfo\s*=\s*'(.*)'\s*$") {
            $trouve = $true
            if ($Matches[1] -match "(^|\s)application_name=") { return "déjà présent" }
            $lignes[$i] = "primary_conninfo = '$($Matches[1]) application_name=$Nom'"
        }
    }
    if (-not $trouve) { throw "primary_conninfo absent de $fichier : la copie n'a pas été créée avec pg_basebackup -R." }
    [IO.File]::WriteAllLines($fichier, $lignes, (New-Object System.Text.UTF8Encoding $false))
    return "ajouté"
}

# Rôle publié par /api/healthcheck (principal | veille | ecarte) ou $null si l'API ne répond pas (ancienne version : « inconnu »).
function Get-RoleApi([string]$Url, [int]$DelaiSecondes = 3) {
    try {
        $r = Invoke-RestMethod $Url -TimeoutSec $DelaiSecondes
        if ($r.role) { return [string]$r.role } else { return "inconnu" }
    } catch { return $null }
}

# Empreinte de la clé des jetons (Jwt__Cle) : 16 caractères hexadécimaux, identique à celle de l'API (Pharma.Api.exe --empreinte-jeton et
# écran « 2e serveur »). HKDF-SHA256 écrit à la main (Windows PowerShell 5.1 n'a pas la classe HKDF) : jamais un hachage brut de la clé.
# Un seul bloc d'expansion (32 octets). Ne jamais afficher ni journaliser la clé.
function Get-EmpreinteJeton([string]$Cle) {
    $hmac = { param([byte[]]$k, [byte[]]$m) $h = New-Object Security.Cryptography.HMACSHA256 -ArgumentList (,$k); try { ,$h.ComputeHash($m) } finally { $h.Dispose() } }
    $utf8 = [Text.Encoding]::UTF8
    $prk = & $hmac (New-Object byte[] 32) ($utf8.GetBytes($Cle))
    $okm = & $hmac $prk ([byte[]]($utf8.GetBytes("pharmaofficine/empreinte-jeton/v1") + [byte]1))
    $mac = & $hmac $okm ($utf8.GetBytes("empreinte-jeton"))
    return (($mac[0..7] | ForEach-Object { $_.ToString("x2") }) -join "")
}

# Contrôle d'installation du 2e serveur (docs/second-serveur-local.md) : la clé des jetons est saisie MASQUÉE (jamais affichée, jamais écrite
# dans un fichier ni dans un journal), son empreinte est comparée à celle du principal ; si elle diffère, l'installation s'arrête (throw).
# $EmpreintePrincipal : affichée sur le principal par « Pharma.Api.exe --empreinte-jeton ». Retourne la clé (en mémoire seulement).
function Read-CleJetonsControlee([string]$EmpreintePrincipal) {
    if ($EmpreintePrincipal -notmatch '^[0-9a-f]{16}$') { throw "Empreinte du principal invalide : 16 caractères hexadécimaux en minuscules (Pharma.Api.exe --empreinte-jeton sur le principal)." }
    $cle = Read-MotDePasse "Clé des jetons du principal (Jwt__Cle, 64 caractères au moins ; la saisie n'est pas affichée)"
    if ($cle.Length -lt 64) { throw "Clé des jetons trop courte (64 caractères au moins) : installation arrêtée." }
    $locale = Get-EmpreinteJeton $cle
    if ($locale -ne $EmpreintePrincipal) {
        throw "Clé des jetons DIFFÉRENTE de celle du principal (empreinte saisie $locale, principal $EmpreintePrincipal) : ressaisir la clé exacte du coffre. Installation arrêtée."
    }
    Write-Host "Clé des jetons identique à celle du principal (empreinte $locale)."
    return $cle
}
