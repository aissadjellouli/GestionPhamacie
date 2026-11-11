-- =====================================================================
-- Reprise de données par le super admin
--   * import.session.lance_par : qui a lancé l'extraction (super admin) ;
--   * import.session.charge_le, charge_par : date du chargement définitif
--     et qui l'a lancé ; la validation (code reçu par SMS ou e-mail) est
--     inscrite au journal des actions sensibles.
--   Les données chargées (réception de reprise, mouvements de stock,
--   registre des psychotropes) restent attribuées au pharmacien titulaire.
-- Idempotent. A exécuter après 051_rappel_lot_deja_bloque.sql.
-- =====================================================================

ALTER TABLE import.session ADD COLUMN IF NOT EXISTS lance_par text;
ALTER TABLE import.session ADD COLUMN IF NOT EXISTS charge_le timestamptz;
ALTER TABLE import.session ADD COLUMN IF NOT EXISTS charge_par text;

COMMENT ON COLUMN import.session.lance_par IS 'Qui a lancé l''extraction (ex. super admin et son identifiant)';
COMMENT ON COLUMN import.session.charge_le IS 'Date du chargement définitif dans la base';
COMMENT ON COLUMN import.session.charge_par IS 'Qui a lancé le chargement définitif';
