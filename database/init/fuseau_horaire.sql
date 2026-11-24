-- =====================================================================
-- Pharma Officine : FUSEAU HORAIRE DE LA BASE (mise en production)
--
-- Africa/Algiers (UTC+1 toute l'année, sans heure d'été). Réglage de la
-- BASE (ALTER DATABASE … SET timezone), absent d'un dump de schéma : à
-- lancer après schema.sql sur une installation neuve
-- (scripts/mep/creer_base_officine.ps1 le fait).
--
-- Copie du script de migration 083_fuseau_horaire.sql, pour que
-- database/init contienne tous les scripts de mise en production. Le corps
-- SQL doit rester IDENTIQUE à celui du 083 (test d'architecture
-- InitCompletMepTests).
--
-- Aucune donnée modifiée. Idempotent.
-- =====================================================================

DO $$
BEGIN
    EXECUTE format('ALTER DATABASE %I SET timezone = %L', current_database(), 'Africa/Algiers');
END
$$;
