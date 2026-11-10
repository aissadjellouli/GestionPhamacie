-- =====================================================================
-- Scanner mobile : boîtes scannées au déballage d'une livraison
--   Le préparateur scanne les boîtes sur le téléphone ; le poste les
--   importe dans sa réception, qui reste validée sur le poste.
-- Idempotent. A exécuter après 018_lot_unique.sql.
-- =====================================================================

CREATE TABLE IF NOT EXISTS scan_reception (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    utilisateur_id  bigint      NOT NULL REFERENCES utilisateur(id),
    produit_id      bigint      NOT NULL REFERENCES produit(id),
    code_brut       text        NOT NULL,
    gtin            text,                       -- Data Matrix (AI 01)
    numero_lot      text,                       -- Data Matrix (AI 10), normalisé
    date_peremption date,                       -- Data Matrix (AI 17)
    numero_serie    text,                       -- Data Matrix (AI 21)
    scanne_le       timestamptz NOT NULL DEFAULT now(),
    importe_le      timestamptz                 -- rempli quand le poste reprend le scan
);

CREATE INDEX IF NOT EXISTS idx_scan_reception_attente ON scan_reception (utilisateur_id) WHERE importe_le IS NULL;

-- Une même boîte sérialisée ne peut être en attente qu'une fois
CREATE UNIQUE INDEX IF NOT EXISTS ux_scan_reception_serie
    ON scan_reception (gtin, numero_serie) WHERE importe_le IS NULL AND numero_serie IS NOT NULL;

COMMENT ON TABLE scan_reception IS 'Boîtes scannées sur le mobile au déballage, en attente d''import dans une réception du poste';
