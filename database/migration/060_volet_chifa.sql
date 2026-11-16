-- =====================================================================
-- Reprise d'une vente Chifa par le QR code du volet (éviter la double saisie)
--   * chifa_code_produit : code produit lu dans le QR du volet (ex. 07607)
--     associé une fois par le pharmacien à un produit du catalogue, puis
--     reconnu automatiquement ;
--   * vente.chifa_facture, chifa_date, chifa_qr : volet Chifa repris par la
--     vente (n° de facture, date, contenu brut du QR) ; un volet n'est repris
--     qu'une fois (sauf vente annulée).
-- Le format du QR est déduit d'un seul volet réel : à confirmer avec
-- l'éditeur de Chifa (docs/chifa.md, docs/a-faire.md).
-- Idempotent. A exécuter après 059_surveillance_fantomes_remises_horaires.sql.
-- =====================================================================

CREATE TABLE IF NOT EXISTS chifa_code_produit (
    code        text        PRIMARY KEY,
    produit_id  bigint      NOT NULL REFERENCES produit (id),
    associe_par bigint      REFERENCES utilisateur (id),
    associe_le  timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE chifa_code_produit IS 'Code produit du QR des volets Chifa associé à un produit du catalogue (fait une fois par le pharmacien)';

ALTER TABLE vente ADD COLUMN IF NOT EXISTS chifa_facture text;
ALTER TABLE vente ADD COLUMN IF NOT EXISTS chifa_date date;
ALTER TABLE vente ADD COLUMN IF NOT EXISTS chifa_qr text;
COMMENT ON COLUMN vente.chifa_facture IS 'N° de la facture Chifa reprise par scan du QR du volet';
COMMENT ON COLUMN vente.chifa_qr IS 'Contenu brut du QR du volet Chifa (format à confirmer avec l''éditeur de Chifa)';

-- Un volet n'est repris qu'une fois ; une vente annulée libère son volet
CREATE UNIQUE INDEX IF NOT EXISTS ux_vente_chifa_facture ON vente (chifa_facture) WHERE chifa_facture IS NOT NULL AND statut <> 'annulee';
