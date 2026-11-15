-- =====================================================================
-- Rapprochement du volet Chifa : décisions du titulaire (2026-11-15)
--   1. Vente « Facturée par Chifa » encaissée sans n° de facture Chifa :
--      motif obligatoire choisi dans la liste réglable « volet_absent »
--      (Administration › Motifs et catégories) ; code et texte de
--      « Autre » gardés sur la vente (vente.chifa_attente_motif_code /
--      chifa_attente_motif_texte), visibles dans Finances › Chifa.
--      Anciennes ventes en attente : sans motif (null).
--   2. Délai des ventes candidates réglable :
--      parametre_caisse.delai_volet_chifa_jours (défaut 7, de 1 à 365).
--      Au-delà, la vente reste en attente, signalée « en retard », et
--      peut toujours être choisie à la main (aucune expiration).
--   3. regularisation_part_organisme : écart de part organisme au
--      rattachement du volet (CNAS et CASNOS, mêmes règles). La créance
--      de l'organisme sur la vente devient la part calculée sur le prix
--      Chifa (part_organisme_chifa) ; la vente elle-même (montant, part
--      enregistrée à la caisse, stock) n'est jamais modifiée.
--        sort = charge_patient   : manque ajouté à la dette du patient
--                                  (mouvement de crédit rattaché à la
--                                  vente, credit_mouvement_id) ;
--               perte_pharmacie  : manque gardé par la pharmacie ;
--               avoir_patient    : surplus rendu au patient (dette
--                                  diminuée, le reste « à rendre ») ;
--               gain_organisme   : surplus sans trop-payé du patient.
--      Une seule régularisation par vente, jamais modifiée ni supprimée.
--      v_vente_part_organisme : créance retenue par vente (part Chifa si
--      régularisée, sinon part de la caisse).
--      v_credit_accorde : une dette « à la charge du patient » compte
--      comme crédit accordé sur sa vente (imputation des règlements,
--      script 087) ; mêmes colonnes ; sans régularisation, inchangée.
-- Aucune donnée supprimée. Idempotent. A exécuter après 096_sorties_tiroir.sql.
-- =====================================================================

-- 1. Liste « volet_absent » dans motif_reglable (contrainte de liste élargie)
DO $$
DECLARE
    c text;
BEGIN
    SELECT conname INTO c FROM pg_constraint
    WHERE conrelid = 'motif_reglable'::regclass AND contype = 'c'
      AND pg_get_constraintdef(oid) LIKE '%liste%' AND pg_get_constraintdef(oid) LIKE '%retour_fournisseur%'
      AND pg_get_constraintdef(oid) NOT LIKE '%volet_absent%'
    LIMIT 1;
    IF c IS NOT NULL THEN
        EXECUTE format('ALTER TABLE motif_reglable DROP CONSTRAINT %I', c);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_motif_reglable_liste') THEN
        ALTER TABLE motif_reglable ADD CONSTRAINT ck_motif_reglable_liste
            CHECK (liste IN ('tiroir', 'attente', 'charge', 'sortie_stock', 'retour_fournisseur', 'volet_absent'));
    END IF;
END $$;

INSERT INTO motif_reglable (liste, code, libelle, systeme, ordre) VALUES
    ('volet_absent', 'carte_absente',      'Pas de carte Chifa présentée',  false, 10),
    ('volet_absent', 'chifa_indisponible', 'CHIFA-Officine indisponible',   false, 20),
    ('volet_absent', 'volet_plus_tard',    'Volet à éditer plus tard',      false, 30),
    ('volet_absent', 'autre',              'Autre (préciser)',              true,  999)
ON CONFLICT (liste, code) DO NOTHING;

ALTER TABLE vente ADD COLUMN IF NOT EXISTS chifa_attente_motif_code  text;
ALTER TABLE vente ADD COLUMN IF NOT EXISTS chifa_attente_motif_texte text;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_vente_chifa_attente_motif_texte') THEN
        ALTER TABLE vente ADD CONSTRAINT ck_vente_chifa_attente_motif_texte
            CHECK (chifa_attente_motif_texte IS NULL OR char_length(btrim(chifa_attente_motif_texte)) BETWEEN 1 AND 200);
    END IF;
END $$;
COMMENT ON COLUMN vente.chifa_attente_motif_code IS 'Vente « Facturée par Chifa » encaissée sans n° de facture : motif (motif_reglable, liste volet_absent) ; null avant 097';
COMMENT ON COLUMN vente.chifa_attente_motif_texte IS 'Texte du motif « Autre » de la vente encaissée sans n° de facture Chifa';

-- 2. Délai des ventes candidates au rattachement d'un volet
ALTER TABLE parametre_caisse ADD COLUMN IF NOT EXISTS delai_volet_chifa_jours integer NOT NULL DEFAULT 7;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_parametre_caisse_delai_volet_chifa') THEN
        ALTER TABLE parametre_caisse ADD CONSTRAINT ck_parametre_caisse_delai_volet_chifa CHECK (delai_volet_chifa_jours BETWEEN 1 AND 365);
    END IF;
END $$;
COMMENT ON COLUMN parametre_caisse.delai_volet_chifa_jours IS 'Ventes Chifa en attente de volet proposées en priorité (jours) ; au-delà : « en retard », toujours rattachables';

-- 3. Régularisation de la part organisme au rattachement du volet
CREATE TABLE IF NOT EXISTS regularisation_part_organisme (
    id                   bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    vente_id             bigint        NOT NULL REFERENCES vente (id),
    organisme_id         bigint        REFERENCES organisme (id),
    part_organisme_vente numeric(12,2) NOT NULL,
    part_organisme_chifa numeric(12,2) NOT NULL CHECK (part_organisme_chifa >= 0),
    ecart                numeric(12,2) NOT NULL CHECK (ecart <> 0),
    sort                 text          NOT NULL CHECK (sort IN ('charge_patient', 'perte_pharmacie', 'avoir_patient', 'gain_organisme')),
    montant_patient      numeric(12,2) NOT NULL DEFAULT 0 CHECK (montant_patient >= 0),
    montant_dette_diminuee numeric(12,2) NOT NULL DEFAULT 0 CHECK (montant_dette_diminuee >= 0),
    montant_a_rendre     numeric(12,2) NOT NULL DEFAULT 0 CHECK (montant_a_rendre >= 0),
    montant_pharmacie    numeric(12,2) NOT NULL DEFAULT 0,
    credit_mouvement_id  bigint        REFERENCES credit_mouvement (id),
    utilisateur_id       bigint        NOT NULL REFERENCES utilisateur (id),
    valide_par           bigint        REFERENCES utilisateur (id),
    cree_le              timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT ux_regularisation_part_organisme_vente UNIQUE (vente_id),
    CONSTRAINT ck_regularisation_sens CHECK (
        (ecart > 0 AND sort IN ('charge_patient', 'perte_pharmacie')) OR (ecart < 0 AND sort IN ('avoir_patient', 'gain_organisme'))),
    CONSTRAINT ck_regularisation_patient CHECK (sort <> 'charge_patient' OR (montant_patient = ecart AND credit_mouvement_id IS NOT NULL)),
    CONSTRAINT ck_regularisation_avoir CHECK (sort <> 'avoir_patient' OR (montant_patient > 0 AND montant_patient = montant_dette_diminuee + montant_a_rendre))
);
COMMENT ON TABLE regularisation_part_organisme IS 'Écart de part organisme au rattachement du volet Chifa : créance ramenée à la part Chifa, différence à la charge du patient, perte pharmacie, avoir patient ou gain organisme ; jamais modifiée ni supprimée';
COMMENT ON COLUMN regularisation_part_organisme.ecart IS 'Part organisme enregistrée à la caisse moins part calculée sur le prix Chifa : > 0 la caisse paiera moins, < 0 elle paiera plus';
COMMENT ON COLUMN regularisation_part_organisme.montant_pharmacie IS 'Effet pour la pharmacie : perte (négatif, perte_pharmacie) ou gain (positif, gain_organisme et part non rendue)';

CREATE OR REPLACE FUNCTION proteger_regularisation_part_organisme() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Une régularisation de part organisme ne se modifie pas et ne se supprime pas';
END $$;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_regularisation_part_organisme_immuable') THEN
        CREATE TRIGGER trg_regularisation_part_organisme_immuable BEFORE UPDATE OR DELETE ON regularisation_part_organisme
            FOR EACH ROW EXECUTE FUNCTION proteger_regularisation_part_organisme();
    END IF;
END $$;

CREATE OR REPLACE VIEW v_vente_part_organisme AS
SELECT v.id AS vente_id, v.organisme_id, v.part_organisme AS part_organisme_caisse,
       COALESCE(r.part_organisme_chifa, v.part_organisme) AS part_organisme_retenue
FROM vente v
LEFT JOIN regularisation_part_organisme r ON r.vente_id = v.id;
COMMENT ON VIEW v_vente_part_organisme IS 'Créance de l''organisme par vente : part Chifa si l''écart a été régularisé (script 097), sinon part enregistrée à la caisse';

-- Ventes à crédit nettes des retours, plus la part « à la charge du patient » d'une régularisation (une vente annulée n'est plus due)
CREATE OR REPLACE VIEW v_credit_accorde AS
SELECT v.id AS vente_id, v.patient_id, v.numero, v.date_vente,
       x.montant - COALESCE((SELECT SUM(rc.montant_rembourse) FROM retour_client rc WHERE rc.vente_id = v.id), 0) AS montant
FROM vente v
JOIN LATERAL (SELECT COALESCE((SELECT SUM(p.montant) FROM paiement p WHERE p.vente_id = v.id AND p.mode = 'credit'), 0)
                   + COALESCE((SELECT SUM(r.montant_patient) FROM regularisation_part_organisme r
                               WHERE r.vente_id = v.id AND r.sort = 'charge_patient'), 0) AS montant) x ON x.montant > 0
WHERE v.statut = 'validee' AND v.patient_id IS NOT NULL;
