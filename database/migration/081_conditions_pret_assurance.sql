-- =====================================================================
-- Conditions d'un prêt (taux, durée), assurance des échéances,
-- psychotrope dont la substance est un stupéfiant à vérifier
--   * pret_bancaire.taux (taux annuel en %, 0 à 100, 4 décimales au plus)
--     et pret_bancaire.duree_mois (1 à 600) : saisis tels qu'au contrat,
--     facultatifs (prêts déjà enregistrés), jamais recalculés ;
--   * echeance_pret.assurance : part de l'échéance (facultative, 0 par
--     défaut) ; capital + intérêts + TVA + marge + assurance = échéance
--     (contrainte echeance_pret_check reposée avec garde : l'ancienne,
--     sans l'assurance, est retirée). Au paiement, l'assurance va avec les
--     intérêts, la TVA et la marge dans la dépense « charges_financieres » ;
--   * correspondance_echeancier.col_assurance et
--     correspondance_echeancier_preteur.col_assurance : colonne de
--     l'assurance dans le tableau de la banque, retenue par banque ou par
--     prêteur hors trésorerie ;
--   * vues nouvelles v_echeance_pret_complete (colonnes de v_echeance_pret,
--     puis l'assurance) et v_pret_bancaire_complet (colonnes de
--     v_pret_bancaire, puis taux, durée et assurance de la prochaine
--     échéance non payée) : v_echeance_pret, v_pret_bancaire et
--     v_dette_financement sont inchangées ;
--   * v_produit_a_verifier : la classe proposée est la plus stricte de
--     celle de la substance surveillée (DCI) et de celle de la nomenclature
--     (n° d'enregistrement), comme au classement d'un produit ; un
--     psychotrope dont elle est stupéfiant est signalé « classe à
--     corriger » (motif « classe »), sauf classe confirmée par le
--     titulaire (080). Mêmes colonnes, dans le même ordre.
-- Idempotent. A exécuter après 080_classe_confirmee_titulaire.sql.
-- =====================================================================

-- ---- Conditions du prêt, saisies telles qu'au contrat ---------------------------------------
ALTER TABLE pret_bancaire ADD COLUMN IF NOT EXISTS taux numeric(7,4) CHECK (taux >= 0 AND taux <= 100);
ALTER TABLE pret_bancaire ADD COLUMN IF NOT EXISTS duree_mois int CHECK (duree_mois BETWEEN 1 AND 600);
COMMENT ON COLUMN pret_bancaire.taux IS 'Taux annuel du contrat, en % (0 à 100, 4 décimales au plus) ; facultatif ; saisi, jamais recalculé';
COMMENT ON COLUMN pret_bancaire.duree_mois IS 'Durée du contrat en mois (1 à 600) ; facultative ; saisie, jamais recalculée';

-- ---- Assurance : part de chaque échéance ------------------------------------------------------
ALTER TABLE echeance_pret ADD COLUMN IF NOT EXISTS assurance numeric(12,2) NOT NULL DEFAULT 0 CHECK (assurance >= 0);
COMMENT ON COLUMN echeance_pret.assurance IS 'Assurance comprise dans l''échéance, facultative ; payée avec les intérêts en charges financières';

-- Égalité de chaque ligne : l'ancienne (sans l'assurance) est retirée, la nouvelle posée une fois
DO $$
DECLARE
    k record;
BEGIN
    FOR k IN SELECT conname FROM pg_constraint
             WHERE conrelid = 'echeance_pret'::regclass AND contype = 'c'
               AND pg_get_constraintdef(oid) LIKE '%= montant%' AND pg_get_constraintdef(oid) NOT LIKE '%assurance%'
    LOOP
        EXECUTE format('ALTER TABLE echeance_pret DROP CONSTRAINT %I', k.conname);
    END LOOP;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'echeance_pret_check' AND conrelid = 'echeance_pret'::regclass) THEN
        ALTER TABLE echeance_pret ADD CONSTRAINT echeance_pret_check CHECK (capital + interets + tva + marge + assurance = montant);
    END IF;
END $$;

COMMENT ON TABLE paiement_echeance_pret IS 'Échéance de prêt payée, confirmée par le titulaire : capital (remboursement_financement) et charges financières (intérêts, assurance, TVA, marge : dépense) sortis du coffre ou de la banque choisie';

-- ---- Colonne de l'assurance dans le tableau de la banque, retenue ------------------------------
ALTER TABLE correspondance_echeancier ADD COLUMN IF NOT EXISTS col_assurance int CHECK (col_assurance >= 0);
ALTER TABLE correspondance_echeancier_preteur ADD COLUMN IF NOT EXISTS col_assurance int CHECK (col_assurance >= 0);

-- ---- Vues nouvelles (les vues de 078 et 079 gardent leurs colonnes) -----------------------------
CREATE OR REPLACE VIEW v_echeance_pret_complete AS
SELECT v.id, v.pret_id, v.pret, v.banque_id, v.banque, v.rang, v.date_echeance, v.capital, v.interets, v.tva, v.marge, v.montant,
       v.capital_restant, v.date_paiement, v.moyen, v.compte_id, v.compte, v.reference, v.part_tiroir, v.employe, e.assurance
FROM v_echeance_pret v
JOIN echeance_pret e ON e.id = v.id;

CREATE OR REPLACE VIEW v_pret_bancaire_complet AS
SELECT v.id, v.compte_id, v.banque, v.reference, v.objet, v.montant, v.date_pret, v.document, v.capital_rembourse, v.reste_du, v.utilise,
       v.echeances, v.echeances_payees, v.echues_non_payees, v.prochaine_echeance, v.prochain_montant, v.employe, v.cree_le,
       p.taux, p.duree_mois,
       (SELECT e.assurance FROM echeance_pret e
        WHERE e.pret_id = v.id AND NOT EXISTS (SELECT 1 FROM paiement_echeance_pret x WHERE x.echeance_id = e.id)
        ORDER BY e.rang LIMIT 1) AS prochaine_assurance
FROM v_pret_bancaire v
JOIN pret_bancaire p ON p.id = v.id;

-- ---- Produits à vérifier : psychotrope dont la substance est un stupéfiant ----------------------
-- Mêmes colonnes que la version 080 ; classe proposée : la plus stricte de la DCI et de la nomenclature
CREATE OR REPLACE VIEW v_produit_a_verifier AS
SELECT p.id AS produit_id, p.nom_commercial, p.dosage, p.forme, p.classe,
       d.nom AS dci, p.num_enregistrement,
       cp.classe AS classe_proposee,
       n.situation,
       CASE WHEN p.classe_confirmee_par IS NULL
                 AND ((cp.classe IN ('psychotrope', 'stupefiant') AND p.classe NOT IN ('psychotrope', 'stupefiant'))
                      OR (cp.classe = 'stupefiant' AND p.classe = 'psychotrope')) THEN 'classe'
            WHEN p.a_verifier THEN 'repris'
            WHEN n.situation IS NULL THEN 'absent'
            ELSE 'situation' END AS motif
FROM produit p
LEFT JOIN dci d ON d.id = p.dci_id
LEFT JOIN LATERAL (SELECT s.classe FROM substance_surveillee s
                   WHERE majuscule_sans_accent(d.nom) LIKE '%' || s.motif || '%'
                   ORDER BY s.classe DESC LIMIT 1) sd ON true
LEFT JOIN LATERAL (SELECT v.classe_proposee, v.situation FROM v_nomenclature v
                   WHERE v.num_enregistrement = p.num_enregistrement
                   ORDER BY v.situation = 'en_vigueur' DESC LIMIT 1) n ON true
CROSS JOIN LATERAL (SELECT CASE WHEN sd.classe = 'stupefiant' OR n.classe_proposee = 'stupefiant' THEN 'stupefiant'
                                ELSE COALESCE(sd.classe, n.classe_proposee) END AS classe) cp
WHERE p.actif
  AND ((p.classe_confirmee_par IS NULL
        AND ((cp.classe IN ('psychotrope', 'stupefiant') AND p.classe NOT IN ('psychotrope', 'stupefiant'))
             OR (cp.classe = 'stupefiant' AND p.classe = 'psychotrope')))
       OR p.a_verifier
       OR n.situation IN ('retire', 'non_renouvele')
       -- N° d'enregistrement renseigné mais introuvable dans la nomenclature chargée
       OR (n.situation IS NULL AND NULLIF(trim(p.num_enregistrement), '') IS NOT NULL
           AND EXISTS (SELECT 1 FROM nomenclature_produit)));
