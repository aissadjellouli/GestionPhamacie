-- =====================================================================
-- Trésorerie : coffre et comptes bancaires, argent hors ventes
--   * comptes : un coffre (créé ici) et des banques déclarées par le
--     titulaire, chacune avec un solde et une date de départ ; une seule
--     banque reçoit les paiements par carte ;
--   * mouvements saisis : dépense (catégorie), encaissement divers,
--     versement d'un organisme (CNAS, CASNOS ; bordereau facultatif),
--     transfert entre comptes, versement d'une caisse au coffre
--     (pendant la journée : diminue l'attendu de la caisse ouverte ;
--     après la clôture : l'argent compté part au coffre) ;
--   * versement des caisses clôturées : manuel ou automatique, réglé par le
--     titulaire (parametre_caisse.versement_cloture) ; en automatique, le
--     compté moins le fond de caisse par défaut part au coffre ;
--   * mouvements calculés, sans double saisie : cartes du jour vers la
--     banque « carte », remises de chèques, règlements des grossistes
--     (espèces du coffre hors complément du tiroir, autres moyens de la
--     banque choisie ; effet à son échéance), remboursements par carte ;
--   * droit « tresorerie.gerer » : titulaire, qui l'accorde ensuite à qui
--     il veut (Utilisateurs et droits).
-- Dates : jour du serveur PostgreSQL (::date), comme le reste du logiciel.
-- Idempotent. A exécuter après 029_reglements.sql.
-- =====================================================================

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM permission WHERE code = 'tresorerie.gerer') THEN
        INSERT INTO permission (code, module, libelle, sensible) VALUES ('tresorerie.gerer', 'Trésorerie', 'Gérer la trésorerie (coffre, banques, dépenses)', true);
        ALTER TABLE role_permission DISABLE TRIGGER trg_proteger_role_systeme;
        INSERT INTO role_permission (role_id, permission_code) SELECT id, 'tresorerie.gerer' FROM role WHERE systeme;
        ALTER TABLE role_permission ENABLE TRIGGER trg_proteger_role_systeme;
    END IF;
END $$;

ALTER TABLE parametre_caisse ADD COLUMN IF NOT EXISTS versement_cloture text NOT NULL DEFAULT 'manuel';
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_versement_cloture') THEN
        ALTER TABLE parametre_caisse ADD CONSTRAINT ck_versement_cloture CHECK (versement_cloture IN ('manuel', 'automatique'));
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS compte_tresorerie (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    type            text          NOT NULL CHECK (type IN ('coffre', 'banque')),
    nom             text          NOT NULL UNIQUE,
    banque          text,
    numero_compte   text,
    recoit_carte    boolean       NOT NULL DEFAULT false,
    solde_initial   numeric(12,2) NOT NULL DEFAULT 0,
    date_initiale   date          NOT NULL DEFAULT current_date,
    actif           boolean       NOT NULL DEFAULT true,
    cree_le         timestamptz   NOT NULL DEFAULT now(),
    CHECK (type = 'banque' OR NOT recoit_carte)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_un_coffre ON compte_tresorerie (type) WHERE type = 'coffre';
CREATE UNIQUE INDEX IF NOT EXISTS ux_une_banque_carte ON compte_tresorerie (recoit_carte) WHERE recoit_carte;
INSERT INTO compte_tresorerie (type, nom) SELECT 'coffre', 'Coffre' WHERE NOT EXISTS (SELECT 1 FROM compte_tresorerie WHERE type = 'coffre');

CREATE TABLE IF NOT EXISTS mouvement_tresorerie (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    compte_id          bigint        NOT NULL REFERENCES compte_tresorerie(id),
    date_mouvement     date          NOT NULL,
    sens               text          NOT NULL CHECK (sens IN ('entree', 'sortie')),
    montant            numeric(12,2) NOT NULL CHECK (montant > 0),
    nature             text          NOT NULL CHECK (nature IN ('depense', 'encaissement', 'versement_organisme', 'transfert', 'versement_caisse')),
    categorie          text,          -- dépense : loyer, energie, salaires, impots, fournitures, entretien, autre
    libelle            text          NOT NULL,
    reference          text,
    organisme_id       bigint        REFERENCES organisme(id),
    bordereau_id       bigint        REFERENCES bordereau(id),
    session_caisse_id  bigint        REFERENCES session_caisse(id),
    pendant_session    boolean       NOT NULL DEFAULT false,   -- versement pris dans une caisse encore ouverte
    automatique        boolean       NOT NULL DEFAULT false,   -- versement fait par le logiciel à la clôture
    transfert_de       bigint        REFERENCES mouvement_tresorerie(id),
    utilisateur_id     bigint        NOT NULL REFERENCES utilisateur(id),
    cree_le            timestamptz   NOT NULL DEFAULT now(),
    CHECK (nature <> 'versement_organisme' OR organisme_id IS NOT NULL),
    CHECK (nature <> 'versement_caisse' OR (session_caisse_id IS NOT NULL AND sens = 'entree')),
    CHECK (nature <> 'depense' OR (sens = 'sortie' AND categorie IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS idx_mouvement_tresorerie ON mouvement_tresorerie (compte_id, date_mouvement);
CREATE INDEX IF NOT EXISTS idx_mouvement_tresorerie_session ON mouvement_tresorerie (session_caisse_id);

CREATE TABLE IF NOT EXISTS remise_cheques (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    compte_id       bigint      NOT NULL REFERENCES compte_tresorerie(id),
    date_remise     date        NOT NULL,
    reference       text,
    utilisateur_id  bigint      NOT NULL REFERENCES utilisateur(id),
    cree_le         timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS remise_cheque_paiement (
    remise_id   bigint NOT NULL REFERENCES remise_cheques(id),
    paiement_id bigint NOT NULL UNIQUE REFERENCES paiement(id),
    PRIMARY KEY (remise_id, paiement_id)
);

-- Règlement d'un grossiste par chèque, virement ou effet : banque débitée
ALTER TABLE reglement_fournisseur ADD COLUMN IF NOT EXISTS compte_id bigint REFERENCES compte_tresorerie(id);

-- Chèques reçus (ventes validées, remboursements de crédit) pas encore remis en banque
CREATE OR REPLACE VIEW v_cheque_a_remettre AS
SELECT p.id AS paiement_id, p.cree_le, p.montant,
       COALESCE(v.numero, 'Règlement de crédit') AS origine,
       NULLIF(trim(concat_ws(' ', pa.nom, pa.prenom)), '') AS patient
FROM paiement p
LEFT JOIN vente v ON v.id = p.vente_id
LEFT JOIN patient pa ON pa.id = COALESCE(v.patient_id, p.patient_id)
WHERE p.mode = 'cheque'
  AND (p.vente_id IS NULL OR v.statut = 'validee')
  AND NOT EXISTS (SELECT 1 FROM remise_cheque_paiement r WHERE r.paiement_id = p.id);

-- Tous les mouvements : saisis et calculés. origine 'saisi' : source_id = mouvement_tresorerie.id
CREATE OR REPLACE VIEW v_mouvement_tresorerie AS
SELECT m.compte_id, m.date_mouvement AS date, m.sens, m.montant, m.nature, m.categorie, m.libelle, m.reference,
       u.nom_complet AS employe, 'saisi'::text AS origine, m.id AS source_id, m.cree_le
FROM mouvement_tresorerie m JOIN utilisateur u ON u.id = m.utilisateur_id
UNION ALL
-- Paiements par carte du jour (ventes et remboursements de crédit) vers la banque « carte »
SELECT c.id, p.cree_le::date, 'entree', SUM(p.montant), 'carte', NULL, 'Paiements par carte du jour', NULL,
       NULL, 'calcule', NULL, max(p.cree_le)
FROM paiement p
JOIN compte_tresorerie c ON c.recoit_carte
LEFT JOIN vente v ON v.id = p.vente_id
WHERE p.mode = 'carte' AND (p.vente_id IS NULL OR v.statut IN ('validee', 'annulee'))
GROUP BY c.id, p.cree_le::date
UNION ALL
-- Remises de chèques en banque
SELECT r.compte_id, r.date_remise, 'entree', SUM(p.montant), 'remise_cheques', NULL,
       format('Remise de %s chèque(s)', count(*)), r.reference, u.nom_complet, 'calcule', r.id, r.cree_le
FROM remise_cheques r
JOIN remise_cheque_paiement rp ON rp.remise_id = r.id
JOIN paiement p ON p.id = rp.paiement_id
JOIN utilisateur u ON u.id = r.utilisateur_id
GROUP BY r.id, u.nom_complet
UNION ALL
-- Règlements des grossistes : espèces du coffre (hors complément du tiroir), autres moyens de la banque choisie
SELECT CASE WHEN g.moyen = 'especes' THEN (SELECT id FROM compte_tresorerie WHERE type = 'coffre') ELSE g.compte_id END,
       CASE WHEN g.moyen = 'effet' THEN g.echeance ELSE g.date_reglement END,
       'sortie', CASE WHEN g.moyen = 'especes' THEN g.montant - g.part_tiroir ELSE g.montant END,
       'reglement_grossiste', NULL, 'Règlement ' || f.raison_sociale, g.reference, u.nom_complet, 'calcule', g.id, g.cree_le
FROM reglement_fournisseur g
JOIN fournisseur f ON f.id = g.fournisseur_id
JOIN utilisateur u ON u.id = g.utilisateur_id
WHERE g.montant > g.part_tiroir
  AND (g.moyen = 'especes' OR g.compte_id IS NOT NULL)
UNION ALL
-- Remboursements par carte (annulations et retours) : sortent de la banque « carte »
SELECT c.id, x.cree_le::date, 'sortie', x.montant_rembourse, 'remboursement_carte', NULL, x.libelle, NULL, NULL, 'calcule', NULL, x.cree_le
FROM (SELECT cree_le, montant_rembourse, 'Remboursement carte (annulation)' AS libelle FROM annulation_vente WHERE mode = 'carte' AND montant_rembourse > 0
      UNION ALL
      SELECT cree_le, montant_rembourse, 'Remboursement carte (retour)' FROM retour_client WHERE mode = 'carte' AND montant_rembourse > 0) x
JOIN compte_tresorerie c ON c.recoit_carte;

-- Solde de chaque compte : solde de départ + mouvements depuis la date de départ
CREATE OR REPLACE VIEW v_solde_tresorerie AS
SELECT c.id, c.type, c.nom, c.banque, c.numero_compte, c.recoit_carte, c.actif, c.solde_initial, c.date_initiale,
       c.solde_initial + COALESCE((SELECT SUM(CASE WHEN m.sens = 'entree' THEN m.montant ELSE -m.montant END)
                                   FROM v_mouvement_tresorerie m WHERE m.compte_id = c.id AND m.date >= c.date_initiale), 0) AS solde
FROM compte_tresorerie c;

-- Caisses clôturées (hors relève, dont l'argent reste au tiroir) dont l'argent n'est pas encore versé au coffre,
-- depuis la date de départ du coffre. Proposé : compté (ou attendu) moins le fond de caisse par défaut.
CREATE OR REPLACE VIEW v_caisse_a_verser AS
SELECT s.id AS session_caisse_id, s.poste, s.fermee_le, u.nom_complet AS ouverte_par,
       COALESCE(s.montant_compte, s.montant_attendu) AS compte,
       GREATEST(COALESCE(s.montant_compte, s.montant_attendu) - (SELECT fond_caisse_defaut FROM parametre_caisse WHERE id = 1), 0) AS propose
FROM session_caisse s
JOIN utilisateur u ON u.id = s.utilisateur_id
WHERE s.fermee_le IS NOT NULL
  AND s.fermee_le::date >= (SELECT date_initiale FROM compte_tresorerie WHERE type = 'coffre')
  AND NOT EXISTS (SELECT 1 FROM session_caisse n WHERE n.releve_de = s.id)
  AND NOT EXISTS (SELECT 1 FROM mouvement_tresorerie m WHERE m.session_caisse_id = s.id AND NOT m.pendant_session);
