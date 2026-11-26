-- =====================================================================
-- Tests du script 170 (prêts : échéance payée en plusieurs fois, remboursement anticipé) et de son retour arrière, en transaction
-- ANNULÉE, depuis la racine du dépôt, après le script 169 :
--   psql -U pharma_app -d <base jetable> -v ON_ERROR_STOP=1 -f database/tests/170_pret_paiement_partiel_remboursement_anticipe_tests.sql
-- Un prêt de 1 000 DA en deux échéances de 550 (500 de capital, 50 d'intérêts) : premier paiement partiel de 300 (50 de charges,
-- 250 de capital) → échéance non payée, capital remboursé 250 ; second paiement de 250 → échéance payée, capital remboursé 500 ;
-- remboursement anticipé de 200 → capital remboursé 700, reste dû 300 ; retour refusé ; puis aller, retour, aller sans paiement partiel.
-- =====================================================================
BEGIN;

\ir ../migration/170_pret_paiement_partiel_remboursement_anticipe.sql

SELECT id AS utilisateur FROM utilisateur ORDER BY id LIMIT 1 \gset
SELECT id AS coffre FROM compte_tresorerie ORDER BY id LIMIT 1 \gset
INSERT INTO pret_bancaire (compte_id, preteur, reference, montant, date_pret, utilisateur_id)
VALUES (NULL, 'Prêteur essai 170', 'ESSAI-170', 1000, DATE '2026-01-01', :utilisateur) RETURNING id AS pret \gset
INSERT INTO echeance_pret (pret_id, rang, date_echeance, capital, interets, montant, capital_restant) VALUES
    (:pret, 1, DATE '2026-02-01', 500, 50, 550, 500), (:pret, 2, DATE '2026-03-01', 500, 50, 550, 0);
SELECT id AS e1 FROM echeance_pret WHERE pret_id = :pret AND rang = 1 \gset

INSERT INTO paiement_echeance_pret (echeance_id, date_paiement, moyen, utilisateur_id, montant, capital_paye, charges_payees)
VALUES (:e1, DATE '2026-02-01', 'especes', :utilisateur, 300, 250, 50);
SELECT (SELECT date_paiement IS NULL FROM v_echeance_pret WHERE id = :e1)
   AND (SELECT capital_rembourse = 250 AND reste_du = 750 AND echeances_payees = 0 AND prochain_montant = 550 FROM v_pret_bancaire_complet WHERE id = :pret)
   AND (SELECT montant_paye = 300 AND capital_paye = 250 AND charges_payees = 50 AND paiements = 1 FROM v_echeance_pret_paye WHERE echeance_id = :e1) AS ok \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'paiement partiel : échéance, capital remboursé ou déjà payé faux'; END $$;
\endif

INSERT INTO paiement_echeance_pret (echeance_id, date_paiement, moyen, utilisateur_id, montant, capital_paye, charges_payees)
VALUES (:e1, DATE '2026-02-05', 'especes', :utilisateur, 250, 250, 0);
SELECT (SELECT date_paiement = DATE '2026-02-05' FROM v_echeance_pret WHERE id = :e1)
   AND (SELECT capital_rembourse = 500 AND echeances_payees = 1 AND prochaine_echeance = DATE '2026-03-01' FROM v_pret_bancaire_complet WHERE id = :pret) AS ok \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'échéance payée en deux fois : date ou capital faux'; END $$;
\endif

INSERT INTO remboursement_anticipe_pret (pret_id, date_paiement, capital, frais, moyen, utilisateur_id)
VALUES (:pret, DATE '2026-02-10', 200, 10, 'especes', :utilisateur);
SELECT capital_rembourse = 700 AND reste_du = 300 AS ok FROM v_pret_bancaire_complet WHERE id = :pret \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'remboursement anticipé : capital remboursé faux'; END $$;
\endif

-- Contrôle des parts : capital + charges = montant
SAVEPOINT parts;
\set ON_ERROR_STOP 0
INSERT INTO paiement_echeance_pret (echeance_id, date_paiement, moyen, utilisateur_id, montant, capital_paye, charges_payees)
VALUES (:e1, DATE '2026-02-06', 'especes', :utilisateur, 100, 10, 10);
\set ON_ERROR_STOP 1
ROLLBACK TO SAVEPOINT parts;
SELECT count(*) = 2 AS ok FROM paiement_echeance_pret WHERE echeance_id = :e1 \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'ck_paiement_echeance_pret_parts n''a pas refusé des parts fausses'; END $$;
\endif

-- Retour refusé tant qu'il y a un paiement partiel ou un remboursement anticipé
SAVEPOINT retour;
\set ON_ERROR_STOP 0
\ir ../migration/retour/170_pret_paiement_partiel_remboursement_anticipe_retour.sql
\set ON_ERROR_STOP 1
ROLLBACK TO SAVEPOINT retour;
SELECT to_regclass('remboursement_anticipe_pret') IS NOT NULL AS ok \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'retour : aurait dû être refusé'; END $$;
\endif

-- Sans paiement partiel ni remboursement anticipé : aller, retour, aller à l'identique
DELETE FROM remboursement_anticipe_pret WHERE pret_id = :pret;
DELETE FROM paiement_echeance_pret WHERE echeance_id IN (SELECT id FROM echeance_pret WHERE pret_id = :pret);
DELETE FROM echeance_pret WHERE pret_id = :pret;
DELETE FROM pret_bancaire WHERE id = :pret;
CREATE FUNCTION pg_temp.structure_170() RETURNS text LANGUAGE sql AS $f$
    SELECT coalesce((SELECT string_agg(viewname || ':' || md5(definition), ',' ORDER BY viewname) FROM pg_views
                     WHERE viewname IN ('v_echeance_pret', 'v_pret_bancaire', 'v_pret_bancaire_complet', 'v_echeance_pret_paye')), '')
        || '|' || coalesce((SELECT string_agg(table_name || '.' || column_name, ',' ORDER BY table_name, column_name) FROM information_schema.columns
                            WHERE table_name IN ('paiement_echeance_pret', 'pret_bancaire', 'remboursement_anticipe_pret')), '')
$f$;
SELECT pg_temp.structure_170() AS structure_aller \gset
\ir ../migration/retour/170_pret_paiement_partiel_remboursement_anticipe_retour.sql
SELECT to_regclass('remboursement_anticipe_pret') IS NULL AND to_regclass('v_echeance_pret_paye') IS NULL AS ok \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'retour incomplet'; END $$;
\endif
\ir ../migration/170_pret_paiement_partiel_remboursement_anticipe.sql
SELECT pg_temp.structure_170() = :'structure_aller' AS identique \gset
\if :identique
\else
    DO $$ BEGIN RAISE EXCEPTION 'aller, retour, aller : la structure n''est pas la même'; END $$;
\endif

ROLLBACK;
