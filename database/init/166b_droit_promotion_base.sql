-- =====================================================================
-- 166b — Droit de promouvoir la base (bascule du 2e serveur, lot V2-4,
-- 2026-11-24). SCRIPT D'ADMINISTRATEUR (superutilisateur PostgreSQL),
-- hors suite numérotée, sur le modèle de 111b et 163b.
--
-- Pourquoi : la promotion automatique du 2e serveur appelle
-- pg_promote(true, 60) depuis l'API. Cette fonction est réservée au
-- superutilisateur : sans ce droit, aucune promotion automatique
-- (alerte « promotion manuelle nécessaire »), le script manuel reste.
--
-- Droit ciblé : le compte de l'API reçoit SEULEMENT le droit d'exécuter
-- pg_promote(boolean, integer), aucun autre droit.
-- À passer sur le serveur PRINCIPAL : le droit est écrit dans le catalogue
-- de la base et recopié tel quel sur le 2e serveur par la réplication.
-- Compte de l'API : pharma_app, avec ou sans la séparation des rôles (111b).
--
-- Mode d'emploi (par base, une fois ; à refaire après une restauration sur
-- une base neuve) :
--   psql -U postgres -d <base> -v ON_ERROR_STOP=1 -f 166b_droit_promotion_base.sql
-- Retour arrière : database/migration/retour/166b_droit_promotion_base_retour.sql.
-- Idempotent.
-- =====================================================================

DO $$
BEGIN
    IF NOT (SELECT rolsuper FROM pg_roles WHERE rolname = current_user) THEN
        RAISE EXCEPTION '166b : à exécuter par un superutilisateur PostgreSQL (pas par pharma_app)';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pharma_app') THEN
        RAISE EXCEPTION '166b : le rôle pharma_app (compte de l''API) n''existe pas';
    END IF;
END;
$$;

GRANT EXECUTE ON FUNCTION pg_promote(boolean, integer) TO pharma_app;

-- Contrôle (à lire dans la sortie) : doit afficher « t »
SELECT has_function_privilege('pharma_app', 'pg_promote(boolean, integer)', 'EXECUTE') AS pharma_app_peut_promouvoir;
