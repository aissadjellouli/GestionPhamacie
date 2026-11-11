-- =====================================================================
-- Coordonnées des employés et des organismes
--   * utilisateur.telephone, utilisateur.email : coordonnées de l'employé,
--     saisies dans « Utilisateurs et droits », affichées dans sa fiche
--     contact (au titulaire et à l'employé lui-même) ;
--   * organisme.telephone, organisme.email : coordonnées de l'organisme
--     (CNAS, CASNOS…), saisies dans « Factures › Organismes ».
-- Colonnes facultatives : les lignes existantes restent sans coordonnées.
-- Idempotent. A exécuter après 040_facture_echeance.sql.
-- =====================================================================

ALTER TABLE utilisateur ADD COLUMN IF NOT EXISTS telephone text;
ALTER TABLE utilisateur ADD COLUMN IF NOT EXISTS email text;
COMMENT ON COLUMN utilisateur.telephone IS 'Téléphone de l''employé (facultatif)';
COMMENT ON COLUMN utilisateur.email IS 'Adresse e-mail de l''employé (facultative)';

ALTER TABLE organisme ADD COLUMN IF NOT EXISTS telephone text;
ALTER TABLE organisme ADD COLUMN IF NOT EXISTS email text;
COMMENT ON COLUMN organisme.telephone IS 'Téléphone de l''organisme (facultatif)';
COMMENT ON COLUMN organisme.email IS 'Adresse e-mail de l''organisme (facultative)';
