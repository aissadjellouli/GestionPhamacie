-- =====================================================================
-- Factures clients : date d'établissement et date d'échéance
--   * facture_client.date_facture reste la date d'établissement ;
--   * facture_client.date_echeance : date à laquelle la facture doit être
--     payée. Obligatoire pour une facture, jamais antérieure à la date
--     d'établissement ; un avoir n'en a pas. Les factures déjà émises
--     reçoivent leur date d'établissement plus 30 jours ;
--   * v_suivi_dettes (039) : la colonne echeance porte l'échéance des
--     factures clients ; une facture est en retard quand son échéance est
--     dépassée. Mêmes colonnes que la version 039.
-- La vue v_facture_client (031, 038) n'est pas modifiée : lui ajouter une
-- colonne empêcherait de rejouer ces scripts. L'échéance se lit dans la
-- table facture_client.
-- Idempotent. A exécuter après 039_suivi_dettes.sql.
-- =====================================================================

ALTER TABLE facture_client ADD COLUMN IF NOT EXISTS date_echeance date;
COMMENT ON COLUMN facture_client.date_facture IS 'Date d''établissement de la facture';
COMMENT ON COLUMN facture_client.date_echeance IS 'Date d''échéance : la facture doit être payée à cette date (pas d''échéance pour un avoir)';

-- Factures déjà émises : échéance à 30 jours de leur établissement
UPDATE facture_client SET date_echeance = date_facture + 30
WHERE type = 'facture' AND date_echeance IS NULL;

ALTER TABLE facture_client DROP CONSTRAINT IF EXISTS ck_facture_echeance;
ALTER TABLE facture_client ADD CONSTRAINT ck_facture_echeance CHECK (
    (type = 'facture' AND date_echeance IS NOT NULL AND date_echeance >= date_facture)
    OR (type = 'avoir' AND date_echeance IS NULL));

CREATE INDEX IF NOT EXISTS idx_facture_client_echeance ON facture_client (date_echeance) WHERE type = 'facture';

-- Sommes dues de tous les tiers : même définition que 039, avec l'échéance des factures clients
CREATE OR REPLACE VIEW v_suivi_dettes AS
-- Patients : ventes à crédit non soldées
SELECT 'creance'::text AS sens, 'patient'::text AS categorie, c.patient_id AS tiers_id,
       trim(concat_ws(' ', p.nom, p.prenom)) AS tiers, 'Vente à crédit ' || c.numero AS libelle,
       c.date_vente::date AS date_reference, NULL::date AS echeance, c.reste AS montant, false AS rejetee
FROM v_vente_credit c JOIN patient p ON p.id = c.patient_id
WHERE c.reste > 0
UNION ALL
-- Patients : dette antérieure aux ventes du logiciel, pas encore soldée
SELECT 'creance', 'patient', p.id, trim(concat_ws(' ', p.nom, p.prenom)), 'Dette antérieure (ancien logiciel ou mise en place du relevé)',
       COALESCE((SELECT min(m.cree_le) FROM credit_mouvement m WHERE m.patient_id = p.id), now())::date, NULL,
       p.solde_credit - COALESCE((SELECT SUM(c.reste) FROM v_vente_credit c WHERE c.patient_id = p.id), 0), false
FROM patient p
WHERE p.solde_credit - COALESCE((SELECT SUM(c.reste) FROM v_vente_credit c WHERE c.patient_id = p.id), 0) > 0
UNION ALL
-- Factures clients à encaisser (patient, entreprise, organisme), rejetées comprises, avec leur échéance
SELECT 'creance', CASE f.destinataire WHEN 'organisme' THEN 'facture_organisme' WHEN 'entreprise' THEN 'facture_entreprise' ELSE 'facture_patient' END,
       COALESCE(f.organisme_id, f.client_id, f.patient_id), f.destinataire_nom, 'Facture ' || f.numero,
       f.date_facture, fc.date_echeance, f.reste, f.statut = 'rejetee'
FROM v_facture_client f JOIN facture_client fc ON fc.id = f.id
WHERE f.type = 'facture' AND f.statut <> 'remplacee' AND NOT f.hors_suivi AND f.reste > 0
UNION ALL
-- Bordereaux d'organismes envoyés, pas encore payés en entier
SELECT 'creance', 'bordereau', b.organisme_id, o.nom, 'Bordereau ' || b.numero,
       COALESCE(b.date_envoi, b.periode_fin), NULL,
       b.montant_total - COALESCE((SELECT SUM(m.montant) FROM mouvement_tresorerie m WHERE m.bordereau_id = b.id AND m.sens = 'entree'), 0),
       b.statut = 'rejet_partiel'
FROM bordereau b JOIN organisme o ON o.id = b.organisme_id
WHERE b.statut IN ('envoye', 'rejet_partiel')
  AND b.montant_total - COALESCE((SELECT SUM(m.montant) FROM mouvement_tresorerie m WHERE m.bordereau_id = b.id AND m.sens = 'entree'), 0) > 0
UNION ALL
-- Grossistes : factures (réceptions) pas encore réglées en entier
SELECT 'dette', 'grossiste', ff.fournisseur_id, ff.fournisseur,
       COALESCE('Facture ' || ff.numero_facture, 'BL ' || ff.numero_bl, 'Réception'),
       ff.date_reception::date, NULL, ff.reste, false
FROM v_facture_fournisseur ff
WHERE ff.reste > 0
UNION ALL
-- Grossistes : dette hors factures connues du logiciel (ancien solde repris), pas encore réglée
SELECT 'dette', 'grossiste', f.id, f.raison_sociale, 'Ancien solde (hors factures du logiciel)',
       COALESCE((SELECT min(m.cree_le) FROM fournisseur_mouvement m WHERE m.fournisseur_id = f.id), now())::date, NULL,
       f.solde - COALESCE((SELECT SUM(ff.reste) FROM v_facture_fournisseur ff WHERE ff.fournisseur_id = f.id AND ff.reste > 0), 0), false
FROM fournisseur f
WHERE f.solde - COALESCE((SELECT SUM(ff.reste) FROM v_facture_fournisseur ff WHERE ff.fournisseur_id = f.id AND ff.reste > 0), 0) > 0
UNION ALL
-- Grossistes : effets remis, à payer à l'échéance
SELECT 'dette', 'effet', g.fournisseur_id, f.raison_sociale, 'Effet ' || COALESCE(g.reference, '') || COALESCE(' (' || g.banque || ')', ''),
       g.date_reglement, g.echeance, g.montant, false
FROM reglement_fournisseur g JOIN fournisseur f ON f.id = g.fournisseur_id
WHERE g.moyen = 'effet' AND g.echeance >= CURRENT_DATE;
