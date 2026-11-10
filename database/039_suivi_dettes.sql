-- =====================================================================
-- Suivi des dettes et des créances de tous les tiers
--   * fournisseur_mouvement : relevé de la dette envers chaque grossiste
--     (réception, règlement, avoir de retour, reprise, correction), écrit
--     par un déclencheur à chaque changement de fournisseur.solde, comme
--     le relevé des patients (038). Non modifiable.
--   * parametre_caisse.delai_alerte_dette_jours : ancienneté à partir de
--     laquelle une dette ou une créance est signalée (30 jours par défaut) ;
--   * v_suivi_dettes : une ligne par somme due, pour tous les tiers :
--     patients (ventes à crédit non soldées, dette reprise), factures
--     clients à encaisser (patients, entreprises, organismes), bordereaux
--     d'organismes envoyés et non payés, factures de grossistes à payer,
--     ancien solde des grossistes (dette reprise, hors factures),
--     effets remis aux grossistes dont l'échéance n'est pas passée.
-- Idempotent. A exécuter après 038_releve_credit.sql.
-- =====================================================================

-- ---------------------------------------------------------------------
-- 1. Relevé de la dette envers les grossistes
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS fournisseur_mouvement (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    fournisseur_id  bigint        NOT NULL REFERENCES fournisseur(id),
    type            text          NOT NULL CHECK (type IN ('ouverture', 'reprise', 'reception', 'reglement', 'avoir', 'correction')),
    montant         numeric(12,2) NOT NULL CHECK (montant <> 0),   -- positif : la dette augmente ; négatif : elle diminue
    solde_apres     numeric(12,2) NOT NULL,
    utilisateur_id  bigint        REFERENCES utilisateur(id),
    reference       text,
    cree_le         timestamptz   NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_fournisseur_mouvement ON fournisseur_mouvement (fournisseur_id, id);
COMMENT ON TABLE fournisseur_mouvement IS 'Relevé de la dette envers les grossistes : une ligne par mouvement de fournisseur.solde (déclencheur trg_fournisseur_dette)';

-- Contexte du prochain mouvement de dette de la transaction (lu par le déclencheur)
CREATE OR REPLACE FUNCTION dette_fournisseur_contexte(p_type text, p_utilisateur_id bigint, p_reference text)
RETURNS void LANGUAGE sql AS $$
    SELECT set_config('pharma.dette_type', COALESCE(p_type, ''), true),
           set_config('pharma.dette_utilisateur', COALESCE(p_utilisateur_id::text, ''), true),
           set_config('pharma.dette_reference', COALESCE(p_reference, ''), true);
$$;

CREATE OR REPLACE FUNCTION tracer_dette_fournisseur() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_delta numeric(12,2);
BEGIN
    v_delta := NEW.solde - CASE WHEN TG_OP = 'INSERT' THEN 0 ELSE OLD.solde END;
    IF v_delta = 0 THEN
        RETURN NEW;
    END IF;
    INSERT INTO fournisseur_mouvement (fournisseur_id, type, montant, solde_apres, utilisateur_id, reference)
    VALUES (NEW.id,
            -- Sans contexte : grossiste créé avec une dette (reprise), sinon correction hors logiciel
            COALESCE(NULLIF(current_setting('pharma.dette_type', true), ''), CASE WHEN TG_OP = 'INSERT' THEN 'reprise' ELSE 'correction' END),
            v_delta, NEW.solde,
            NULLIF(current_setting('pharma.dette_utilisateur', true), '')::bigint,
            NULLIF(current_setting('pharma.dette_reference', true), ''));
    PERFORM dette_fournisseur_contexte(NULL, NULL, NULL);
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_fournisseur_dette ON fournisseur;
CREATE TRIGGER trg_fournisseur_dette AFTER INSERT OR UPDATE OF solde ON fournisseur
    FOR EACH ROW EXECUTE FUNCTION tracer_dette_fournisseur();

CREATE OR REPLACE FUNCTION proteger_fournisseur_mouvement() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Le relevé des dettes ne se modifie pas : corrigez par un nouveau mouvement';
END $$;

DROP TRIGGER IF EXISTS trg_fournisseur_mouvement_immuable ON fournisseur_mouvement;
CREATE TRIGGER trg_fournisseur_mouvement_immuable BEFORE UPDATE OR DELETE ON fournisseur_mouvement
    FOR EACH ROW EXECUTE FUNCTION proteger_fournisseur_mouvement();

-- Mise en place sur une base existante : la dette déjà là devient le premier mouvement du relevé
INSERT INTO fournisseur_mouvement (fournisseur_id, type, montant, solde_apres, reference)
SELECT f.id, 'ouverture', f.solde, f.solde, 'Dette à la mise en place du relevé'
FROM fournisseur f
WHERE f.solde <> 0
  AND NOT EXISTS (SELECT 1 FROM fournisseur_mouvement m WHERE m.fournisseur_id = f.id);

-- ---------------------------------------------------------------------
-- 2. Délai d'alerte des dettes et créances
-- ---------------------------------------------------------------------
ALTER TABLE parametre_caisse ADD COLUMN IF NOT EXISTS delai_alerte_dette_jours int NOT NULL DEFAULT 30;
ALTER TABLE parametre_caisse DROP CONSTRAINT IF EXISTS ck_delai_alerte_dette;
ALTER TABLE parametre_caisse ADD CONSTRAINT ck_delai_alerte_dette CHECK (delai_alerte_dette_jours BETWEEN 1 AND 365);
COMMENT ON COLUMN parametre_caisse.delai_alerte_dette_jours IS 'Ancienneté (jours) à partir de laquelle une dette ou une créance est signalée';

-- ---------------------------------------------------------------------
-- 3. Tout ce qui est dû, à la pharmacie (sens « creance ») ou par elle (sens « dette »)
--    date_reference : date depuis laquelle la somme est due (vente, facture, envoi, réception) ;
--    echeance : seulement pour un effet (date de paiement prévue, postérieure à aujourd'hui ou du jour).
-- ---------------------------------------------------------------------
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
-- Factures clients à encaisser (patient, entreprise, organisme), rejetées comprises
SELECT 'creance', CASE f.destinataire WHEN 'organisme' THEN 'facture_organisme' WHEN 'entreprise' THEN 'facture_entreprise' ELSE 'facture_patient' END,
       COALESCE(f.organisme_id, f.client_id, f.patient_id), f.destinataire_nom, 'Facture ' || f.numero,
       f.date_facture, NULL, f.reste, f.statut = 'rejetee'
FROM v_facture_client f
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
