-- =====================================================================
-- Emplacements de rangement : rayon, étagère, tiroir (ex. code A-02-3).
-- Chaque produit a un emplacement de vente et, s'il le faut, un emplacement
-- en réserve : on retrouve vite les boîtes à retirer (alerte, rappel de lot)
-- et l'inventaire tournant compte un rayon à la fois.
-- A exécuter après 015_catalogue.sql. Idempotent.
-- =====================================================================

CREATE TABLE IF NOT EXISTS emplacement (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code        text        NOT NULL UNIQUE,              -- rayon-étagère-tiroir, en majuscules
    rayon       text        NOT NULL,
    etagere     text,
    tiroir      text,
    libelle     text,                                     -- ex. « Antibiotiques », « Réfrigérateur »
    zone        text        NOT NULL DEFAULT 'vente' CHECK (zone IN ('vente', 'reserve')),
    actif       boolean     NOT NULL DEFAULT true,
    cree_le     timestamptz NOT NULL DEFAULT now(),
    CHECK (code = upper(code)),
    CHECK (tiroir IS NULL OR etagere IS NOT NULL)         -- pas de tiroir sans étagère
);
CREATE INDEX IF NOT EXISTS idx_emplacement_rayon ON emplacement (rayon, code);

ALTER TABLE produit ADD COLUMN IF NOT EXISTS emplacement_id bigint REFERENCES emplacement(id);
ALTER TABLE produit ADD COLUMN IF NOT EXISTS reserve_id     bigint REFERENCES emplacement(id);
CREATE INDEX IF NOT EXISTS idx_produit_emplacement ON produit (emplacement_id);

-- Un emplacement de vente ne sert pas de réserve, et inversement
CREATE OR REPLACE FUNCTION verifier_zones_produit() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.emplacement_id IS NOT NULL
       AND (SELECT zone FROM emplacement WHERE id = NEW.emplacement_id) <> 'vente' THEN
        RAISE EXCEPTION 'L''emplacement de vente doit être un emplacement en zone de vente';
    END IF;
    IF NEW.reserve_id IS NOT NULL
       AND (SELECT zone FROM emplacement WHERE id = NEW.reserve_id) <> 'reserve' THEN
        RAISE EXCEPTION 'L''emplacement de réserve doit être en zone de réserve';
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE TRIGGER trg_zones_produit
    BEFORE INSERT OR UPDATE OF emplacement_id, reserve_id ON produit
    FOR EACH ROW EXECUTE FUNCTION verifier_zones_produit();
