-- =====================================================================
-- Libellé « Encaissement de dette » dans la trésorerie (2026-11-15)
--   * v_cheque_a_remettre : un chèque reçu sans vente (paiement d'une
--     dette au comptoir) avait pour origine « Règlement de crédit » ;
--     il devient « Encaissement de dette », comme au relevé de crédit,
--     au reçu et au panneau Crédit de la caisse.
--   * Mêmes colonnes, même ordre, même filtre (CREATE OR REPLACE VIEW).
--   * Aucun code ne compare ce texte (affiché seulement).
--   * Aucune donnée modifiée ni supprimée.
-- Idempotent. A exécuter après 087_imputation_credit.sql.
-- =====================================================================

CREATE OR REPLACE VIEW v_cheque_a_remettre AS
SELECT p.id AS paiement_id, p.cree_le, p.montant,
       COALESCE(v.numero, 'Encaissement de dette') AS origine,
       NULLIF(trim(concat_ws(' ', pa.nom, pa.prenom)), '') AS patient
FROM paiement p
LEFT JOIN vente v ON v.id = p.vente_id
LEFT JOIN patient pa ON pa.id = COALESCE(v.patient_id, p.patient_id)
WHERE p.mode = 'cheque'
  AND (p.vente_id IS NULL OR v.statut = 'validee')
  AND NOT EXISTS (SELECT 1 FROM remise_cheque_paiement r WHERE r.paiement_id = p.id);
