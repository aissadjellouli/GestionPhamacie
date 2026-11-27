-- =====================================================================
-- 174 -- Postes autorisés : preuve toujours exigée
--   Décision du titulaire du 2026-11-27 : « Chaque poste doit être autorisé avec son code, sinon pas de connexion, et un poste non autorisé
--   n'entre pas dans le total / maximum autorisé. » Les modes « inactif » et « transition » ne s'appliquent plus : l'API se comporte partout
--   comme en mode « exige » et ne lit plus parametre_postes.mode (une valeur changée à la main dans la base n'ouvre rien).
--   * parametre_postes.mode : passé à 'exige' (ligne id = 1) ; valeur par défaut 'exige' ; commentaire de colonne « plus lu depuis 174 ».
--     La colonne et sa contrainte (inactif / transition / exige) sont gardées pour les versions précédentes de l'API.
--   Données : la seule ligne de parametre_postes (mode). Aucun poste autorisé, aucune connexion ni aucun journal touchés.
--   Retour arrière : database/migration/retour/174_postes_preuve_exigee_retour.sql (remet le défaut 'transition' ; la valeur reste 'exige').
-- Idempotent et rejouable sur une base plus récente.
-- A exécuter après 173_remise_nulle_hors_controle.sql.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

ALTER TABLE parametre_postes ALTER COLUMN mode SET DEFAULT 'exige';
INSERT INTO parametre_postes (id, mode) VALUES (1, 'exige') ON CONFLICT (id) DO NOTHING;
UPDATE parametre_postes SET mode = 'exige' WHERE id = 1 AND mode IS DISTINCT FROM 'exige';
COMMENT ON COLUMN parametre_postes.mode IS 'Plus lu depuis 174 : preuve toujours exigée (décision du titulaire du 2026-11-27) ; gardé pour les versions précédentes de l''API.';
