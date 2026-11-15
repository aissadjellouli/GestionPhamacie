-- =====================================================================
-- Droit « Valider les doublons d'import » (2026-11-15)
--   * nouveau droit « reprise.doublons » : la liste des doublons d'import
--     de patients et les décisions (Administration › Doublons d'import,
--     script 092) l'exigent (avant : « credit.accorder », que les vendeurs ont) ;
--   * titulaire et pharmacien assistant par défaut, pas le vendeur ;
--     à donner à un employé dans la matrice des droits
--     (Administration › Utilisateurs et droits) ;
--   * décision d'administration : jamais autorisable par le code comptoir
--     d'un collègue.
-- Idempotent. A exécuter après 092_import_patients_doublons.sql.
-- =====================================================================

-- Le droit et ses attributions de départ ne sont posés qu'à la première exécution :
-- une réexécution n'annule jamais un choix fait ensuite par le titulaire.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM permission WHERE code = 'reprise.doublons') THEN
        INSERT INTO permission (code, module, libelle, sensible)
        VALUES ('reprise.doublons', 'Administration', 'Valider les doublons d''import', true)
        ON CONFLICT DO NOTHING;

        -- Les droits du titulaire sont protégés par un déclencheur : levé le temps de cette transaction
        ALTER TABLE role_permission DISABLE TRIGGER trg_proteger_role_systeme;
        INSERT INTO role_permission (role_id, permission_code)
        SELECT r.id, 'reprise.doublons' FROM role r WHERE r.code IN ('titulaire', 'assistant')
        ON CONFLICT DO NOTHING;
        ALTER TABLE role_permission ENABLE TRIGGER trg_proteger_role_systeme;
    END IF;
END $$;
