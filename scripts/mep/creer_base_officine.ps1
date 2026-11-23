<#
.SYNOPSIS
    Crée une base NEUVE d'officine : rôle pharma_app, base, structure, référentiel de départ, séparation des rôles.

.DESCRIPTION
    À utiliser pour une installation neuve (manuel de mise en production, parcours A, étapes 2.2 et 3).
    Ne sert JAMAIS à une base existante : si la base existe déjà, le script refuse et ne supprime rien.

    Contrôles avant de commencer (rien n'est créé si l'un échoue) :
      - psql, pg_dump, pg_restore trouvés ;
      - les fichiers database\init\schema.sql et seed_referentiel.sql citent « 001 à NNN », NNN étant le dernier
        script numéroté de database\migration (sinon l'init est en retard : refus) ;
      - PostgreSQL répond ; la base n'existe pas ; le compte d'administration peut créer la base (et, sans
        -SansSeparationRoles, est superutilisateur).

    Étapes :
      1. rôle pharma_app (créé avec LOGIN et CREATEDB s'il manque ; CREATEDB ajouté s'il manque) ;
      2. CREATE DATABASE <Base> OWNER pharma_app ENCODING UTF8 ;
      3. database\init\schema.sql (compte pharma_app) ;
      4. fuseau horaire de la base : database\migration\083_fuseau_horaire.sql (non repris dans schema.sql) ;
      5. database\init\seed_referentiel.sql (référentiel de départ, compte « titulaire ») ;
      6. séparation des rôles : database\migration\111b_roles_integrite.sql, compte superutilisateur
         (sauf -SansSeparationRoles : les journaux ne sont alors PAS protégés) ;
      7. vérifications finales : objets_encore_a_pharma_app = 0 (si séparé), comptages de référence
         (wilayas, rôles, utilisateurs, ventes = 0), lecture et écriture par pharma_app, droits sur les journaux.

    Le script 101b (suppression de num_facture_chifa) n'est JAMAIS lancé par ce script.

    Mots de passe : jamais en paramètre. Ils sont saisis (Read-Host -AsSecureString), gardés en mémoire du
    processus et transmis à PostgreSQL par la variable PGPASSWORD du processus enfant. Si PGPASSWORD est déjà défini
    ou si un fichier pgpass.conf existe, rien n'est demandé. Si pharma_app n'existe pas, le script demande le
    mot de passe à lui donner (12 caractères au moins, propre à cette pharmacie, à noter au coffre).

    Code de sortie : 0 si tout est bon (ou en simulation), 1 en cas d'échec.

.PARAMETER Base
    Nom de la base à créer (minuscules, chiffres, _), par exemple « pharmacie ».

.PARAMETER Hote
    Serveur PostgreSQL (défaut : localhost).

.PARAMETER Port
    Port PostgreSQL (défaut : 5432).

.PARAMETER CompteAdmin
    Compte qui crée le rôle et la base (défaut : postgres). Doit être superutilisateur pour la séparation des rôles.
    Sur un poste de développement, pharma_app (droit CREATEDB) suffit avec -SansSeparationRoles.

.PARAMETER SansSeparationRoles
    Ne lance pas 111b. Avertissement : pharma_app reste propriétaire de la base et peut désactiver les déclencheurs
    qui protègent les journaux ; la vérification nocturne détecte la falsification mais ne l'empêche pas.

.PARAMETER PgBin
    Dossier bin de PostgreSQL (défaut : version la plus récente de C:\Program Files\PostgreSQL).

.PARAMETER DossierDepot
    Racine du dépôt ou de la livraison, là où se trouvent database\init et database\migration (défaut : le dépôt de ce script).

.EXAMPLE
    .\scripts\mep\creer_base_officine.ps1 -Base pharmacie
    Base neuve complète, séparation des rôles comprise (demande les mots de passe de postgres et de pharma_app).

.EXAMPLE
    .\scripts\mep\creer_base_officine.ps1 -Base pharmacie -WhatIf
    Montre ce qui serait fait, sans rien créer.

.EXAMPLE
    .\scripts\mep\creer_base_officine.ps1 -Base pharmacie_essai -CompteAdmin pharma_app -SansSeparationRoles
    Base d'essai sans superutilisateur.

.NOTES
    Si PowerShell refuse le script : powershell -ExecutionPolicy Bypass -File .\scripts\mep\creer_base_officine.ps1 -Base <base>
    Documentation : docs\scripts-base-de-donnees.md
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory = $true)][string]$Base,
    [string]$Hote = "localhost",
    [int]$Port = 5432,
    [string]$CompteAdmin = "postgres",
    [switch]$SansSeparationRoles,
    [string]$PgBin = "",
    [string]$DossierDepot = ""
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "outils_mep.ps1")
if (-not $DossierDepot) { $DossierDepot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
$RoleApp = "pharma_app"

Invoke-Principal {
    Write-Etape "Contrôles préalables (sans connexion à la base)"
    Test-NomBase $Base
    Initialize-OutilsMep -PgBin $PgBin -Hote $Hote -Port $Port -Simulation $WhatIfPreference

    $dossierInit = Join-Path $DossierDepot "database\init"
    $dossierMigration = Join-Path $DossierDepot "database\migration"
    $schema = Join-Path $dossierInit "schema.sql"
    $seed = Join-Path $dossierInit "seed_referentiel.sql"
    $scriptFuseau = Get-ScriptsNumerotes $dossierMigration | Where-Object { $_.Name -like "083_*" } | Select-Object -First 1
    $script111b = Join-Path $dossierMigration "111b_roles_integrite.sql"
    if (-not $scriptFuseau) { throw "Script 083 (fuseau horaire) introuvable dans $dossierMigration" }
    if (-not $SansSeparationRoles -and -not (Test-Path -LiteralPath $script111b)) { throw "Script introuvable : $script111b" }
    $scriptsOfficine = Get-ScriptsNumerotes $dossierMigration
    Test-SuiteSansTrou $scriptsOfficine
    $dernier = ($scriptsOfficine[-1].Name).Substring(0, 3)
    Test-EnteteInit -Fichier $schema -Dernier $dernier
    Test-EnteteInit -Fichier $seed -Dernier $dernier
    Write-Ok "L'init (schema.sql, seed_referentiel.sql) couvre les scripts 001 à $dernier : il est à jour."
    if ($SansSeparationRoles) {
        Write-Avert "-SansSeparationRoles : 111b ne sera PAS lancé. Les journaux ne sont pas protégés : $RoleApp reste propriétaire et peut désactiver les déclencheurs d'immuabilité. À faire avant la vraie mise en service (docs\scripts-base-de-donnees.md, « 111b »)."
    }

    # Mots de passe (en simulation : aucune saisie, les contrôles qui ont besoin du serveur sont ignorés)
    $connexion = Request-MotDePasse -Compte $CompteAdmin -Invite "Mot de passe PostgreSQL du compte $CompteAdmin"
    $mdpNouveauRole = $null
    $roleExiste = $false
    $baseExiste = $false

    if ($connexion) {
        Write-Etape "Contrôles du serveur"
        $version = Get-Scalaire -Compte $CompteAdmin -Base "postgres" -Sql "SHOW server_version"
        Write-Ok "PostgreSQL joignable ($Hote`:$Port), version $version, compte $CompteAdmin."
        $baseExiste = (Get-Scalaire -Compte $CompteAdmin -Base "postgres" -Sql "SELECT count(*) FROM pg_database WHERE datname = '$Base'") -ne "0"
        if ($baseExiste) { throw "La base « $Base » existe déjà. Ce script ne crée que des bases NEUVES et ne supprime jamais rien. Pour une base en service : mettre_a_jour_base_officine.ps1." }
        Write-Ok "La base « $Base » n'existe pas."
        $admin = (Get-Scalaire -Compte $CompteAdmin -Base "postgres" -Sql "SELECT rolsuper::text || '|' || rolcreatedb::text || '|' || rolcreaterole::text FROM pg_roles WHERE rolname = current_user").Split("|")
        if (-not $SansSeparationRoles -and $admin[0] -ne "true") { throw "Le compte $CompteAdmin n'est pas superutilisateur : requis pour la séparation des rôles (111b). Utiliser postgres, ou -SansSeparationRoles pour un essai." }
        if ($admin[0] -ne "true" -and $admin[1] -ne "true") { throw "Le compte $CompteAdmin n'a pas le droit de créer une base (CREATEDB)." }
        $roleExiste = (Get-Scalaire -Compte $CompteAdmin -Base "postgres" -Sql "SELECT count(*) FROM pg_roles WHERE rolname = '$RoleApp'") -ne "0"
        if ($roleExiste) { Write-Ok "Le rôle $RoleApp existe." }
        else {
            if ($admin[0] -ne "true" -and $admin[2] -ne "true") { throw "Le rôle $RoleApp n'existe pas et le compte $CompteAdmin ne peut pas le créer." }
            Write-Info "Le rôle $RoleApp n'existe pas : il sera créé (LOGIN CREATEDB)."
        }
    }
    else { Write-Simul "Pas de mot de passe disponible : contrôles du serveur (connexion, base absente, rôle) non exécutés." }

    # Mot de passe de pharma_app : à définir (rôle neuf) ou à fournir (init lancé avec ce compte)
    if ($connexion -and -not $roleExiste) {
        if ($WhatIfPreference) { Write-Simul "Le mot de passe du nouveau rôle $RoleApp serait demandé." }
        else {
            $s1 = Read-Host "Mot de passe à donner au nouveau compte $RoleApp (12 caractères au moins, propre à cette pharmacie)" -AsSecureString
            $s2 = Read-Host "Retapez ce mot de passe" -AsSecureString
            $mdpNouveauRole = ConvertFrom-SecureStringEnClair $s1
            if ($mdpNouveauRole -cne (ConvertFrom-SecureStringEnClair $s2)) { throw "Les deux saisies sont différentes : rien n'a été fait." }
            if ($mdpNouveauRole.Length -lt 12) { throw "Mot de passe trop court (12 caractères au moins) : rien n'a été fait." }
            Set-MotDePasseMemoire $RoleApp $mdpNouveauRole
        }
    }
    elseif ($CompteAdmin -eq $RoleApp) { if ($script:MotsDePasse.ContainsKey($CompteAdmin)) { Set-MotDePasseMemoire $RoleApp $script:MotsDePasse[$CompteAdmin] } }
    else { [void](Request-MotDePasse -Compte $RoleApp -Invite "Mot de passe PostgreSQL du compte $RoleApp") }

    Write-Etape "Création"
    $libelleAction = "créer la base « $Base » : rôle $RoleApp, structure, référentiel" + $(if ($SansSeparationRoles) { "" } else { ", séparation des rôles" })
    if ($PSCmdlet.ShouldProcess("serveur ${Hote}:$Port", $libelleAction)) {
        # 1. rôle
        if (-not $roleExiste) {
            $echappe = $mdpNouveauRole.Replace("'", "''")
            [void](Invoke-Psql -Compte $CompteAdmin -Base "postgres" -Entree "CREATE ROLE $RoleApp LOGIN CREATEDB PASSWORD '$echappe';")
            Write-Ok "Rôle $RoleApp créé (LOGIN, CREATEDB)."
        }
        elseif ((Get-Scalaire -Compte $CompteAdmin -Base "postgres" -Sql "SELECT rolcreatedb::text FROM pg_roles WHERE rolname = '$RoleApp'") -ne "true") {
            [void](Invoke-Psql -Compte $CompteAdmin -Base "postgres" -Arguments @("-c", "ALTER ROLE $RoleApp CREATEDB"))
            Write-Ok "Droit CREATEDB ajouté à $RoleApp (restauration depuis l'application)."
        }
        else { Write-Ok "Rôle $RoleApp déjà conforme (CREATEDB)." }

        # 2. base
        [void](Invoke-Psql -Compte $CompteAdmin -Base "postgres" -Arguments @("-c", "CREATE DATABASE `"$Base`" OWNER $RoleApp ENCODING 'UTF8' TEMPLATE template0"))
        Write-Ok "Base « $Base » créée (propriétaire $RoleApp, UTF8)."

        # 3 à 5. structure, fuseau, référentiel (compte pharma_app)
        [void](Invoke-Psql -Compte $RoleApp -Base $Base -Arguments @("-f", $schema))
        $tables = Get-Scalaire -Compte $RoleApp -Base $Base -Sql "SELECT count(*) FROM pg_tables WHERE schemaname = 'public'"
        if ([int]$tables -lt 10) { throw "schema.sql n'a pas créé la structure ($tables tables seulement)." }
        Write-Ok "Structure créée par schema.sql ($tables tables)."

        [void](Invoke-Psql -Compte $RoleApp -Base $Base -Arguments @("-f", $scriptFuseau.FullName))
        Write-Ok "Fuseau horaire : $($scriptFuseau.Name)."

        $r = Invoke-Psql -Compte $RoleApp -Base $Base -Arguments @("-f", $seed)
        $r.Lignes | Where-Object { $_ } | ForEach-Object { Write-Info $_ }
        Write-Ok "Référentiel chargé (seed_referentiel.sql)."

        # 6. séparation des rôles
        if (-not $SansSeparationRoles) {
            $r = Invoke-Psql -Compte $CompteAdmin -Base $Base -Arguments @("-f", $script111b)
            $r.Lignes | Where-Object { $_ } | ForEach-Object { Write-Info $_ }
            Write-Ok "Séparation des rôles faite (111b)."
        }

        # 7. vérifications finales
        Write-Etape "Vérifications finales"
        $tz = Get-Scalaire -Compte $RoleApp -Base $Base -Sql "SHOW timezone"
        if ($tz -ne "Africa/Algiers") { throw "Fuseau de la base : « $tz » au lieu de Africa/Algiers." }
        Write-Ok "Fuseau horaire de la base : $tz."

        $comptes = @{}
        foreach ($t in "wilaya", "role", "utilisateur", "vente") {
            $comptes[$t] = [int](Get-Scalaire -Compte $RoleApp -Base $Base -Sql "SELECT count(*) FROM $t")
        }
        if ($comptes["wilaya"] -lt 58) { throw "Référentiel incomplet : $($comptes['wilaya']) wilaya(s), 58 au moins attendues." }
        if ($comptes["role"] -lt 1 -or $comptes["utilisateur"] -lt 1) { throw "Référentiel incomplet : role=$($comptes['role']), utilisateur=$($comptes['utilisateur'])." }
        if ($comptes["vente"] -ne 0) { throw "La base n'est pas vierge : $($comptes['vente']) vente(s)." }
        Write-Ok "Comptages : wilaya=$($comptes['wilaya']), role=$($comptes['role']), utilisateur=$($comptes['utilisateur']), vente=0."

        # lecture et écriture par pharma_app (écriture annulée)
        $ecr = Get-Scalaire -Compte $RoleApp -Base $Base -Sql "BEGIN; WITH u AS (UPDATE utilisateur SET identifiant = identifiant WHERE id = (SELECT min(id) FROM utilisateur) RETURNING 1) SELECT count(*) FROM u; ROLLBACK;"
        if ($ecr -ne "1") { throw "$RoleApp ne peut pas écrire dans la base (résultat « $ecr »)." }
        Write-Ok "$RoleApp lit et écrit (écriture d'essai annulée)."

        $proprietaireBase = Get-Scalaire -Compte $RoleApp -Base $Base -Sql "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = current_database()"
        if (-not $SansSeparationRoles) {
            $restants = Get-Scalaire -Compte $RoleApp -Base $Base -Sql "SELECT count(*) FROM pg_class c JOIN pg_roles r ON r.oid = c.relowner WHERE r.rolname = '$RoleApp' AND c.relnamespace = 'public'::regnamespace"
            if ($restants -ne "0") { throw "objets_encore_a_pharma_app = $restants (0 attendu) : séparation des rôles incomplète." }
            if ($proprietaireBase -ne "pharma_proprietaire") { throw "Propriétaire de la base : $proprietaireBase (pharma_proprietaire attendu)." }
            $peutModifier = Get-Scalaire -Compte $RoleApp -Base $Base -Sql "SELECT has_table_privilege('$RoleApp', 'journal_audit', 'UPDATE')::text"
            if ($peutModifier -ne "false") { throw "$RoleApp peut encore modifier journal_audit : séparation des rôles inefficace." }
            Write-Ok "objets_encore_a_pharma_app = 0 ; propriétaire = pharma_proprietaire ; $RoleApp ne peut plus modifier les journaux."
        }
        else { Write-Avert "Rôles NON séparés (propriétaire de la base : $proprietaireBase). Journaux non protégés." }

        Write-Etape "Résumé"
        Write-Info "Base « $Base » créée et vérifiée. Compte de départ : titulaire (mot de passe provisoire indiqué par le référentiel, à changer à la première connexion)."
        Write-Info "Séparation des rôles : $(if ($SansSeparationRoles) { 'NON FAITE' } else { 'faite' }). Script 101b : jamais lancé."
        Write-Info "Suite : parcours A, étape 4 (API). Les scripts suivants se lanceront avec mettre_a_jour_base_officine.ps1."
    }
    else {
        Write-Simul "Rôle $RoleApp : $(if ($roleExiste) { 'déjà présent' } else { 'à créer (LOGIN CREATEDB)' })"
        Write-Simul "CREATE DATABASE $Base OWNER $RoleApp ENCODING 'UTF8' TEMPLATE template0 (compte $CompteAdmin)"
        Write-Simul "$schema (compte $RoleApp)"
        Write-Simul "$($scriptFuseau.Name) (compte $RoleApp)"
        Write-Simul "$seed (compte $RoleApp)"
        if (-not $SansSeparationRoles) { Write-Simul "111b_roles_integrite.sql (compte $CompteAdmin, superutilisateur)" }
        Write-Simul "Vérifications finales : fuseau, comptages, lecture et écriture, objets_encore_a_pharma_app = 0"
    }
}
