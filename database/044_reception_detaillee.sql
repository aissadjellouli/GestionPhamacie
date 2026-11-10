-- =====================================================================
-- Réception détaillée comme la facture du grossiste
--   * reception : montant HT avant remise, remise, TVA, timbre et total SHP
--     (montant_ttc reste le net à payer, qui fait la dette du grossiste :
--     net HT + TVA + timbre). Le SHP (supplément d'honoraire du pharmacien,
--     par boîte selon la liste du médicament) est à part : il ne fait pas
--     partie du montant dû au grossiste ;
--   * reception_ligne : une ligne par lot reçu, telle qu'écrite sur la
--     facture : prix d'achat HT avant remise, remise %, PPA facturé, SHP par
--     boîte, taux de TVA. Le coût du stock (lot, mouvement, boîtes) est le
--     prix net de remise.
-- Les réceptions déjà enregistrées gardent leur total (montant_ttc) et n'ont
-- pas de lignes détaillées.
-- Idempotent. A exécuter après 043_historique_factures.sql.
-- =====================================================================

ALTER TABLE reception ADD COLUMN IF NOT EXISTS montant_ht numeric(12,2);
ALTER TABLE reception ADD COLUMN IF NOT EXISTS remise numeric(12,2) NOT NULL DEFAULT 0;
ALTER TABLE reception ADD COLUMN IF NOT EXISTS montant_tva numeric(12,2);
ALTER TABLE reception ADD COLUMN IF NOT EXISTS timbre numeric(12,2) NOT NULL DEFAULT 0;
ALTER TABLE reception ADD COLUMN IF NOT EXISTS shp_total numeric(12,2) NOT NULL DEFAULT 0;
COMMENT ON COLUMN reception.montant_ht IS 'Total HT avant remise (null : réception enregistrée avant le détail)';
COMMENT ON COLUMN reception.remise IS 'Remise accordée par le grossiste sur la facture (DA)';
COMMENT ON COLUMN reception.montant_tva IS 'TVA de la facture (null : réception enregistrée avant le détail)';
COMMENT ON COLUMN reception.timbre IS 'Droit de timbre de la facture (DA)';
COMMENT ON COLUMN reception.shp_total IS 'Total du supplément d''honoraire du pharmacien (SHP) indiqué sur la facture ; non dû au grossiste';
COMMENT ON COLUMN reception.montant_ttc IS 'Net à payer au grossiste : net HT + TVA + timbre';

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_reception_montants') THEN
        ALTER TABLE reception ADD CONSTRAINT ck_reception_montants
            CHECK (remise >= 0 AND timbre >= 0 AND shp_total >= 0 AND (montant_ht IS NULL OR montant_ht >= 0) AND (montant_tva IS NULL OR montant_tva >= 0));
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS reception_ligne (
    id              bigserial     PRIMARY KEY,
    reception_id    bigint        NOT NULL REFERENCES reception(id),
    lot_id          bigint        NOT NULL REFERENCES lot(id),
    produit_id      bigint        NOT NULL REFERENCES produit(id),
    quantite        int           NOT NULL CHECK (quantite > 0),
    prix_achat_ht   numeric(12,2) NOT NULL CHECK (prix_achat_ht >= 0),          -- avant remise, comme sur la facture
    remise_pct      numeric(5,2)  NOT NULL DEFAULT 0 CHECK (remise_pct >= 0 AND remise_pct <= 100),
    prix_net_ht     numeric(12,2) NOT NULL CHECK (prix_net_ht >= 0),            -- après remise : coût du stock
    ppa             numeric(12,2) CHECK (ppa IS NULL OR ppa >= 0),              -- prix public algérien facturé
    shp             numeric(8,2)  NOT NULL DEFAULT 0 CHECK (shp >= 0),          -- par boîte
    taux_tva        numeric(5,2)  NOT NULL DEFAULT 0
);
COMMENT ON TABLE reception_ligne IS 'Lignes d''une réception telles qu''écrites sur la facture du grossiste (044)';
CREATE INDEX IF NOT EXISTS idx_reception_ligne_reception ON reception_ligne (reception_id);
CREATE INDEX IF NOT EXISTS idx_reception_ligne_lot ON reception_ligne (lot_id);
