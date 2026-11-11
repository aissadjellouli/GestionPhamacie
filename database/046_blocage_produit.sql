-- =====================================================================
-- Blocage d'un produit à l'achat, à la vente, ou les deux
--   * produit.bloque_achat : plus de commande, ni de réception, ni de
--     suggestion d'achat pour ce produit ;
--   * produit.bloque_vente : la caisse refuse de le vendre ;
--   * produit.motif_blocage : raison indiquée (rappel, litige, arrêt…).
-- Nouveau droit « produits.bloquer » : titulaire et pharmacien assistant
-- par défaut, réglable ensuite dans la matrice des droits.
-- Idempotent. A exécuter après 045_prix_lot_ug_categories.sql.
-- =====================================================================

ALTER TABLE produit ADD COLUMN IF NOT EXISTS bloque_achat boolean NOT NULL DEFAULT false;
ALTER TABLE produit ADD COLUMN IF NOT EXISTS bloque_vente boolean NOT NULL DEFAULT false;
ALTER TABLE produit ADD COLUMN IF NOT EXISTS motif_blocage text;
COMMENT ON COLUMN produit.bloque_achat IS 'Produit bloqué à l''achat : ni commande, ni réception, ni suggestion';
COMMENT ON COLUMN produit.bloque_vente IS 'Produit bloqué à la vente : la caisse refuse de le vendre';
COMMENT ON COLUMN produit.motif_blocage IS 'Raison du blocage à l''achat ou à la vente';

-- Le droit et ses attributions de départ ne sont posés qu'à la première exécution :
-- une réexécution n'annule jamais un choix fait ensuite par le titulaire.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM permission WHERE code = 'produits.bloquer') THEN
        INSERT INTO permission (code, module, libelle, sensible)
        VALUES ('produits.bloquer', 'Stock', 'Bloquer un produit à l''achat ou à la vente', true);

        -- Les droits du titulaire sont protégés par un déclencheur : levé le temps de cette transaction
        ALTER TABLE role_permission DISABLE TRIGGER trg_proteger_role_systeme;
        INSERT INTO role_permission (role_id, permission_code)
        SELECT r.id, 'produits.bloquer' FROM role r WHERE r.code IN ('titulaire', 'assistant')
        ON CONFLICT DO NOTHING;
        ALTER TABLE role_permission ENABLE TRIGGER trg_proteger_role_systeme;
    END IF;
END $$;
