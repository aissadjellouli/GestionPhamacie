-- =====================================================================
-- Stock vendable : les lots bloqués (rappel, blocage) ne comptent plus
--   * v_etat_stock : un lot bloqué ne masque plus une rupture ou un stock
--     critique, et la commande suggérée en tient compte ;
--   * lot_fefo (recherche par code-barres, scanner mobile) : jamais un lot
--     bloqué quand un autre lot est vendable.
-- Mêmes colonnes et même calcul qu'en 062 : seul le filtre « NOT bloque » est
-- ajouté.
-- Idempotent. A exécuter après 064_caisse_cloturee.sql.
-- =====================================================================

SET client_encoding = 'UTF8';

CREATE OR REPLACE VIEW v_etat_stock AS
WITH prm AS (
    SELECT delai_fournisseur_jours, jours_securite, jours_cible_commande, jours_dormant, jours_surstock
    FROM parametre_alerte WHERE id = 1
),
stock AS (
    SELECT produit_id, SUM(quantite_disponible) AS qte
    FROM lot
    WHERE date_peremption > current_date AND NOT bloque   -- ni le périmé ni le bloqué (rappel) ne comptent
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

CREATE OR REPLACE FUNCTION lot_fefo(p_produit_id bigint) RETURNS bigint
LANGUAGE sql STABLE AS $$
    SELECT id FROM lot
    WHERE produit_id = p_produit_id
      AND quantite_disponible > 0
      AND date_peremption > current_date
      AND NOT bloque
    ORDER BY date_peremption, id
    LIMIT 1;
$$;
