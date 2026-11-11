-- =====================================================================
-- TVA à reverser sur les sorties de stock sans vente (pertes et dons)
--   * parametre_rapport : pour chaque motif de sortie (périmé, casse, don
--     ou échantillon), la TVA d'achat de la boîte doit-elle être reversée ?
--     Réglé par le titulaire selon l'avis de son comptable ; « non » par
--     défaut (règle fiscale à confirmer, voir docs/a-faire.md).
--   * Les rapports en tirent la « marge après pertes et dons » :
--     marge brute - valeur d'achat HT des boîtes sorties - TVA à reverser.
-- Idempotent. A exécuter après 060_volet_chifa.sql.
-- =====================================================================

CREATE TABLE IF NOT EXISTS parametre_rapport (
    id                   integer PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    tva_reversee_perime  boolean NOT NULL DEFAULT false,
    tva_reversee_casse   boolean NOT NULL DEFAULT false,
    tva_reversee_don     boolean NOT NULL DEFAULT false
);
COMMENT ON TABLE parametre_rapport IS 'Réglages des rapports : TVA d''achat à reverser selon le motif de la sortie de stock sans vente';
COMMENT ON COLUMN parametre_rapport.tva_reversee_perime IS 'TVA d''achat reversée sur les boîtes périmées détruites';
COMMENT ON COLUMN parametre_rapport.tva_reversee_casse IS 'TVA d''achat reversée sur les boîtes cassées';
COMMENT ON COLUMN parametre_rapport.tva_reversee_don IS 'TVA d''achat reversée sur les boîtes données ou remises en échantillon';

INSERT INTO parametre_rapport DEFAULT VALUES ON CONFLICT (id) DO NOTHING;
