-- =====================================================================
-- Inventaire complet (annuel) : une campagne qui regroupe l'inventaire
-- de chaque rayon (mêmes règles que l'inventaire tournant : comptage à
-- l'aveugle, petits écarts corrigés, gros écarts validés par le titulaire).
--   * mode choisi par le pharmacien au lancement : « ouverte » (les
--     ventes continuent ; chaque lot est relevé au moment de son comptage)
--     ou « fermee » (la caisse refuse les ventes jusqu'à la clôture) ;
--   * les lots sans emplacement sont comptés dans le rayon
--     « (Sans emplacement) » ;
--   * clôture : tous les rayons comptés et tous les gros écarts décidés ;
--     l'état d'inventaire (stock par lot valorisé au prix d'achat HT) est
--     figé dans campagne_inventaire_ligne.
-- Idempotent. A exécuter après 031_facture_client.sql.
-- =====================================================================

CREATE TABLE IF NOT EXISTS campagne_inventaire (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    mode            text          NOT NULL CHECK (mode IN ('ouverte', 'fermee')),
    statut          text          NOT NULL DEFAULT 'en_cours' CHECK (statut IN ('en_cours', 'cloturee', 'annulee')),
    commence_le     timestamptz   NOT NULL DEFAULT now(),
    commence_par    bigint        NOT NULL REFERENCES utilisateur(id),
    cloture_le      timestamptz,
    cloture_par     bigint        REFERENCES utilisateur(id),
    nombre_lots     int,
    nombre_boites   int,
    valeur_stock    numeric(14,2),     -- au prix d'achat HT, à la clôture
    commentaire     text
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_campagne_en_cours ON campagne_inventaire ((true)) WHERE statut = 'en_cours';

ALTER TABLE inventaire ADD COLUMN IF NOT EXISTS campagne_id bigint REFERENCES campagne_inventaire(id);
CREATE INDEX IF NOT EXISTS idx_inventaire_campagne ON inventaire (campagne_id);

-- État d'inventaire figé à la clôture (une ligne par lot en stock)
CREATE TABLE IF NOT EXISTS campagne_inventaire_ligne (
    campagne_id      bigint        NOT NULL REFERENCES campagne_inventaire(id),
    lot_id           bigint        NOT NULL REFERENCES lot(id),
    produit          text          NOT NULL,
    numero_lot       text          NOT NULL,
    date_peremption  date          NOT NULL,
    rayon            text          NOT NULL,
    quantite         int           NOT NULL,
    prix_achat_ht    numeric(12,2) NOT NULL,
    valeur           numeric(14,2) NOT NULL,
    PRIMARY KEY (campagne_id, lot_id)
);
