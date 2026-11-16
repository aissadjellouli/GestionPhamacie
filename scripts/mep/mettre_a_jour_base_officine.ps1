<#
.SYNOPSIS
    Met à jour la base d'une officine EN SERVICE : sauvegarde obligatoire, puis tous les scripts numérotés rejoués dans l'ordre.

.DESCRIPTION
    À utiliser pour une mise à jour (manuel de mise en production, parcours B, étapes 3 à 10). Ne lance jamais
    schema.sql ni clean.sql. Les scripts de database\migration sont idempotents : ils sont rejoués dans l'ordre
    des numéros, sans table de suivi des migrations, TOUJOURS depuis le script 001 (rejouables sur une base à jour : voir
    docs\scripts-base-de-donnees.md ; -ScriptDepart reste réservé à la reprise après incident). Rejouer un ancien script seul ramènerait une ancienne version d'une
    fonction : ne jamais lancer un script isolé à la main, toujours toute la suite.

    Étapes :
      1. contrôles sans connexion : outils, scripts trouvés, service de l'API arrêté ;
      2. détection de l'état des rôles (111b fait ou non : rôle pharma_proprietaire, propriétaire de la base et des tables) ;
      3. sauvegarde pg_dump -Fc dans -DossierSauvegarde, vérifiée par pg_restore --list (obligatoire : sans elle, rien n'est modifié) ;
      4. avant le script 101 : contrôle en lecture seule des n° de facture Chifa (3 requêtes du manuel). S'il y a des lignes,
         ARRÊT avec la liste : voir le titulaire. -ContinuerMalgreConflitsChifa pour reprendre après sa décision ;
      5. rejeu des scripts NNN_*.sql (001 à 999) dans l'ordre, depuis le script 001 (sauf -ScriptDepart, reprise après
         incident), un par un, chacun hors transaction
         (le script 033 contient CREATE INDEX CONCURRENTLY, qui est interdit dans une transaction) ;
      6. avec -SeparerRoles : le script 111b est lancé juste après le script 111 (donc avant le premier script suivant) s'il
         n'est pas déjà fait ;
      7. résumé : scripts joués, durée, état des rôles.

    Comptes utilisés :
      - rôles non séparés : pharma_app (propriétaire des objets) ;
      - rôles séparés (après 111b) : le superutilisateur avec SET ROLE pharma_proprietaire, dans la même session psql.
        [À VÉRIFIER] La méthode SET ROLE (psql -c "SET ROLE pharma_proprietaire" -f script) n'a pas encore été validée par
        un essai sur une copie avec le compte postgres. À essayer sur une copie avant la production.
      - 111b : toujours le superutilisateur.

    Le script 101b (suppression de num_facture_chifa, irréversible) n'est JAMAIS lancé par ce script : décision du titulaire.

    Mots de passe : jamais en paramètre. Saisie par Read-Host -AsSecureString, gardés en mémoire, transmis par PGPASSWORD au
    processus enfant seulement ; ou fichier pgpass.conf existant ; ou PGPASSWORD déjà défini. Rien n'est écrit sur disque.

    Code de sortie : 0 si tout est bon (ou en simulation), 1 en cas d'échec. Après un échec, corriger la cause et relancer
    (scripts idempotents) ; en dernier recours, restaurer la sauvegarde (docs\scripts-base-de-donnees.md).

.PARAMETER Base
    Nom de la base de l'officine.

.PARAMETER DossierSauvegarde
    Dossier où écrire la sauvegarde (créé s'il manque), de préférence sur un disque externe. Fichier :
    <base>_avant_maj_<date>_<heure>.dump (jamais écrasé).

.PARAMETER Hote
    Serveur PostgreSQL (défaut : localhost).

.PARAMETER Port
    Port PostgreSQL (défaut : 5432).

.PARAMETER CompteSuperUtilisateur
    Superutilisateur PostgreSQL (défaut : postgres), demandé seulement si les rôles sont (ou vont être) séparés.

.PARAMETER NomService
    Service Windows de l'API à contrôler (défaut : « PharmaOfficine API »).

.PARAMETER SansControleService
    Ne contrôle pas le service. Réservé aux essais sur une COPIE de base, jamais en production.

.PARAMETER SeparerRoles
    Si la séparation des rôles (111b) n'est pas faite, la fait juste après le script 111.

.PARAMETER ContinuerMalgreConflitsChifa
    Reprend après un arrêt sur conflit de n° de facture Chifa, une fois la décision du titulaire prise.

.PARAMETER ScriptDepart
    Numéro du premier script à rejouer (défaut 0 = depuis le script 001, toujours). À n'utiliser qu'en cas d'incident : reprise après un
    échec, en donnant le numéro du script en échec (les scripts sont rejouables, mais le reste de la suite doit ensuite être rejoué :
    relancer sans ce paramètre une fois la cause corrigée). Aucun départ automatique : depuis la correction des scripts 007, 018, 024, 097,
    098, 126 et 129, toute la suite se rejoue sur une base à jour.

.PARAMETER DossierMigration
    Dossier des scripts (défaut : database\migration du dépôt). Pour une livraison : <livraison>\Api\database\migration.

.PARAMETER Script111b
    Chemin du script 111b (défaut : 111b_roles_integrite.sql du dossier des scripts, sinon celui du dépôt).

.PARAMETER PgBin
    Dossier bin de PostgreSQL (défaut : version la plus récente de C:\Program Files\PostgreSQL).

.EXAMPLE
    .\scripts\mep\mettre_a_jour_base_officine.ps1 -Base pharmacie -DossierSauvegarde E:\Sauvegardes\Pharmacie
    Mise à jour complète : sauvegarde, contrôle Chifa, tous les scripts.

.EXAMPLE
    .\scripts\mep\mettre_a_jour_base_officine.ps1 -Base pharmacie -DossierSauvegarde E:\Sauvegardes\Pharmacie -SeparerRoles
    Idem, et sépare les rôles (111b) juste après le script 111 s'ils ne le sont pas.

.EXAMPLE
    .\scripts\mep\mettre_a_jour_base_officine.ps1 -Base pharmacie -DossierSauvegarde E:\Sauvegardes\Pharmacie -WhatIf
    Montre le plan sans rien modifier.

.NOTES
    Si PowerShell refuse le script : powershell -ExecutionPolicy Bypass -File .\scripts\mep\mettre_a_jour_base_officine.ps1 ...
    Documentation : docs\scripts-base-de-donnees.md
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory = $true)][string]$Base,
    [Parameter(Mandatory = $true)][string]$DossierSauvegarde,
    [string]$Hote = "localhost",
    [int]$Port = 5432,
    [string]$CompteSuperUtilisateur = "postgres",
    [string]$NomService = "PharmaOfficine API",
    [switch]$SansControleService,
    [switch]$SeparerRoles,
    [switch]$ContinuerMalgreConflitsChifa,
    [int]$ScriptDepart = 0,
    [string]$DossierMigration = "",
    [string]$Script111b = "",
    [string]$PgBin = "",
    [string]$DossierDepot = ""
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "outils_mep.ps1")
if (-not $DossierDepot) { $DossierDepot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
if (-not $DossierMigration) { $DossierMigration = Join-Path $DossierDepot "database\migration" }
$RoleApp = "pharma_app"
$RoleProprietaire = "pharma_proprietaire"

# État de la séparation des rôles : 'separee', 'non_separee' ou 'incoherent'
function Get-EtatRoles {
    param([string]$Base)
    $proprioBase = Get-Scalaire -Compte $RoleApp -Base $Base -Sql "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = current_database()"
    $roleExiste = (Get-Scalaire -Compte $RoleApp -Base $Base -Sql "SELECT count(*) FROM pg_roles WHERE rolname = '$RoleProprietaire'") -ne "0"
    $parApp = [int](Get-Scalaire -Compte $RoleApp -Base $Base -Sql "SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND tableowner = '$RoleApp'")
    $parProprio = [int](Get-Scalaire -Compte $RoleApp -Base $Base -Sql "SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND tableowner = '$RoleProprietaire'")
    $texte = "rôle $RoleProprietaire : $(if ($roleExiste) { 'présent' } else { 'absent' }) ; propriétaire de la base : $proprioBase ; tables à $RoleApp : $parApp ; tables à $RoleProprietaire : $parProprio"
    $etat = "incoherent"
    if ($roleExiste -and $proprioBase -eq $RoleProprietaire -and $parApp -eq 0 -and $parProprio -gt 0) { $etat = "separee" }
    elseif ($proprioBase -ne $RoleProprietaire -and $parProprio -eq 0) { $etat = "non_separee" }
    return [pscustomobject]@{ Etat = $etat; Texte = $texte }
}

# Contrôle en lecture seule des n° de facture Chifa (manuel, parcours B, ancienne étape 7). Retourne les lignes trouvées.
function Get-ConflitsChifa {
    param([string]$Base, [string]$Compte, [string]$Role)
    $requetes = @(
        @{ Titre = "N° de facture Chifa non conformes (8 chiffres)"; Sql = "SELECT id, numero, date_vente, chifa_facture, num_facture_chifa FROM vente WHERE chifa_facture !~ '^[0-9]{8}$' OR num_facture_chifa !~ '^[0-9]{8}$' ORDER BY id" },
        @{ Titre = "Conflits : les deux colonnes remplies et différentes"; Sql = "SELECT id, numero, date_vente, num_facture_chifa, chifa_facture FROM vente WHERE num_facture_chifa IS NOT NULL AND chifa_facture IS NOT NULL AND btrim(num_facture_chifa) <> btrim(chifa_facture) ORDER BY id" },
        @{ Titre = "Recopie impossible : n° déjà porté par une autre vente non annulée"; Sql = "SELECT v.id, v.numero, v.num_facture_chifa, a.numero AS deja_sur FROM vente v JOIN vente a ON a.id <> v.id AND a.statut <> 'annulee' AND a.chifa_facture = btrim(v.num_facture_chifa) WHERE v.chifa_facture IS NULL AND v.statut <> 'annulee'" }
    )
    $trouve = @()
    foreach ($q in $requetes) {
        $r = Invoke-Psql -Compte $Compte -Base $Base -Role $Role -Arguments @("-A", "-F", " | ", "-P", "footer=off", "-c", $q.Sql)
        $lignes = @($r.Lignes | Where-Object { $_ -ne "" })
        if ($lignes.Count -gt 1) { $trouve += [pscustomobject]@{ Titre = $q.Titre; Lignes = $lignes } }
    }
    return $trouve
}

Invoke-Principal {
    $compteurs = [Diagnostics.Stopwatch]::StartNew()
    Write-Etape "Contrôles préalables (sans connexion à la base)"
    Test-NomBase $Base
    Initialize-OutilsMep -PgBin $PgBin -Hote $Hote -Port $Port -Simulation $WhatIfPreference

    $scripts = Get-ScriptsNumerotes $DossierMigration
    Write-Ok "$($scripts.Count) scripts numérotés dans $DossierMigration (du $($scripts[0].Name.Substring(0,3)) au $($scripts[-1].Name.Substring(0,3)))."
    if (-not $Script111b) {
        $Script111b = Join-Path $DossierMigration "111b_roles_integrite.sql"
        if (-not (Test-Path -LiteralPath $Script111b)) { $Script111b = Join-Path $DossierDepot "database\migration\111b_roles_integrite.sql" }
    }
    if ($SeparerRoles -and -not (Test-Path -LiteralPath $Script111b)) { throw "-SeparerRoles : script 111b introuvable ($Script111b). Le copier depuis le dépôt (il n'est pas dans le dossier publié) ou préciser -Script111b." }

    if ($SansControleService) { Write-Avert "-SansControleService : le service de l'API n'est pas contrôlé (essai sur une copie seulement)." }
    else {
        $service = Get-Service -Name $NomService -ErrorAction SilentlyContinue
        if (-not $service) { throw "Service « $NomService » introuvable. Si ce n'est pas le PC serveur de l'officine (essai sur une copie), ajouter -SansControleService." }
        if ($service.Status -ne "Stopped") { throw "Le service « $NomService » est $($service.Status). L'arrêter (Stop-Service `"$NomService`") puis relancer." }
        Write-Ok "Service « $NomService » arrêté."
    }

    $connexion = Request-MotDePasse -Compte $RoleApp -Invite "Mot de passe PostgreSQL du compte $RoleApp"
    $etat = $null
    if ($connexion) {
        Write-Etape "État de la base"
        $version = Get-Scalaire -Compte $RoleApp -Base $Base -Sql "SHOW server_version"
        Write-Ok "Base « $Base » joignable ($Hote`:$Port), PostgreSQL $version."
        $autres = Get-Scalaire -Compte $RoleApp -Base $Base -Sql "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()"
        if ($autres -ne "0") { Write-Avert "$autres autre(s) connexion(s) ouverte(s) sur la base (API arrêtée ? outil d'administration ?)." }
        $etat = Get-EtatRoles -Base $Base
        Write-Info $etat.Texte
        switch ($etat.Etat) {
            "separee"     { Write-Ok "Séparation des rôles (111b) : DÉJÀ FAITE. Les scripts passent par $CompteSuperUtilisateur + SET ROLE $RoleProprietaire." }
            "non_separee" { Write-Ok "Séparation des rôles (111b) : pas encore faite. Les scripts passent par $RoleApp." }
            default       { throw "État des rôles incohérent (séparation partielle). Relancer d'abord 111b à la main avec le superutilisateur (il est idempotent), puis ce script." }
        }
    }
    else { Write-Simul "Pas de mot de passe disponible : état de la base non lu (connexion, rôles)." }

    $separee = ($etat -and $etat.Etat -eq "separee")
    $besoinSuper = $separee -or $SeparerRoles
    if ($besoinSuper) {
        $superOk = Request-MotDePasse -Compte $CompteSuperUtilisateur -Invite "Mot de passe PostgreSQL du superutilisateur $CompteSuperUtilisateur"
        if ($superOk) {
            $estSuper = Get-Scalaire -Compte $CompteSuperUtilisateur -Base $Base -Sql "SELECT rolsuper::text FROM pg_roles WHERE rolname = current_user"
            if ($estSuper -ne "true") { throw "Le compte $CompteSuperUtilisateur n'est pas superutilisateur." }
            if ($separee) {
                $essai = Get-Scalaire -Compte $CompteSuperUtilisateur -Base $Base -Role $RoleProprietaire -Sql "SELECT current_user"
                if ($essai -ne $RoleProprietaire) { throw "SET ROLE $RoleProprietaire n'a pas donné le rôle attendu (« $essai »). Méthode [À VÉRIFIER] : voir docs\scripts-base-de-donnees.md." }
            }
            Write-Ok "Superutilisateur $CompteSuperUtilisateur vérifié$(if ($separee) { " (SET ROLE $RoleProprietaire possible)" })."
        }
    }

    # Point de départ du rejeu : toujours 001, sauf reprise manuelle après incident (-ScriptDepart)
    $depart = if ($ScriptDepart -gt 0) { $ScriptDepart } else { 1 }
    if ($ScriptDepart -gt 1) { Write-Info "Reprise manuelle à partir du script $ScriptDepart (incident) : rejouer ensuite toute la suite depuis 001." }
    $aRejouer = @($scripts | Where-Object { [int]$_.Name.Substring(0, 3) -ge $depart })
    if ($aRejouer.Count -eq 0) { throw "Aucun script à partir du numéro $depart." }
    Write-Ok "Rejeu : $($aRejouer.Count) scripts, du $($aRejouer[0].Name.Substring(0,3)) au $($aRejouer[-1].Name.Substring(0,3))."

    $idHorodatage = Get-Date -Format "yyyyMMdd_HHmmss"
    $fichierDump = Join-Path $DossierSauvegarde "${Base}_avant_maj_$idHorodatage.dump"

    # Plan
    if (-not $PSCmdlet.ShouldProcess("base « $Base » ($Hote`:$Port)", "sauvegarder, puis rejouer $($aRejouer.Count) scripts")) {
        Write-Simul "Sauvegarde : pg_dump -Fc -> $fichierDump (puis pg_restore --list)"
        $compte = if ($separee) { "$CompteSuperUtilisateur + SET ROLE $RoleProprietaire" } else { $RoleApp }
        $fait111b = $separee
        foreach ($s in $aRejouer) {
            if ($SeparerRoles -and -not $fait111b -and [int]$s.Name.Substring(0, 3) -gt 111) {
                Write-Simul "111b_roles_integrite.sql ($CompteSuperUtilisateur, superutilisateur), puis les suivants avec SET ROLE $RoleProprietaire"
                $compte = "$CompteSuperUtilisateur + SET ROLE $RoleProprietaire"; $fait111b = $true
            }
            if ($s.Name -like "101_*") { Write-Simul "Contrôle des n° de facture Chifa (lecture seule, si 101 pas encore appliqué), puis arrêt s'il y a des lignes (sauf -ContinuerMalgreConflitsChifa)" }
            Write-Simul "$($s.Name) ($compte)"
        }
        if ($SeparerRoles -and -not $fait111b) { Write-Simul "111b_roles_integrite.sql ($CompteSuperUtilisateur, superutilisateur)" }
        Write-Simul "Le script 101b n'est jamais lancé."
        return
    }

    # Sauvegarde obligatoire
    Write-Etape "Sauvegarde avant modification"
    if (-not (Test-Path -LiteralPath $DossierSauvegarde)) { New-Item -ItemType Directory -Path $DossierSauvegarde -Force | Out-Null }
    if (Test-Path -LiteralPath $fichierDump) { throw "Le fichier de sauvegarde existe déjà : $fichierDump" }
    $r = Invoke-OutilPg -Outil $script:PgDump -Compte $RoleApp -Arguments @("-h", $Hote, "-p", "$Port", "-U", $RoleApp, "-w", "-Fc", "-f", $fichierDump, $Base)
    if ($r.Code -ne 0) { throw "pg_dump a échoué (aucune modification faite) :`n$($r.Texte)" }
    if (-not (Test-Path -LiteralPath $fichierDump) -or (Get-Item -LiteralPath $fichierDump).Length -lt 1024) { throw "Sauvegarde vide ou absente : $fichierDump" }
    $r = Invoke-OutilPg -Outil $script:PgRestore -Arguments @("--list", $fichierDump)
    if ($r.Code -ne 0 -or $r.Lignes.Count -lt 20) { throw "La sauvegarde n'est pas lisible (pg_restore --list, code $($r.Code)) : $fichierDump" }
    Write-Ok ("Sauvegarde : {0} ({1:N1} Mo), lisible ({2} lignes de table des matières)." -f $fichierDump, ((Get-Item -LiteralPath $fichierDump).Length / 1MB), $r.Lignes.Count)
    Write-Avert "Copier cette sauvegarde hors du PC serveur, ainsi que super-admin.dat et cle-integrite.dat (parcours A, étape 4.9)."

    # Rejeu
    Write-Etape "Rejeu des scripts numérotés (dans l'ordre, hors transaction)"
    $joues = 0
    $separationFaiteIci = $false
    $conflitsAffiches = $false
    foreach ($s in $aRejouer) {
        $numero = [int]$s.Name.Substring(0, 3)
        # Départ après 111 : 111b est lancé avant le premier script suivant
        if ($SeparerRoles -and -not $separee -and -not $separationFaiteIci -and $numero -gt 111) {
            $r = Invoke-Psql -Compte $CompteSuperUtilisateur -Base $Base -Arguments @("-f", $Script111b)
            $r.Lignes | Where-Object { $_ } | ForEach-Object { Write-Info "  $_" }
            $separationFaiteIci = $true
            Write-Ok "111b_roles_integrite.sql : séparation des rôles faite. Les scripts suivants passent par $CompteSuperUtilisateur + SET ROLE $RoleProprietaire."
        }
        if ($s.Name -like "101_*") {
            $absent = (Get-Scalaire -Compte $RoleApp -Base $Base -Sql "SELECT (to_regclass('public.vente_chifa_facture_conflit') IS NULL)::text") -eq "true"
            $colonnes = Get-Scalaire -Compte $RoleApp -Base $Base -Sql "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'vente' AND column_name IN ('chifa_facture', 'num_facture_chifa')"
            if ($absent -and $colonnes -eq "2") {
                Write-Info "Contrôle des n° de facture Chifa avant le script 101 (lecture seule)..."
                $conflits = @(Get-ConflitsChifa -Base $Base -Compte $(if ($separee -or $separationFaiteIci) { $CompteSuperUtilisateur } else { $RoleApp }) -Role $(if ($separee -or $separationFaiteIci) { $RoleProprietaire } else { "" }))
                if ($conflits.Count -gt 0) {
                    foreach ($c in $conflits) { Write-Host ""; Write-Host "   $($c.Titre) :" -ForegroundColor Yellow; $c.Lignes | ForEach-Object { Write-Host "     $_" } }
                    Write-Host ""
                    if (-not $ContinuerMalgreConflitsChifa) {
                        throw "ARRÊT avant le script 101 : n° de facture Chifa à examiner (liste ci-dessus). NE PAS CONTINUER sans en parler au titulaire : ces anomalies ne se corrigent pas sans lui. Après sa décision, relancer avec -ContinuerMalgreConflitsChifa. Les scripts 001 à 100 déjà rejoués sont sans danger (idempotents) ; sauvegarde : $fichierDump"
                    }
                    Write-Avert "-ContinuerMalgreConflitsChifa : reprise après décision du titulaire."
                }
                else { Write-Ok "Aucun n° de facture Chifa à examiner." }
            }
            else { Write-Info "Contrôle Chifa sans objet (script 101 déjà appliqué ou colonnes absentes)." }
        }

        try {
            if ($separee -or $separationFaiteIci) {
                [void](Invoke-Psql -Compte $CompteSuperUtilisateur -Base $Base -Role $RoleProprietaire -Arguments @("-f", $s.FullName))
            }
            else {
                [void](Invoke-Psql -Compte $RoleApp -Base $Base -Arguments @("-f", $s.FullName))
            }
        }
        catch {
            throw "Arrêt sur $($s.Name) ($joues script(s) joué(s) avant). Corriger la cause puis relancer le même script, sans -ScriptDepart (toute la suite est rejouable ; -ScriptDepart $numero seulement en cas d'incident) ; sauvegarde : $fichierDump`n$($_.Exception.Message)"
        }
        $joues++
        Write-Info $s.Name

        # 111b juste après 111 (une fois par base)
        if ($SeparerRoles -and -not $separee -and -not $separationFaiteIci -and $s.Name -like "111_*") {
            $r = Invoke-Psql -Compte $CompteSuperUtilisateur -Base $Base -Arguments @("-f", $Script111b)
            $r.Lignes | Where-Object { $_ } | ForEach-Object { Write-Info "  $_" }
            $separationFaiteIci = $true
            Write-Ok "111b_roles_integrite.sql : séparation des rôles faite. Les scripts suivants passent par $CompteSuperUtilisateur + SET ROLE $RoleProprietaire."
        }
    }
    if ($SeparerRoles -and -not $separee -and -not $separationFaiteIci) {
        # La liste s'arrête au script 111 ou avant : 111b en dernier
        $r = Invoke-Psql -Compte $CompteSuperUtilisateur -Base $Base -Arguments @("-f", $Script111b)
        $r.Lignes | Where-Object { $_ } | ForEach-Object { Write-Info "  $_" }
        $separationFaiteIci = $true
        Write-Ok "111b_roles_integrite.sql : séparation des rôles faite."
    }
    Write-Ok "$joues scripts rejoués sans erreur."

    # Vérifications finales
    Write-Etape "Vérifications finales"
    $utilisateurs = Get-Scalaire -Compte $RoleApp -Base $Base -Sql "SELECT count(*) FROM utilisateur"
    Write-Ok "$RoleApp lit la base (utilisateurs : $utilisateurs)."
    $etatFinal = Get-EtatRoles -Base $Base
    Write-Info $etatFinal.Texte
    if ($etatFinal.Etat -eq "separee") {
        $restants = Get-Scalaire -Compte $RoleApp -Base $Base -Sql "SELECT count(*) FROM pg_class c JOIN pg_roles r ON r.oid = c.relowner WHERE r.rolname = '$RoleApp' AND c.relnamespace = 'public'::regnamespace"
        if ($restants -ne "0") { throw "objets_encore_a_pharma_app = $restants (0 attendu)." }
        Write-Ok "objets_encore_a_pharma_app = 0."
    }
    elseif ($etatFinal.Etat -eq "incoherent") { throw "État des rôles incohérent après le rejeu : $($etatFinal.Texte)" }

    Write-Etape "Résumé"
    Write-Info "Scripts joués : $joues (de $($aRejouer[0].Name.Substring(0,3)) à $($aRejouer[-1].Name.Substring(0,3)))."
    Write-Info ("Durée du rejeu : {0:N0} s." -f $compteurs.Elapsed.TotalSeconds)
    Write-Info "État des rôles : $(if ($etatFinal.Etat -eq 'separee') { 'séparés (111b fait)' } else { 'NON séparés : journaux non protégés, relancer avec -SeparerRoles (ou 111b à la main)' })."
    Write-Info "Sauvegarde : $fichierDump"
    Write-Info "Script 101b : jamais lancé (décision du titulaire). Suite : parcours B, étape 11 (remplacer les fichiers de l'API)."
}
