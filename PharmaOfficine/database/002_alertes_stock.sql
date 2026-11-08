-- =====================================================================
-- Module d'alertes : péremption et état de stock
-- A exécuter après schema_pharmacie.sql (PostgreSQL 17+)
-- =====================================================================

-- ---------------------------------------------------------------------
-- 1. Paramètres réglables par la pharmacie (une seule ligne)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS parametre_alerte (
    id                      int PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    jours_orange            int NOT NULL DEFAULT 180,  -- péremption proche : agir (retour fournisseur)
    jours_rouge             int NOT NULL DEFAULT 90,   -- péremption imminente : vendre en priorité
    horizon_risque_jours    int NOT NULL DEFAULT 365,  -- horizon du calcul de risque d'écoulement
    fenetre_conso_jours     int NOT NULL DEFAULT 90,   -- période de calcul des ventes moyennes
    delai_fournisseur_jours int NOT NULL DEFAULT 2,
    jours_securite          int NOT NULL DEFAULT 7,
    jours_cible_commande    int NOT NULL DEFAULT 30,   -- stock visé après commande
    jours_dormant           int NOT NULL DEFAULT 90,
    jours_surstock          int NOT NULL DEFAULT 180
);
INSERT INTO parametre_alerte DEFAULT VALUES ON CONFLICT (id) DO NOTHING;

-- Produit en tension / rupture nationale (signalé manuellement ou par le grossiste)
ALTER TABLE produit ADD COLUMN IF NOT EXISTS en_tension boolean NOT NULL DEFAULT false;

-- ---------------------------------------------------------------------
-- 2. Consommation moyenne journalière par produit
-- ---------------------------------------------------------------------
CREATE OR REPLACE VIEW v_consommation AS
WITH prm AS (
    SELECT fenetre_conso_jours AS f FROM parametre_alerte WHERE id = 1
),
ventes AS (
    SELECT lv.produit_id, SUM(lv.quantite) AS qte
    FROM ligne_vente lv
    JOIN vente v ON v.id = lv.vente_id
    CROSS JOIN prm
    WHERE v.statut = 'validee'
      AND v.date_vente >= now() - make_interval(days => prm.f)
    GROUP BY lv.produit_id
)
SELECT p.id AS produit_id,
       COALESCE(ventes.qte, 0)::numeric / prm.f AS conso_jour
FROM produit p
CROSS JOIN prm
LEFT JOIN ventes ON ventes.produit_id = p.id;

-- ---------------------------------------------------------------------
-- 3. Risque de péremption par lot
--    Simule l'écoulement FEFO : les lots qui périment en premier sont
--    vendus en premier. Calcul volontairement prudent.
-- ---------------------------------------------------------------------
-- La vue change de colonnes d'un script à l'autre : on la recrée
DROP VIEW IF EXISTS v_risque_peremption;
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
           ) - l.quantite_disponible AS qte_avant     -- stock des lots à écouler avant
    FROM lot l
    JOIN produit p        ON p.id = l.produit_id
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
       END AS niveau
FROM calc
CROSS JOIN prm;

-- ---------------------------------------------------------------------
-- 4. État du stock par produit (seuils dynamiques)
-- ---------------------------------------------------------------------
CREATE OR REPLACE VIEW v_etat_stock AS
WITH prm AS (
    SELECT * FROM parametre_alerte WHERE id = 1
),
stock AS (
    SELECT produit_id, SUM(quantite_disponible) AS qte
    FROM lot
    WHERE date_peremption > current_date          -- le périmé ne compte pas
    GROUP BY produit_id
),
derniere AS (
    SELECT lv.produit_id, MAX(v.date_vente) AS derniere_vente
    FROM ligne_vente lv
    JOIN vente v ON v.id = lv.vente_id
    WHERE v.statut = 'validee'
    GROUP BY lv.produit_id
),
base AS (
    SELECT p.id AS produit_id, p.nom_commercial, p.en_tension,
           COALESCE(s.qte, 0) AS stock, c.conso_jour, d.derniere_vente,
           GREATEST(p.stock_minimum,
                    CEIL(c.conso_jour * (prm.delai_fournisseur_jours + prm.jours_securite)))::int AS seuil_alerte,
           CEIL(c.conso_jour * prm.jours_cible_commande)::int AS stock_cible,
           prm.jours_dormant, prm.jours_surstock
    FROM produit p
    CROSS JOIN prm
    JOIN v_consommation c ON c.produit_id = p.id
    LEFT JOIN stock s     ON s.produit_id = p.id
    LEFT JOIN derniere d  ON d.produit_id = p.id
    WHERE p.actif
)
SELECT produit_id, nom_commercial, en_tension, stock,
       ROUND(conso_jour, 2) AS conso_jour,
       CASE WHEN conso_jour > 0 THEN ROUND(stock / conso_jour) END AS couverture_jours,
       seuil_alerte, derniere_vente,
       CASE
           WHEN stock = 0 AND (conso_jour > 0 OR seuil_alerte > 0) THEN 'rupture'
           WHEN stock < seuil_alerte                               THEN 'critique'
           WHEN stock > 0 AND (derniere_vente IS NULL
                OR derniere_vente < now() - make_interval(days => jours_dormant)) THEN 'dormant'
           WHEN conso_jour > 0 AND stock / conso_jour > jours_surstock THEN 'surstock'
           ELSE 'ok'
       END AS statut,
       GREATEST(0, stock_cible - stock) AS qte_suggeree
FROM base;

-- ---------------------------------------------------------------------
-- 5. Alternatives en cas de rupture (même DCI, dosage et forme)
-- ---------------------------------------------------------------------
CREATE OR REPLACE VIEW v_alternatives AS
SELECT p.id AS produit_id, alt.id AS alternative_id,
       alt.nom_commercial AS alternative, e.stock AS stock_alternative
FROM produit p
JOIN produit alt ON alt.dci_id = p.dci_id
                AND alt.dosage IS NOT DISTINCT FROM p.dosage
                AND alt.forme  IS NOT DISTINCT FROM p.forme
                AND alt.id <> p.id
                AND alt.actif
JOIN v_etat_stock e ON e.produit_id = alt.id
WHERE e.stock > 0;

-- ---------------------------------------------------------------------
-- 6. Lot à servir en caisse (FEFO)
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION lot_fefo(p_produit_id bigint)
RETURNS bigint LANGUAGE sql STABLE AS $$
    SELECT id FROM lot
    WHERE produit_id = p_produit_id
      AND quantite_disponible > 0
      AND date_peremption > current_date
    ORDER BY date_peremption, id
    LIMIT 1;
$$;

-- ---------------------------------------------------------------------
-- 7. Blocage absolu de la vente d'un lot périmé
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION verifier_lot_vendable() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    d date;
BEGIN
    SELECT date_peremption INTO d FROM lot WHERE id = NEW.lot_id;
    IF d <= current_date THEN
        RAISE EXCEPTION 'Lot périmé depuis le %, vente interdite', to_char(d, 'DD/MM/YYYY');
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE TRIGGER trg_ligne_vente_peremption
    BEFORE INSERT ON ligne_vente
    FOR EACH ROW EXECUTE FUNCTION verifier_lot_vendable();

-- ---------------------------------------------------------------------
-- 8. Table des alertes, avec suivi des actions
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS alerte (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    type                text NOT NULL CHECK (type IN (
                            'peremption', 'perime', 'risque_ecoulement',
                            'rupture', 'stock_critique', 'dormant', 'surstock')),
    niveau              text NOT NULL CHECK (niveau IN ('info', 'attention', 'urgent')),
    produit_id          bigint NOT NULL REFERENCES produit(id),
    lot_id              bigint REFERENCES lot(id),
    message             text NOT NULL,
    valeur_dzd          numeric(12,2),
    statut              text NOT NULL DEFAULT 'nouvelle' CHECK (statut IN (
                            'nouvelle', 'vue', 'reportee', 'traitee', 'resolue')),
    reportee_jusqu_au   date,
    action_prise        text,       -- ex. 'retour fournisseur', 'mis en avant', 'détruit'
    cree_le             timestamptz NOT NULL DEFAULT now(),
    mise_a_jour_le      timestamptz NOT NULL DEFAULT now(),
    traitee_par         bigint REFERENCES utilisateur(id),
    traitee_le          timestamptz
);

-- Une seule alerte ouverte par type, produit et lot
CREATE UNIQUE INDEX IF NOT EXISTS ux_alerte_ouverte
    ON alerte (type, produit_id, COALESCE(lot_id, 0))
    WHERE statut IN ('nouvelle', 'vue', 'reportee');

CREATE INDEX IF NOT EXISTS idx_alerte_ouverte_tri
    ON alerte (niveau, valeur_dzd DESC)
    WHERE statut IN ('nouvelle', 'vue');

-- ---------------------------------------------------------------------
-- 9. Génération des alertes (appelée chaque matin et après chaque réception)
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION generer_alertes() RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
    nb integer;
BEGIN
    -- Les alertes reportées dont le délai est passé redeviennent visibles
    UPDATE alerte SET statut = 'nouvelle', mise_a_jour_le = now()
    WHERE statut = 'reportee' AND reportee_jusqu_au <= current_date;

    DROP TABLE IF EXISTS _candidats;
    CREATE TEMP TABLE _candidats ON COMMIT DROP AS

    -- Péremption
    SELECT CASE r.niveau
               WHEN 'perime'            THEN 'perime'
               WHEN 'risque_ecoulement' THEN 'risque_ecoulement'
               ELSE 'peremption'
           END AS type,
           CASE r.niveau
               WHEN 'perime' THEN 'urgent'
               WHEN 'rouge'  THEN 'urgent'
               ELSE 'attention'
           END AS niveau,
           r.produit_id,
           r.lot_id,
           CASE r.niveau
               WHEN 'perime' THEN format('%s, lot %s : PÉRIMÉ depuis le %s, %s unités à retirer du rayon.',
                                  r.nom_commercial, r.numero_lot,
                                  to_char(r.date_peremption, 'DD/MM/YYYY'), r.quantite_disponible)
               ELSE format('%s, lot %s : expire le %s (%s j). %s unités sur %s risquent de ne pas être vendues.',
                           r.nom_commercial, r.numero_lot,
                           to_char(r.date_peremption, 'DD/MM/YYYY'), r.jours_restants,
                           r.qte_a_risque, r.quantite_disponible)
           END AS message,
           r.valeur_a_risque AS valeur_dzd
    FROM v_risque_peremption r
    WHERE r.niveau <> 'ok'

    UNION ALL

    -- État du stock
    SELECT CASE e.statut
               WHEN 'rupture'  THEN 'rupture'
               WHEN 'critique' THEN 'stock_critique'
               WHEN 'dormant'  THEN 'dormant'
               ELSE 'surstock'
           END,
           CASE e.statut
               WHEN 'rupture'  THEN 'urgent'
               WHEN 'critique' THEN 'attention'
               ELSE 'info'
           END,
           e.produit_id,
           NULL::bigint,
           CASE e.statut
               WHEN 'rupture'  THEN format('%s en rupture.%s Commande suggérée : %s unités.',
                                    e.nom_commercial,
                                    CASE WHEN e.en_tension THEN ' Produit en tension nationale.' ELSE '' END,
                                    e.qte_suggeree)
               WHEN 'critique' THEN format('%s : stock %s, couverture %s jours. Commande suggérée : %s unités.',
                                    e.nom_commercial, e.stock,
                                    COALESCE(e.couverture_jours::text, '?'), e.qte_suggeree)
               WHEN 'dormant'  THEN format('%s : %s unités sans vente récente.',
                                    e.nom_commercial, e.stock)
               ELSE format('%s : %s jours de stock, surstock.',
                           e.nom_commercial, e.couverture_jours)
           END,
           NULL::numeric
    FROM v_etat_stock e
    WHERE e.statut <> 'ok';

    -- Créer les nouvelles alertes ; mettre à jour celles qui existent.
    -- Si le niveau s'aggrave, l'alerte redevient « nouvelle » même si elle était reportée.
    INSERT INTO alerte AS a (type, niveau, produit_id, lot_id, message, valeur_dzd)
    SELECT type, niveau, produit_id, lot_id, message, valeur_dzd FROM _candidats
    ON CONFLICT (type, produit_id, COALESCE(lot_id, 0))
        WHERE statut IN ('nouvelle', 'vue', 'reportee')
    DO UPDATE SET
        message        = EXCLUDED.message,
        valeur_dzd     = EXCLUDED.valeur_dzd,
        statut         = CASE WHEN a.niveau <> EXCLUDED.niveau THEN 'nouvelle' ELSE a.statut END,
        niveau         = EXCLUDED.niveau,
        mise_a_jour_le = now();

    GET DIAGNOSTICS nb = ROW_COUNT;

    -- Clôture automatique des alertes dont la cause a disparu
    UPDATE alerte a
    SET statut = 'resolue', traitee_le = now(), mise_a_jour_le = now()
    WHERE a.statut IN ('nouvelle', 'vue', 'reportee')
      AND NOT EXISTS (
          SELECT 1 FROM _candidats c
          WHERE c.type = a.type
            AND c.produit_id = a.produit_id
            AND COALESCE(c.lot_id, 0) = COALESCE(a.lot_id, 0)
      );

    RETURN nb;
END;
$$;
