-- =====================================================================
-- Règlements
--   * crédit client : le patient rembourse sa dette au comptoir ; le
--     paiement (vente_id NULL, patient_id) entre dans la caisse ouverte du
--     poste, la dette du patient diminue ;
--   * grossistes : règlement par facture (réception), en espèces, chèque,
--     virement ou effet ; les espèces sortent d'un coffre à part, avec un
--     complément éventuel pris dans le tiroir de la caisse ouverte (seul ce
--     complément diminue l'attendu de clôture) ; la dette du grossiste
--     (fournisseur.solde) diminue du montant réglé.
-- Idempotent. A exécuter après 028_retour_client.sql.
-- =====================================================================

-- Nouveau droit, posé une seule fois : le titulaire a tous les droits (sa protection est levée le temps de l'ajout),
-- l'assistant le reçoit au départ ; le titulaire l'ajuste ensuite dans la matrice des droits
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM permission WHERE code = 'achats.regler') THEN
        INSERT INTO permission (code, module, libelle, sensible)
        VALUES ('achats.regler', 'Achats', 'Régler les factures des grossistes', true);
        ALTER TABLE role_permission DISABLE TRIGGER trg_proteger_role_systeme;
        INSERT INTO role_permission (role_id, permission_code) SELECT id, 'achats.regler' FROM role WHERE systeme;
        ALTER TABLE role_permission ENABLE TRIGGER trg_proteger_role_systeme;
        INSERT INTO role_permission (role_id, permission_code) SELECT id, 'achats.regler' FROM role WHERE code = 'assistant';
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS reglement_fournisseur (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    fournisseur_id     bigint        NOT NULL REFERENCES fournisseur(id),
    date_reglement     date          NOT NULL,
    montant            numeric(12,2) NOT NULL CHECK (montant > 0),
    moyen              text          NOT NULL CHECK (moyen IN ('especes', 'cheque', 'virement', 'effet')),
    reference          text,          -- n° de chèque, de virement ou d'effet
    banque             text,
    echeance           date,          -- effet / traite
    part_tiroir        numeric(12,2) NOT NULL DEFAULT 0 CHECK (part_tiroir >= 0),
    session_caisse_id  bigint        REFERENCES session_caisse(id),   -- caisse d'où sort le complément en espèces
    utilisateur_id     bigint        NOT NULL REFERENCES utilisateur(id),
    commentaire        text,
    cree_le            timestamptz   NOT NULL DEFAULT now(),
    CHECK (part_tiroir <= montant),
    CHECK (part_tiroir = 0 OR (moyen = 'especes' AND session_caisse_id IS NOT NULL)),
    CHECK (moyen <> 'effet' OR echeance IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS idx_reglement_fournisseur ON reglement_fournisseur (fournisseur_id, date_reglement);
CREATE INDEX IF NOT EXISTS idx_reglement_session ON reglement_fournisseur (session_caisse_id);

CREATE TABLE IF NOT EXISTS reglement_facture (
    reglement_id  bigint        NOT NULL REFERENCES reglement_fournisseur(id),
    reception_id  bigint        NOT NULL REFERENCES reception(id),
    montant       numeric(12,2) NOT NULL CHECK (montant > 0),
    PRIMARY KEY (reglement_id, reception_id)
);
CREATE INDEX IF NOT EXISTS idx_reglement_facture_reception ON reglement_facture (reception_id);

-- Factures des grossistes : montant, déjà réglé, reste à payer
CREATE OR REPLACE VIEW v_facture_fournisseur AS
SELECT r.id AS reception_id, r.fournisseur_id, f.raison_sociale AS fournisseur,
       r.numero_facture, r.numero_bl, r.date_reception,
       COALESCE(r.montant_ttc, 0) AS montant,
       COALESCE((SELECT SUM(x.montant) FROM reglement_facture x WHERE x.reception_id = r.id), 0) AS regle,
       COALESCE(r.montant_ttc, 0) - COALESCE((SELECT SUM(x.montant) FROM reglement_facture x WHERE x.reception_id = r.id), 0) AS reste
FROM reception r JOIN fournisseur f ON f.id = r.fournisseur_id;
