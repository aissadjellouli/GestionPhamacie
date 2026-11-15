-- =====================================================================
-- Volet Chifa : compléments (2026-11-15, modèle Wasfa, lot 1)
--   1. vente.chifa_date_soins : date des soins (saisie au volet ou au
--      rattachement). La 2e date du QR n'y est PAS rangée automatiquement
--      (décision D-1 : à confirmer sur un volet réel ; docs/chifa.md).
--   2. centre_payeur : centre payeur d'un organisme (CNAS ou CASNOS,
--      contrôlé par l'application), code libre de 1 à 10 caractères
--      unique par organisme (D-2), libellé, actif ; jamais supprimé.
--      vente.centre_payeur_id (centre de la vente) et
--      patient.centre_payeur_id (centre habituel, proposé à la vente).
--      Un bordereau par centre : pour le futur écran Bordereaux.
--   3. vente.chifa_saisi_par : employé qui a saisi la vente dans
--      CHIFA-Officine (défaut : le vendeur, modifiable).
--   4. Majoration Chifa (D-3) : type (code de la liste réglable
--      « majoration_chifa ») et montant >= 0 ; la majoration s'ajoute à
--      la part organisme (créance : v_vente_part_organisme, colonne
--      part_organisme_retenue ; nouvelle colonne majoration_chifa).
--   5. Exclusion d'une vente Chifa du traitement (« Exclure du
--      traitement ») sans nouveau statut de vente : chifa_exclue_le,
--      chifa_exclue_par, chifa_exclusion_motif_code (liste réglable
--      « chifa_exclusion », D-7), chifa_exclusion_texte (« Autre ») ;
--      tout ou rien (contrainte). Réversible (« Rétablir »), journalisé.
--   6. motif_reglable : contrainte de liste recréée avec
--      « chifa_exclusion », « majoration_chifa » et « retour_client »
--      (cette dernière utilisée par le lot 5, pour ne recréer la
--      contrainte qu'une fois) ; les valeurs déjà admises sont relues
--      dans la contrainte en place et conservées.
--   7. Droit « chifa.exclure » (« Exclure une vente Chifa », module
--      Chifa, sensible) : titulaire et pharmacien assistant (D-8), posé à
--      la première exécution seulement ; jamais autorisable par le code
--      comptoir d'un collègue.
-- Aucune donnée supprimée. Idempotent. A exécuter après 099.
-- =====================================================================

-- ---- 2. Centres payeurs -------------------------------------------------
CREATE TABLE IF NOT EXISTS centre_payeur (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    organisme_id bigint NOT NULL REFERENCES organisme (id),
    code         text NOT NULL,
    nom          text NOT NULL,
    actif        boolean NOT NULL DEFAULT true,
    cree_le      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_centre_payeur_code UNIQUE (organisme_id, code),
    CONSTRAINT ck_centre_payeur_code CHECK (code = btrim(code) AND char_length(code) BETWEEN 1 AND 10),
    CONSTRAINT ck_centre_payeur_nom CHECK (char_length(btrim(nom)) BETWEEN 1 AND 120)
);
COMMENT ON TABLE centre_payeur IS 'Centre payeur d''un organisme Chifa (CNAS, CASNOS) ; code libre 1 à 10 caractères (D-2) ; jamais supprimé, désactivé (script 100)';

ALTER TABLE vente   ADD COLUMN IF NOT EXISTS centre_payeur_id bigint REFERENCES centre_payeur (id);
ALTER TABLE patient ADD COLUMN IF NOT EXISTS centre_payeur_id bigint REFERENCES centre_payeur (id);
COMMENT ON COLUMN vente.centre_payeur_id IS 'Centre payeur de la vente Chifa (script 100)';
COMMENT ON COLUMN patient.centre_payeur_id IS 'Centre payeur habituel, proposé aux ventes Chifa du patient (script 100)';

-- ---- 1, 3, 4, 5. Colonnes Chifa de la vente ------------------------------
ALTER TABLE vente ADD COLUMN IF NOT EXISTS chifa_date_soins          date;
ALTER TABLE vente ADD COLUMN IF NOT EXISTS chifa_saisi_par           bigint REFERENCES utilisateur (id);
ALTER TABLE vente ADD COLUMN IF NOT EXISTS chifa_majoration_code     text;
ALTER TABLE vente ADD COLUMN IF NOT EXISTS chifa_majoration_montant  numeric(12,2) NOT NULL DEFAULT 0;
ALTER TABLE vente ADD COLUMN IF NOT EXISTS chifa_exclue_le           timestamptz;
ALTER TABLE vente ADD COLUMN IF NOT EXISTS chifa_exclue_par          bigint REFERENCES utilisateur (id);
ALTER TABLE vente ADD COLUMN IF NOT EXISTS chifa_exclusion_motif_code text;
ALTER TABLE vente ADD COLUMN IF NOT EXISTS chifa_exclusion_texte     text;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_vente_chifa_majoration') THEN
        ALTER TABLE vente ADD CONSTRAINT ck_vente_chifa_majoration
            CHECK (chifa_majoration_montant >= 0 AND (chifa_majoration_montant = 0 OR chifa_majoration_code IS NOT NULL));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_vente_chifa_exclusion') THEN
        ALTER TABLE vente ADD CONSTRAINT ck_vente_chifa_exclusion CHECK (
            (chifa_exclue_le IS NULL AND chifa_exclue_par IS NULL AND chifa_exclusion_motif_code IS NULL AND chifa_exclusion_texte IS NULL)
            OR (chifa_exclue_le IS NOT NULL AND chifa_exclue_par IS NOT NULL AND chifa_exclusion_motif_code IS NOT NULL));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_vente_chifa_exclusion_texte') THEN
        ALTER TABLE vente ADD CONSTRAINT ck_vente_chifa_exclusion_texte
            CHECK (chifa_exclusion_texte IS NULL OR char_length(btrim(chifa_exclusion_texte)) BETWEEN 1 AND 200);
    END IF;
END $$;

COMMENT ON COLUMN vente.chifa_date_soins IS 'Date des soins de la vente Chifa, saisie (la 2e date du QR n''y est pas rangée automatiquement, D-1 ; script 100)';
COMMENT ON COLUMN vente.chifa_saisi_par IS 'Employé qui a saisi la vente dans CHIFA-Officine (défaut : le vendeur ; script 100)';
COMMENT ON COLUMN vente.chifa_majoration_code IS 'Type de majoration Chifa (motif_reglable, liste majoration_chifa ; script 100)';
COMMENT ON COLUMN vente.chifa_majoration_montant IS 'Majoration Chifa, ajoutée à la part organisme (D-3 ; v_vente_part_organisme ; script 100)';
COMMENT ON COLUMN vente.chifa_exclue_le IS 'Vente Chifa exclue du traitement (hors attente de volet et bordereaux) ; null : non exclue (script 100)';
COMMENT ON COLUMN vente.chifa_exclusion_motif_code IS 'Motif d''exclusion (motif_reglable, liste chifa_exclusion ; script 100)';

CREATE INDEX IF NOT EXISTS ix_vente_centre_payeur ON vente (centre_payeur_id) WHERE centre_payeur_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_vente_chifa_exclue ON vente (chifa_exclue_le DESC) WHERE chifa_exclue_le IS NOT NULL;

-- ---- 6. Listes de motifs réglables ---------------------------------------
-- La contrainte en place est relue : toute valeur déjà admise (y compris
-- ajoutée par un autre script) est gardée, les trois nouvelles s'y ajoutent.
DO $$
DECLARE
    c        text;
    def      text;
    valeurs  text[];
BEGIN
    SELECT conname, pg_get_constraintdef(oid) INTO c, def FROM pg_constraint
    WHERE conrelid = 'motif_reglable'::regclass AND contype = 'c' AND conname = 'ck_motif_reglable_liste';
    IF def IS NOT NULL AND def LIKE '%''chifa_exclusion''%' AND def LIKE '%''majoration_chifa''%' AND def LIKE '%''retour_client''%' THEN
        RETURN;
    END IF;
    SELECT array_agg(DISTINCT v ORDER BY v) INTO valeurs FROM (
        SELECT (regexp_matches(COALESCE(def, ''), '''([a-z0-9_]+)''', 'g'))[1] AS v
        UNION SELECT liste FROM motif_reglable
        UNION SELECT unnest(ARRAY['tiroir', 'attente', 'charge', 'sortie_stock', 'retour_fournisseur', 'volet_absent',
                                  'chifa_exclusion', 'majoration_chifa', 'retour_client'])
    ) x;
    IF c IS NOT NULL THEN
        EXECUTE format('ALTER TABLE motif_reglable DROP CONSTRAINT %I', c);
    END IF;
    EXECUTE format('ALTER TABLE motif_reglable ADD CONSTRAINT ck_motif_reglable_liste CHECK (liste = ANY (%L::text[]))', valeurs);
END $$;

-- Motifs de départ (D-7) ; « Autre » demande un texte et ne se désactive pas
INSERT INTO motif_reglable (liste, code, libelle, systeme, ordre) VALUES
    ('chifa_exclusion', 'deja_traitee',       'Déjà traitée autrement', false, 10),
    ('chifa_exclusion', 'ordonnance_annulee', 'Ordonnance annulée',     false, 20),
    ('chifa_exclusion', 'erreur_saisie',      'Erreur de saisie',       false, 30),
    ('chifa_exclusion', 'autre',              'Autre (préciser)',       true,  999),
    -- Types de majoration : liste à compléter par le titulaire (Administration › Motifs et catégories)
    ('majoration_chifa', 'garde',             'Garde',                  false, 10)
ON CONFLICT (liste, code) DO NOTHING;

-- ---- 4. Créance de l'organisme : majoration ajoutée ------------------------
CREATE OR REPLACE VIEW v_vente_part_organisme AS
SELECT v.id AS vente_id, v.organisme_id, v.part_organisme AS part_organisme_caisse,
       (COALESCE(r.part_organisme_chifa, v.part_organisme) + v.chifa_majoration_montant)::numeric(12,2) AS part_organisme_retenue,
       v.chifa_majoration_montant AS majoration_chifa
FROM vente v
LEFT JOIN regularisation_part_organisme r ON r.vente_id = v.id AND r.statut = 'active';

-- ---- 7. Droit « chifa.exclure » ------------------------------------------
-- Posé à la première exécution seulement : une réexécution n'annule jamais un choix fait ensuite par le titulaire.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM permission WHERE code = 'chifa.exclure') THEN
        INSERT INTO permission (code, module, libelle, sensible)
        VALUES ('chifa.exclure', 'Chifa', 'Exclure une vente Chifa', true)
        ON CONFLICT DO NOTHING;

        -- Les droits du titulaire sont protégés par un déclencheur : levé le temps de cette transaction
        ALTER TABLE role_permission DISABLE TRIGGER trg_proteger_role_systeme;
        INSERT INTO role_permission (role_id, permission_code)
        SELECT r.id, 'chifa.exclure' FROM role r WHERE r.code IN ('titulaire', 'assistant')
        ON CONFLICT DO NOTHING;
        ALTER TABLE role_permission ENABLE TRIGGER trg_proteger_role_systeme;
    END IF;
END $$;
