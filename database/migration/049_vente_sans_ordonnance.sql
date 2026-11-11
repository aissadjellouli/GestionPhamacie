-- =====================================================================
-- Vente sans ordonnance d'un médicament de liste I ou II
--   * la caisse demande une ordonnance (médecin prescripteur) pour un
--     médicament de liste I ou II ;
--   * nouveau droit « caisse.vendre_sans_ordonnance » : le vendre sans
--     ordonnance (vente inscrite au journal). Titulaire et pharmacien
--     assistant par défaut, à donner au rôle vendeur dans la matrice des
--     droits (Administration › Utilisateurs et droits) ; sans ce droit,
--     la vente peut être autorisée sur place par le code comptoir d'un
--     employé qui l'a ;
--   * psychotropes et stupéfiants : l'ordonnance reste obligatoire pour
--     tous, ce droit ne s'y applique pas.
-- Idempotent. A exécuter après 048_reprise_corrections.sql.
-- =====================================================================

-- Le droit et ses attributions de départ ne sont posés qu'à la première exécution :
-- une réexécution n'annule jamais un choix fait ensuite par le titulaire.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM permission WHERE code = 'caisse.vendre_sans_ordonnance') THEN
        INSERT INTO permission (code, module, libelle, sensible)
        VALUES ('caisse.vendre_sans_ordonnance', 'Caisse', 'Vendre sans ordonnance un médicament de liste I ou II', true);

        -- Les droits du titulaire sont protégés par un déclencheur : levé le temps de cette transaction
        ALTER TABLE role_permission DISABLE TRIGGER trg_proteger_role_systeme;
        INSERT INTO role_permission (role_id, permission_code)
        SELECT r.id, 'caisse.vendre_sans_ordonnance' FROM role r WHERE r.code IN ('titulaire', 'assistant')
        ON CONFLICT DO NOTHING;
        ALTER TABLE role_permission ENABLE TRIGGER trg_proteger_role_systeme;
    END IF;
END $$;
