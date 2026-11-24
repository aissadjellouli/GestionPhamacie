-- =====================================================================
-- Retour arrière de 166b (droit de promouvoir la base) :
-- retire à pharma_app le droit d'exécuter pg_promote(boolean, integer). La
-- promotion automatique du 2e serveur est alors impossible (alerte
-- « promotion manuelle nécessaire ») ; le script manuel reste. SCRIPT
-- D'ADMINISTRATEUR (superutilisateur). Rejouable sans erreur.
--   psql -U postgres -d <base> -v ON_ERROR_STOP=1 -f retour/166b_droit_promotion_base_retour.sql
-- =====================================================================

DO $$
BEGIN
    IF NOT (SELECT rolsuper FROM pg_roles WHERE rolname = current_user) THEN
        RAISE EXCEPTION '166b (retour) : à exécuter par un superutilisateur PostgreSQL (pas par pharma_app)';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pharma_app') THEN
        REVOKE EXECUTE ON FUNCTION pg_promote(boolean, integer) FROM pharma_app;
    END IF;
END;
$$;
