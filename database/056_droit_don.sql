-- =====================================================================
-- Échantillons, gratuités et dons aux nécessiteux
--   * nouveau droit « stock.donner » : sortir du stock des boîtes données
--     (échantillon, gratuité, don à un nécessiteux ; à qui et pourquoi,
--     obligatoire ; jamais un produit sous registre) ;
--   * titulaire et pharmacien assistant par défaut, à donner à un vendeur
--     dans la matrice des droits (Administration › Utilisateurs et droits) ;
--   * chaque don reste surveillé (seuil des échantillons, alerte au
--     titulaire) et inscrit au journal.
-- Idempotent. A exécuter après 055_surveillance_stock.sql.
-- =====================================================================

-- Le droit et ses attributions de départ ne sont posés qu'à la première exécution :
-- une réexécution n'annule jamais un choix fait ensuite par le titulaire.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM permission WHERE code = 'stock.donner') THEN
        INSERT INTO permission (code, module, libelle, sensible)
        VALUES ('stock.donner', 'Stock', 'Donner un échantillon ou une gratuité (don à un nécessiteux)', true);

        -- Les droits du titulaire sont protégés par un déclencheur : levé le temps de cette transaction
        ALTER TABLE role_permission DISABLE TRIGGER trg_proteger_role_systeme;
        INSERT INTO role_permission (role_id, permission_code)
        SELECT r.id, 'stock.donner' FROM role r WHERE r.code IN ('titulaire', 'assistant')
        ON CONFLICT DO NOTHING;
        ALTER TABLE role_permission ENABLE TRIGGER trg_proteger_role_systeme;
    END IF;
END $$;
