#!/usr/bin/env bash
# =====================================================================
# Démo cloud, étape 1 : création de la base de démonstration (docs/demo-cloud.md)
#
# Lancé par l'image postgres (dossier /docker-entrypoint-initdb.d) au PREMIER démarrage seulement,
# quand le volume des données est vide. Compte superutilisateur, par le socket local du serveur temporaire.
#
# Même ordre que la mise en production (scripts/mep/creer_base_officine.ps1) :
#   1. rôle applicatif pharma_app (LOGIN CREATEDB) ;
#   2. base OWNER pharma_app, UTF8, template0 (propriétaire : il crée pg_trgm et pgcrypto) ;
#   3. database/init/schema.sql, 4. database/migration/083_*.sql (fuseau), 5. database/init/seed_referentiel.sql,
#      avec le compte pharma_app, ON_ERROR_STOP et PGOPTIONS « pharma.reglage_central=on » ;
#   puis les données FICTIVES 900 à 904 (database/data).
# Non fait à cette étape : 111b (séparation des rôles : journaux non protégés, base de démonstration seulement),
# 905 (jeu de test) et la nomenclature nationale (étape 2).
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
# Données fictives chargées à cette étape (905 et la nomenclature : étape 2)
DONNEES_DEMO=(900 901 902 903 904)

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

# Un seul fichier attendu par numéro (ex. 083_*.sql)
script_numerote() {
    local dossier="$1" numero="$2"
    local trouves=("$dossier"/"$numero"_*.sql)
    [[ ${#trouves[@]} -eq 1 && -f "${trouves[0]}" ]] || erreur "script $numero introuvable ou en double dans $dossier"
    printf '%s' "${trouves[0]}"
}

[[ "$BASE" =~ ^[a-z_][a-z0-9_]*$ ]] || erreur "nom de base refusé : $BASE"
for f in "$SCRIPTS/init/schema.sql" "$SCRIPTS/init/seed_referentiel.sql"; do
    [[ -f "$f" ]] || erreur "fichier introuvable : $f (montage ../../database dans docker-compose.yml)"
done
SCRIPT_FUSEAU="$(script_numerote "$SCRIPTS/migration" 083)"

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

info "structure : init/schema.sql"
psql_app -f "$SCRIPTS/init/schema.sql"
info "fuseau horaire : $(basename "$SCRIPT_FUSEAU")"
psql_app -f "$SCRIPT_FUSEAU"
info "référentiel : init/seed_referentiel.sql"
psql_app -f "$SCRIPTS/init/seed_referentiel.sql"

for numero in "${DONNEES_DEMO[@]}"; do
    fichier="$(script_numerote "$SCRIPTS/data" "$numero")"
    info "données fictives : $(basename "$fichier")"
    psql_app -f "$fichier"
done

# Vérifications (lecture seule)
fuseau="$(psql_app -tA -c "SHOW timezone")"
[[ "$fuseau" == "Africa/Algiers" ]] || erreur "fuseau de la base : « $fuseau » au lieu de Africa/Algiers"
info "fuseau de la base : $fuseau"

demo="$(psql_app -tA -c "SELECT count(*) FROM reception WHERE numero_bl = 'BL-DEMO-1'")"
if [[ "$demo" == "0" ]]; then
    # Constat du code : seed_referentiel.sql crée le compte « titulaire » (mot de passe provisoire), et 900_donnees_demo.sql
    # s'arrête quand ce compte existe déjà. Décision du titulaire attendue (docs/demo-cloud.md, étape 1).
    echo "init démo : ATTENTION, données fictives 900 à 904 NON chargées (compte « titulaire » déjà créé par seed_referentiel.sql)." >&2
    echo "init démo : seul le compte « titulaire » du référentiel existe (mot de passe provisoire, à changer à la première connexion)." >&2
else
    info "données fictives chargées"
fi

unset PGPASSWORD MDP_APP
info "terminé : base $BASE prête"
