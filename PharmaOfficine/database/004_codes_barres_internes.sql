-- =====================================================================
-- Codes-barres internes pour les boîtes sans code lisible
-- A exécuter après migration_provenance.sql
--
-- Format : EAN-13 commençant par 20 (préfixe GS1 réservé à l'usage
-- interne, jamais attribué à un vrai produit) + 10 chiffres + clé.
-- Le code identifie un LOT précis : produit, grossiste, péremption.
-- =====================================================================

CREATE SEQUENCE IF NOT EXISTS seq_code_interne START 1;

ALTER TABLE lot ADD COLUMN IF NOT EXISTS code_interne text UNIQUE;

-- Clé de contrôle EAN-13 à partir des 12 premiers chiffres
CREATE OR REPLACE FUNCTION ean13_cle(p12 text) RETURNS char(1)
LANGUAGE plpgsql IMMUTABLE AS $$
DECLARE
    somme int := 0;
    i     int;
BEGIN
    IF p12 !~ '^[0-9]{12}$' THEN
        RAISE EXCEPTION 'Il faut exactement 12 chiffres : %', p12;
    END IF;
    FOR i IN 1..12 LOOP
        somme := somme + substr(p12, i, 1)::int * CASE WHEN i % 2 = 0 THEN 3 ELSE 1 END;
    END LOOP;
    RETURN ((10 - somme % 10) % 10)::text;
END;
$$;

-- Renvoie le code du lot, en le créant au premier appel
CREATE OR REPLACE FUNCTION generer_code_interne(p_lot_id bigint) RETURNS text
LANGUAGE plpgsql AS $$
DECLARE
    existant text;
    base     text;
    code     text;
BEGIN
    SELECT code_interne INTO existant FROM lot WHERE id = p_lot_id FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Lot % introuvable', p_lot_id;
    END IF;
    IF existant IS NOT NULL THEN
        RETURN existant;
    END IF;

    base := '20' || lpad(nextval('seq_code_interne')::text, 10, '0');
    code := base || ean13_cle(base);
    UPDATE lot SET code_interne = code WHERE id = p_lot_id;
    RETURN code;
END;
$$;

-- ---------------------------------------------------------------------
-- Recherche au scan, dans l'ordre :
--   1. code interne  -> le lot exact (provenance connue à la boîte)
--   2. code fabricant -> le produit, puis le lot FEFO
-- (le Data Matrix GS1 est décodé côté application avant cet appel)
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION rechercher_par_code(p_code text)
RETURNS TABLE (produit_id bigint, lot_id bigint, source text)
LANGUAGE plpgsql STABLE AS $$
BEGIN
    RETURN QUERY
        SELECT l.produit_id, l.id, 'code_interne'::text
        FROM lot l
        WHERE l.code_interne = p_code;     -- même périmé : la caisse affichera
    IF FOUND THEN RETURN; END IF;          -- le blocage plutôt que « introuvable »

    RETURN QUERY
        SELECT p.id, lot_fefo(p.id), 'code_fabricant'::text
        FROM produit p
        WHERE p.code_barre = p_code AND p.actif;
END;
$$;
