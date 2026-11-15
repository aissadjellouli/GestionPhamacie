-- =====================================================================
-- Annulations (règles du titulaire, 2026-11-15)
--   1. Annulation d'une vente dont la part organisme a été régularisée au
--      rattachement du volet (script 097) : la régularisation n'est jamais
--      supprimée, elle passe au statut « annulee » (date, auteur, motif).
--      « À la charge du patient » : la part encore due est annulée par
--      écriture inverse au relevé (annulation_credit_mouvement_id) ; la part
--      réellement payée (trop-payé) est répartie au choix du patient entre
--      ses autres dettes (annulation_trop_paye_impute, imputées par
--      credit_imputation, script 087) et un remboursement par le tiroir
--      (annulation_rembourse, annulation_mouvement_tiroir_id : sortie
--      « Remboursement d'un client », bon de sortie). Autres sorts :
--      neutralisée (aucun mouvement).
--      Le déclencheur de protection n'autorise que ce passage
--      active → annulee, sans toucher aux montants d'origine.
--      v_vente_part_organisme et v_credit_accorde ne comptent plus une
--      régularisation annulée.
--   2. Annulation d'un règlement de grossiste en espèces payé depuis le
--      tiroir : reglement_fournisseur.statut (valide, annule), jamais
--      supprimé ; ses imputations restent mais ne comptent plus dans
--      v_facture_fournisseur (factures rouvertes selon l'imputation
--      d'origine) ; la dette du grossiste revient (relevé, type
--      « annulation_reglement ») ; l'argent revient au tiroir par une
--      entrée de correction liée au mouvement d'origine (corrige_id).
-- Aucune donnée supprimée ni modifiée (colonnes ajoutées avec défaut).
-- Idempotent. A exécuter après 097_rapprochement_decisions.sql.
-- =====================================================================

-- 1. Régularisation de part organisme : statut et effet de l'annulation
ALTER TABLE regularisation_part_organisme ADD COLUMN IF NOT EXISTS statut text NOT NULL DEFAULT 'active';
ALTER TABLE regularisation_part_organisme ADD COLUMN IF NOT EXISTS annulee_le timestamptz;
ALTER TABLE regularisation_part_organisme ADD COLUMN IF NOT EXISTS annulee_par bigint REFERENCES utilisateur (id);
ALTER TABLE regularisation_part_organisme ADD COLUMN IF NOT EXISTS motif_annulation text;
ALTER TABLE regularisation_part_organisme ADD COLUMN IF NOT EXISTS annulation_credit_mouvement_id bigint REFERENCES credit_mouvement (id);
ALTER TABLE regularisation_part_organisme ADD COLUMN IF NOT EXISTS annulation_dette_annulee numeric(12,2) NOT NULL DEFAULT 0;
ALTER TABLE regularisation_part_organisme ADD COLUMN IF NOT EXISTS annulation_trop_paye_impute numeric(12,2) NOT NULL DEFAULT 0;
ALTER TABLE regularisation_part_organisme ADD COLUMN IF NOT EXISTS annulation_rembourse numeric(12,2) NOT NULL DEFAULT 0;
ALTER TABLE regularisation_part_organisme ADD COLUMN IF NOT EXISTS annulation_mouvement_tiroir_id bigint REFERENCES mouvement_tiroir (id);
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_regularisation_statut') THEN
        ALTER TABLE regularisation_part_organisme ADD CONSTRAINT ck_regularisation_statut CHECK (
            (statut = 'active' AND annulee_le IS NULL AND annulee_par IS NULL)
            OR (statut = 'annulee' AND annulee_le IS NOT NULL AND annulee_par IS NOT NULL));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_regularisation_annulation_montants') THEN
        ALTER TABLE regularisation_part_organisme ADD CONSTRAINT ck_regularisation_annulation_montants CHECK (
            annulation_dette_annulee >= 0 AND annulation_trop_paye_impute >= 0 AND annulation_rembourse >= 0
            AND annulation_dette_annulee + annulation_trop_paye_impute + annulation_rembourse <= montant_patient);
    END IF;
END $$;
COMMENT ON COLUMN regularisation_part_organisme.statut IS 'active ; annulee : vente annulée (script 098), jamais supprimée';
COMMENT ON COLUMN regularisation_part_organisme.annulation_dette_annulee IS 'Part encore due annulée par écriture inverse (annulation_credit_mouvement_id)';
COMMENT ON COLUMN regularisation_part_organisme.annulation_trop_paye_impute IS 'Trop-payé déduit des autres dettes du patient, à son choix';
COMMENT ON COLUMN regularisation_part_organisme.annulation_rembourse IS 'Trop-payé rendu par le tiroir (annulation_mouvement_tiroir_id)';

-- Protection : ni suppression, ni modification, sauf le passage unique active → annulee (montants d'origine inchangés)
CREATE OR REPLACE FUNCTION proteger_regularisation_part_organisme() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND OLD.statut = 'active' AND NEW.statut = 'annulee'
       AND (NEW.id, NEW.vente_id, NEW.organisme_id, NEW.part_organisme_vente, NEW.part_organisme_chifa, NEW.ecart, NEW.sort, NEW.montant_patient,
            NEW.montant_dette_diminuee, NEW.montant_a_rendre, NEW.montant_pharmacie, NEW.credit_mouvement_id, NEW.utilisateur_id, NEW.valide_par, NEW.cree_le)
           IS NOT DISTINCT FROM
           (OLD.id, OLD.vente_id, OLD.organisme_id, OLD.part_organisme_vente, OLD.part_organisme_chifa, OLD.ecart, OLD.sort, OLD.montant_patient,
            OLD.montant_dette_diminuee, OLD.montant_a_rendre, OLD.montant_pharmacie, OLD.credit_mouvement_id, OLD.utilisateur_id, OLD.valide_par, OLD.cree_le)
    THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'Une régularisation de part organisme ne se modifie pas et ne se supprime pas (seule l''annulation de sa vente la marque annulée)';
END $$;

CREATE OR REPLACE VIEW v_vente_part_organisme AS
SELECT v.id AS vente_id, v.organisme_id, v.part_organisme AS part_organisme_caisse,
       COALESCE(r.part_organisme_chifa, v.part_organisme) AS part_organisme_retenue
FROM vente v
LEFT JOIN regularisation_part_organisme r ON r.vente_id = v.id AND r.statut = 'active';

CREATE OR REPLACE VIEW v_credit_accorde AS
SELECT v.id AS vente_id, v.patient_id, v.numero, v.date_vente,
       x.montant - COALESCE((SELECT SUM(rc.montant_rembourse) FROM retour_client rc WHERE rc.vente_id = v.id), 0) AS montant
FROM vente v
JOIN LATERAL (SELECT COALESCE((SELECT SUM(p.montant) FROM paiement p WHERE p.vente_id = v.id AND p.mode = 'credit'), 0)
                   + COALESCE((SELECT SUM(r.montant_patient) FROM regularisation_part_organisme r
                               WHERE r.vente_id = v.id AND r.sort = 'charge_patient' AND r.statut = 'active'), 0) AS montant) x ON x.montant > 0
WHERE v.statut = 'validee' AND v.patient_id IS NOT NULL;

-- 2. Règlement de grossiste : statut et annulation
ALTER TABLE reglement_fournisseur ADD COLUMN IF NOT EXISTS statut text NOT NULL DEFAULT 'valide';
ALTER TABLE reglement_fournisseur ADD COLUMN IF NOT EXISTS annule_le timestamptz;
ALTER TABLE reglement_fournisseur ADD COLUMN IF NOT EXISTS annule_par bigint REFERENCES utilisateur (id);
ALTER TABLE reglement_fournisseur ADD COLUMN IF NOT EXISTS motif_annulation text;
ALTER TABLE reglement_fournisseur ADD COLUMN IF NOT EXISTS annulation_valide_par bigint REFERENCES utilisateur (id);
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_reglement_fournisseur_statut') THEN
        ALTER TABLE reglement_fournisseur ADD CONSTRAINT ck_reglement_fournisseur_statut CHECK (
            (statut = 'valide' AND annule_le IS NULL AND annule_par IS NULL AND motif_annulation IS NULL)
            OR (statut = 'annule' AND annule_le IS NOT NULL AND annule_par IS NOT NULL
                AND char_length(btrim(motif_annulation)) BETWEEN 3 AND 300 AND moyen = 'especes' AND part_tiroir = montant));
    END IF;
END $$;
COMMENT ON COLUMN reglement_fournisseur.statut IS 'valide ; annule : règlement en espèces payé depuis le tiroir et annulé (script 098), jamais supprimé';
COMMENT ON COLUMN reglement_fournisseur.annulation_valide_par IS 'Ayant droit achats.regler qui a autorisé l''annulation par son code comptoir (null : droit propre)';

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fournisseur_mouvement_type_check'
                   AND pg_get_constraintdef(oid) LIKE '%annulation_reglement%') THEN
        ALTER TABLE fournisseur_mouvement DROP CONSTRAINT IF EXISTS fournisseur_mouvement_type_check;
        ALTER TABLE fournisseur_mouvement ADD CONSTRAINT fournisseur_mouvement_type_check
            CHECK (type IN ('ouverture', 'reprise', 'reception', 'reglement', 'avoir', 'correction', 'annulation_reglement'));
    END IF;
END $$;

-- Factures des grossistes : les imputations d'un règlement annulé ne comptent plus (factures rouvertes)
CREATE OR REPLACE VIEW v_facture_fournisseur AS
SELECT r.id AS reception_id, r.fournisseur_id, f.raison_sociale AS fournisseur, r.numero_facture, r.numero_bl, r.date_reception,
       COALESCE(r.montant_ttc, 0) AS montant,
       COALESCE((SELECT sum(x.montant) FROM reglement_facture x JOIN reglement_fournisseur g ON g.id = x.reglement_id
                 WHERE x.reception_id = r.id AND g.statut = 'valide'), 0) AS regle,
       COALESCE(r.montant_ttc, 0) - COALESCE((SELECT sum(x.montant) FROM reglement_facture x JOIN reglement_fournisseur g ON g.id = x.reglement_id
                                               WHERE x.reception_id = r.id AND g.statut = 'valide'), 0) AS reste
FROM reception r
JOIN fournisseur f ON f.id = r.fournisseur_id;
