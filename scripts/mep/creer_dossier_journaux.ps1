<#
.SYNOPSIS
    Prépare le dossier commun des journaux du logiciel (C:\Logs\PharmaOfficine) et règle ses droits.

.DESCRIPTION
    Décision du titulaire du 2026-11-28 : tous les journaux en fichiers du logiciel vont sous <lecteur du système>\Logs\PharmaOfficine :
      - Api\            journal du serveur de l'officine (pharma-api-AAAAMMJJ.log) ;
      - Poste\<nom PC>\ journaux du poste (erreurs.log, liaison.log) ;
      - Central\        journaux du serveur central (central-api-…, central-ecrans-…), sur le serveur central seulement.
    Le logiciel crée lui-même ces dossiers s'il le peut, et se replie sur l'ancien emplacement sinon (C:\ProgramData\PharmaOfficine\Journaux
    pour l'API, %LOCALAPPDATA%\PharmaOfficine pour le poste). Ce script les crée à l'avance et règle les droits, car un dossier créé à la
    racine du disque est lisible par tous les comptes du PC :
      - Api et Central : administrateurs et compte système seulement (plus le compte de -CompteService s'il est donné) ;
      - Poste : en plus, les utilisateurs du PC peuvent y écrire (chaque poste écrit son journal sous son compte Windows).
    Les fichiers déjà écrits ailleurs ne sont ni déplacés ni supprimés. À lancer en administrateur ; -WhatIf montre sans rien faire.
    Voir docs\manuel-mep.md (« Journaux »).

.PARAMETER Roles
    Dossiers à préparer sur ce PC : Api, Poste, Central (défaut : Api et Poste ; un PC de caisse seul : Poste).

.PARAMETER Racine
    Dossier commun (défaut : <lecteur du système>\Logs\PharmaOfficine, celui que le logiciel utilise sans réglage).

.PARAMETER CompteService
    Compte Windows du service de l'API ou du central s'il n'est pas le compte système (ex. « NT SERVICE\PharmaOfficine API »).

.EXAMPLE
    .\scripts\mep\creer_dossier_journaux.ps1 -WhatIf
    .\scripts\mep\creer_dossier_journaux.ps1
    .\scripts\mep\creer_dossier_journaux.ps1 -Roles Poste
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [ValidateSet("Api", "Poste", "Central")][string[]]$Roles = @("Api", "Poste"),
    [string]$Racine = "",
    [string]$CompteService = ""
)

$ErrorActionPreference = "Stop"
if (-not $Racine) { $Racine = Join-Path ([System.IO.Path]::GetPathRoot([Environment]::SystemDirectory)) "Logs\PharmaOfficine" }

$identite = [Security.Principal.WindowsIdentity]::GetCurrent()
$administrateur = (New-Object Security.Principal.WindowsPrincipal $identite).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $administrateur -and -not $WhatIfPreference) { throw "À lancer dans une fenêtre PowerShell ouverte en administrateur (droits du dossier $Racine)." }

# Comptes désignés par leur identifiant, indépendant de la langue de Windows
$administrateurs = "*S-1-5-32-544"
$systeme = "*S-1-5-18"
$utilisateurs = "*S-1-5-11"

function Set-Droits([string]$Dossier, [string[]]$Droits) {
    if (-not $PSCmdlet.ShouldProcess($Dossier, "créer le dossier et régler ses droits ($($Droits -join ' ; '))")) { return }
    New-Item -ItemType Directory -Force -Path $Dossier | Out-Null
    $arguments = @($Dossier, "/inheritance:r") + ($Droits | ForEach-Object { "/grant:r"; $_ })
    $sortie = & icacls.exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "Droits non réglés sur $Dossier (icacls, code $LASTEXITCODE) : $($sortie -join ' ')" }
    Write-Host "   [OK] $Dossier"
}

Write-Host ""
Write-Host "== Dossier commun des journaux : $Racine" -ForegroundColor Cyan
$reserve = @("${administrateurs}:(OI)(CI)F", "${systeme}:(OI)(CI)F")
if ($CompteService) { $reserve += "${CompteService}:(OI)(CI)M" }

# La racine se traverse par tous (pour atteindre Poste\) sans que son contenu soit listé ni lu
Set-Droits $Racine ($reserve + "${utilisateurs}:(X)")
foreach ($role in $Roles) {
    $dossier = Join-Path $Racine $role
    if ($role -eq "Poste") { Set-Droits $dossier ($reserve + "${utilisateurs}:(OI)(CI)M") }
    else { Set-Droits $dossier $reserve }
}

Write-Host ""
Write-Host "Terminé. Les anciens journaux restent à leur place ; le logiciel écrit dans $Racine à son prochain démarrage." -ForegroundColor Green
