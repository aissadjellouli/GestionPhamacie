-- =====================================================================
-- Remboursement du trop-payé d'une régularisation Chifa (recette du
-- 2026-11-15, D4)
--   Au rattachement d'un volet Chifa où la caisse paiera PLUS que la part
--   enregistrée (script 097), le trop-payé du patient lui est rendu : sa
--   dette est d'abord diminuée, le reste est « à rendre ». Ce montant
--   « à rendre » est désormais rendu par une sortie du tiroir
--   « Remboursement d'un client » liée à la régularisation :
--   mouvement_tiroir.regularisation_id. Le montant est rendu tant qu'un
--   bon lié existe sans écriture inverse (corrige_id) ; corrigé, il
--   redevient à rendre.
-- Aucune donnée supprimée ni modifiée (colonne ajoutée, vide).
-- Idempotent. A exécuter après 098_annulations_regularisation_reglement.sql.
-- =====================================================================

ALTER TABLE mouvement_tiroir ADD COLUMN IF NOT EXISTS regularisation_id bigint REFERENCES regularisation_part_organisme (id);
COMMENT ON COLUMN mouvement_tiroir.regularisation_id IS 'Remboursement du trop-payé « à rendre » de cette régularisation Chifa (script 099) ; rendu tant que le bon n''est pas corrigé';
CREATE INDEX IF NOT EXISTS ix_mouvement_tiroir_regularisation ON mouvement_tiroir (regularisation_id) WHERE regularisation_id IS NOT NULL;

DO $$
BEGIN
    -- Seul un remboursement d'un client (détail « patient ») rend un trop-payé
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_mouvement_tiroir_regularisation') THEN
        ALTER TABLE mouvement_tiroir ADD CONSTRAINT ck_mouvement_tiroir_regularisation
            CHECK (regularisation_id IS NULL OR (detail = 'patient' AND sens = 'sortie' AND corrige_id IS NULL));
    END IF;
END $$;
