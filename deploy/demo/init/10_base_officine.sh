#!/usr/bin/env bash
# =====================================================================
# Démo cloud, étape 1 : création de la base de démonstration (docs/demo-cloud.md)
#
# Lancé par l'image postgres (dossier /docker-entrypoint-initdb.d) au PREMIER démarrage seulement,
# quand le volume des données est vide. Compte superutilisateur, par le socket local du serveur temporaire.
#
# Même ordre que les bases de développement (scripts/initialiser_base_locale.ps1 ; décision du titulaire du 2026-11-20,
# choix A : la voie de la mise en production, init + référentiel, crée déjà le compte « titulaire » et 900_donnees_demo.sql
# ne chargeait alors rien) :
#   1. rôle applicatif pharma_app (LOGIN CREATEDB) ;
#   2. base OWNER pharma_app, UTF8, template0 (propriétaire : il crée pg_trgm et pgcrypto) ;
#   3. toutes les migrations database/migration/NNN_*.sql, dans l'ordre des numéros (les scripts « b », 101b et 111b,
#      sont exclus comme en développement : ils se lancent à part, sur décision) ;
#   4. toutes les données FICTIVES database/data/*.sql, par ordre de nom (905, le grand jeu de test, ne fait rien tant que
#      la nomenclature nationale n'est pas importée : étape 2) ;
#   avec le compte pharma_app, ON_ERROR_STOP et PGOPTIONS « pharma.reglage_central=on » (comme en développement).
#
# Mots de passe : lus dans les fichiers des secrets compose, jamais écrits dans un fichier ni sur une ligne de commande.
# =====================================================================

# Fichier non exécutable : l'image le « source » au lieu de le lancer ; il est alors relancé dans un bash à part
# pour que « set -euo pipefail » ne change pas les options du script de l'image.
if [[ "${BASH_SOURCE[0]}" != "${0}" ]]; then
    bash "${BASH_SOURCE[0]}"
    return
fi

set -euo pipefail

BASE="${PHARMA_BASE:-pharmacie_demo}"
SCRIPTS="${PHARMA_SCRIPTS:-/pharma/database}"
ROLE_APP="pharma_app"

erreur() { echo "init démo : $*" >&2; exit 1; }
info() { echo "init démo : $*"; }

lire_secret() {
    local fichier="$1"
    [[ -n "$fichier" && -r "$fichier" ]] || erreur "secret illisible (${fichier:-non réglé})"
    local valeur
    valeur="$(tr -d '\r\n' < "$fichier")"
    [[ -n "$valeur" ]] || erreur "secret vide ($fichier)"
    [[ "$valeur" != A-REMPLACER* ]] || erreur "secret resté à la valeur d'exemple ($fichier) : voir deploy/demo/.env.example"
    printf '%s' "$valeur"
}

[[ "$BASE" =~ ^[a-z_][a-z0-9_]*$ ]] || erreur "nom de base refusé : $BASE"

# Migrations : NNN_*.sql seulement (les scripts « b » sont exclus), triées par numéro
# Numéros toujours sur trois chiffres : l'ordre alphabétique est l'ordre des numéros
mapfile -t MIGRATIONS < <(find "$SCRIPTS/migration" -maxdepth 1 -type f -regextype posix-extended -regex '.*/[0-9]{3}_[^/]*[.]sql' | sort)
(( ${#MIGRATIONS[@]} > 0 )) || erreur "aucune migration dans $SCRIPTS/migration (montage ../../database dans docker-compose.yml)"
# Un numéro en double arrêterait tout plutôt que de passer deux scripts dans un ordre incertain
doublons="$(for m in "${MIGRATIONS[@]}"; do basename "$m" | cut -c1-3; done | sort | uniq -d)"
[[ -z "$doublons" ]] || erreur "numéros de migration en double : $doublons"
# Données fictives : tous les .sql de database/data, par ordre de nom
mapfile -t DONNEES < <(find "$SCRIPTS/data" -maxdepth 1 -type f -name '*.sql' | sort)
(( ${#DONNEES[@]} > 0 )) || erreur "aucune donnée fictive dans $SCRIPTS/data"

MDP_APP="$(lire_secret "${PHARMA_APP_MDP_FILE:-}")"

export PGCLIENTENCODING=UTF8

# 1 et 2. Rôle et base (superutilisateur). Le mot de passe passe par une variable d'environnement du seul processus psql.
info "rôle $ROLE_APP et base $BASE"
MDP_APP_INIT="$MDP_APP" psql -X -q -v ON_ERROR_STOP=1 --username "${POSTGRES_USER:-postgres}" --dbname postgres \
    -v base="$BASE" <<'SQL'
\getenv mdp MDP_APP_INIT
SELECT NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pharma_app') AS role_absent \gset
\if :role_absent
CREATE ROLE pharma_app LOGIN CREATEDB PASSWORD :'mdp';
\else
ALTER ROLE pharma_app LOGIN CREATEDB PASSWORD :'mdp';
\endif
CREATE DATABASE :"base" OWNER pharma_app ENCODING 'UTF8' TEMPLATE template0;
SQL

# 3 à 5, puis données fictives : compte pharma_app (comme scripts/mep), marqueur des réglages mutualisés (script 141)
export PGOPTIONS="-c client_min_messages=warning -c pharma.reglage_central=on"
export PGPASSWORD="$MDP_APP"
psql_app() { psql -X -q -v ON_ERROR_STOP=1 --username "$ROLE_APP" --dbname "$BASE" "$@"; }

info "migrations : ${#MIGRATIONS[@]} scripts, de $(basename "${MIGRATIONS[0]}") à $(basename "${MIGRATIONS[-1]}")"
for fichier in "${MIGRATIONS[@]}"; do
    psql_app -f "$fichier"
done

for fichier in "${DONNEES[@]}"; do
    info "données fictives : $(basename "$fichier")"
    psql_app -f "$fichier"
done

# Vérifications (lecture seule)
fuseau="$(psql_app -tA -c "SHOW timezone")"
[[ "$fuseau" == "Africa/Algiers" ]] || erreur "fuseau de la base : « $fuseau » au lieu de Africa/Algiers"
info "fuseau de la base : $fuseau"

demo="$(psql_app -tA -c "SELECT count(*) FROM reception WHERE numero_bl = 'BL-DEMO-1'")"
[[ "$demo" != "0" ]] || erreur "données fictives non chargées (réception BL-DEMO-1 absente) : voir le journal ci-dessus"
comptes="$(psql_app -tA -c "SELECT count(*) FROM utilisateur WHERE identifiant IN ('titulaire', 'assistant', 'vendeur')")"
[[ "$comptes" == "3" ]] || erreur "comptes de démonstration incomplets ($comptes sur 3 : titulaire, assistant, vendeur)"
info "données fictives chargées ; comptes titulaire, assistant, vendeur (mot de passe de démonstration : voir docs/demo-cloud.md)"

unset PGPASSWORD MDP_APP
info "terminé : base $BASE prête"
