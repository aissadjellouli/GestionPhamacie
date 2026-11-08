-- =====================================================================
-- Seuils d'alerte de péremption paramétrables sur 3 niveaux
--   produit  >  grossiste  >  réglage général de la pharmacie
-- A exécuter après migration_provenance.sql
-- =====================================================================

-- Par grossiste : son délai de reprise des produits avant péremption
ALTER TABLE fournisseur ADD COLUMN jours_orange int CHECK (jours_orange > 0);
ALTER TABLE fournisseur ADD COLUMN jours_rouge  int CHECK (jours_rouge  > 0);

-- Par produit : cas particuliers (produits chers, froid, vente lente...)
ALTER TABLE produit ADD COLUMN jours_orange int CHECK (jours_orange > 0);
ALTER TABLE produit ADD COLUMN jours_rouge  int CHECK (jours_rouge  > 0);

-- Le seuil rouge doit rester plus court que l'orange
ALTER TABLE parametre_alerte ADD CONSTRAINT ck_seuils_general
    CHECK (jours_rouge < jours_orange);
ALTER TABLE fournisseur ADD CONSTRAINT ck_seuils_fournisseur
    CHECK (jours_rouge IS NULL OR jours_orange IS NULL OR jours_rouge < jours_orange);
ALTER TABLE produit ADD CONSTRAINT ck_seuils_produit
    CHECK (jours_rouge IS NULL OR jours_orange IS NULL OR jours_rouge < jours_orange);

-- ---------------------------------------------------------------------
-- La vue de risque applique le seuil le plus précis disponible
-- (colonnes ajoutées en fin de vue, sans casser l'existant)
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
           l.fournisseur_id, f.raison_sociale AS fournisseur,
           COALESCE(p.jours_orange, f.jours_orange, prm.jours_orange) AS seuil_orange,
           COALESCE(p.jours_rouge,  f.jours_rouge,  prm.jours_rouge)  AS seuil_rouge,
           prm.horizon_risque_jours
    FROM lot l
    CROSS JOIN prm
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

-- ---------------------------------------------------------------------
-- Exemples de réglage
-- ---------------------------------------------------------------------
-- Réglage général : alerte orange à 4 mois, rouge à 2 mois
--   UPDATE parametre_alerte SET jours_orange = 120, jours_rouge = 60 WHERE id = 1;
--
-- Un grossiste qui reprend jusqu'à 6 mois avant péremption :
-- l'alerter 2 semaines avant pour avoir le temps de préparer le retour
--   UPDATE fournisseur SET jours_orange = 195 WHERE id = 3;
--
-- Un produit cher à rotation lente : être prévenu plus tôt
--   UPDATE produit SET jours_orange = 270, jours_rouge = 120 WHERE id = 42;
