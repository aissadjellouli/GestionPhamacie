-- =====================================================================
-- Vues : réglages lus par colonnes nommées, libellé nommé dans la
-- trésorerie (structure identique entre base neuve et base mise à jour)
--   * v_etat_stock et v_risque_peremption lisaient leurs réglages par
--     « SELECT * FROM parametre_alerte » : le « * » est figé à la création
--     de la vue ; une base dont tous les scripts sont rejoués (restauration
--     d'une sauvegarde) embarquait alors les colonnes ajoutées ensuite à
--     parametre_alerte (053) et différait de init/schema.sql.
--   * Les deux vues sont recréées à l'identique (mêmes colonnes, même
--     calcul) ; seule la lecture des réglages nomme ses colonnes.
--   * v_mouvement_tresorerie (030) : la ligne « Remboursement carte
--     (retour) » reçoit le nom de colonne « libelle », comme la ligne
--     d'annulation ; même résultat, mais une base créée par
--     init/schema.sql a alors exactement la même structure qu'une base
--     mise à jour par les scripts.
-- Idempotent. A exécuter après 061_tva_sorties.sql.
-- =====================================================================

-- Mêmes colonnes et même calcul que 020_seuils_stock.sql
CREATE OR REPLACE VIEW v_etat_stock AS
WITH prm AS (
    SELECT delai_fournisseur_jours, jours_securite, jours_cible_commande, jours_dormant, jours_surstock
    FROM parametre_alerte WHERE id = 1
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

-- Mêmes colonnes et même calcul que 021_prix_boite.sql
CREATE OR REPLACE VIEW v_risque_peremption AS
WITH prm AS (
    SELECT jours_orange, jours_rouge, horizon_risque_jours
    FROM parametre_alerte WHERE id = 1
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

-- Même définition que 030_tresorerie.sql, colonne nommée sur les deux lignes du UNION
CREATE OR REPLACE VIEW v_mouvement_tresorerie AS
SELECT m.compte_id, m.date_mouvement AS date, m.sens, m.montant, m.nature, m.categorie, m.libelle, m.reference,
       u.nom_complet AS employe, 'saisi'::text AS origine, m.id AS source_id, m.cree_le
FROM mouvement_tresorerie m JOIN utilisateur u ON u.id = m.utilisateur_id
UNION ALL
-- Paiements par carte du jour (ventes et remboursements de crédit) vers la banque « carte »
SELECT c.id, p.cree_le::date, 'entree', SUM(p.montant), 'carte', NULL, 'Paiements par carte du jour', NULL,
       NULL, 'calcule', NULL, max(p.cree_le)
FROM paiement p
JOIN compte_tresorerie c ON c.recoit_carte
LEFT JOIN vente v ON v.id = p.vente_id
WHERE p.mode = 'carte' AND (p.vente_id IS NULL OR v.statut IN ('validee', 'annulee'))
GROUP BY c.id, p.cree_le::date
UNION ALL
-- Remises de chèques en banque
SELECT r.compte_id, r.date_remise, 'entree', SUM(p.montant), 'remise_cheques', NULL,
       format('Remise de %s chèque(s)', count(*)), r.reference, u.nom_complet, 'calcule', r.id, r.cree_le
FROM remise_cheques r
JOIN remise_cheque_paiement rp ON rp.remise_id = r.id
JOIN paiement p ON p.id = rp.paiement_id
JOIN utilisateur u ON u.id = r.utilisateur_id
GROUP BY r.id, u.nom_complet
UNION ALL
-- Règlements des grossistes : espèces du coffre (hors complément du tiroir), autres moyens de la banque choisie
SELECT CASE WHEN g.moyen = 'especes' THEN (SELECT id FROM compte_tresorerie WHERE type = 'coffre') ELSE g.compte_id END,
       CASE WHEN g.moyen = 'effet' THEN g.echeance ELSE g.date_reglement END,
       'sortie', CASE WHEN g.moyen = 'especes' THEN g.montant - g.part_tiroir ELSE g.montant END,
       'reglement_grossiste', NULL, 'Règlement ' || f.raison_sociale, g.reference, u.nom_complet, 'calcule', g.id, g.cree_le
FROM reglement_fournisseur g
JOIN fournisseur f ON f.id = g.fournisseur_id
JOIN utilisateur u ON u.id = g.utilisateur_id
WHERE g.montant > g.part_tiroir
  AND (g.moyen = 'especes' OR g.compte_id IS NOT NULL)
UNION ALL
-- Remboursements par carte (annulations et retours) : sortent de la banque « carte »
SELECT c.id, x.cree_le::date, 'sortie', x.montant_rembourse, 'remboursement_carte', NULL, x.libelle, NULL, NULL, 'calcule', NULL, x.cree_le
FROM (SELECT cree_le, montant_rembourse, 'Remboursement carte (annulation)' AS libelle FROM annulation_vente WHERE mode = 'carte' AND montant_rembourse > 0
      UNION ALL
      SELECT cree_le, montant_rembourse, 'Remboursement carte (retour)' AS libelle FROM retour_client WHERE mode = 'carte' AND montant_rembourse > 0) x
JOIN compte_tresorerie c ON c.recoit_carte;
