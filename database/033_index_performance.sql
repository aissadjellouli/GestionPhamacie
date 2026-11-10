-- =====================================================================
-- Performance : index des tables qui grossissent, vues financières
-- lues seulement sur la période utile.
-- Mesuré sur un volume simulé de 5 ans (300 000 ventes, 600 000 lignes,
-- 8 000 produits) : lignes d'une vente, contrôle de traçabilité à chaque
-- encaissement, crédits des patients, chèques à remettre, soldes de
-- trésorerie, ventes à facturer.
--
-- Index créés en CONCURRENTLY : la pharmacie peut travailler pendant la
-- création. Ce script ne doit donc PAS être lancé dans une transaction
-- (psql -f l'exécute instruction par instruction : c'est le cas normal).
-- Idempotent. A exécuter après 032_inventaire_complet.sql.
-- =====================================================================

-- Une création interrompue laisse un index INVALIDE que IF NOT EXISTS sauterait ensuite :
-- on supprime d'abord ceux de ce script restés invalides (commande \gexec de psql).
SELECT format('DROP INDEX CONCURRENTLY IF EXISTS %s', i.indexrelid::regclass)
FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
WHERE NOT i.indisvalid AND c.relname IN (
  'idx_ligne_vente_vente', 'idx_ligne_vente_produit', 'idx_paiement_vente', 'idx_paiement_patient', 'idx_vente_patient', 'idx_vente_organisme',
  'idx_vente_ordonnance', 'idx_ordonnance_patient', 'idx_boite_ligne_vente', 'idx_boite_numero_serie', 'idx_boite_en_stock', 'idx_boite_reception',
  'idx_lot_reception', 'idx_registre_lot', 'idx_registre_date', 'idx_retour_ligne_retour', 'idx_ligne_inventaire_lot', 'idx_session_fermee',
  'idx_facture_ligne_ligne_vente', 'idx_facture_origine', 'idx_reglement_facture_client', 'idx_paiement_carte', 'idx_paiement_cheque',
  'idx_paiement_credit', 'idx_produit_nom_sans_accent', 'idx_dci_nom_sans_accent')
\gexec

-- ---------------------------------------------------------------------
-- 1. Index des clés étrangères lues à chaque vente, ticket, retour, crédit
-- ---------------------------------------------------------------------
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_ligne_vente_vente      ON ligne_vente (vente_id);
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_ligne_vente_produit    ON ligne_vente (produit_id);
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_paiement_vente         ON paiement (vente_id);
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_paiement_patient       ON paiement (patient_id) WHERE patient_id IS NOT NULL;
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_vente_patient          ON vente (patient_id, date_vente) WHERE patient_id IS NOT NULL;
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_vente_organisme        ON vente (organisme_id, date_vente) WHERE organisme_id IS NOT NULL;
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_vente_ordonnance       ON vente (ordonnance_id) WHERE ordonnance_id IS NOT NULL;
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_ordonnance_patient     ON ordonnance (patient_id);
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_boite_ligne_vente      ON boite (ligne_vente_id) WHERE ligne_vente_id IS NOT NULL;
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_boite_numero_serie     ON boite (numero_serie);
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_boite_en_stock         ON boite (lot_id) WHERE statut = 'en_stock';
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_boite_reception        ON boite (reception_id);
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_lot_reception          ON lot (reception_id);
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_registre_lot           ON registre_psychotrope (lot_id);
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_registre_date          ON registre_psychotrope (cree_le);
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_retour_ligne_retour    ON retour_client_ligne (retour_id);
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_ligne_inventaire_lot   ON ligne_inventaire (lot_id);
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_session_fermee         ON session_caisse (fermee_le) WHERE fermee_le IS NOT NULL;

-- Factures clients : contrôle « déjà facturée », avoirs, règlements
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_facture_ligne_ligne_vente ON facture_client_ligne (ligne_vente_id) WHERE ligne_vente_id IS NOT NULL;
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_facture_origine        ON facture_client (facture_origine_id) WHERE facture_origine_id IS NOT NULL;
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_reglement_facture_client ON reglement_facture_client (facture_id);

-- Trésorerie : paiements par carte (par date) et chèques (peu nombreux parmi tous les paiements)
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_paiement_carte         ON paiement (cree_le) WHERE mode = 'carte';
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_paiement_cheque        ON paiement (id) WHERE mode = 'cheque';
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_paiement_credit        ON paiement (vente_id) WHERE mode = 'credit';

-- Recherche produit au comptoir : nom sans accents indexé (trigrammes). La même fonction
-- sert dans la requête du logiciel (CatalogueLecture), sinon l'index ne servirait pas.
CREATE OR REPLACE FUNCTION sans_accent(t text) RETURNS text
    LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
    RETURN translate(t, 'àâäáãçéèêëíìîïñóòôöõúùûüÿœÀÂÄÁÃÇÉÈÊËÍÌÎÏÑÓÒÔÖÕÚÙÛÜŸŒ', 'aaaaaceeeeiiiinooooouuuuyoAAAAACEEEEIIIINOOOOOUUUUYO');
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_produit_nom_sans_accent ON produit USING gin (sans_accent(nom_commercial) gin_trgm_ops);
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_dci_nom_sans_accent ON dci USING gin (sans_accent(nom) gin_trgm_ops);

-- ---------------------------------------------------------------------
-- 2. Soldes de trésorerie : chaque source lue depuis la date de départ
--    du compte seulement (avant : tout l'historique des cartes regroupé
--    par jour, pour chaque compte). Même résultat que la version 030.
-- ---------------------------------------------------------------------
CREATE OR REPLACE VIEW v_solde_tresorerie AS
SELECT c.id, c.type, c.nom, c.banque, c.numero_compte, c.recoit_carte, c.actif, c.solde_initial, c.date_initiale,
       c.solde_initial
       -- Mouvements saisis
       + COALESCE((SELECT SUM(CASE WHEN m.sens = 'entree' THEN m.montant ELSE -m.montant END)
                   FROM mouvement_tresorerie m WHERE m.compte_id = c.id AND m.date_mouvement >= c.date_initiale), 0)
       -- Paiements par carte (ventes encaissées ou annulées, remboursements de crédit)
       + CASE WHEN c.recoit_carte THEN COALESCE((
             SELECT SUM(p.montant) FROM paiement p
             WHERE p.mode = 'carte' AND p.cree_le >= c.date_initiale
               AND (p.vente_id IS NULL OR EXISTS (SELECT 1 FROM vente v WHERE v.id = p.vente_id AND v.statut IN ('validee', 'annulee')))), 0) ELSE 0 END
       -- Remises de chèques
       + COALESCE((SELECT SUM(p.montant) FROM remise_cheques r JOIN remise_cheque_paiement rp ON rp.remise_id = r.id JOIN paiement p ON p.id = rp.paiement_id
                   WHERE r.compte_id = c.id AND r.date_remise >= c.date_initiale), 0)
       -- Règlements des grossistes : espèces du coffre (hors complément du tiroir), autres moyens de la banque choisie
       - COALESCE((SELECT SUM(CASE WHEN g.moyen = 'especes' THEN g.montant - g.part_tiroir ELSE g.montant END)
                   FROM reglement_fournisseur g
                   WHERE g.montant > g.part_tiroir
                     AND ((g.moyen = 'especes' AND c.type = 'coffre') OR (g.moyen <> 'especes' AND g.compte_id = c.id))
                     AND CASE WHEN g.moyen = 'effet' THEN g.echeance ELSE g.date_reglement END >= c.date_initiale), 0)
       -- Remboursements par carte (annulations et retours)
       - CASE WHEN c.recoit_carte THEN
             COALESCE((SELECT SUM(montant_rembourse) FROM annulation_vente WHERE mode = 'carte' AND montant_rembourse > 0 AND cree_le >= c.date_initiale), 0)
           + COALESCE((SELECT SUM(montant_rembourse) FROM retour_client WHERE mode = 'carte' AND montant_rembourse > 0 AND cree_le >= c.date_initiale), 0)
         ELSE 0 END AS solde
FROM compte_tresorerie c;

-- ---------------------------------------------------------------------
-- 3. Ventes facturables : retours lus ligne par ligne (avant : tous les
--    retours regroupés à chaque appel). Mêmes colonnes que la version 031.
-- ---------------------------------------------------------------------
CREATE OR REPLACE VIEW v_ligne_vente_facturable AS
SELECT v.id AS vente_id, v.numero, v.date_vente, v.patient_id, v.organisme_id, v.ordonnance_id, o.numero AS ordonnance_numero,
       l.id AS ligne_vente_id, p.nom_commercial AS designation, p.taux_tva, l.prix_unitaire, l.quantite,
       l.quantite - COALESCE(rt.quantite, 0) AS quantite_nette,
       round(l.montant_patient * (l.quantite - COALESCE(rt.quantite, 0)) / l.quantite, 2) AS montant_patient,
       round(l.montant_organisme * (l.quantite - COALESCE(rt.quantite, 0)) / l.quantite, 2) AS montant_organisme,
       v.montant_remise,
       EXISTS (SELECT 1 FROM facture_client_ligne fl JOIN facture_client f ON f.id = fl.facture_id
               WHERE fl.ligne_vente_id = l.id AND fl.part = 'patient' AND f.statut IN ('emise', 'rejetee') AND f.type = 'facture') AS facturee_patient,
       EXISTS (SELECT 1 FROM facture_client_ligne fl JOIN facture_client f ON f.id = fl.facture_id
               WHERE fl.ligne_vente_id = l.id AND fl.part = 'organisme' AND f.statut IN ('emise', 'rejetee') AND f.type = 'facture') AS facturee_organisme
FROM vente v
JOIN ligne_vente l ON l.vente_id = v.id
JOIN produit p ON p.id = l.produit_id
LEFT JOIN ordonnance o ON o.id = v.ordonnance_id
LEFT JOIN LATERAL (SELECT SUM(quantite) AS quantite FROM retour_client_ligne r WHERE r.ligne_vente_id = l.id) rt ON true
WHERE v.statut = 'validee' AND l.quantite > 0;

ANALYZE ligne_vente; ANALYZE paiement; ANALYZE vente; ANALYZE boite; ANALYZE facture_client_ligne;
