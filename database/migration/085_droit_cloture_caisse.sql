-- =====================================================================
-- Droit « Faire la relève et clôturer la caisse » (2026-11-15)
--   * nouveau droit « caisse.cloturer » : la relève et la clôture de la
--     caisse l'exigent (avant : « caisse.vendre » suffisait) ;
--   * titulaire et pharmacien assistant par défaut, pas le vendeur ;
--     à donner à un employé dans la matrice des droits
--     (Administration › Utilisateurs et droits) ;
--   * un vendeur peut faire la relève ou clôturer avec le code comptoir
--     d'un employé qui a ce droit (« Faire autoriser », inscrit au journal).
-- Idempotent. A exécuter après 084_avoir_dette_patient.sql.
-- =====================================================================

-- Le droit et ses attributions de départ ne sont posés qu'à la première exécution :
-- une réexécution n'annule jamais un choix fait ensuite par le titulaire.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM permission WHERE code = 'caisse.cloturer') THEN
        INSERT INTO permission (code, module, libelle, sensible)
        VALUES ('caisse.cloturer', 'Caisse', 'Faire la relève et clôturer la caisse', true)
        ON CONFLICT DO NOTHING;

        -- Les droits du titulaire sont protégés par un déclencheur : levé le temps de cette transaction
        ALTER TABLE role_permission DISABLE TRIGGER trg_proteger_role_systeme;
        INSERT INTO role_permission (role_id, permission_code)
        SELECT r.id, 'caisse.cloturer' FROM role r WHERE r.code IN ('titulaire', 'assistant')
        ON CONFLICT DO NOTHING;
        ALTER TABLE role_permission ENABLE TRIGGER trg_proteger_role_systeme;
    END IF;
END $$;
