-- =====================================================================
-- Retour arrière du script 170 (prêts : échéance payée en plusieurs fois, remboursement anticipé)
--   REFUSE (rien n'est modifié) s'il existe un paiement partiel (échéance payée en plusieurs fois, ou paiement différent de
--   l'échéance) ou un remboursement anticipé : les vues d'avant le script 170 ne savent pas les compter. Sinon : vues remises dans
--   leur version des scripts 079 et 081, vue v_echeance_pret_paye, table remboursement_anticipe_pret (vide) et colonne
--   pret_bancaire.echeancier_a_remplacer supprimées, colonnes montant / capital_paye / charges_payees retirées des paiements,
--   contrainte unique sur echeance_id remise.
--   Rejouable sans erreur. A lancer seul, hors de la suite numérotée : ce dossier n'est jamais lu par les outils de mise à jour.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

DO $garde$ BEGIN
    IF to_regclass('remboursement_anticipe_pret') IS NOT NULL AND EXISTS (SELECT 1 FROM remboursement_anticipe_pret) THEN
        RAISE EXCEPTION 'Retour du script 170 refusé : des remboursements anticipés sont enregistrés. Rien n''a été modifié.';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'paiement_echeance_pret' AND column_name = 'montant')
       AND EXISTS (SELECT 1 FROM paiement_echeance_pret p JOIN echeance_pret e ON e.id = p.echeance_id
                   WHERE p.montant <> e.montant OR (SELECT count(*) FROM paiement_echeance_pret x WHERE x.echeance_id = p.echeance_id) > 1) THEN
        RAISE EXCEPTION 'Retour du script 170 refusé : une échéance est payée en plusieurs fois. Rien n''a été modifié.';
    END IF;
END $garde$;

-- Vues des scripts 079 et 081
CREATE OR REPLACE VIEW v_echeance_pret AS
SELECT e.id, e.pret_id, p.reference AS pret, p.compte_id AS banque_id, COALESCE(c.nom, p.preteur) AS banque, e.rang, e.date_echeance, e.capital,
       e.interets, e.tva, e.marge, e.montant, e.capital_restant, pe.date_paiement, pe.moyen, pe.compte_id, cp.nom AS compte, pe.reference,
       pe.part_tiroir, u.nom_complet AS employe
FROM echeance_pret e
JOIN pret_bancaire p ON p.id = e.pret_id
LEFT JOIN compte_tresorerie c ON c.id = p.compte_id
LEFT JOIN paiement_echeance_pret pe ON pe.echeance_id = e.id
LEFT JOIN compte_tresorerie cp ON cp.id = pe.compte_id
LEFT JOIN utilisateur u ON u.id = pe.utilisateur_id;

CREATE OR REPLACE VIEW v_pret_bancaire AS
SELECT p.id, p.compte_id, COALESCE(c.nom, p.preteur) AS banque, p.reference, p.objet, p.montant, p.date_pret, p.document,
       COALESCE(e.capital_paye, 0) AS capital_rembourse,
       p.montant - COALESCE(e.capital_paye, 0) AS reste_du,
       COALESCE((SELECT sum(x.montant) FROM utilisation_financement x WHERE x.pret_id = p.id), 0) AS utilise,
       COALESCE(e.nombre, 0)::int AS echeances,
       COALESCE(e.payees, 0)::int AS echeances_payees,
       COALESCE(e.echues, 0)::int AS echues_non_payees,
       (SELECT v.date_echeance FROM v_echeance_pret v WHERE v.pret_id = p.id AND v.date_paiement IS NULL ORDER BY v.rang LIMIT 1) AS prochaine_echeance,
       (SELECT v.montant FROM v_echeance_pret v WHERE v.pret_id = p.id AND v.date_paiement IS NULL ORDER BY v.rang LIMIT 1) AS prochain_montant,
       u.nom_complet AS employe, p.cree_le
FROM pret_bancaire p
LEFT JOIN compte_tresorerie c ON c.id = p.compte_id
JOIN utilisateur u ON u.id = p.utilisateur_id
LEFT JOIN LATERAL (
    SELECT count(*) AS nombre, count(v.date_paiement) AS payees,
           sum(v.capital) FILTER (WHERE v.date_paiement IS NOT NULL) AS capital_paye,
           count(*) FILTER (WHERE v.date_paiement IS NULL AND v.date_echeance < current_date) AS echues
    FROM v_echeance_pret v WHERE v.pret_id = p.id) e ON true;

CREATE OR REPLACE VIEW v_pret_bancaire_complet AS
SELECT v.id, v.compte_id, v.banque, v.reference, v.objet, v.montant, v.date_pret, v.document, v.capital_rembourse, v.reste_du, v.utilise,
       v.echeances, v.echeances_payees, v.echues_non_payees, v.prochaine_echeance, v.prochain_montant, v.employe, v.cree_le,
       p.taux, p.duree_mois,
       (SELECT e.assurance FROM echeance_pret e
        WHERE e.pret_id = v.id AND NOT EXISTS (SELECT 1 FROM paiement_echeance_pret x WHERE x.echeance_id = e.id)
        ORDER BY e.rang LIMIT 1) AS prochaine_assurance
FROM v_pret_bancaire v
JOIN pret_bancaire p ON p.id = v.id;

DROP VIEW IF EXISTS v_echeance_pret_paye;

DELETE FROM sync_table WHERE nom_table = 'remboursement_anticipe_pret';
DROP TABLE IF EXISTS remboursement_anticipe_pret;
DO $$ BEGIN PERFORM sync_installer_declencheurs(); END $$;

ALTER TABLE pret_bancaire DROP COLUMN IF EXISTS echeancier_a_remplacer;

DROP INDEX IF EXISTS ix_paiement_echeance_pret_echeance;
ALTER TABLE paiement_echeance_pret DROP CONSTRAINT IF EXISTS ck_paiement_echeance_pret_parts;
ALTER TABLE paiement_echeance_pret DROP COLUMN IF EXISTS montant;
ALTER TABLE paiement_echeance_pret DROP COLUMN IF EXISTS capital_paye;
ALTER TABLE paiement_echeance_pret DROP COLUMN IF EXISTS charges_payees;
DO $garde$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'paiement_echeance_pret_echeance_id_key') THEN
        ALTER TABLE paiement_echeance_pret ADD CONSTRAINT paiement_echeance_pret_echeance_id_key UNIQUE (echeance_id);
    END IF;
END $garde$;
