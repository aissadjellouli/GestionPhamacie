-- =====================================================================
-- Vérification d'un destinataire des notifications par un code
--   * « Vérifier » envoie au destinataire un code à 6 chiffres (e-mail, et
--     SMS si un modem est réglé), valable 15 minutes ; le titulaire saisit
--     dans le logiciel le code reçu : juste, l'adresse est vérifiée.
--   * Seul le haché du code est gardé ; 5 codes faux : il faut en
--     redemander un.
-- Idempotent. A exécuter après 067_controle_interne.sql.
-- =====================================================================

SET client_encoding = 'UTF8';

ALTER TABLE destinataire_notification ADD COLUMN IF NOT EXISTS code_verification_hash text;
ALTER TABLE destinataire_notification ADD COLUMN IF NOT EXISTS code_verification_expire timestamptz;
ALTER TABLE destinataire_notification ADD COLUMN IF NOT EXISTS code_verification_essais integer NOT NULL DEFAULT 0;
COMMENT ON COLUMN destinataire_notification.code_verification_hash IS 'Code de vérification envoyé au destinataire (haché), effacé une fois vérifié';
