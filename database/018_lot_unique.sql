-- =====================================================================
-- Lot unique et boîtes rattachées à leur livraison
--   * un lot = produit + n° de lot + grossiste (le même lot livré à nouveau
--     par le même grossiste s'ajoute au lot existant) ;
--   * n° de lot enregistré en majuscules, sans espaces autour ;
--   * chaque boîte et chaque entrée en stock gardent leur propre réception
--     (bon de livraison) et leur prix d'achat.
-- Idempotent. A exécuter après 017_inventaire.sql.
-- =====================================================================

-- ---------------------------------------------------------------------
-- 1. N° de lot normalisé
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION normaliser_numero_lot() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    NEW.numero_lot := upper(btrim(NEW.numero_lot));
    IF NEW.numero_lot = '' THEN
        RAISE EXCEPTION 'Numéro de lot vide';
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE TRIGGER trg_lot_numero
    BEFORE INSERT OR UPDATE OF numero_lot ON lot
    FOR EACH ROW EXECUTE FUNCTION normaliser_numero_lot();

-- Lots existants : s'arrête avec un message clair si la normalisation créerait un doublon
DO $$
DECLARE
    doublons text;
BEGIN
    SELECT string_agg(format('produit %s, grossiste %s, lot %s', produit_id, fournisseur_id, n), ' ; ')
    INTO doublons
    FROM (SELECT produit_id, fournisseur_id, upper(btrim(numero_lot)) AS n
          FROM lot GROUP BY 1, 2, 3 HAVING count(*) > 1) d;
    IF doublons IS NOT NULL THEN
        RAISE EXCEPTION 'Lots en double à regrouper avant ce script : %', doublons;
    END IF;
END $$;

UPDATE lot SET numero_lot = upper(btrim(numero_lot)) WHERE numero_lot <> upper(btrim(numero_lot));

-- ---------------------------------------------------------------------
-- 2. Unicité : produit + n° de lot + grossiste
-- ---------------------------------------------------------------------
ALTER TABLE lot DROP CONSTRAINT IF EXISTS ux_lot_reception;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ux_lot_fournisseur') THEN
        ALTER TABLE lot ADD CONSTRAINT ux_lot_fournisseur UNIQUE (produit_id, fournisseur_id, numero_lot);
    END IF;
END $$;

COMMENT ON COLUMN lot.reception_id IS 'Première réception du lot ; chaque boîte et chaque entrée gardent la leur';

-- ---------------------------------------------------------------------
-- 3. Entrées en stock : réception et prix d'achat de chaque livraison
-- ---------------------------------------------------------------------
ALTER TABLE mouvement_stock ADD COLUMN IF NOT EXISTS reception_id  bigint REFERENCES reception(id);
ALTER TABLE mouvement_stock ADD COLUMN IF NOT EXISTS prix_achat_ht numeric(12,2);
CREATE INDEX IF NOT EXISTS idx_mouvement_reception ON mouvement_stock (reception_id) WHERE reception_id IS NOT NULL;

UPDATE mouvement_stock m SET reception_id = l.reception_id, prix_achat_ht = l.prix_achat_ht
FROM lot l
WHERE l.id = m.lot_id AND m.type = 'entree' AND m.reception_id IS NULL;

-- ---------------------------------------------------------------------
-- 4. Boîtes : réception propre à chaque boîte
-- ---------------------------------------------------------------------
ALTER TABLE boite ADD COLUMN IF NOT EXISTS reception_id bigint REFERENCES reception(id);
UPDATE boite b SET reception_id = l.reception_id FROM lot l WHERE l.id = b.lot_id AND b.reception_id IS NULL;

CREATE OR REPLACE FUNCTION completer_reception_boite() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.reception_id IS NULL THEN
        SELECT reception_id INTO NEW.reception_id FROM lot WHERE id = NEW.lot_id;
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE TRIGGER trg_boite_reception
    BEFORE INSERT ON boite
    FOR EACH ROW EXECUTE FUNCTION completer_reception_boite();

ALTER TABLE boite ALTER COLUMN reception_id SET NOT NULL;

CREATE OR REPLACE FUNCTION journaliser_entree_boite() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO boite_evenement (boite_id, type, reception_id) VALUES (NEW.id, 'reception', NEW.reception_id);
    RETURN NULL;
END;
$$;

-- Étiquettes internes : la réception peut être précisée (étiquetage à la livraison)
DROP FUNCTION IF EXISTS creer_boites_internes(bigint, int);
CREATE OR REPLACE FUNCTION creer_boites_internes(p_lot_id bigint, p_nombre int, p_reception_id bigint DEFAULT NULL)
RETURNS TABLE (o_boite_id bigint, o_code text)
LANGUAGE plpgsql AS $$
DECLARE
    v_dispo   int;
    v_deja    int;
    v_base    text;
    i         int;
BEGIN
    SELECT quantite_disponible INTO v_dispo FROM lot WHERE id = p_lot_id FOR UPDATE;
    SELECT count(*) INTO v_deja FROM boite WHERE lot_id = p_lot_id AND statut = 'en_stock';
    IF p_nombre < 1 OR v_deja + p_nombre > v_dispo THEN
        RAISE EXCEPTION 'Lot % : % boîtes en stock, % déjà étiquetées, % demandées',
            p_lot_id, v_dispo, v_deja, p_nombre;
    END IF;

    FOR i IN 1..p_nombre LOOP
        v_base := '20' || lpad(nextval('seq_code_interne')::text, 10, '0');
        o_code := v_base || ean13_cle(v_base);
        INSERT INTO boite (lot_id, code_etiquette, reception_id) VALUES (p_lot_id, o_code, p_reception_id)
        RETURNING id INTO o_boite_id;
        RETURN NEXT;
    END LOOP;
END;
$$;

-- Parcours : le bon de livraison est celui de la boîte
CREATE OR REPLACE VIEW v_parcours_boite AS
SELECT b.id AS boite_id,
       COALESCE(b.code_etiquette, b.gtin || ' / ' || b.numero_serie) AS identifiant,
       p.nom_commercial, l.numero_lot, l.date_peremption,
       f.raison_sociale AS fournisseur, r.numero_bl, r.date_reception,
       b.statut, l.bloque,
       v.numero AS ticket, v.date_vente, v.num_facture_chifa,
       NULLIF(btrim(COALESCE(pt.nom, '') || ' ' || COALESCE(pt.prenom, '')), '') AS patient,
       pt.telephone
FROM boite b
JOIN lot l          ON l.id = b.lot_id
JOIN produit p      ON p.id = l.produit_id
JOIN fournisseur f  ON f.id = l.fournisseur_id
JOIN reception r    ON r.id = b.reception_id
LEFT JOIN ligne_vente lv ON lv.id = b.ligne_vente_id
LEFT JOIN vente v        ON v.id = lv.vente_id
LEFT JOIN patient pt     ON pt.id = v.patient_id;
