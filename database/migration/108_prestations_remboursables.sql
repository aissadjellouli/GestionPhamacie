-- =====================================================================
-- Prestations remboursables par un organisme (décision du titulaire,
-- 2026-11-15)
--   type_prestation.remboursable : la prestation est prise en charge en
--   tiers payant, comme un produit : part de l'organisme = taux ×
--   min(prix, tarif de référence) × quantité ; non remboursable : 0
--   (comportement d'avant). Réglé dans Administration › Prestations.
--   type_prestation.tarif_reference : tarif de référence (> 0)
--   obligatoire pour une prestation remboursable, NULL sinon.
--   ligne_vente : la contrainte ck_ligne_vente_produit_ou_prestation
--   n'impose plus montant_organisme = 0 sur une ligne de prestation
--   (reste >= 0 par la contrainte de la colonne d'origine).
--   Aucune donnée existante n'est modifiée : les prestations existantes
--   restent non remboursables (valeur par défaut false, tarif NULL) ;
--   les lignes de vente existantes (part organisme 0) restent valides.
-- Idempotent. À exécuter après 107.
-- =====================================================================

ALTER TABLE type_prestation ADD COLUMN IF NOT EXISTS remboursable boolean NOT NULL DEFAULT false;
ALTER TABLE type_prestation ADD COLUMN IF NOT EXISTS tarif_reference numeric(12,2);
COMMENT ON COLUMN type_prestation.remboursable IS 'Prestation prise en charge par l''organisme en tiers payant (script 108) : taux × min(prix, tarif de référence)';
COMMENT ON COLUMN type_prestation.tarif_reference IS 'Tarif de référence du remboursement (script 108) ; obligatoire (> 0) si remboursable, NULL sinon';

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_type_prestation_remboursable') THEN
        ALTER TABLE type_prestation ADD CONSTRAINT ck_type_prestation_remboursable CHECK (
            (remboursable AND tarif_reference IS NOT NULL AND tarif_reference > 0)
            OR (NOT remboursable AND tarif_reference IS NULL));
    END IF;

    -- Ligne de prestation : la part de l'organisme n'est plus forcément 0 (prestation remboursable)
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_ligne_vente_produit_ou_prestation'
               AND pg_get_constraintdef(oid) LIKE '%montant_organisme = %') THEN
        ALTER TABLE ligne_vente DROP CONSTRAINT ck_ligne_vente_produit_ou_prestation;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_ligne_vente_produit_ou_prestation') THEN
        ALTER TABLE ligne_vente ADD CONSTRAINT ck_ligne_vente_produit_ou_prestation CHECK (
            (produit_id IS NOT NULL AND lot_id IS NOT NULL AND type_prestation_id IS NULL AND libelle IS NULL AND taux_tva IS NULL)
            OR (type_prestation_id IS NOT NULL AND produit_id IS NULL AND lot_id IS NULL
                AND libelle IS NOT NULL AND taux_tva IS NOT NULL AND taux_tva >= 0 AND taux_tva <= 100
                AND shp = 0 AND prix_chifa IS NULL AND raison_lot IS NULL));
    END IF;
END $$;
