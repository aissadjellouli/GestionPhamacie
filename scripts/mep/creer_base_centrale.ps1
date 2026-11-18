<#
.SYNOPSIS
    Crée la base d'un serveur central de synchronisation NEUF : scripts database\cloud 001 à 019 dans l'ordre, contrôles, tests facultatifs.

.DESCRIPTION
    À utiliser pour le parcours E, étape 2 (serveur central). Ne sert jamais sur la base d'une officine.
    Il n'existe pas de schema.sql central : les scripts database\cloud\NNN_*.sql sont la seule source (un seul endroit à tenir à jour).

    Contrôles avant de commencer :
      - psql trouvé ; scripts cloud trouvés (suite numérotée sans trou à partir de 001) ;
      - PostgreSQL joignable ; le compte propriétaire existe et n'est pas pharma_cloud_api ;
      - la base n'existe pas, ou existe VIDE de tables centrales (refus si elle contient déjà « officine » : jamais de suppression).

    Étapes :
      1. création de la base (propriétaire = -Proprietaire, UTF8) par -CompteAdmin si elle n'existe pas ;
      2. scripts 001 à 019 de database\cloud, un par un, avec le compte propriétaire. Le script 001 crée le rôle
         applicatif pharma_cloud_api SANS connexion (non propriétaire des tables : la sécurité au niveau des lignes s'applique) ;
      3. contrôles : tables centrales et miroir présentes, sécurité au niveau des lignes activée sur les tables de données,
         rôle pharma_cloud_api sans connexion et non propriétaire, partitions de lot_recu ;
      4. avec -AvecTests : tests de database\cloud\tests (transaction annulée, rien n'est conservé) ; chacun doit finir par « tous réussis ».
         À lancer sur une base neuve ou une copie, pas sur la production (docs\mise-en-production.md, parcours E, étape 2.2).

    Le mot de passe de pharma_cloud_api n'est PAS fixé par ce script : étape 2.4 du parcours E, par le superutilisateur
    (ALTER ROLE pharma_cloud_api LOGIN ; \password pharma_cloud_api), mot de passe tiré au sort et noté au coffre.

    Mots de passe : jamais en paramètre. Saisie par Read-Host -AsSecureString, transmis par PGPASSWORD au processus enfant
    seulement ; ou pgpass.conf existant ; ou PGPASSWORD déjà défini. Rien n'est écrit sur disque.

    Code de sortie : 0 si tout est bon (ou en simulation), 1 en cas d'échec.

.PARAMETER Base
    Nom de la base centrale (minuscules, chiffres, _).

.PARAMETER Proprietaire
    Compte propriétaire de la base, distinct du rôle applicatif (À CONFIRMER : nom du compte, manuel parcours E étape 1.3).
    Il doit exister. C'est lui qui passe les scripts.

.PARAMETER CompteAdmin
    Compte qui crée la base si elle n'existe pas (défaut : postgres). Inutile si la base existe déjà (vide).
    Peut être le propriétaire lui-même s'il a le droit CREATEDB.

.PARAMETER Hote
    Serveur PostgreSQL (défaut : localhost).

.PARAMETER Port
    Port PostgreSQL (défaut : 5432).

.PARAMETER AvecTests
    Lance aussi les tests de database\cloud\tests (transaction annulée).

.PARAMETER PgBin
    Dossier bin de PostgreSQL (défaut : version la plus récente de C:\Program Files\PostgreSQL).

.EXAMPLE
    .\scripts\mep\creer_base_centrale.ps1 -Base pharmacie_central -Proprietaire central_proprietaire
    Crée la base et passe 001 à 019.

.EXAMPLE
    .\scripts\mep\creer_base_centrale.ps1 -Base pharmacie_central -Proprietaire central_proprietaire -AvecTests -WhatIf
    Montre le plan (avec les tests) sans rien créer.

.NOTES
    Si PowerShell refuse le script : powershell -ExecutionPolicy Bypass -File .\scripts\mep\creer_base_centrale.ps1 ...
    Documentation : docs\scripts-base-de-donnees.md
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory = $true)][string]$Base,
    [Parameter(Mandatory = $true)][string]$Proprietaire,
    [string]$CompteAdmin = "postgres",
    [string]$Hote = "localhost",
    [int]$Port = 5432,
    [switch]$AvecTests,
    [string]$PgBin = "",
    [string]$DossierDepot = ""
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "outils_mep.ps1")
if (-not $DossierDepot) { $DossierDepot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
$RoleCloud = "pharma_cloud_api"

Invoke-Principal {
    Write-Etape "Contrôles préalables (sans connexion à la base)"
    Test-NomBase $Base
    Test-NomBase $Proprietaire
    if ($Proprietaire -eq $RoleCloud) { throw "Le propriétaire de la base ne peut pas être $RoleCloud (rôle applicatif, non propriétaire)." }
    Initialize-OutilsMep -PgBin $PgBin -Hote $Hote -Port $Port -Simulation $WhatIfPreference

    $dossierCloud = Join-Path $DossierDepot "database\cloud"
    $scripts = Get-ScriptsNumerotes $dossierCloud
    for ($i = 0; $i -lt $scripts.Count; $i++) {
        if ([int]$scripts[$i].Name.Substring(0, 3) -ne ($i + 1)) { throw "Suite des scripts cloud interrompue : « $($scripts[$i].Name) » trouvé à la place du numéro $('{0:000}' -f ($i + 1))." }
    }
    Write-Ok "$($scripts.Count) scripts cloud (001 à $($scripts[-1].Name.Substring(0,3))) dans $dossierCloud."
    $tests = @()
    if ($AvecTests) {
        $dossierTests = Join-Path $dossierCloud "tests"
        $tests = @(Get-ScriptsNumerotes $dossierTests)
        Write-Ok "$($tests.Count) fichiers de test dans $dossierTests."
    }

    $connexionAdmin = Request-MotDePasse -Compte $CompteAdmin -Invite "Mot de passe PostgreSQL du compte $CompteAdmin"
    $baseExiste = $false
    if ($connexionAdmin) {
        Write-Etape "Contrôles du serveur"
        $version = Get-Scalaire -Compte $CompteAdmin -Base "postgres" -Sql "SHOW server_version"
        Write-Ok "PostgreSQL joignable ($Hote`:$Port), version $version."
        if ((Get-Scalaire -Compte $CompteAdmin -Base "postgres" -Sql "SELECT count(*) FROM pg_roles WHERE rolname = '$Proprietaire'") -eq "0") {
            throw "Le compte propriétaire « $Proprietaire » n'existe pas. Le créer d'abord (parcours E, étape 1.3), il n'est pas créé par ce script."
        }
        Write-Ok "Compte propriétaire $Proprietaire présent."
        $baseExiste = (Get-Scalaire -Compte $CompteAdmin -Base "postgres" -Sql "SELECT count(*) FROM pg_database WHERE datname = '$Base'") -ne "0"
    }
    else { Write-Simul "Pas de mot de passe disponible : contrôles du serveur non exécutés." }

    if ($Proprietaire -ne $CompteAdmin) { [void](Request-MotDePasse -Compte $Proprietaire -Invite "Mot de passe PostgreSQL du compte propriétaire $Proprietaire") }

    if ($baseExiste) {
        $deja = Get-Scalaire -Compte $Proprietaire -Base $Base -Sql "SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND tablename IN ('officine', 'cloud_table_miroir', 'lot_recu')"
        if ($deja -ne "0") { throw "La base « $Base » contient déjà des tables centrales : ce script ne crée qu'un serveur central NEUF et ne supprime rien. Mise à jour du central : parcours E, étape 9." }
        Write-Ok "La base « $Base » existe, sans table centrale : elle sera utilisée."
    }

    Write-Etape "Création"
    if ($PSCmdlet.ShouldProcess("serveur ${Hote}:$Port", "créer la base centrale « $Base » (scripts cloud 001 à $($scripts[-1].Name.Substring(0,3))$(if ($AvecTests) { ', tests' }))")) {
        if (-not $baseExiste) {
            [void](Invoke-Psql -Compte $CompteAdmin -Base "postgres" -Arguments @("-c", "CREATE DATABASE `"$Base`" OWNER `"$Proprietaire`" ENCODING 'UTF8' TEMPLATE template0"))
            Write-Ok "Base « $Base » créée (propriétaire $Proprietaire, UTF8)."
        }
        foreach ($s in $scripts) {
            [void](Invoke-Psql -Compte $Proprietaire -Base $Base -Arguments @("-f", $s.FullName))
            Write-Info $s.Name
        }
        Write-Ok "$($scripts.Count) scripts cloud passés sans erreur."

        Write-Etape "Contrôles"
        $manque = Get-Scalaire -Compte $Proprietaire -Base $Base -Sql "SELECT count(*) FROM (VALUES ('officine'), ('certificat'), ('code_activation'), ('journal_securite'), ('officine_epoque'), ('lot_recu'), ('cloud_table_miroir')) v(t) WHERE to_regclass('public.' || v.t) IS NULL"
        if ($manque -ne "0") { throw "$manque table(s) centrale(s) manquante(s) après les scripts." }
        $miroirs = Get-Scalaire -Compte $Proprietaire -Base $Base -Sql "SELECT count(*) FROM cloud_table_miroir"
        if ([int]$miroirs -lt 1) { throw "Le catalogue des tables miroir est vide." }
        Write-Ok "Tables centrales présentes ; catalogue des tables miroir : $miroirs."
        $sansRls = Get-Scalaire -Compte $Proprietaire -Base $Base -Sql "SELECT count(*) FROM pg_class c WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r', 'p') AND NOT c.relispartition AND c.relname IN (SELECT nom_table FROM cloud_table_miroir) AND NOT (c.relrowsecurity AND c.relforcerowsecurity)"
        if ($sansRls -ne "0") { throw "$sansRls table(s) miroir sans sécurité au niveau des lignes forcée." }
        Write-Ok "Sécurité au niveau des lignes forcée sur toutes les tables miroir."
        $role = Get-Scalaire -Compte $Proprietaire -Base $Base -Sql "SELECT rolcanlogin::text FROM pg_roles WHERE rolname = '$RoleCloud'"
        if ($role -eq "") { throw "Le rôle $RoleCloud n'a pas été créé par le script 001." }
        $proprio = Get-Scalaire -Compte $Proprietaire -Base $Base -Sql "SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND tableowner = '$RoleCloud'"
        if ($proprio -ne "0") { throw "$RoleCloud est propriétaire de $proprio table(s) : la sécurité au niveau des lignes ne s'appliquerait pas." }
        Write-Ok "Rôle $RoleCloud présent (connexion possible : $role), propriétaire d'aucune table. Le mot de passe se règle à l'étape 2.4 du parcours E."
        $parts = Get-Scalaire -Compte $Proprietaire -Base $Base -Sql "SELECT count(*) FROM pg_inherits WHERE inhparent = 'public.lot_recu'::regclass"
        Write-Ok "Partitions de lot_recu : $parts."

        if ($AvecTests) {
            Write-Etape "Tests SQL (transaction annulée)"
            foreach ($t in $tests) {
                $r = Invoke-Psql -Compte $Proprietaire -Base $Base -Arguments @("-f", $t.FullName)
                if ($r.Texte -notmatch "tous r\u00e9ussis") { throw "Le test $($t.Name) ne s'est pas terminé par « tous réussis » :`n$($r.Texte)" }
                Write-Info "$($t.Name) : tous réussis"
            }
            Write-Ok "$($tests.Count) fichiers de test réussis."
        }

        Write-Etape "Résumé"
        Write-Info "Base centrale « $Base » créée et vérifiée (scripts 001 à $($scripts[-1].Name.Substring(0,3)))."
        Write-Info "Suite : parcours E, étape 2.4 (mot de passe de $RoleCloud par le superutilisateur), puis étape 3."
    }
    else {
        if (-not $baseExiste) { Write-Simul "CREATE DATABASE $Base OWNER $Proprietaire ENCODING 'UTF8' TEMPLATE template0 (compte $CompteAdmin)" }
        foreach ($s in $scripts) { Write-Simul "$($s.Name) (compte $Proprietaire)" }
        Write-Simul "Contrôles : tables centrales, sécurité au niveau des lignes, rôle $RoleCloud"
        foreach ($t in $tests) { Write-Simul "test $($t.Name) (compte $Proprietaire)" }
    }
}
