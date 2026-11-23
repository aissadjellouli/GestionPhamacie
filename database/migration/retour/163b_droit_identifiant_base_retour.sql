-- =====================================================================
-- Retour arrière de 163b (droit de lire l'identifiant de la base) :
-- retire à pharma_app le droit d'exécuter pg_control_system(). La
-- détection de lignée rend alors de nouveau « inconnu ». SCRIPT
-- D'ADMINISTRATEUR (superutilisateur). Rejouable sans erreur.
--   psql -U postgres -d <base> -v ON_ERROR_STOP=1 -f retour/163b_droit_identifiant_base_retour.sql
-- =====================================================================

DO $$
BEGIN
    IF NOT (SELECT rolsuper FROM pg_roles WHERE rolname = current_user) THEN
        RAISE EXCEPTION '163b (retour) : à exécuter par un superutilisateur PostgreSQL (pas par pharma_app)';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pharma_app') THEN
        REVOKE EXECUTE ON FUNCTION pg_control_system() FROM pharma_app;
    END IF;
END;
$$;
