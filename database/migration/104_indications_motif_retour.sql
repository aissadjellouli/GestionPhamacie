-- =====================================================================
-- Indications thérapeutiques et motif réglable du retour client
-- (modèle Pharma, 2026-11-15, lot 5)
--   indication : libellé (unique sans tenir compte de la casse ni des
--   espaces autour), actif ; jamais supprimée (désactivée, elle n'est
--   plus proposée). Gérée dans Administration › Indications avec le droit
--   existant « catalogue.creer ».
--   produit_indication : association produit ↔ indication (plusieurs
--   indications par produit), posée sur la fiche produit ; recherche au
--   comptoir par indication (« toux », « fièvre »).
--   Indications de départ : petite liste courante, À VALIDER PAR LE
--   TITULAIRE, insérée seulement si le libellé est absent.
--   retour_client : motif_code (liste réglable « retour_client », déjà
--   admise par ck_motif_reglable_liste depuis le script 100) et
--   motif_texte (précision de « Autre ») ; la colonne motif (texte) reste
--   remplie (libellé du motif) ; les anciens retours gardent leur motif
--   libre, motif_code vide.
--   Motifs de départ : Erreur de délivrance, Produit non utilisé, Effet
--   indésirable, Autre (texte obligatoire, ne se désactive pas).
-- Idempotent. À exécuter après 100.
-- =====================================================================

-- ---- 1. Indications ------------------------------------------------
CREATE TABLE IF NOT EXISTS indication (
    id       bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    libelle  text NOT NULL,
    actif    boolean NOT NULL DEFAULT true,
    cree_le  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_indication_libelle CHECK (length(btrim(libelle)) BETWEEN 1 AND 80)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_indication_libelle ON indication (lower(btrim(libelle)));

CREATE TABLE IF NOT EXISTS produit_indication (
    produit_id     bigint NOT NULL REFERENCES produit(id),
    indication_id  bigint NOT NULL REFERENCES indication(id),
    cree_le        timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (produit_id, indication_id)
);
CREATE INDEX IF NOT EXISTS idx_produit_indication_indication ON produit_indication (indication_id);

-- Indications de départ (à valider par le titulaire) : ajoutées seulement si le libellé est absent
INSERT INTO indication (libelle)
SELECT x.libelle
FROM (VALUES ('Toux'), ('Fièvre'), ('Douleur'), ('Rhume'), ('Mal de gorge'), ('Allergie'),
             ('Diarrhée'), ('Constipation'), ('Brûlures d''estomac'), ('Nausées')) AS x(libelle)
WHERE NOT EXISTS (SELECT 1 FROM indication i WHERE lower(btrim(i.libelle)) = lower(btrim(x.libelle)));

-- ---- 2. Motif réglable du retour client -----------------------------
ALTER TABLE retour_client ADD COLUMN IF NOT EXISTS motif_code  text;
ALTER TABLE retour_client ADD COLUMN IF NOT EXISTS motif_texte text;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_retour_client_motif_texte') THEN
        ALTER TABLE retour_client ADD CONSTRAINT ck_retour_client_motif_texte
            CHECK (motif_texte IS NULL OR (motif_code IS NOT NULL AND length(btrim(motif_texte)) BETWEEN 1 AND 200));
    END IF;
END $$;

-- Motifs de départ ; « Autre » demande un texte et ne se désactive pas
INSERT INTO motif_reglable (liste, code, libelle, systeme, ordre) VALUES
    ('retour_client', 'erreur_delivrance', 'Erreur de délivrance', false, 10),
    ('retour_client', 'non_utilise',       'Produit non utilisé',  false, 20),
    ('retour_client', 'effet_indesirable', 'Effet indésirable',    false, 30),
    ('retour_client', 'autre',             'Autre (préciser)',     true,  999)
ON CONFLICT (liste, code) DO NOTHING;
