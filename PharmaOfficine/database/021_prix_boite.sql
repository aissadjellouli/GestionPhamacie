-- =====================================================================
-- Prix d'achat réel par boîte et lots de même numéro à dates différentes
--   * chaque boîte garde le prix de sa livraison (boite.prix_achat_ht) ;
--     le lot ne fait plus de moyenne : lot.prix_achat_ht reste le prix
--     de sa première livraison et sert aux boîtes sans identité ;
--   * valeur d'un lot = prix réel de ses boîtes identifiées en stock
--     + prix du lot pour les boîtes sans identité (vue v_valeur_lot) ;
--   * un même n° de lot peut exister avec deux dates de péremption
--     (choix « lot séparé » fait à la réception).
-- Idempotent. A exécuter après 020_seuils_stock.sql.
-- =====================================================================

-- ---------------------------------------------------------------------
-- 1. Unicité : produit + n° de lot + grossiste + péremption
-- ---------------------------------------------------------------------
ALTER TABLE lot DROP CONSTRAINT IF EXISTS ux_lot_fournisseur;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ux_lot_fournisseur_date') THEN
        ALTER TABLE lot ADD CONSTRAINT ux_lot_fournisseur_date UNIQUE (produit_id, fournisseur_id, numero_lot, date_peremption);
    END IF;
END $$;

COMMENT ON COLUMN lot.prix_achat_ht IS 'Prix de la première livraison du lot ; chaque boîte identifiée garde le sien (boite.prix_achat_ht)';

-- ---------------------------------------------------------------------
-- 2. Prix de chaque boîte : celui de sa livraison
-- ---------------------------------------------------------------------
ALTER TABLE boite ADD COLUMN IF NOT EXISTS prix_achat_ht numeric(12,2) CHECK (prix_achat_ht >= 0);

-- Boîtes existantes : prix de l'entrée en stock de leur livraison, sinon prix du lot
UPDATE boite b SET prix_achat_ht = COALESCE(
    (SELECT m.prix_achat_ht FROM mouvement_stock m
     WHERE m.lot_id = b.lot_id AND m.reception_id = b.reception_id AND m.type = 'entree' AND m.prix_achat_ht IS NOT NULL
     ORDER BY m.id DESC LIMIT 1),
    (SELECT l.prix_achat_ht FROM lot l WHERE l.id = b.lot_id))
WHERE b.prix_achat_ht IS NULL;

CREATE OR REPLACE FUNCTION completer_reception_boite() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.reception_id IS NULL THEN
        SELECT reception_id INTO NEW.reception_id FROM lot WHERE id = NEW.lot_id;
    END IF;
    IF NEW.prix_achat_ht IS NULL THEN
        NEW.prix_achat_ht := COALESCE(
            (SELECT m.prix_achat_ht FROM mouvement_stock m
             WHERE m.lot_id = NEW.lot_id AND m.reception_id = NEW.reception_id AND m.type = 'entree' AND m.prix_achat_ht IS NOT NULL
             ORDER BY m.id DESC LIMIT 1),
            (SELECT prix_achat_ht FROM lot WHERE id = NEW.lot_id));
    END IF;
    RETURN NEW;
END;
$$;

ALTER TABLE boite ALTER COLUMN prix_achat_ht SET NOT NULL;

-- Étiquettes internes : réception et prix de la livraison (facultatifs)
DROP FUNCTION IF EXISTS creer_boites_internes(bigint, int);
DROP FUNCTION IF EXISTS creer_boites_internes(bigint, int, bigint);
CREATE OR REPLACE FUNCTION creer_boites_internes(p_lot_id bigint, p_nombre int, p_reception_id bigint DEFAULT NULL, p_prix numeric DEFAULT NULL)
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
        INSERT INTO boite (lot_id, code_etiquette, reception_id, prix_achat_ht) VALUES (p_lot_id, o_code, p_reception_id, p_prix)
        RETURNING id INTO o_boite_id;
        RETURN NEXT;
    END LOOP;
END;
$$;

-- ---------------------------------------------------------------------
-- 3. Valeur réelle de chaque lot en stock
-- ---------------------------------------------------------------------
CREATE OR REPLACE VIEW v_valeur_lot AS
SELECT l.id AS lot_id, l.quantite_disponible,
       ROUND(CASE
           WHEN l.quantite_disponible <= 0 THEN 0
           WHEN COALESCE(b.nb, 0) >= l.quantite_disponible THEN b.prix_moyen * l.quantite_disponible
           ELSE COALESCE(b.somme, 0) + (l.quantite_disponible - COALESCE(b.nb, 0)) * l.prix_achat_ht
       END, 2) AS valeur,
       CASE WHEN l.quantite_disponible > 0 THEN ROUND(CASE
           WHEN COALESCE(b.nb, 0) >= l.quantite_disponible THEN b.prix_moyen
           ELSE (COALESCE(b.somme, 0) + (l.quantite_disponible - COALESCE(b.nb, 0)) * l.prix_achat_ht) / l.quantite_disponible
       END, 2) ELSE l.prix_achat_ht END AS prix_unitaire
FROM lot l
LEFT JOIN (
    SELECT lot_id, count(*) AS nb, SUM(prix_achat_ht) AS somme, AVG(prix_achat_ht) AS prix_moyen
    FROM boite WHERE statut = 'en_stock' GROUP BY lot_id
) b ON b.lot_id = l.id;

-- ---------------------------------------------------------------------
-- 4. Valeur à risque de péremption au prix réel (mêmes colonnes que 005)
-- ---------------------------------------------------------------------
CREATE OR REPLACE VIEW v_risque_peremption AS
WITH prm AS (
    SELECT * FROM parametre_alerte WHERE id = 1
),
lots AS (
    SELECT l.id AS lot_id, l.produit_id, p.nom_commercial, l.numero_lot,
           l.date_peremption, l.quantite_disponible, COALESCE(vl.prix_unitaire, l.prix_achat_ht) AS prix_achat_ht, c.conso_jour,
           (l.date_peremption - current_date) AS jours_restants,
           SUM(l.quantite_disponible) OVER (
               PARTITION BY l.produit_id ORDER BY l.date_peremption, l.id
           ) - l.quantite_disponible AS qte_avant,
           l.fournisseur_id, f.raison_sociale AS fournisseur,
           COALESCE(p.jours_orange, f.jours_orange, prm.jours_orange) AS seuil_orange,
           COALESCE(p.jours_rouge,  f.jours_rouge,  prm.jours_rouge)  AS seuil_rouge,
           prm.horizon_risque_jours
    FROM lot l
    CROSS JOIN prm
    JOIN produit p        ON p.id = l.produit_id
    JOIN fournisseur f    ON f.id = l.fournisseur_id
    JOIN v_consommation c ON c.produit_id = l.produit_id
    LEFT JOIN v_valeur_lot vl ON vl.lot_id = l.id
    WHERE l.quantite_disponible > 0
),
calc AS (
    SELECT lots.*,
           GREATEST(0, LEAST(
               quantite_disponible,
               FLOOR(conso_jour * GREATEST(jours_restants, 0)) - qte_avant
           ))::int AS qte_vendable
    FROM lots
)
SELECT calc.lot_id, calc.produit_id, calc.nom_commercial, calc.numero_lot,
       calc.date_peremption, calc.jours_restants, calc.quantite_disponible,
       calc.qte_vendable,
       calc.quantite_disponible - calc.qte_vendable                          AS qte_a_risque,
       ROUND((calc.quantite_disponible - calc.qte_vendable) * calc.prix_achat_ht, 2) AS valeur_a_risque,
       CASE
           WHEN calc.jours_restants <= 0                  THEN 'perime'
           WHEN calc.jours_restants <= calc.seuil_rouge   THEN 'rouge'
           WHEN calc.jours_restants <= calc.seuil_orange  THEN 'orange'
           WHEN calc.jours_restants <= calc.horizon_risque_jours
                AND calc.quantite_disponible - calc.qte_vendable > 0 THEN 'risque_ecoulement'
           ELSE 'ok'
       END AS niveau,
       calc.fournisseur_id,
       calc.fournisseur,
       calc.seuil_orange,
       calc.seuil_rouge
FROM calc;
