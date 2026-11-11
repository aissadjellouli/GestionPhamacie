-- =====================================================================
-- GED (gestion électronique des documents) et sauvegardes
--   * la sauvegarde quotidienne copie la base sur le disque externe ET
--     dans le dossier dédié de la GED du serveur (GED\Sauvegardes), et les
--     documents de la GED sur le disque externe (nouveaux et modifiés) ;
--   * parametre_sauvegarde.retention_ged_jours : jours de conservation
--     de la copie de la base dans la GED (vide : réglage du serveur
--     Ged:RetentionSauvegardesJours, 7 jours par défaut) ;
--   * parametre_sauvegarde.nettoyage_ged_annees : documents de la GED
--     supprimés au-delà de ce nombre d'années (vide : jamais, la GED est
--     gardée).
-- Idempotent. A exécuter après 049_vente_sans_ordonnance.sql.
-- =====================================================================

ALTER TABLE parametre_sauvegarde ADD COLUMN IF NOT EXISTS retention_ged_jours integer;
ALTER TABLE parametre_sauvegarde ADD COLUMN IF NOT EXISTS nettoyage_ged_annees integer;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_sauvegarde_retention_ged') THEN
        ALTER TABLE parametre_sauvegarde ADD CONSTRAINT ck_sauvegarde_retention_ged
            CHECK (retention_ged_jours IS NULL OR retention_ged_jours BETWEEN 1 AND 365);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_sauvegarde_nettoyage_ged') THEN
        ALTER TABLE parametre_sauvegarde ADD CONSTRAINT ck_sauvegarde_nettoyage_ged
            CHECK (nettoyage_ged_annees IS NULL OR nettoyage_ged_annees BETWEEN 1 AND 100);
    END IF;
END $$;

COMMENT ON COLUMN parametre_sauvegarde.retention_ged_jours IS 'Jours de conservation de la copie de la base dans la GED (NULL : réglage du serveur, 7 par défaut)';
COMMENT ON COLUMN parametre_sauvegarde.nettoyage_ged_annees IS 'Documents de la GED supprimés au-delà de ce nombre d''années (NULL : jamais)';
