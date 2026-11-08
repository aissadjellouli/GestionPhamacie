-- =====================================================================
-- Migration : provenance de chaque lot et de chaque boîte
-- A exécuter après schema_pharmacie.sql et alertes_stock.sql
-- =====================================================================

-- ---------------------------------------------------------------------
-- 1. Réception : chaque livraison d'un grossiste (avec ou sans commande)
-- ---------------------------------------------------------------------
CREATE TABLE reception (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    fournisseur_id  bigint      NOT NULL REFERENCES fournisseur(id),
    commande_id     bigint      REFERENCES commande_fournisseur(id),
    numero_bl       text,                       -- bon de livraison
    numero_facture  text,
    date_reception  timestamptz NOT NULL DEFAULT now(),
    utilisateur_id  bigint      REFERENCES utilisateur(id)
);
CREATE INDEX idx_reception_fournisseur ON reception (fournisseur_id, date_reception);

-- ---------------------------------------------------------------------
-- 2. Chaque lot en stock est rattaché à une réception, donc à un grossiste
--    (un même numéro de lot reçu de deux grossistes = deux lignes distinctes)
-- ---------------------------------------------------------------------
ALTER TABLE lot ADD COLUMN reception_id   bigint REFERENCES reception(id);
ALTER TABLE lot ADD COLUMN fournisseur_id bigint REFERENCES fournisseur(id);

-- Si la table lot contient déjà des données, renseigner ces deux colonnes
-- avant d'exécuter les deux lignes suivantes.
ALTER TABLE lot ALTER COLUMN reception_id   SET NOT NULL;
ALTER TABLE lot ALTER COLUMN fournisseur_id SET NOT NULL;

ALTER TABLE lot DROP CONSTRAINT IF EXISTS lot_produit_id_numero_lot_key;
ALTER TABLE lot ADD CONSTRAINT ux_lot_reception UNIQUE (reception_id, produit_id, numero_lot);
CREATE INDEX idx_lot_fournisseur ON lot (fournisseur_id);

-- Garantit que le grossiste du lot est bien celui de la réception
CREATE OR REPLACE FUNCTION verifier_fournisseur_lot() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.fournisseur_id IS DISTINCT FROM
       (SELECT fournisseur_id FROM reception WHERE id = NEW.reception_id) THEN
        RAISE EXCEPTION 'Le fournisseur du lot ne correspond pas à celui de la réception';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_lot_fournisseur
    BEFORE INSERT OR UPDATE OF fournisseur_id, reception_id ON lot
    FOR EACH ROW EXECUTE FUNCTION verifier_fournisseur_lot();

-- ---------------------------------------------------------------------
-- 3. Boîtes sérialisées (Data Matrix) : provenance à l'unité
--    Utilisé dès que la boîte porte un numéro de série.
-- ---------------------------------------------------------------------
CREATE TABLE boite (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    lot_id          bigint NOT NULL REFERENCES lot(id),
    gtin            text   NOT NULL,            -- identifiant produit (GS1 AI 01)
    numero_serie    text   NOT NULL,            -- numéro de série (GS1 AI 21)
    statut          text   NOT NULL DEFAULT 'en_stock' CHECK (statut IN (
                        'en_stock', 'vendue', 'retournee_fournisseur', 'detruite', 'retour_client')),
    ligne_vente_id  bigint REFERENCES ligne_vente(id),
    scannee_le      timestamptz NOT NULL DEFAULT now(),
    UNIQUE (gtin, numero_serie)                -- une même boîte ne peut entrer deux fois
);
CREATE INDEX idx_boite_lot ON boite (lot_id);

-- ---------------------------------------------------------------------
-- 4. Retours fournisseur : une boîte ne repart que chez son grossiste
-- ---------------------------------------------------------------------
CREATE TABLE retour_fournisseur (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    fournisseur_id  bigint      NOT NULL REFERENCES fournisseur(id),
    numero          text        NOT NULL UNIQUE,
    motif           text        NOT NULL CHECK (motif IN (
                        'peremption_proche', 'perime', 'defaut', 'erreur_livraison', 'rappel_lot')),
    statut          text        NOT NULL DEFAULT 'brouillon' CHECK (statut IN (
                        'brouillon', 'envoye', 'avoir_recu', 'refuse')),
    montant_avoir   numeric(12,2),
    cree_le         timestamptz NOT NULL DEFAULT now(),
    utilisateur_id  bigint      REFERENCES utilisateur(id)
);

CREATE TABLE ligne_retour_fournisseur (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    retour_id   bigint  NOT NULL REFERENCES retour_fournisseur(id) ON DELETE CASCADE,
    lot_id      bigint  NOT NULL REFERENCES lot(id),
    quantite    integer NOT NULL CHECK (quantite > 0)
);

CREATE OR REPLACE FUNCTION verifier_retour_bon_fournisseur() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    f_lot    bigint;
    f_retour bigint;
BEGIN
    SELECT fournisseur_id INTO f_lot    FROM lot                WHERE id = NEW.lot_id;
    SELECT fournisseur_id INTO f_retour FROM retour_fournisseur WHERE id = NEW.retour_id;
    IF f_lot <> f_retour THEN
        RAISE EXCEPTION 'Ce lot provient d''un autre grossiste : retour impossible sur ce bon';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_retour_bon_fournisseur
    BEFORE INSERT OR UPDATE ON ligne_retour_fournisseur
    FOR EACH ROW EXECUTE FUNCTION verifier_retour_bon_fournisseur();

-- ---------------------------------------------------------------------
-- 5. Le risque de péremption affiche désormais le grossiste du lot
--    (colonnes ajoutées en fin de vue, sans casser l'existant)
-- ---------------------------------------------------------------------
CREATE OR REPLACE VIEW v_risque_peremption AS
WITH prm AS (
    SELECT * FROM parametre_alerte WHERE id = 1
),
lots AS (
    SELECT l.id AS lot_id, l.produit_id, p.nom_commercial, l.numero_lot,
           l.date_peremption, l.quantite_disponible, l.prix_achat_ht, c.conso_jour,
           (l.date_peremption - current_date) AS jours_restants,
           SUM(l.quantite_disponible) OVER (
               PARTITION BY l.produit_id ORDER BY l.date_peremption, l.id
           ) - l.quantite_disponible AS qte_avant,
           l.fournisseur_id, f.raison_sociale AS fournisseur
    FROM lot l
    JOIN produit p        ON p.id = l.produit_id
    JOIN fournisseur f    ON f.id = l.fournisseur_id
    JOIN v_consommation c ON c.produit_id = l.produit_id
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
           WHEN calc.jours_restants <= 0                THEN 'perime'
           WHEN calc.jours_restants <= prm.jours_rouge  THEN 'rouge'
           WHEN calc.jours_restants <= prm.jours_orange THEN 'orange'
           WHEN calc.jours_restants <= prm.horizon_risque_jours
                AND calc.quantite_disponible - calc.qte_vendable > 0 THEN 'risque_ecoulement'
           ELSE 'ok'
       END AS niveau,
       calc.fournisseur_id,
       calc.fournisseur
FROM calc
CROSS JOIN prm;

-- ---------------------------------------------------------------------
-- 6. Stock actuel par produit et par grossiste
-- ---------------------------------------------------------------------
CREATE OR REPLACE VIEW v_stock_par_fournisseur AS
SELECT l.produit_id, p.nom_commercial, l.fournisseur_id, f.raison_sociale AS fournisseur,
       SUM(l.quantite_disponible)                    AS quantite,
       MIN(l.date_peremption)                        AS premiere_peremption,
       ROUND(SUM(l.quantite_disponible * l.prix_achat_ht), 2) AS valeur_achat
FROM lot l
JOIN produit p     ON p.id = l.produit_id
JOIN fournisseur f ON f.id = l.fournisseur_id
WHERE l.quantite_disponible > 0
GROUP BY l.produit_id, p.nom_commercial, l.fournisseur_id, f.raison_sociale;
