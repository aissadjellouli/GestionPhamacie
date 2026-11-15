-- =====================================================================
-- Avoir sur une facture passée en dette du patient (règle du titulaire,
-- 2026-11-15)
--   * Une facture émise non payée dont les ventes sont à crédit : son dû
--     est dans la dette du patient. Le patient rapporte un produit : on
--     rattache un avoir à la facture ; le reste dû de la facture ET la
--     dette du patient baissent du même montant, une seule fois.
--   * À l'émission de l'avoir, l'application diminue la dette de
--     min(avoir, reste dû à crédit des ventes de la facture) (mouvement de
--     crédit « correction », référence « Avoir AV-… sur facture F-… »,
--     vente rattachée) et garde ce montant dans facture_client.part_credit
--     de l'avoir (sur le modèle de reglement_facture_client.part_credit).
--   * v_facture_client : ce qui a diminué la dette apparaît déjà comme
--     réglé sur les ventes à crédit ; l'avoir ne retire donc du reste que
--     sum(montant_ttc - part_credit) (sinon double compte : 80 au lieu de
--     100 pour 120 à crédit et un avoir de 20). Mêmes colonnes.
--   * Sans rattrapage : les avoirs déjà émis gardent part_credit = 0.
-- Idempotent. A exécuter après 083_fuseau_horaire.sql.
-- =====================================================================

ALTER TABLE facture_client ADD COLUMN IF NOT EXISTS part_credit numeric(12,2) NOT NULL DEFAULT 0;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_facture_client_part_credit' AND conrelid = 'facture_client'::regclass) THEN
        ALTER TABLE facture_client ADD CONSTRAINT ck_facture_client_part_credit CHECK (
            part_credit >= 0 AND (part_credit = 0 OR (type = 'avoir' AND part_credit <= montant_ttc)));
    END IF;
END $$;

CREATE OR REPLACE VIEW v_facture_client AS
 SELECT f.id,
    f.type,
    f.numero,
    f.date_facture,
    f.destinataire,
    f.patient_id,
    f.client_id,
    f.organisme_id,
    f.destinataire_nom,
    f.destinataire_infos,
    f.periode_du,
    f.periode_au,
    f.montant_ht,
    f.montant_tva,
    f.montant_ttc,
    f.deja_paye,
    f.statut,
    f.version,
    f.motif_rejet,
    f.remplace_id,
    f.remplacee_par,
    f.facture_origine_id,
    f.motif,
    f.hors_suivi,
    f.motif_hors_suivi,
    f.commentaire,
    f.utilisateur_id,
    f.cree_le,
    COALESCE(( SELECT sum(a.montant_ttc) AS sum
           FROM facture_client a
          WHERE a.facture_origine_id = f.id AND a.type = 'avoir'), 0::numeric) AS avoirs,
    COALESCE(( SELECT sum(r.montant) AS sum
           FROM reglement_facture_client r
          WHERE r.facture_id = f.id), 0::numeric) AS regle,
        CASE
            WHEN f.type = 'avoir' OR f.statut = 'remplacee' THEN 0::numeric
            ELSE GREATEST(f.montant_ttc - f.deja_paye
              - COALESCE(( SELECT sum(a.montant_ttc - a.part_credit) AS sum
                   FROM facture_client a
                  WHERE a.facture_origine_id = f.id AND a.type = 'avoir'), 0::numeric)
              - COALESCE(( SELECT sum(r.montant - r.part_credit) AS sum
                   FROM reglement_facture_client r
                  WHERE r.facture_id = f.id), 0::numeric)
              - COALESCE(( SELECT sum(c.regle) AS sum
                   FROM v_vente_credit c
                  WHERE c.vente_id IN ( SELECT fl.vente_id
                           FROM facture_client_ligne fl
                          WHERE fl.facture_id = f.id AND fl.part = 'patient' AND fl.vente_id IS NOT NULL)), 0::numeric), 0::numeric)
        END AS reste,
    u.nom_complet AS employe,
    ( SELECT x.numero
           FROM facture_client x
          WHERE x.id = f.remplace_id) AS remplace_numero,
    ( SELECT x.numero
           FROM facture_client x
          WHERE x.id = f.remplacee_par) AS remplacee_par_numero,
    ( SELECT x.numero
           FROM facture_client x
          WHERE x.id = f.facture_origine_id) AS origine_numero
   FROM facture_client f
     JOIN utilisateur u ON u.id = f.utilisateur_id;
