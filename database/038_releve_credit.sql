-- =====================================================================
-- Relevé de la dette de chaque patient, et ventes à crédit soldées
--   * credit_mouvement : une ligne par mouvement de la dette (vente à
--     crédit, remboursement, retour, annulation, reprise, correction),
--     avec la date, le montant, le solde après, l'employé, le poste et la
--     vente concernée. Écrite par un déclencheur à chaque changement de
--     patient.solde_credit : aucun mouvement de dette n'échappe au relevé,
--     même fait hors du logiciel. Le logiciel précise le contexte (type,
--     vente, employé, poste) par credit_contexte() avant de modifier la
--     dette ; sans contexte, le mouvement est noté « correction ».
--   * le relevé ne se modifie pas et ne s'efface pas ;
--   * v_vente_credit : pour chaque vente à crédit, ce qui est réglé et ce
--     qui reste dû. Les remboursements soldent les dettes les plus
--     anciennes d'abord : dette reprise de l'ancien logiciel, puis ventes
--     à crédit dans l'ordre des dates.
-- Idempotent. A exécuter après 037_produit_a_verifier.sql.
-- =====================================================================

CREATE TABLE IF NOT EXISTS credit_mouvement (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    patient_id      bigint        NOT NULL REFERENCES patient(id),
    type            text          NOT NULL CHECK (type IN (
                        'ouverture', 'reprise', 'vente_credit', 'remboursement', 'retour', 'annulation', 'correction')),
    montant         numeric(12,2) NOT NULL CHECK (montant <> 0),   -- positif : la dette augmente ; négatif : elle diminue
    solde_apres     numeric(12,2) NOT NULL,
    vente_id        bigint        REFERENCES vente(id),
    utilisateur_id  bigint        REFERENCES utilisateur(id),
    poste           text,
    reference       text,
    cree_le         timestamptz   NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_credit_mouvement_patient ON credit_mouvement (patient_id, id);
CREATE INDEX IF NOT EXISTS idx_credit_mouvement_vente ON credit_mouvement (vente_id) WHERE vente_id IS NOT NULL;
COMMENT ON TABLE credit_mouvement IS 'Relevé de la dette des patients : une ligne par mouvement de patient.solde_credit (déclencheur trg_patient_credit)';

-- Contexte du prochain mouvement de dette de la transaction (lu par le déclencheur)
CREATE OR REPLACE FUNCTION credit_contexte(p_type text, p_vente_id bigint, p_utilisateur_id bigint, p_poste text, p_reference text)
RETURNS void LANGUAGE sql AS $$
    SELECT set_config('pharma.credit_type', COALESCE(p_type, ''), true),
           set_config('pharma.credit_vente', COALESCE(p_vente_id::text, ''), true),
           set_config('pharma.credit_utilisateur', COALESCE(p_utilisateur_id::text, ''), true),
           set_config('pharma.credit_poste', COALESCE(p_poste, ''), true),
           set_config('pharma.credit_reference', COALESCE(p_reference, ''), true);
$$;

CREATE OR REPLACE FUNCTION tracer_credit_patient() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_delta numeric(12,2);
    v_type  text := NULLIF(current_setting('pharma.credit_type', true), '');
BEGIN
    v_delta := NEW.solde_credit - CASE WHEN TG_OP = 'INSERT' THEN 0 ELSE OLD.solde_credit END;
    IF v_delta = 0 THEN
        RETURN NEW;
    END IF;
    INSERT INTO credit_mouvement (patient_id, type, montant, solde_apres, vente_id, utilisateur_id, poste, reference)
    VALUES (NEW.id,
            -- Sans contexte : patient créé avec une dette (reprise de l'ancien logiciel), sinon correction hors logiciel
            COALESCE(v_type, CASE WHEN TG_OP = 'INSERT' THEN 'reprise' ELSE 'correction' END),
            v_delta, NEW.solde_credit,
            NULLIF(current_setting('pharma.credit_vente', true), '')::bigint,
            NULLIF(current_setting('pharma.credit_utilisateur', true), '')::bigint,
            NULLIF(current_setting('pharma.credit_poste', true), ''),
            NULLIF(current_setting('pharma.credit_reference', true), ''));
    -- Le contexte ne vaut que pour un mouvement
    PERFORM credit_contexte(NULL, NULL, NULL, NULL, NULL);
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_patient_credit ON patient;
CREATE TRIGGER trg_patient_credit AFTER INSERT OR UPDATE OF solde_credit ON patient
    FOR EACH ROW EXECUTE FUNCTION tracer_credit_patient();

-- Le relevé est une trace : ni modification ni suppression
CREATE OR REPLACE FUNCTION proteger_credit_mouvement() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Le relevé des dettes ne se modifie pas : corrigez par un nouveau mouvement';
END $$;

DROP TRIGGER IF EXISTS trg_credit_mouvement_immuable ON credit_mouvement;
CREATE TRIGGER trg_credit_mouvement_immuable BEFORE UPDATE OR DELETE ON credit_mouvement
    FOR EACH ROW EXECUTE FUNCTION proteger_credit_mouvement();

-- Ventes à crédit nettes des retours (une vente annulée n'est plus due)
CREATE OR REPLACE VIEW v_credit_accorde AS
SELECT v.id AS vente_id, v.patient_id, v.numero, v.date_vente,
       x.montant - COALESCE((SELECT SUM(rc.montant_rembourse) FROM retour_client rc WHERE rc.vente_id = v.id), 0) AS montant
FROM vente v
JOIN LATERAL (SELECT SUM(p.montant) AS montant FROM paiement p WHERE p.vente_id = v.id AND p.mode = 'credit') x ON x.montant > 0
WHERE v.statut = 'validee' AND v.patient_id IS NOT NULL;

-- Mise en place sur une base existante : la dette déjà là devient le premier mouvement du relevé
-- (« ouverture »), sans détail de ce qui l'a constituée avant ce script.
INSERT INTO credit_mouvement (patient_id, type, montant, solde_apres, reference)
SELECT p.id, 'ouverture', p.solde_credit, p.solde_credit, 'Dette à la mise en place du relevé'
FROM patient p
WHERE p.solde_credit <> 0
  AND NOT EXISTS (SELECT 1 FROM credit_mouvement m WHERE m.patient_id = p.id);

-- Dette d'ouverture retenue pour l'ordre d'imputation : ce que le patient devait avant ses ventes à crédit
-- connues du logiciel. Pour une dette mise en place par ce script, c'est la dette du moment moins les ventes
-- à crédit d'alors, sans descendre sous zéro.
CREATE TABLE IF NOT EXISTS credit_ouverture (
    patient_id  bigint PRIMARY KEY REFERENCES patient(id),
    montant     numeric(12,2) NOT NULL CHECK (montant >= 0)
);
COMMENT ON TABLE credit_ouverture IS 'Dette antérieure aux ventes à crédit du logiciel (reprise) : soldée en premier par les remboursements';

INSERT INTO credit_ouverture (patient_id, montant)
SELECT m.patient_id,
       GREATEST(m.montant - COALESCE((SELECT SUM(a.montant) FROM v_credit_accorde a
                                      WHERE a.patient_id = m.patient_id AND a.date_vente <= m.cree_le), 0), 0)
FROM credit_mouvement m
WHERE m.type = 'ouverture'
ON CONFLICT (patient_id) DO NOTHING;

-- Une dette reprise par import.charger (patient créé avec sa dette) est une dette d'ouverture
CREATE OR REPLACE FUNCTION noter_credit_ouverture() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.type = 'reprise' AND NEW.montant > 0 THEN
        INSERT INTO credit_ouverture (patient_id, montant) VALUES (NEW.patient_id, NEW.montant)
        ON CONFLICT (patient_id) DO UPDATE SET montant = credit_ouverture.montant + EXCLUDED.montant;
    END IF;
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_credit_ouverture ON credit_mouvement;
CREATE TRIGGER trg_credit_ouverture AFTER INSERT ON credit_mouvement
    FOR EACH ROW EXECUTE FUNCTION noter_credit_ouverture();

-- Ce qui est réglé sur chaque vente à crédit. Total réglé par le patient = dette d'ouverture + ventes à
-- crédit - dette actuelle ; il solde d'abord la dette d'ouverture, puis les ventes de la plus ancienne à
-- la plus récente. Toujours cohérent avec patient.solde_credit, y compris après un retour ou une annulation.
CREATE OR REPLACE VIEW v_vente_credit AS
SELECT t.vente_id, t.patient_id, t.numero, t.date_vente, t.montant,
       LEAST(GREATEST(t.regle_total - (t.ouverture + t.cumul - t.montant), 0), t.montant) AS regle,
       t.montant - LEAST(GREATEST(t.regle_total - (t.ouverture + t.cumul - t.montant), 0), t.montant) AS reste
FROM (SELECT a.vente_id, a.patient_id, a.numero, a.date_vente, a.montant,
             COALESCE(o.montant, 0) AS ouverture,
             SUM(a.montant) OVER (PARTITION BY a.patient_id ORDER BY a.date_vente, a.vente_id) AS cumul,
             GREATEST(COALESCE(o.montant, 0) + SUM(a.montant) OVER (PARTITION BY a.patient_id) - p.solde_credit, 0) AS regle_total
      FROM v_credit_accorde a
      JOIN patient p ON p.id = a.patient_id
      LEFT JOIN credit_ouverture o ON o.patient_id = a.patient_id
      WHERE a.montant > 0) t;

-- ---------------------------------------------------------------------
-- Factures : une vente à crédit facturée est soldée quand le patient règle sa dette
-- (avant : le « déjà payé » figé à l'émission ignorait les règlements de crédit, la
-- facture restait « à encaisser »). Le reste à encaisser retire maintenant ce qui est
-- réglé sur les ventes à crédit de la facture. Mêmes colonnes que la version 031 (ce
-- script et 031 restent ainsi rejouables dans l'ordre).
-- ---------------------------------------------------------------------
CREATE OR REPLACE VIEW v_facture_client AS
-- Colonnes de la table nommées une à une (et non f.*) : une colonne ajoutée plus tard à facture_client
-- ne change pas celles de la vue, qui reste ainsi rejouable (date_echeance, 040, se lit dans la table)
SELECT f.id, f.type, f.numero, f.date_facture, f.destinataire, f.patient_id, f.client_id, f.organisme_id, f.destinataire_nom,
       f.destinataire_infos, f.periode_du, f.periode_au, f.montant_ht, f.montant_tva, f.montant_ttc, f.deja_paye, f.statut, f.version,
       f.motif_rejet, f.remplace_id, f.remplacee_par, f.facture_origine_id, f.motif, f.hors_suivi, f.motif_hors_suivi, f.commentaire,
       f.utilisateur_id, f.cree_le,
       COALESCE((SELECT SUM(a.montant_ttc) FROM facture_client a WHERE a.facture_origine_id = f.id AND a.type = 'avoir'), 0) AS avoirs,
       COALESCE((SELECT SUM(r.montant) FROM reglement_facture_client r WHERE r.facture_id = f.id), 0) AS regle,
       CASE WHEN f.type = 'avoir' OR f.statut = 'remplacee' THEN 0
            ELSE GREATEST(f.montant_ttc - f.deja_paye
                 - COALESCE((SELECT SUM(a.montant_ttc) FROM facture_client a WHERE a.facture_origine_id = f.id AND a.type = 'avoir'), 0)
                 - COALESCE((SELECT SUM(r.montant) FROM reglement_facture_client r WHERE r.facture_id = f.id), 0)
                 - COALESCE((SELECT SUM(c.regle) FROM v_vente_credit c
                             WHERE c.vente_id IN (SELECT fl.vente_id FROM facture_client_ligne fl
                                                  WHERE fl.facture_id = f.id AND fl.part = 'patient' AND fl.vente_id IS NOT NULL)), 0), 0) END AS reste,
       u.nom_complet AS employe,
       (SELECT numero FROM facture_client x WHERE x.id = f.remplace_id) AS remplace_numero,
       (SELECT numero FROM facture_client x WHERE x.id = f.remplacee_par) AS remplacee_par_numero,
       (SELECT numero FROM facture_client x WHERE x.id = f.facture_origine_id) AS origine_numero
FROM facture_client f JOIN utilisateur u ON u.id = f.utilisateur_id;
