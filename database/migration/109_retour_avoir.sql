-- =====================================================================
-- Retour client d'une vente payée en partie par avoir (décision du
-- titulaire, 2026-11-15, défaut D2 de la recette)
--   Le remboursement d'un retour recrédite d'abord l'avoir du patient, à
--   hauteur de ce qui a été payé par avoir et pas encore recrédité ; le
--   reste suit le mode d'origine de la vente (espèces, carte, chèque,
--   crédit).
--   retour_client.mode : nouvelle valeur 'avoir' (part recréditée en
--   avoir : ne sort ni de la caisse ni de la banque, mais compte dans le
--   « déjà rendu » de la vente).
--   avoir_mouvement.origine : nouvelle valeur 'retour_client' (crédit,
--   vente obligatoire).
--   Aucune donnée existante n'est modifiée.
-- Idempotent (contraintes recréées seulement si la valeur manque).
-- À exécuter après 108.
-- =====================================================================

DO $garde$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'retour_client_mode_check'
                   AND pg_get_constraintdef(oid) LIKE '%''avoir''%') THEN
        ALTER TABLE retour_client DROP CONSTRAINT IF EXISTS retour_client_mode_check;
        ALTER TABLE retour_client ADD CONSTRAINT retour_client_mode_check
            CHECK (mode IN ('especes', 'carte', 'cheque', 'credit', 'avoir'));
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_avoir_mouvement_origine'
                   AND pg_get_constraintdef(oid) LIKE '%''retour_client''%') THEN
        ALTER TABLE avoir_mouvement DROP CONSTRAINT IF EXISTS ck_avoir_mouvement_origine;
        ALTER TABLE avoir_mouvement ADD CONSTRAINT ck_avoir_mouvement_origine
            CHECK (origine IN ('annulation_vente', 'trop_paye_regularisation', 'saisie', 'paiement', 'remboursement', 'retour_client'));
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_avoir_mouvement_sens_origine'
                   AND pg_get_constraintdef(oid) LIKE '%''retour_client''%') THEN
        ALTER TABLE avoir_mouvement DROP CONSTRAINT IF EXISTS ck_avoir_mouvement_sens_origine;
        ALTER TABLE avoir_mouvement ADD CONSTRAINT ck_avoir_mouvement_sens_origine CHECK (
            (origine IN ('annulation_vente', 'trop_paye_regularisation', 'saisie', 'retour_client') AND sens = 'credit')
            OR (origine IN ('paiement', 'remboursement') AND sens = 'debit'));
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_avoir_mouvement_liens'
                   AND pg_get_constraintdef(oid) LIKE '%''retour_client''%') THEN
        ALTER TABLE avoir_mouvement DROP CONSTRAINT IF EXISTS ck_avoir_mouvement_liens;
        ALTER TABLE avoir_mouvement ADD CONSTRAINT ck_avoir_mouvement_liens CHECK (
            (origine NOT IN ('annulation_vente', 'trop_paye_regularisation', 'retour_client') OR vente_id IS NOT NULL)
            AND (origine <> 'paiement' OR paiement_id IS NOT NULL)
            AND (origine <> 'remboursement' OR mouvement_tiroir_id IS NOT NULL));
    END IF;
END
$garde$;
