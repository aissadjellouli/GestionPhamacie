-- =====================================================================
-- Rapprochement du volet Chifa avec une vente déjà encaissée (2026-11-15)
-- Circuit « la caisse d'abord » (règle du titulaire) : la vente Chifa est
-- encaissée sans n° de facture Chifa (elle attend son volet) ; au scan du
-- QR du volet, le volet est rattaché à cette vente au lieu d'en créer une.
--   * vente.chifa_date_ordonnance : date de l'ordonnance lue dans le QR
--     (7e zone, hypothèse à confirmer avec l'éditeur de Chifa) ;
--   * vente.chifa_rattache_le / chifa_rattache_par : rattachement d'un
--     volet à une vente déjà encaissée (null : volet repris à
--     l'encaissement, ou pas de volet) ;
--   * vente.chifa_ecart_motif / chifa_ecart_detail /
--     chifa_ecart_part_organisme : volet rattaché malgré des différences
--     (motif obligatoire, résumé des différences, part organisme de la
--     vente moins part calculée sur le prix Chifa) ; liste « Volets avec
--     écart ». La vente elle-même n'est jamais modifiée (stock et argent).
--   * index partiels : ventes en attente de volet, volets avec écart.
-- Unicité du volet : index existant ux_vente_chifa_facture (script 060).
-- Aucune donnée supprimée ni modifiée. Idempotent.
-- A exécuter après 094_motifs_reglables.sql.
-- =====================================================================

ALTER TABLE vente ADD COLUMN IF NOT EXISTS chifa_date_ordonnance date;
ALTER TABLE vente ADD COLUMN IF NOT EXISTS chifa_rattache_le timestamptz;
ALTER TABLE vente ADD COLUMN IF NOT EXISTS chifa_rattache_par bigint REFERENCES utilisateur (id);
ALTER TABLE vente ADD COLUMN IF NOT EXISTS chifa_ecart_motif text;
ALTER TABLE vente ADD COLUMN IF NOT EXISTS chifa_ecart_detail text;
ALTER TABLE vente ADD COLUMN IF NOT EXISTS chifa_ecart_part_organisme numeric(12,2);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_vente_chifa_ecart_motif') THEN
        ALTER TABLE vente ADD CONSTRAINT ck_vente_chifa_ecart_motif
            CHECK (chifa_ecart_motif IS NULL OR char_length(btrim(chifa_ecart_motif)) BETWEEN 1 AND 500);
    END IF;
END $$;

COMMENT ON COLUMN vente.chifa_date_ordonnance IS 'Date de l''ordonnance lue dans le QR du volet Chifa (7e zone, à confirmer avec l''éditeur de Chifa)';
COMMENT ON COLUMN vente.chifa_rattache_le IS 'Volet Chifa rattaché à la vente déjà encaissée (circuit « la caisse d''abord ») : date du rattachement';
COMMENT ON COLUMN vente.chifa_rattache_par IS 'Employé qui a rattaché le volet Chifa à la vente déjà encaissée';
COMMENT ON COLUMN vente.chifa_ecart_motif IS 'Volet rattaché malgré des différences avec la vente : motif obligatoire (droit prix.modifier)';
COMMENT ON COLUMN vente.chifa_ecart_detail IS 'Différences volet / vente au rattachement (produits, quantités, prix, part organisme)';
COMMENT ON COLUMN vente.chifa_ecart_part_organisme IS 'Part organisme de la vente moins part calculée sur le prix Chifa du volet (non recalculée)';

-- Ventes Chifa en attente de leur volet (liste et recherche des ventes candidates)
CREATE INDEX IF NOT EXISTS ix_vente_chifa_attente ON vente (date_vente)
    WHERE canal_organisme = 'chifa' AND chifa_facture IS NULL AND statut = 'validee';
-- Volets rattachés avec écart
CREATE INDEX IF NOT EXISTS ix_vente_chifa_ecart ON vente (chifa_rattache_le)
    WHERE chifa_ecart_motif IS NOT NULL;
