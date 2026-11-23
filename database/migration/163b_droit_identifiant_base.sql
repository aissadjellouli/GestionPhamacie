-- =====================================================================
-- 163b — Droit de lire l'identifiant de la base (bascule du 2e serveur,
-- lot 6, 2026-11-23). SCRIPT D'ADMINISTRATEUR (superutilisateur
-- PostgreSQL), hors suite numérotée, sur le modèle de 111b.
--
-- Pourquoi : l'API compare l'identifiant de la base (pg_control_system())
-- pour savoir si deux serveurs descendent de la même base. Cette fonction
-- est réservée au superutilisateur : sans ce droit, la détection rend
-- « inconnu » et n'écarte jamais un serveur.
--
-- Droit ciblé : le compte de l'API reçoit SEULEMENT le droit d'exécuter
-- pg_control_system(), aucun autre droit.
-- Compte de l'API : pharma_app, avec ou sans la séparation des rôles
-- (111b) ; 111b ne change pas le compte de connexion de l'API, seulement
-- la propriété des objets.
--
-- Mode d'emploi (par base, une fois ; les droits sur les fonctions du
-- catalogue sont propres à chaque base, donc à refaire après une
-- restauration sur une base neuve) :
--   psql -U postgres -d <base> -v ON_ERROR_STOP=1 -f 163b_droit_identifiant_base.sql
-- Retour arrière : retour/163b_droit_identifiant_base_retour.sql.
-- Idempotent.
-- =====================================================================

DO $$
BEGIN
    IF NOT (SELECT rolsuper FROM pg_roles WHERE rolname = current_user) THEN
        RAISE EXCEPTION '163b : à exécuter par un superutilisateur PostgreSQL (pas par pharma_app)';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pharma_app') THEN
        RAISE EXCEPTION '163b : le rôle pharma_app (compte de l''API) n''existe pas';
    END IF;
END;
$$;

GRANT EXECUTE ON FUNCTION pg_control_system() TO pharma_app;

-- Contrôle (à lire dans la sortie) : doit afficher « t »
SELECT has_function_privilege('pharma_app', 'pg_control_system()', 'EXECUTE') AS pharma_app_peut_lire_identifiant_base;
