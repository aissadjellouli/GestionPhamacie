-- =====================================================================
-- Avoir patient (modèle Wasfa, 2026-11-15, lot 7)
--   avoir_mouvement : argent que la pharmacie doit au patient, séparé de
--   sa dette (patient.solde_credit n'est jamais rendu négatif).
--   Chaque ligne : sens « credit » (l'avoir augmente) ou « debit » (il
--   diminue), montant > 0, origine :
--     annulation_vente          trop-payé d'une vente annulée laissé en
--                               avoir, ou avoir utilisé sur la vente rendu
--     trop_paye_regularisation  trop-payé d'une régularisation Chifa
--                               annulée (098) laissé en avoir
--     saisie                    avoir saisi (réservé)
--     paiement                  avoir utilisé en paiement (vente ou
--                               encaissement d'une dette, ligne paiement
--                               « avoir »)
--     remboursement             avoir rendu par le tiroir (sortie
--                               « Remboursement d'un client », bon lié)
--   v_avoir_patient : solde par patient (jamais négatif). Le trigger
--   tg_avoir_mouvement_solde verrouille le patient et refuse tout débit
--   au-delà du solde (deux caisses ne consomment pas le même avoir).
--   paiement.mode admet « avoir » : il n'entre jamais dans les espèces
--   attendues à la clôture (calculées sur le mode « especes » seul).
--   Pas d'expiration. Aucune donnée existante modifiée.
-- Idempotent. À exécuter après 105.
-- =====================================================================

CREATE TABLE IF NOT EXISTS avoir_mouvement (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    patient_id          bigint NOT NULL REFERENCES patient (id),
    sens                text NOT NULL,
    montant             numeric(12,2) NOT NULL,
    origine             text NOT NULL,
    vente_id            bigint REFERENCES vente (id),
    paiement_id         bigint REFERENCES paiement (id),
    mouvement_tiroir_id bigint REFERENCES mouvement_tiroir (id),
    motif               text,
    utilisateur_id      bigint NOT NULL REFERENCES utilisateur (id),
    cree_le             timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_avoir_mouvement_sens CHECK (sens IN ('credit', 'debit')),
    CONSTRAINT ck_avoir_mouvement_montant CHECK (montant > 0),
    CONSTRAINT ck_avoir_mouvement_origine CHECK (origine IN ('annulation_vente', 'trop_paye_regularisation', 'saisie', 'paiement', 'remboursement')),
    CONSTRAINT ck_avoir_mouvement_sens_origine CHECK (
        (origine IN ('annulation_vente', 'trop_paye_regularisation', 'saisie') AND sens = 'credit')
        OR (origine IN ('paiement', 'remboursement') AND sens = 'debit')),
    CONSTRAINT ck_avoir_mouvement_liens CHECK (
        (origine NOT IN ('annulation_vente', 'trop_paye_regularisation') OR vente_id IS NOT NULL)
        AND (origine <> 'paiement' OR paiement_id IS NOT NULL)
        AND (origine <> 'remboursement' OR mouvement_tiroir_id IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS idx_avoir_mouvement_patient ON avoir_mouvement (patient_id, cree_le);
CREATE INDEX IF NOT EXISTS idx_avoir_mouvement_vente ON avoir_mouvement (vente_id) WHERE vente_id IS NOT NULL;

COMMENT ON TABLE avoir_mouvement IS 'Avoir du patient (argent dû par la pharmacie), séparé de sa dette ; solde : v_avoir_patient';

CREATE OR REPLACE VIEW v_avoir_patient AS
SELECT patient_id,
       SUM(CASE WHEN sens = 'credit' THEN montant ELSE -montant END)::numeric(12,2) AS solde,
       MAX(cree_le) AS dernier_mouvement
FROM avoir_mouvement
GROUP BY patient_id;

-- Débit sous verrou du patient : jamais au-delà du solde
CREATE OR REPLACE FUNCTION avoir_mouvement_controle_solde() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_solde numeric(12,2);
BEGIN
    PERFORM 1 FROM patient WHERE id = NEW.patient_id FOR UPDATE;
    IF NEW.sens = 'debit' THEN
        SELECT COALESCE(SUM(CASE WHEN sens = 'credit' THEN montant ELSE -montant END), 0) INTO v_solde
        FROM avoir_mouvement WHERE patient_id = NEW.patient_id;
        IF v_solde < NEW.montant THEN
            RAISE EXCEPTION 'Avoir insuffisant (solde : % DA)', v_solde USING ERRCODE = 'P0001';
        END IF;
    END IF;
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS tg_avoir_mouvement_solde ON avoir_mouvement;
CREATE TRIGGER tg_avoir_mouvement_solde BEFORE INSERT ON avoir_mouvement
    FOR EACH ROW EXECUTE FUNCTION avoir_mouvement_controle_solde();

-- Mouvements jamais modifiés ni supprimés (une erreur se corrige par un mouvement inverse)
CREATE OR REPLACE FUNCTION avoir_mouvement_immuable() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Un mouvement d''avoir ne se modifie pas et ne se supprime pas.';
END $$;

DROP TRIGGER IF EXISTS tg_avoir_mouvement_immuable ON avoir_mouvement;
CREATE TRIGGER tg_avoir_mouvement_immuable BEFORE UPDATE OR DELETE ON avoir_mouvement
    FOR EACH ROW EXECUTE FUNCTION avoir_mouvement_immuable();

-- Mode de paiement « avoir »
ALTER TABLE paiement DROP CONSTRAINT IF EXISTS paiement_mode_check;
ALTER TABLE paiement ADD CONSTRAINT paiement_mode_check
    CHECK (mode IN ('especes', 'carte', 'cheque', 'credit', 'remboursement_credit', 'avoir'));
