-- =====================================================================
-- Caisse partagée par poste et code comptoir
--   * une seule caisse ouverte par poste (un tiroir) ; plusieurs employés
--     y vendent, chaque vente reste à leur nom (vente.utilisateur_id) ;
--   * relève facultative : la session est clôturée avec un comptage et une
--     nouvelle s'ouvre aussitôt (releve_de = session précédente) ;
--   * code comptoir (4 à 6 chiffres, haché) pour changer d'utilisateur sans
--     fermer la caisse ; 5 erreurs bloquent le code quelques minutes.
-- Idempotent. A exécuter après 021_prix_boite.sql.
-- =====================================================================

-- Avant de changer la règle : aucun poste ne doit avoir deux caisses ouvertes
DO $$
DECLARE
    doublons text;
BEGIN
    SELECT string_agg(format('%s (%s caisses)', poste, n), ', ') INTO doublons
    FROM (SELECT poste, count(*) AS n FROM session_caisse WHERE fermee_le IS NULL GROUP BY poste HAVING count(*) > 1) d;
    IF doublons IS NOT NULL THEN
        RAISE EXCEPTION 'Clôturez d''abord les caisses en double sur : %', doublons;
    END IF;
END $$;

DROP INDEX IF EXISTS ux_session_ouverte;
CREATE UNIQUE INDEX IF NOT EXISTS ux_session_poste_ouverte ON session_caisse (poste) WHERE fermee_le IS NULL;

ALTER TABLE session_caisse ADD COLUMN IF NOT EXISTS releve_de bigint REFERENCES session_caisse(id);
COMMENT ON COLUMN session_caisse.utilisateur_id IS 'Employé qui a ouvert la caisse du poste (les ventes gardent leur vendeur)';
COMMENT ON COLUMN session_caisse.releve_de IS 'Session clôturée par une relève, juste avant celle-ci';

ALTER TABLE utilisateur ADD COLUMN IF NOT EXISTS code_comptoir_essais int NOT NULL DEFAULT 0;
ALTER TABLE utilisateur ADD COLUMN IF NOT EXISTS code_comptoir_bloque_jusqu timestamptz;
-- Le code lui-même est pin_hash (prévu dans 006_droits_utilisateurs.sql, haché par l'application)
COMMENT ON COLUMN utilisateur.pin_hash IS 'Code comptoir (haché) pour reprendre la caisse du poste ; accès limité au comptoir';
