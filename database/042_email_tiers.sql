-- =====================================================================
-- Adresse e-mail des grossistes, des entreprises clientes et des patients
--   * fournisseur.email, client_facture.email, patient.email : information
--     essentielle mais facultative. Saisie à la création (patient à la
--     caisse, entreprise dans l'éditeur de facture) ou depuis la fiche
--     contact (« Modifier les coordonnées ») ; la fiche la signale
--     « à compléter » tant qu'elle manque.
-- Les lignes existantes restent sans e-mail.
-- Idempotent. A exécuter après 041_contacts.sql.
-- =====================================================================

ALTER TABLE fournisseur ADD COLUMN IF NOT EXISTS email text;
ALTER TABLE client_facture ADD COLUMN IF NOT EXISTS email text;
ALTER TABLE patient ADD COLUMN IF NOT EXISTS email text;
COMMENT ON COLUMN fournisseur.email IS 'Adresse e-mail du grossiste (facultative)';
COMMENT ON COLUMN client_facture.email IS 'Adresse e-mail de l''entreprise cliente (facultative)';
COMMENT ON COLUMN patient.email IS 'Adresse e-mail du patient (facultative)';
