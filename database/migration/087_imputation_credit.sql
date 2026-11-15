-- =====================================================================
-- Imputation des règlements de crédit vente par vente (règle du
-- titulaire, 2026-11-15)
--   * Avoir ou règlement LIÉ À UNE FACTURE : il solde d'abord les ventes à
--     crédit de cette facture, jamais une autre dette plus ancienne.
--   * Règlement de crédit au comptoir (sans facture) : le patient choisit
--     les ventes à solder ; le montant est imputé sur les ventes cochées.
--   * credit_imputation : part d'un mouvement de crédit (remboursement,
--     correction par avoir) rattachée à UNE vente ; un mouvement peut
--     couvrir plusieurs ventes (plusieurs lignes).
--   * v_vente_credit : réglé d'une vente = ses imputations + sa part du
--     reliquat non rattaché (historique : ouverture + ventes - dette -
--     imputations), réparti des plus anciennes aux plus récentes comme
--     avant. Mêmes colonnes. Sans imputation, résultat identique à 038.
--   * v_facture_client inchangée : elle lit le réglé de v_vente_credit.
--   * Paiements déjà enregistrés : aucune imputation créée, ils restent
--     dans le reliquat (plus anciennes d'abord). Aucune donnée modifiée.
--   * patient.solde_credit et ses déclencheurs ne changent pas.
-- Idempotent. A exécuter après 086_seuil_ecart_chifa.sql.
-- =====================================================================

CREATE TABLE IF NOT EXISTS credit_imputation (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    credit_mouvement_id bigint NOT NULL REFERENCES credit_mouvement(id),
    vente_id bigint NOT NULL REFERENCES vente(id),
    montant numeric(12,2) NOT NULL CONSTRAINT ck_credit_imputation_montant CHECK (montant > 0),
    cree_le timestamp with time zone NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_credit_imputation_vente ON credit_imputation (vente_id);
CREATE INDEX IF NOT EXISTS ix_credit_imputation_mouvement ON credit_imputation (credit_mouvement_id);

CREATE OR REPLACE VIEW v_vente_credit AS
 SELECT vente_id,
    patient_id,
    numero,
    date_vente,
    montant,
    LEAST(impute + LEAST(GREATEST(libre - ((ouverture + cumul) - capacite), 0::numeric), capacite), montant) AS regle,
    montant - LEAST(impute + LEAST(GREATEST(libre - ((ouverture + cumul) - capacite), 0::numeric), capacite), montant) AS reste
   FROM ( SELECT a.vente_id,
            a.patient_id,
            a.numero,
            a.date_vente,
            a.montant,
            LEAST(COALESCE(i.impute, 0::numeric), a.montant) AS impute,
            a.montant - LEAST(COALESCE(i.impute, 0::numeric), a.montant) AS capacite,
            COALESCE(o.montant, 0::numeric) AS ouverture,
            sum(a.montant - LEAST(COALESCE(i.impute, 0::numeric), a.montant))
                OVER (PARTITION BY a.patient_id ORDER BY a.date_vente, a.vente_id) AS cumul,
            GREATEST(COALESCE(o.montant, 0::numeric) + sum(a.montant) OVER (PARTITION BY a.patient_id) - p.solde_credit
                - sum(LEAST(COALESCE(i.impute, 0::numeric), a.montant)) OVER (PARTITION BY a.patient_id), 0::numeric) AS libre
           FROM v_credit_accorde a
             JOIN patient p ON p.id = a.patient_id
             LEFT JOIN credit_ouverture o ON o.patient_id = a.patient_id
             LEFT JOIN ( SELECT ci.vente_id, sum(ci.montant) AS impute
                           FROM credit_imputation ci
                          GROUP BY ci.vente_id) i ON i.vente_id = a.vente_id
          WHERE a.montant > 0::numeric) t;
