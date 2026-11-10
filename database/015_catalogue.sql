-- =====================================================================
-- Création d'un produit absent du catalogue (fiche Stock ou réception).
-- Nouveau droit « catalogue.creer » : titulaire et pharmacien assistant
-- par défaut, réglable ensuite dans la matrice des droits.
-- A exécuter après 014_sauvegardes.sql. Idempotent.
-- =====================================================================

-- Le droit et ses attributions de départ ne sont posés qu'à la première exécution :
-- une réexécution n'annule jamais un choix fait ensuite par le titulaire.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM permission WHERE code = 'catalogue.creer') THEN
        INSERT INTO permission (code, module, libelle, sensible)
        VALUES ('catalogue.creer', 'Stock', 'Créer un produit au catalogue', true);

        -- Les droits du titulaire sont protégés par un déclencheur : levé le temps de cette transaction
        ALTER TABLE role_permission DISABLE TRIGGER trg_proteger_role_systeme;
        INSERT INTO role_permission (role_id, permission_code)
        SELECT r.id, 'catalogue.creer' FROM role r WHERE r.code IN ('titulaire', 'assistant')
        ON CONFLICT DO NOTHING;
        ALTER TABLE role_permission ENABLE TRIGGER trg_proteger_role_systeme;
    END IF;
END $$;

-- Doublons refusés aussi par la base : même nom, dosage et forme (sans tenir compte de la casse)
CREATE UNIQUE INDEX IF NOT EXISTS ux_produit_nom_dosage_forme
    ON produit (lower(nom_commercial), lower(COALESCE(dosage, '')), lower(COALESCE(forme, '')));
