-- =====================================================================
-- Seuil d'alerte de stock par produit
--   * produit.stock_minimum devient le seuil saisi par le pharmacien
--     (en boîtes) ; NULL = calcul automatique ;
--   * seuil appliqué = seuil saisi s'il existe, sinon automatique :
--     ventes moyennes par jour × (délai de livraison + stock de sécurité) ;
--   * un seuil saisi à 0 = pas d'alerte de stock pour ce produit.
-- Idempotent. A exécuter après 019_mobile.sql.
-- =====================================================================

-- Une seule fois : l'ancien 0 par défaut signifiait « rien de saisi »
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_attribute
               WHERE attrelid = 'produit'::regclass AND attname = 'stock_minimum' AND attnotnull) THEN
        ALTER TABLE produit ALTER COLUMN stock_minimum DROP NOT NULL;
        ALTER TABLE produit ALTER COLUMN stock_minimum DROP DEFAULT;
        UPDATE produit SET stock_minimum = NULL WHERE stock_minimum = 0;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'produit_stock_minimum_positif') THEN
        ALTER TABLE produit ADD CONSTRAINT produit_stock_minimum_positif CHECK (stock_minimum IS NULL OR stock_minimum BETWEEN 0 AND 100000);
    END IF;
END $$;

COMMENT ON COLUMN produit.stock_minimum IS 'Seuil d''alerte saisi par le pharmacien (boîtes). NULL : seuil automatique. 0 : pas d''alerte de stock';

-- Mêmes colonnes que dans 002_alertes_stock.sql : seuil_alerte est le seuil appliqué
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
           COALESCE(p.stock_minimum,
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
           WHEN stock = 0 AND seuil_alerte > 0                     THEN 'rupture'
           WHEN stock < seuil_alerte                               THEN 'critique'
           WHEN stock > 0 AND (derniere_vente IS NULL
                OR derniere_vente < now() - make_interval(days => jours_dormant)) THEN 'dormant'
           WHEN conso_jour > 0 AND stock / conso_jour > jours_surstock THEN 'surstock'
           ELSE 'ok'
       END AS statut,
       -- La commande suggérée remonte au moins au seuil appliqué
       GREATEST(0, GREATEST(stock_cible, seuil_alerte) - stock) AS qte_suggeree
FROM base;
