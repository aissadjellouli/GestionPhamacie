#!/usr/bin/env bash
# =====================================================================
# Démo cloud, étape 2 : remise à zéro de la base de démonstration (docs/demo-cloud.md)
#
# Service « reinit » du docker-compose de la démo (image postgres, psql). Ne touche QUE la base de démonstration du volume
# Docker de la démo (base PHARMA_BASE, gabarit PHARMA_GABARIT, serveur « db » du compose) ; jamais une base de développement,
# de test ou d'officine (le nom de base et la présence du marqueur base_demonstration sont vérifiés avant toute suppression).
#
#   remise_a_zero.sh            boucle : prépare la base (905), puis remise à zéro chaque jour à REINIT_HEURE (défaut 04:00)
#   remise_a_zero.sh maintenant une remise à zéro tout de suite, puis 905 (commande manuelle, voir docs/demo-cloud.md)
#   remise_a_zero.sh preparer   seulement : attendre la nomenclature (importée par l'API), puis passer 905 s'il n'est pas passé
#
# Remise à zéro : connexions coupées, base supprimée (DROP DATABASE … WITH (FORCE)), recréée depuis le gabarit (CREATE DATABASE
# … TEMPLATE), fuseau de la base reposé ; l'API (Demo:Actif) réimporte la nomenclature nationale, puis 905 est repassé : ses dates
# sont calculées par rapport au jour d'exécution (ventes des 90 derniers jours toujours récentes).
#
# Mots de passe : lus dans les fichiers des secrets compose, passés à psql par son environnement seulement.
# =====================================================================
set -euo pipefail

MODE="${1:-boucle}"
BASE="${PHARMA_BASE:-pharmacie_demo}"
GABARIT="${PHARMA_GABARIT:-${BASE}_gabarit}"
SCRIPTS="${PHARMA_SCRIPTS:-/pharma/database}"
HOTE="${PHARMA_HOTE:-db}"
HEURE="${REINIT_HEURE:-04:00}"
ATTENTE_NOMENCLATURE_MIN="${ATTENTE_NOMENCLATURE_MIN:-30}"
ROLE_APP="pharma_app"

erreur() { echo "reinit démo : $*" >&2; exit 1; }
info() { echo "reinit démo : $(date '+%F %T') $*"; }

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
[[ "$GABARIT" =~ ^[a-z_][a-z0-9_]*$ && "$GABARIT" != "$BASE" ]] || erreur "nom du gabarit refusé : $GABARIT"
# Garde-fou : jamais les bases de développement ou de test du poste, ni une base d'officine
case "$BASE" in
    pharmacie_dev|pharmacie_test|pharmacie) erreur "base $BASE refusée : la remise à zéro ne concerne que la base de démonstration" ;;
esac
[[ "$HEURE" =~ ^([01][0-9]|2[0-3]):[0-5][0-9]$ ]] || erreur "REINIT_HEURE illisible : $HEURE (format HH:MM)"

MDP_SUPER="$(lire_secret "${POSTGRES_MDP_FILE:-}")"
MDP_APP="$(lire_secret "${PHARMA_APP_MDP_FILE:-}")"
export PGCLIENTENCODING=UTF8 PGHOST="$HOTE" PGPORT="${PHARMA_PORT:-5432}"

psql_super() { PGPASSWORD="$MDP_SUPER" psql -X -q -v ON_ERROR_STOP=1 --username "${POSTGRES_USER:-postgres}" --dbname postgres "$@"; }
psql_app() {
    PGPASSWORD="$MDP_APP" PGOPTIONS="-c client_min_messages=warning -c pharma.reglage_central=on" \
        psql -X -q -v ON_ERROR_STOP=1 --username "$ROLE_APP" --dbname "$BASE" "$@"
}

attendre_serveur() {
    local essais=0
    until pg_isready -q -h "$HOTE" -p "$PGPORT"; do
        (( ++essais < 60 )) || erreur "serveur $HOTE injoignable"
        sleep 5
    done
}

# Garde-fou avant suppression : la base visée porte le marqueur de démonstration
verifier_marqueur() {
    local present
    present="$(psql_app -tA -c "SELECT to_regclass('public.base_demonstration') IS NOT NULL")"
    [[ "$present" == "t" ]] || erreur "base $BASE sans marqueur de démonstration : rien n'est supprimé"
}

# Nomenclature nationale importée par l'API (Demo:FichierNomenclature) : prérequis de 905
attendre_nomenclature() {
    local limite=$(( $(date +%s) + ATTENTE_NOMENCLATURE_MIN * 60 )) nombre
    while :; do
        nombre="$(psql_app -tA -c "SELECT count(*) FROM nomenclature_produit" 2>/dev/null || echo 0)"
        (( nombre > 1000 )) && { info "nomenclature présente ($nombre médicaments)"; return 0; }
        (( $(date +%s) < limite )) || { info "nomenclature absente après $ATTENTE_NOMENCLATURE_MIN min : 905 non passé (voir le journal de l'API)"; return 1; }
        sleep 20
    done
}

# Grand jeu de test FICTIF : sans effet s'il est déjà passé (garde « Caisse démo » du script) ou sans nomenclature
passer_905() {
    attendre_nomenclature || return 0
    info "jeu de test 905"
    psql_app -f "$SCRIPTS/data/905_jeu_de_test.sql"
}

remettre_a_zero() {
    attendre_serveur
    local gabarit
    gabarit="$(psql_super -tA -v gabarit="$GABARIT" <<'SQL'
SELECT count(*) FROM pg_database WHERE datname = :'gabarit' AND datistemplate
SQL
)"
    [[ "$gabarit" == "1" ]] || erreur "gabarit $GABARIT absent : la base n'est pas remise à zéro (voir deploy/demo/init/10_base_officine.sh)"
    verifier_marqueur
    info "remise à zéro de $BASE depuis $GABARIT (connexions coupées)"
    psql_super -v base="$BASE" -v gabarit="$GABARIT" <<'SQL'
DROP DATABASE IF EXISTS :"base" WITH (FORCE);
CREATE DATABASE :"base" TEMPLATE :"gabarit" OWNER pharma_app;
ALTER DATABASE :"base" SET timezone = 'Africa/Algiers';
SQL
    verifier_marqueur
    info "base $BASE recréée ; attente de la nomenclature (importée par l'API)"
    passer_905
    info "remise à zéro terminée"
}

secondes_avant() {
    local maintenant cible
    maintenant="$(date +%s)"
    cible="$(date -d "today $HEURE" +%s)"
    (( cible > maintenant )) || cible="$(date -d "tomorrow $HEURE" +%s)"
    echo $(( cible - maintenant ))
}

case "$MODE" in
    maintenant) remettre_a_zero ;;
    preparer) attendre_serveur; verifier_marqueur; passer_905 ;;
    boucle)
        attendre_serveur
        verifier_marqueur
        passer_905
        while :; do
            attente="$(secondes_avant)"
            info "prochaine remise à zéro à $HEURE (dans $(( attente / 60 )) min)"
            sleep "$attente"
            # Une remise à zéro en échec est notée ; la suivante est tentée le lendemain
            ( remettre_a_zero ) || info "remise à zéro en échec (voir ci-dessus)"
        done
        ;;
    *) erreur "mode inconnu : $MODE (boucle, maintenant ou preparer)" ;;
esac
