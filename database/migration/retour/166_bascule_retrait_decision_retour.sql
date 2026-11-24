-- =====================================================================
-- Retour arrière du script 166 (mise hors jeu du serveur 1 et décisions de promotion)
--   Refusé (rien n'est modifié) si un écart de nature « retrait » ou une décision de promotion existe : ce sont des traces de bascule
--   à garder. Sinon : supprime la table bascule_decision, sa fonction et son classement, remet timeline_retenue obligatoire et la
--   contrainte du script 163, retire la colonne nature.
--   Rejouable sans erreur. A lancer seul, hors de la suite numérotée : ce dossier n'est jamais lu par les outils de mise à jour.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

DO $retour$ BEGIN
    -- Deux IF imbriqués : la requête qui cite « nature » n'est analysée que si la colonne existe (second passage du retour)
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'serveur_ecarte' AND column_name = 'nature') THEN
        IF EXISTS (SELECT 1 FROM serveur_ecarte WHERE nature = 'retrait') THEN
            RAISE EXCEPTION 'Retour du script 166 refusé : un écart « retrait » existe (trace de bascule à garder). Rien n''a été modifié.';
        END IF;
    END IF;
    IF to_regclass('bascule_decision') IS NOT NULL THEN
        IF EXISTS (SELECT 1 FROM bascule_decision) THEN
            RAISE EXCEPTION 'Retour du script 166 refusé : une décision de promotion existe (trace de bascule à garder). Rien n''a été modifié.';
        END IF;
    END IF;
END $retour$;

DELETE FROM sync_table WHERE nom_table = 'bascule_decision';
DROP TABLE IF EXISTS bascule_decision;
DROP FUNCTION IF EXISTS bascule_decision_proteger();

ALTER TABLE serveur_ecarte DROP CONSTRAINT IF EXISTS ck_serveur_ecarte_timelines;
ALTER TABLE serveur_ecarte ALTER COLUMN timeline_retenue SET NOT NULL;
ALTER TABLE serveur_ecarte ADD CONSTRAINT ck_serveur_ecarte_timelines CHECK (timeline_retenue > timeline_locale);
ALTER TABLE serveur_ecarte DROP CONSTRAINT IF EXISTS ck_serveur_ecarte_nature;
ALTER TABLE serveur_ecarte DROP COLUMN IF EXISTS nature;
