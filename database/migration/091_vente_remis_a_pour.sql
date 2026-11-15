-- =====================================================================
-- Vente : « Remis à » et « Pour » (règle du titulaire, 2026-11-15 ;
-- docs/regles-metier.md)
--
-- Pas de dette familiale : la vente (et sa dette) reste au nom d'un seul
-- patient (vente.patient_id).
--   - Vente avec ordonnance : au nom du patient de l'ordonnance ;
--     « Remis à » = la personne qui récupère les médicaments.
--   - Vente sans ordonnance : au nom de celui qui passe la commande ;
--     « Pour » = le membre de la famille concerné.
-- Chaque champ : référence d'une fiche patient (membre de la famille,
-- script 090) OU nom libre (texte, 200 caractères au plus), jamais les deux.
-- ON DELETE SET NULL : la suppression d'une fiche n'efface pas la vente.
-- Aucune donnée existante n'est modifiée. Idempotent.
-- A exécuter après 090_lien_patient_mdn.sql.
-- =====================================================================

ALTER TABLE vente ADD COLUMN IF NOT EXISTS remis_a_patient_id bigint NULL;
ALTER TABLE vente ADD COLUMN IF NOT EXISTS remis_a_nom text NULL;
ALTER TABLE vente ADD COLUMN IF NOT EXISTS pour_patient_id bigint NULL;
ALTER TABLE vente ADD COLUMN IF NOT EXISTS pour_nom text NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_vente_remis_a_patient') THEN
        ALTER TABLE vente ADD CONSTRAINT fk_vente_remis_a_patient
            FOREIGN KEY (remis_a_patient_id) REFERENCES patient(id) ON DELETE SET NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_vente_pour_patient') THEN
        ALTER TABLE vente ADD CONSTRAINT fk_vente_pour_patient
            FOREIGN KEY (pour_patient_id) REFERENCES patient(id) ON DELETE SET NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_vente_remis_a_nom') THEN
        ALTER TABLE vente ADD CONSTRAINT ck_vente_remis_a_nom
            CHECK (remis_a_nom IS NULL OR (char_length(btrim(remis_a_nom)) BETWEEN 1 AND 200));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_vente_pour_nom') THEN
        ALTER TABLE vente ADD CONSTRAINT ck_vente_pour_nom
            CHECK (pour_nom IS NULL OR (char_length(btrim(pour_nom)) BETWEEN 1 AND 200));
    END IF;
    -- Référence OU nom libre, pas les deux
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_vente_remis_a_un_seul') THEN
        ALTER TABLE vente ADD CONSTRAINT ck_vente_remis_a_un_seul
            CHECK (remis_a_patient_id IS NULL OR remis_a_nom IS NULL);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_vente_pour_un_seul') THEN
        ALTER TABLE vente ADD CONSTRAINT ck_vente_pour_un_seul
            CHECK (pour_patient_id IS NULL OR pour_nom IS NULL);
    END IF;
END $$;

COMMENT ON COLUMN vente.remis_a_patient_id IS 'Vente avec ordonnance : membre de la famille qui récupère les médicaments (fiche patient)';
COMMENT ON COLUMN vente.remis_a_nom IS 'Vente avec ordonnance : personne qui récupère les médicaments (nom libre, hors fiche)';
COMMENT ON COLUMN vente.pour_patient_id IS 'Vente sans ordonnance : membre de la famille concerné (fiche patient)';
COMMENT ON COLUMN vente.pour_nom IS 'Vente sans ordonnance : personne concernée (nom libre, hors fiche)';

CREATE INDEX IF NOT EXISTS ix_vente_remis_a_patient ON vente (remis_a_patient_id) WHERE remis_a_patient_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_vente_pour_patient ON vente (pour_patient_id) WHERE pour_patient_id IS NOT NULL;
