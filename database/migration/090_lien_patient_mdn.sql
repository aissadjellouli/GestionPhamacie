-- =====================================================================
-- Lien avec l'assuré et assuré principal d'un patient ; organisme MDN
-- (règles du titulaire, 2026-11-15 ; docs/regles-metier.md)
--
-- A. patient.lien : lien du patient avec l'assuré, liste fermée
--      assure, conjoint, enfant, ascendant, autre ; NULL accepté (fiches
--      existantes, non renseigné). Aucune fiche existante n'est modifiée.
--    patient.assure_principal_id : fiche de l'assuré principal (NULL pour
--      l'assuré lui-même). ON DELETE SET NULL. Un patient n'est pas son
--      propre assuré principal (contrainte). Les autres contrôles (pas de
--      chaîne, lien « assure » sans assuré principal) sont faits par le
--      serveur.
--    Index : assure_principal_id (ayants droit d'un assuré) et n° d'assuré
--      de base (12 premiers chiffres) pour « Même carte » et la proposition
--      de rattachement. Rien n'est déduit du suffixe (docs/chifa.md).
-- B. Organisme payeur MDN (militaires) ajouté s'il n'existe pas (mêmes
--    colonnes et valeurs par défaut que CNAS / CASNOS) ; s'il existe déjà,
--    rien n'est modifié.
-- Idempotent. A exécuter après 089_canal_organisme_assure_unique.sql.
-- =====================================================================

-- ---------------------------------------------------------------------
-- A. Lien et assuré principal
-- ---------------------------------------------------------------------
ALTER TABLE patient ADD COLUMN IF NOT EXISTS lien text NULL;
ALTER TABLE patient ADD COLUMN IF NOT EXISTS assure_principal_id bigint NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_patient_lien') THEN
        ALTER TABLE patient ADD CONSTRAINT ck_patient_lien
            CHECK (lien IN ('assure', 'conjoint', 'enfant', 'ascendant', 'autre'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_patient_assure_principal') THEN
        ALTER TABLE patient ADD CONSTRAINT fk_patient_assure_principal
            FOREIGN KEY (assure_principal_id) REFERENCES patient(id) ON DELETE SET NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_patient_assure_principal') THEN
        ALTER TABLE patient ADD CONSTRAINT ck_patient_assure_principal CHECK (assure_principal_id <> id);
    END IF;
END $$;

COMMENT ON COLUMN patient.lien IS 'Lien avec l''assuré : assure, conjoint, enfant, ascendant, autre ; NULL : non renseigné';
COMMENT ON COLUMN patient.assure_principal_id IS 'Fiche de l''assuré principal (NULL pour l''assuré lui-même) ; jamais déduit du suffixe du n° d''assuré';

CREATE INDEX IF NOT EXISTS ix_patient_assure_principal ON patient (assure_principal_id) WHERE assure_principal_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_patient_num_assure_base ON patient ((left(regexp_replace(num_assure, '\D', '', 'g'), 12)))
    WHERE num_assure IS NOT NULL;

-- ---------------------------------------------------------------------
-- B. Organisme payeur MDN (militaires)
-- ---------------------------------------------------------------------
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM organisme WHERE upper(code) = 'MDN') THEN
        RAISE NOTICE 'Organisme MDN déjà présent : inchangé.';
    ELSE
        INSERT INTO organisme (code, nom) VALUES ('MDN', 'Ministère de la Défense nationale (militaires)');
        RAISE NOTICE 'Organisme MDN ajouté.';
    END IF;
END $$;
