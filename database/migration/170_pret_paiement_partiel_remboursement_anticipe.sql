-- =====================================================================
-- 170 -- Prêts : échéance payée en plusieurs fois, remboursement anticipé
--   Décision du titulaire du 2026-11-26 (lot 2, point 10, « 10 a-i b-i », frais en charges financières) :
--   * une échéance peut être payée en plusieurs fois ; chaque paiement couvre d'abord les charges (intérêts, assurance, TVA, marge)
--     puis le capital ; l'échéance n'est « payée » qu'une fois couverte en entier ;
--   * remboursement anticipé d'une partie du capital (et ses frais, comptés en charges financières) ; le prêt passe alors
--     « échéancier à remplacer » : ses échéances non payées restent visibles mais ne se paient plus jusqu'à l'import du nouveau
--     tableau de la banque.
--   Structure :
--   * paiement_echeance_pret : colonnes montant, capital_paye, charges_payees (paiements existants : l'échéance entière, recopiée de
--     echeance_pret) ; contrainte unique sur echeance_id retirée (plusieurs paiements), index ix_paiement_echeance_pret_echeance ;
--   * pret_bancaire.echeancier_a_remplacer (false par défaut) ;
--   * remboursement_anticipe_pret : capital, frais, moyen (espèces, chèque, virement, prélèvement), compte, référence, part du tiroir,
--     mouvements de trésorerie ; classée « montante » (envoyée au serveur central comme les autres tables des prêts) ;
--   * v_echeance_pret : mêmes colonnes ; une échéance n'a de date de paiement qu'une fois couverte en entier (dernier paiement) ;
--   * v_pret_bancaire : mêmes colonnes ; capital remboursé = capital des paiements (même partiels) + capital remboursé par anticipation ;
--   * v_pret_bancaire_complet : mêmes colonnes ; assurance de la prochaine échéance non entièrement payée ;
--   * v_echeance_pret_paye (nouvelle) : déjà payé de chaque échéance (montant, capital, charges, nombre de paiements).
--   Données : seuls les paiements existants reçoivent leurs nouvelles colonnes (valeurs de l'échéance payée). Rien d'autre ne change.
--   Retour arrière : database/migration/retour/170_pret_paiement_partiel_remboursement_anticipe_retour.sql (refuse s'il existe un
--   paiement partiel ou un remboursement anticipé : les vues d'avant ne savent pas les compter).
-- Idempotent et rejouable sur une base plus récente.
-- A exécuter après 169_contenance_contenant_seul_pilulier.sql.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

-- ---- Paiements d'une échéance : plusieurs, chacun avec ses parts -------------------------------------
ALTER TABLE paiement_echeance_pret ADD COLUMN IF NOT EXISTS montant numeric(12,2);
ALTER TABLE paiement_echeance_pret ADD COLUMN IF NOT EXISTS capital_paye numeric(12,2);
ALTER TABLE paiement_echeance_pret ADD COLUMN IF NOT EXISTS charges_payees numeric(12,2);
UPDATE paiement_echeance_pret p
SET montant = e.montant, capital_paye = e.capital, charges_payees = e.montant - e.capital
FROM echeance_pret e
WHERE e.id = p.echeance_id AND p.montant IS NULL;
ALTER TABLE paiement_echeance_pret ALTER COLUMN montant SET NOT NULL;
ALTER TABLE paiement_echeance_pret ALTER COLUMN capital_paye SET NOT NULL;
ALTER TABLE paiement_echeance_pret ALTER COLUMN charges_payees SET NOT NULL;
DO $garde$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_paiement_echeance_pret_parts') THEN
        ALTER TABLE paiement_echeance_pret ADD CONSTRAINT ck_paiement_echeance_pret_parts
            CHECK (montant > 0 AND capital_paye >= 0 AND charges_payees >= 0 AND capital_paye + charges_payees = montant);
    END IF;
END $garde$;
ALTER TABLE paiement_echeance_pret DROP CONSTRAINT IF EXISTS paiement_echeance_pret_echeance_id_key;
CREATE INDEX IF NOT EXISTS ix_paiement_echeance_pret_echeance ON paiement_echeance_pret (echeance_id);
COMMENT ON COLUMN paiement_echeance_pret.montant IS 'Montant de ce paiement (script 170) : une échéance peut être payée en plusieurs fois';
COMMENT ON COLUMN paiement_echeance_pret.capital_paye IS 'Part capital de ce paiement (script 170) : charges couvertes d''abord, puis le capital';
COMMENT ON COLUMN paiement_echeance_pret.charges_payees IS 'Part charges financières de ce paiement (intérêts, assurance, TVA, marge ; script 170)';

-- ---- Prêt : échéancier à remplacer après un remboursement anticipé ----------------------------------
ALTER TABLE pret_bancaire ADD COLUMN IF NOT EXISTS echeancier_a_remplacer boolean NOT NULL DEFAULT false;
COMMENT ON COLUMN pret_bancaire.echeancier_a_remplacer IS 'Remboursement anticipé enregistré (script 170) : échéances non payées bloquées jusqu''à l''import du nouveau tableau de la banque';

-- ---- Remboursement anticipé ------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS remboursement_anticipe_pret (
    id                    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    pret_id               bigint NOT NULL REFERENCES pret_bancaire(id),
    date_paiement         date NOT NULL,
    capital               numeric(12,2) NOT NULL CHECK (capital > 0),
    frais                 numeric(12,2) NOT NULL DEFAULT 0 CHECK (frais >= 0),
    moyen                 text NOT NULL CHECK (moyen IN ('especes', 'cheque', 'virement', 'prelevement')),
    compte_id             bigint REFERENCES compte_tresorerie(id),
    reference             text,
    part_tiroir           numeric(12,2) NOT NULL DEFAULT 0 CHECK (part_tiroir >= 0),
    versement_id          bigint REFERENCES mouvement_tresorerie(id),
    mouvement_capital_id  bigint REFERENCES mouvement_tresorerie(id),
    mouvement_frais_id    bigint REFERENCES mouvement_tresorerie(id),
    utilisateur_id        bigint NOT NULL REFERENCES utilisateur(id),
    cree_le               timestamptz NOT NULL DEFAULT now(),
    officine_id           uuid NOT NULL DEFAULT officine_courante(),
    CONSTRAINT ck_remboursement_anticipe_pret_moyen CHECK (moyen = 'especes' OR (compte_id IS NOT NULL AND reference IS NOT NULL)),
    CONSTRAINT ck_remboursement_anticipe_pret_tiroir CHECK (part_tiroir = 0 OR (moyen = 'especes' AND versement_id IS NOT NULL))
);
COMMENT ON TABLE remboursement_anticipe_pret IS 'Remboursement anticipé d''une partie du capital d''un prêt (script 170) : capital (remboursement_financement) et frais (dépense « charges financières ») sortis du coffre ou de la banque choisie';
CREATE INDEX IF NOT EXISTS ix_remboursement_anticipe_pret_pret ON remboursement_anticipe_pret (pret_id);
CREATE INDEX IF NOT EXISTS ix_remboursement_anticipe_pret_compte ON remboursement_anticipe_pret (compte_id);
CREATE INDEX IF NOT EXISTS ix_remboursement_anticipe_pret_versement ON remboursement_anticipe_pret (versement_id);
CREATE INDEX IF NOT EXISTS ix_remboursement_anticipe_pret_mouvement_capital ON remboursement_anticipe_pret (mouvement_capital_id);
CREATE INDEX IF NOT EXISTS ix_remboursement_anticipe_pret_mouvement_frais ON remboursement_anticipe_pret (mouvement_frais_id);
CREATE INDEX IF NOT EXISTS ix_remboursement_anticipe_pret_utilisateur ON remboursement_anticipe_pret (utilisateur_id);

-- Synchronisation : montante, comme pret_bancaire et paiement_echeance_pret (script 126) ; déclencheurs reposés si elle est active
-- Même forme que le script 126 (lue par le générateur du miroir du central) ; clé {id} et colonnes texte {} : valeurs par défaut
INSERT INTO sync_table (nom_table, sens, colonnes_exclues, colonnes_patient, porteur, motif) VALUES
    ('remboursement_anticipe_pret', 'montante', '{}', '{}', false, 'Remboursement anticipé d''un prêt, script 170')
ON CONFLICT (nom_table) DO NOTHING;
DO $$ BEGIN PERFORM sync_installer_declencheurs(); END $$;

-- ---- Vues : mêmes colonnes, dans le même ordre (règle des vues rejouées) ------------------------------
CREATE OR REPLACE VIEW v_echeance_pret AS
SELECT e.id, e.pret_id, p.reference AS pret, p.compte_id AS banque_id, COALESCE(c.nom, p.preteur) AS banque, e.rang, e.date_echeance, e.capital,
       e.interets, e.tva, e.marge, e.montant, e.capital_restant,
       CASE WHEN x.paye >= e.montant THEN pe.date_paiement END AS date_paiement,
       CASE WHEN x.paye >= e.montant THEN pe.moyen END AS moyen,
       CASE WHEN x.paye >= e.montant THEN pe.compte_id END AS compte_id,
       CASE WHEN x.paye >= e.montant THEN cp.nom END AS compte,
       CASE WHEN x.paye >= e.montant THEN pe.reference END AS reference,
       (CASE WHEN x.paye >= e.montant THEN x.tiroir END)::numeric(12,2) AS part_tiroir,
       CASE WHEN x.paye >= e.montant THEN u.nom_complet END AS employe
FROM echeance_pret e
JOIN pret_bancaire p ON p.id = e.pret_id
LEFT JOIN compte_tresorerie c ON c.id = p.compte_id
LEFT JOIN LATERAL (SELECT sum(a.montant) AS paye, sum(a.part_tiroir) AS tiroir FROM paiement_echeance_pret a WHERE a.echeance_id = e.id) x ON true
LEFT JOIN LATERAL (SELECT a.date_paiement, a.moyen, a.compte_id, a.reference, a.utilisateur_id FROM paiement_echeance_pret a
                   WHERE a.echeance_id = e.id ORDER BY a.date_paiement DESC, a.id DESC LIMIT 1) pe ON true
LEFT JOIN compte_tresorerie cp ON cp.id = pe.compte_id
LEFT JOIN utilisateur u ON u.id = pe.utilisateur_id;

CREATE OR REPLACE VIEW v_pret_bancaire AS
SELECT p.id, p.compte_id, COALESCE(c.nom, p.preteur) AS banque, p.reference, p.objet, p.montant, p.date_pret, p.document,
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
LEFT JOIN compte_tresorerie c ON c.id = p.compte_id
JOIN utilisateur u ON u.id = p.utilisateur_id
LEFT JOIN LATERAL (
    SELECT count(*) AS nombre, count(v.date_paiement) AS payees,
           COALESCE((SELECT sum(a.capital_paye) FROM paiement_echeance_pret a JOIN echeance_pret b ON b.id = a.echeance_id WHERE b.pret_id = p.id), 0)
           + COALESCE((SELECT sum(r.capital) FROM remboursement_anticipe_pret r WHERE r.pret_id = p.id), 0) AS capital_paye,
           count(*) FILTER (WHERE v.date_paiement IS NULL AND v.date_echeance < current_date) AS echues
    FROM v_echeance_pret v WHERE v.pret_id = p.id) e ON true;

CREATE OR REPLACE VIEW v_pret_bancaire_complet AS
SELECT v.id, v.compte_id, v.banque, v.reference, v.objet, v.montant, v.date_pret, v.document, v.capital_rembourse, v.reste_du, v.utilise,
       v.echeances, v.echeances_payees, v.echues_non_payees, v.prochaine_echeance, v.prochain_montant, v.employe, v.cree_le,
       p.taux, p.duree_mois,
       (SELECT e.assurance FROM v_echeance_pret x JOIN echeance_pret e ON e.id = x.id
        WHERE x.pret_id = v.id AND x.date_paiement IS NULL
        ORDER BY x.rang LIMIT 1) AS prochaine_assurance
FROM v_pret_bancaire v
JOIN pret_bancaire p ON p.id = v.id;

CREATE OR REPLACE VIEW v_echeance_pret_paye AS
SELECT a.echeance_id, sum(a.montant) AS montant_paye, sum(a.capital_paye) AS capital_paye, sum(a.charges_payees) AS charges_payees,
       count(*)::int AS paiements
FROM paiement_echeance_pret a
GROUP BY a.echeance_id;
COMMENT ON VIEW v_echeance_pret_paye IS 'Déjà payé de chaque échéance de prêt (script 170) : montant, capital, charges, nombre de paiements';
