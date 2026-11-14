-- =====================================================================
-- Financements : prêts bancaires, compte du titulaire, usage des sources
--   * pret_bancaire : prêt accordé par une banque de la trésorerie
--     (compte de type banque, contrôlé par le logiciel), montant, date,
--     référence, objet, document joint (chemin dans la GED, facultatif) ;
--   * echeance_pret : échéancier du prêt (capital, intérêts, TVA et marge
--     facultatives, échéance, capital restant dû). Contrôles complets
--     (sommes, dates croissantes, encours décroissant) faits par le
--     logiciel avant l'enregistrement ; la base garde l'égalité de chaque
--     ligne ;
--   * paiement_echeance_pret : échéance payée, confirmée par le titulaire
--     (espèces : coffre, complété au besoin par l'argent du tiroir porté au
--     coffre ; chèque, virement, prélèvement : banque et référence
--     obligatoires). Part capital : mouvement « remboursement_financement » ;
--     intérêts, TVA et marge : dépense « charges_financieres » ;
--   * correspondance_echeancier : colonnes d'un tableau d'échéances
--     (Excel, CSV, texte lu) retenues pour chaque banque ;
--   * utilisation_financement : usage d'un prêt ou d'un apport du titulaire
--     (argent versé au coffre ou en banque, dépense payée, facture ou
--     traite d'un grossiste payée « hors trésorerie ») ;
--   * remboursement_titulaire : le titulaire se rembourse ses apports
--     (sortie du coffre ou d'une banque) ;
--   * mouvement_tresorerie.nature : « financement » (entrée d'un prêt ou
--     d'un apport) et « remboursement_financement » (capital d'une échéance,
--     remboursement du titulaire) ; reglement_fournisseur.moyen et
--     paiement_effet.moyen : « financement » (payé par un prêt ou un apport,
--     aucun compte de trésorerie débité). Contraintes élargies avec garde,
--     en gardant les valeurs déjà admises par la base ;
--   * vues nouvelles : v_pret_bancaire, v_echeance_pret,
--     v_compte_titulaire, v_dette_financement (mêmes colonnes que
--     v_suivi_dettes, lue avec elle par le suivi des dettes).
-- Idempotent. A exécuter après 077_alertes_non_envoyees_doublons.sql.
-- =====================================================================

-- ---- Valeurs admises : élargies sans perdre celles déjà ajoutées par d'autres scripts ----
DO $$
DECLARE
    c record;
    existantes text[];
    toutes text[];
BEGIN
    FOR c IN SELECT * FROM (VALUES
        ('mouvement_tresorerie', 'mouvement_tresorerie_nature_check', 'nature', ARRAY['financement', 'remboursement_financement']),
        ('reglement_fournisseur', 'reglement_fournisseur_moyen_check', 'moyen', ARRAY['financement']),
        ('paiement_effet', 'paiement_effet_moyen_check', 'moyen', ARRAY['financement'])) AS t(tab, nom, col, nouvelles)
    LOOP
        SELECT array_agg(v.m[1]) INTO existantes
        FROM pg_constraint k
        CROSS JOIN LATERAL regexp_matches(pg_get_constraintdef(k.oid), '''([a-z_]+)''', 'g') AS v(m)
        WHERE k.conname = c.nom AND k.conrelid = c.tab::regclass;
        IF existantes IS NULL THEN
            RAISE NOTICE 'Contrainte % absente : aucune liste à élargir sur %.%', c.nom, c.tab, c.col;
        ELSIF NOT existantes @> c.nouvelles THEN
            toutes := existantes || ARRAY(SELECT unnest(c.nouvelles) EXCEPT SELECT unnest(existantes));
            EXECUTE format('ALTER TABLE %I DROP CONSTRAINT %I', c.tab, c.nom);
            EXECUTE format('ALTER TABLE %I ADD CONSTRAINT %I CHECK (%I IN (%s))', c.tab, c.nom, c.col,
                           (SELECT string_agg(quote_literal(x), ', ') FROM unnest(toutes) AS x));
        END IF;
    END LOOP;
END $$;

-- Traite payée par un prêt ou un apport : aucun compte de trésorerie (comme les espèces, qui vont au coffre)
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_constraint
               WHERE conname = 'paiement_effet_check' AND conrelid = 'paiement_effet'::regclass
                 AND pg_get_constraintdef(oid) NOT LIKE '%financement%') THEN
        ALTER TABLE paiement_effet DROP CONSTRAINT paiement_effet_check;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'paiement_effet_check' AND conrelid = 'paiement_effet'::regclass) THEN
        ALTER TABLE paiement_effet ADD CONSTRAINT paiement_effet_check CHECK (moyen IN ('especes', 'financement') OR compte_id IS NOT NULL);
    END IF;
END $$;

-- ---- Prêts bancaires --------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS pret_bancaire (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    compte_id       bigint NOT NULL REFERENCES compte_tresorerie(id),
    reference       text NOT NULL,
    objet           text,
    montant         numeric(12,2) NOT NULL CHECK (montant > 0),
    date_pret       date NOT NULL,
    document        text,
    utilisateur_id  bigint NOT NULL REFERENCES utilisateur(id),
    cree_le         timestamptz NOT NULL DEFAULT now(),
    UNIQUE (compte_id, reference)
);
COMMENT ON TABLE pret_bancaire IS 'Prêt accordé par une banque de la trésorerie (compte de type banque) ; réservé au titulaire';
COMMENT ON COLUMN pret_bancaire.document IS 'Document joint (contrat, tableau d''amortissement) : chemin « Type\nom » dans la GED du serveur';

CREATE TABLE IF NOT EXISTS echeance_pret (
    id               bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    pret_id          bigint NOT NULL REFERENCES pret_bancaire(id),
    rang             int NOT NULL CHECK (rang > 0),
    date_echeance    date NOT NULL,
    capital          numeric(12,2) NOT NULL CHECK (capital >= 0),
    interets         numeric(12,2) NOT NULL DEFAULT 0 CHECK (interets >= 0),
    tva              numeric(12,2) NOT NULL DEFAULT 0 CHECK (tva >= 0),
    marge            numeric(12,2) NOT NULL DEFAULT 0 CHECK (marge >= 0),
    montant          numeric(12,2) NOT NULL CHECK (montant > 0),
    capital_restant  numeric(12,2) NOT NULL CHECK (capital_restant >= 0),
    UNIQUE (pret_id, rang),
    CHECK (capital + interets + tva + marge = montant)
);
COMMENT ON TABLE echeance_pret IS 'Échéancier d''un prêt ; une échéance payée n''est plus modifiée';
COMMENT ON COLUMN echeance_pret.marge IS 'Marge du financement islamique (mourabaha), facultative';
CREATE INDEX IF NOT EXISTS idx_echeance_pret_date ON echeance_pret (date_echeance);

CREATE TABLE IF NOT EXISTS paiement_echeance_pret (
    id                    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    echeance_id           bigint NOT NULL UNIQUE REFERENCES echeance_pret(id),
    date_paiement         date NOT NULL,
    moyen                 text NOT NULL CHECK (moyen IN ('especes', 'cheque', 'virement', 'prelevement')),
    compte_id             bigint REFERENCES compte_tresorerie(id),
    reference             text,
    part_tiroir           numeric(12,2) NOT NULL DEFAULT 0 CHECK (part_tiroir >= 0),
    versement_id          bigint REFERENCES mouvement_tresorerie(id),
    mouvement_capital_id  bigint REFERENCES mouvement_tresorerie(id),
    mouvement_charges_id  bigint REFERENCES mouvement_tresorerie(id),
    utilisateur_id        bigint NOT NULL REFERENCES utilisateur(id),
    cree_le               timestamptz NOT NULL DEFAULT now(),
    CHECK (moyen = 'especes' OR (compte_id IS NOT NULL AND reference IS NOT NULL)),
    CHECK (part_tiroir = 0 OR (moyen = 'especes' AND versement_id IS NOT NULL))
);
COMMENT ON TABLE paiement_echeance_pret IS 'Échéance de prêt payée, confirmée par le titulaire : capital (remboursement_financement) et charges financières (dépense) sortis du coffre ou de la banque choisie';

-- ---- Colonnes du tableau d'échéances, retenues par banque ----------------------------------
CREATE TABLE IF NOT EXISTS correspondance_echeancier (
    compte_id        bigint PRIMARY KEY REFERENCES compte_tresorerie(id),
    col_date         int NOT NULL CHECK (col_date >= 0),
    col_capital      int CHECK (col_capital >= 0),
    col_interets     int CHECK (col_interets >= 0),
    col_tva          int CHECK (col_tva >= 0),
    col_marge        int CHECK (col_marge >= 0),
    col_montant      int CHECK (col_montant >= 0),
    col_encours      int CHECK (col_encours >= 0),
    lignes_ignorees  int NOT NULL DEFAULT 0 CHECK (lignes_ignorees >= 0),
    utilisateur_id   bigint NOT NULL REFERENCES utilisateur(id),
    modifie_le       timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE correspondance_echeancier IS 'Colonnes (à partir de 0) du tableau d''échéances fourni par cette banque, et lignes d''en-tête à ignorer ; proposées au prochain import';

-- ---- Usage d'un prêt ou d'un apport du titulaire --------------------------------------------
CREATE TABLE IF NOT EXISTS utilisation_financement (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source             text NOT NULL CHECK (source IN ('pret', 'titulaire')),
    pret_id            bigint REFERENCES pret_bancaire(id),
    usage              text NOT NULL CHECK (usage IN ('tresorerie', 'depense', 'reglement', 'traite')),
    date_usage         date NOT NULL,
    montant            numeric(12,2) NOT NULL CHECK (montant > 0),
    libelle            text NOT NULL,
    mouvement_id       bigint REFERENCES mouvement_tresorerie(id),
    depense_id         bigint REFERENCES mouvement_tresorerie(id),
    reglement_id       bigint REFERENCES reglement_fournisseur(id),
    paiement_effet_id  bigint REFERENCES paiement_effet(id),
    utilisateur_id     bigint NOT NULL REFERENCES utilisateur(id),
    cree_le            timestamptz NOT NULL DEFAULT now(),
    CHECK ((source = 'pret') = (pret_id IS NOT NULL)),
    CHECK (usage <> 'tresorerie' OR mouvement_id IS NOT NULL),
    CHECK (usage <> 'depense' OR (mouvement_id IS NOT NULL AND depense_id IS NOT NULL)),
    CHECK (usage <> 'reglement' OR reglement_id IS NOT NULL),
    CHECK (usage <> 'traite' OR paiement_effet_id IS NOT NULL)
);
COMMENT ON TABLE utilisation_financement IS 'Usage d''un prêt ou d''un apport du titulaire : versé au coffre ou en banque, dépense payée, facture ou traite d''un grossiste payée hors trésorerie. Un apport du titulaire est une somme que la pharmacie lui doit';
COMMENT ON COLUMN utilisation_financement.mouvement_id IS 'Entrée « financement » sur le compte (versement, ou passage de la dépense payée)';
COMMENT ON COLUMN utilisation_financement.depense_id IS 'Dépense payée par la source, inscrite sur le même compte que l''entrée (solde du compte inchangé)';
CREATE INDEX IF NOT EXISTS idx_utilisation_financement_pret ON utilisation_financement (pret_id);

CREATE TABLE IF NOT EXISTS remboursement_titulaire (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    date_remboursement  date NOT NULL,
    montant             numeric(12,2) NOT NULL CHECK (montant > 0),
    compte_id           bigint NOT NULL REFERENCES compte_tresorerie(id),
    mouvement_id        bigint NOT NULL REFERENCES mouvement_tresorerie(id),
    libelle             text NOT NULL,
    reference           text,
    utilisateur_id      bigint NOT NULL REFERENCES utilisateur(id),
    cree_le             timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE remboursement_titulaire IS 'Le titulaire se rembourse ses apports : sortie du coffre ou d''une banque (mouvement remboursement_financement)';

-- ---- Vues ------------------------------------------------------------------------------------
CREATE OR REPLACE VIEW v_echeance_pret AS
SELECT e.id, e.pret_id, p.reference AS pret, p.compte_id AS banque_id, c.nom AS banque, e.rang, e.date_echeance, e.capital, e.interets,
       e.tva, e.marge, e.montant, e.capital_restant, pe.date_paiement, pe.moyen, pe.compte_id, cp.nom AS compte, pe.reference,
       pe.part_tiroir, u.nom_complet AS employe
FROM echeance_pret e
JOIN pret_bancaire p ON p.id = e.pret_id
JOIN compte_tresorerie c ON c.id = p.compte_id
LEFT JOIN paiement_echeance_pret pe ON pe.echeance_id = e.id
LEFT JOIN compte_tresorerie cp ON cp.id = pe.compte_id
LEFT JOIN utilisateur u ON u.id = pe.utilisateur_id;

CREATE OR REPLACE VIEW v_pret_bancaire AS
SELECT p.id, p.compte_id, c.nom AS banque, p.reference, p.objet, p.montant, p.date_pret, p.document,
       COALESCE(e.capital_paye, 0) AS capital_rembourse,
       p.montant - COALESCE(e.capital_paye, 0) AS reste_du,
       COALESCE((SELECT sum(x.montant) FROM utilisation_financement x WHERE x.pret_id = p.id), 0) AS utilise,
       COALESCE(e.nombre, 0)::int AS echeances,
       COALESCE(e.payees, 0)::int AS echeances_payees,
       COALESCE(e.echues, 0)::int AS echues_non_payees,
       (SELECT v.date_echeance FROM v_echeance_pret v WHERE v.pret_id = p.id AND v.date_paiement IS NULL ORDER BY v.rang LIMIT 1) AS prochaine_echeance,
       (SELECT v.montant FROM v_echeance_pret v WHERE v.pret_id = p.id AND v.date_paiement IS NULL ORDER BY v.rang LIMIT 1) AS prochain_montant,
       u.nom_complet AS employe, p.cree_le
FROM pret_bancaire p
JOIN compte_tresorerie c ON c.id = p.compte_id
JOIN utilisateur u ON u.id = p.utilisateur_id
LEFT JOIN LATERAL (
    SELECT count(*) AS nombre, count(v.date_paiement) AS payees,
           sum(v.capital) FILTER (WHERE v.date_paiement IS NOT NULL) AS capital_paye,
           count(*) FILTER (WHERE v.date_paiement IS NULL AND v.date_echeance < current_date) AS echues
    FROM v_echeance_pret v WHERE v.pret_id = p.id) e ON true;

-- Compte du titulaire : apports (usages de la source « titulaire ») et remboursements
CREATE OR REPLACE VIEW v_compte_titulaire AS
SELECT 'apport'::text AS sens, f.date_usage AS date, f.montant, f.usage, f.libelle, f.id AS source_id, u.nom_complet AS employe, f.cree_le
FROM utilisation_financement f
JOIN utilisateur u ON u.id = f.utilisateur_id
WHERE f.source = 'titulaire'
UNION ALL
SELECT 'remboursement'::text AS sens, r.date_remboursement AS date, r.montant, 'remboursement'::text AS usage,
       r.libelle || ' (' || c.nom || ')' AS libelle, r.id AS source_id, u.nom_complet AS employe, r.cree_le
FROM remboursement_titulaire r
JOIN compte_tresorerie c ON c.id = r.compte_id
JOIN utilisateur u ON u.id = r.utilisateur_id;

-- Dettes envers les banques (capital restant dû) et envers le titulaire : mêmes colonnes que v_suivi_dettes
CREATE OR REPLACE VIEW v_dette_financement AS
SELECT 'dette'::text AS sens, 'pret'::text AS categorie, NULL::bigint AS tiers_id, p.banque AS tiers,
       'Prêt '::text || p.reference || COALESCE(' · ' || p.objet, '')
       || CASE WHEN p.echues_non_payees > 0 THEN ' · échéance échue, non payée' ELSE '' END AS libelle,
       p.date_pret AS date_reference, p.prochaine_echeance AS echeance, p.reste_du AS montant, false AS rejetee
FROM v_pret_bancaire p
WHERE p.reste_du > 0
UNION ALL
SELECT 'dette'::text AS sens, 'titulaire'::text AS categorie, NULL::bigint AS tiers_id, 'Titulaire'::text AS tiers,
       'Apports du titulaire non remboursés'::text AS libelle, t.depuis AS date_reference, NULL::date AS echeance, t.solde AS montant,
       false AS rejetee
FROM (SELECT COALESCE(sum(CASE WHEN sens = 'apport' THEN montant ELSE -montant END), 0) AS solde,
             min(date) FILTER (WHERE sens = 'apport') AS depuis
      FROM v_compte_titulaire) t
WHERE t.solde > 0;
