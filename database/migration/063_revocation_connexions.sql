-- =====================================================================
-- Révocation des connexions en cours
--   * utilisateur.jetons_revoques_le : toute connexion (jeton) ouverte avant
--     cette date est refusée par l'API à l'appel suivant ;
--   * posée automatiquement quand le mot de passe change (par l'employé ou
--     réinitialisé par l'administrateur), quand le compte est désactivé ou
--     remis en « mot de passe à changer », et quand l'administrateur efface
--     le code comptoir (code oublié ou divulgué) ;
--   * posée pour tous les comptes après une restauration de la base.
-- Idempotent. A exécuter après 062_vues_alertes_colonnes_nommees.sql.
-- =====================================================================

SET client_encoding = 'UTF8';

ALTER TABLE utilisateur ADD COLUMN IF NOT EXISTS jetons_revoques_le timestamptz;
COMMENT ON COLUMN utilisateur.jetons_revoques_le IS 'Connexions ouvertes avant cette date refusées (mot de passe changé, compte désactivé, code effacé, restauration)';

CREATE OR REPLACE FUNCTION revoquer_connexions_utilisateur()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.mot_de_passe_hash IS DISTINCT FROM OLD.mot_de_passe_hash
       OR (OLD.actif AND NOT NEW.actif)
       OR (NEW.doit_changer_mot_de_passe AND NOT OLD.doit_changer_mot_de_passe)
       OR (OLD.pin_hash IS NOT NULL AND NEW.pin_hash IS NULL)
    THEN
        NEW.jetons_revoques_le := now();
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_revoquer_connexions ON utilisateur;
CREATE TRIGGER trg_revoquer_connexions BEFORE UPDATE ON utilisateur
    FOR EACH ROW EXECUTE FUNCTION revoquer_connexions_utilisateur();
