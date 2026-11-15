-- =====================================================================
-- Contrôle de la fin de droits Chifa (2026-11-15)
--   parametre_caisse.jours_alerte_fin_droit : nombre de jours (0 à 365,
--   défaut 30) avant la date de fin de droits d'un patient CNAS/CASNOS à
--   partir duquel la caisse affiche « Droits Chifa jusqu'au … : prévenir
--   le patient » (orange) ; réglable dans Administration › Caisse.
--   Aucune vente n'est bloquée.
-- Aucune donnée supprimée ni modifiée (colonne ajoutée avec sa valeur
-- par défaut). Idempotent. A exécuter après 103_stock_shp_correction_facture.sql.
-- =====================================================================

ALTER TABLE parametre_caisse ADD COLUMN IF NOT EXISTS jours_alerte_fin_droit integer NOT NULL DEFAULT 30;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_parametre_caisse_jours_alerte_fin_droit') THEN
        ALTER TABLE parametre_caisse ADD CONSTRAINT ck_parametre_caisse_jours_alerte_fin_droit
            CHECK (jours_alerte_fin_droit BETWEEN 0 AND 365);
    END IF;
END $$;

COMMENT ON COLUMN parametre_caisse.jours_alerte_fin_droit IS 'Jours avant la fin des droits Chifa à partir desquels la caisse prévient (orange) ; 0 à 365, défaut 30 (script 107)';
