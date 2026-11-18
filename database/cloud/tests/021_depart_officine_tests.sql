-- =====================================================================
-- Tests du script 021 (lot S6.3 : départ d'une officine, conservation, effacement), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/021_depart_officine_tests.sql
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000021', 'Essai 021 A'), ('bbbbbbbb-0000-0000-0000-000000000021', 'Essai 021 B');
INSERT INTO certificat (empreinte, officine_id, serveur, numero_serie, echeance)
VALUES (repeat('1', 64), 'aaaaaaaa-0000-0000-0000-000000000021', 'principal', 'S1', now() + interval '1 year'),
       (repeat('2', 64), 'aaaaaaaa-0000-0000-0000-000000000021', 'secours', 'S2', now() + interval '1 year'),
       (repeat('3', 64), 'bbbbbbbb-0000-0000-0000-000000000021', 'principal', 'S3', now() + interval '1 year');

-- Données reçues de A (époques 1 puis 2 : une ligne archivée) et de B
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000021', true) \g /dev/null
SELECT cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"alerte","id":"1","op":"creation","donnees":{"id":1,"message":"a"}},'
    '{"n":2,"table":"alerte","id":"2","op":"creation","donnees":{"id":2,"message":"b"}}]}', 'UTF8')) \g /dev/null
SELECT cloud_appliquer_lot(2, 1, repeat('b', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"alerte","id":"1","op":"creation","donnees":{"id":1,"message":"autre"}}]}', 'UTF8')) \g /dev/null
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000021', true) \g /dev/null
SELECT cloud_appliquer_lot(1, 1, repeat('c', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"alerte","id":"7","op":"creation","donnees":{"id":7,"message":"B"}}]}', 'UTF8')) \g /dev/null
-- L'API n'a aucune fonction d'administration du départ
DO $$ BEGIN
    BEGIN PERFORM cloud_depart_officine('aaaaaaaa-0000-0000-0000-000000000021', 'x'); RAISE EXCEPTION 'départ permis à l''API';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM * FROM cloud_effacer_officine('aaaaaaaa-0000-0000-0000-000000000021'); RAISE EXCEPTION 'effacement permis à l''API';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM count(*) FROM journal_depart_officine; RAISE EXCEPTION 'journal lisible par l''API';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
END $$;
RESET ROLE;
-- Journal chaîné de A (déclencheur d'ajout seul du script 012), écrit par le propriétaire
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000021', true) \g /dev/null
INSERT INTO journal_vente (officine_id, id, sync_epoque, sync_numero) VALUES ('aaaaaaaa-0000-0000-0000-000000000021', 1, 1, 9);
SELECT set_config('app.officine_id', '', true) \g /dev/null

-- 1. Réglage : 6 ans par défaut, 6 minimum, surcharge par officine
DO $$ BEGIN
    ASSERT (SELECT conservation_depart_ans FROM cloud_parametre WHERE id = 1) = 6, 'défaut : 6 ans';
    ASSERT cloud_conservation_depart_ans('aaaaaaaa-0000-0000-0000-000000000021') = 6, 'officine sans surcharge : 6 ans';
    BEGIN PERFORM cloud_regler_conservation_depart(5); RAISE EXCEPTION '5 ans accepté'; EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE '%6 ans minimum%' THEN RAISE; END IF; END;
    BEGIN UPDATE officine SET conservation_depart_ans = 3 WHERE id = 'aaaaaaaa-0000-0000-0000-000000000021'; RAISE EXCEPTION '3 ans accepté';
    EXCEPTION WHEN check_violation THEN NULL; END;
    PERFORM cloud_regler_conservation_depart(8, 'bbbbbbbb-0000-0000-0000-000000000021');
    ASSERT cloud_conservation_depart_ans('bbbbbbbb-0000-0000-0000-000000000021') = 8, 'surcharge : 8 ans';
END $$;

-- 2. Effacement refusé avant le départ (simulation comprise)
DO $$ BEGIN
    BEGIN PERFORM * FROM cloud_effacer_officine('aaaaaaaa-0000-0000-0000-000000000021'); RAISE EXCEPTION 'simulation permise avant départ';
    EXCEPTION WHEN raise_exception THEN IF SQLERRM NOT LIKE '%pas partie%' THEN RAISE; END IF; END;
END $$;

-- 3. Départ : certificats révoqués, resiliee_le posée, journaux ; rejoué : date gardée
DO $$
DECLARE r record; v_le timestamptz;
BEGIN
    SELECT * INTO r FROM cloud_depart_officine('aaaaaaaa-0000-0000-0000-000000000021', 'Administrateur essai');
    ASSERT r.certificats_revoques = 2 AND NOT r.deja_partie AND r.resiliee_le IS NOT NULL, 'départ : 2 certificats révoqués';
    ASSERT NOT EXISTS (SELECT 1 FROM certificat WHERE officine_id = 'aaaaaaaa-0000-0000-0000-000000000021' AND revoque_le IS NULL), 'tous révoqués';
    ASSERT EXISTS (SELECT 1 FROM certificat WHERE officine_id = 'bbbbbbbb-0000-0000-0000-000000000021' AND revoque_le IS NULL), 'B non touchée';
    ASSERT (SELECT bool_and(resiliee) FROM cloud_certificat(repeat('1', 64))), 'certificat refusé : officine résiliée';
    v_le := r.resiliee_le;
    SELECT * INTO r FROM cloud_depart_officine('aaaaaaaa-0000-0000-0000-000000000021', 'Administrateur essai');
    ASSERT r.certificats_revoques = 0 AND r.deja_partie AND r.resiliee_le = v_le, 'départ rejoué : date gardée';
    ASSERT (SELECT count(*) FROM journal_depart_officine WHERE officine_id = 'aaaaaaaa-0000-0000-0000-000000000021' AND action = 'depart') = 2, 'journal du départ';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE officine_id = 'aaaaaaaa-0000-0000-0000-000000000021' AND evenement = 'depart_officine'), 'journal de sécurité';
    BEGIN PERFORM cloud_depart_officine('aaaaaaaa-0000-0000-0000-000000000021', ' '); RAISE EXCEPTION 'départ sans opérateur';
    EXCEPTION WHEN raise_exception THEN IF SQLERRM NOT LIKE '%Opérateur%' THEN RAISE; END IF; END;
END $$;

-- 4. Effacement refusé tant que la durée n'est pas écoulée (simulation comprise)
DO $$ BEGIN
    BEGIN PERFORM * FROM cloud_effacer_officine('aaaaaaaa-0000-0000-0000-000000000021', true, 'Administrateur essai'); RAISE EXCEPTION 'effacement avant le délai';
    EXCEPTION WHEN raise_exception THEN IF SQLERRM NOT LIKE '%non écoulée%' THEN RAISE; END IF; END;
END $$;
-- Les gardes restent fermées même si l'opération est posée à la main avant le délai
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000021', true) \g /dev/null
SELECT set_config('cloud.effacement_officine', 'aaaaaaaa-0000-0000-0000-000000000021', true) \g /dev/null
SELECT set_config('cloud.lot_recu_operation', 'effacer_officine', true) \g /dev/null
DO $$ BEGIN
    BEGIN DELETE FROM lot_recu; RAISE EXCEPTION 'lot brut supprimé avant le délai'; EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE '%ajout seul%' THEN RAISE; END IF; END;
    BEGIN DELETE FROM ligne_remplacee; RAISE EXCEPTION 'archive supprimée avant le délai'; EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE '%ajout seul%' THEN RAISE; END IF; END;
    DELETE FROM journal_vente;
    ASSERT (SELECT count(*) FROM journal_vente) = 1, 'journal chaîné gardé avant le délai';
END $$;
SELECT set_config('cloud.effacement_officine', '', true) \g /dev/null
SELECT set_config('cloud.lot_recu_operation', '', true) \g /dev/null
SELECT set_config('app.officine_id', '', true) \g /dev/null

-- 5. Après le délai (départ reculé de 7 ans pour l'essai) : simulation, puis effacement confirmé avec procès-verbal
UPDATE officine SET resiliee_le = now() - interval '7 years' WHERE id = 'aaaaaaaa-0000-0000-0000-000000000021';
DO $$
DECLARE v jsonb;
BEGIN
    SELECT jsonb_object_agg(nom_table, lignes) INTO v FROM cloud_effacer_officine('aaaaaaaa-0000-0000-0000-000000000021');
    ASSERT v = '{"alerte": 2, "journal_vente": 1, "ligne_remplacee": 1, "lot_recu": 2}'::jsonb, 'simulation : ' || v::text;
    ASSERT NOT EXISTS (SELECT 1 FROM journal_depart_officine WHERE action = 'effacement'), 'simulation : pas de procès-verbal';
    BEGIN PERFORM * FROM cloud_effacer_officine('aaaaaaaa-0000-0000-0000-000000000021', true); RAISE EXCEPTION 'effacement sans opérateur';
    EXCEPTION WHEN raise_exception THEN IF SQLERRM NOT LIKE '%Opérateur%' THEN RAISE; END IF; END;
    SELECT jsonb_object_agg(nom_table, lignes) INTO v FROM cloud_effacer_officine('aaaaaaaa-0000-0000-0000-000000000021', true, 'Administrateur essai');
    ASSERT v = '{"alerte": 2, "journal_vente": 1, "ligne_remplacee": 1, "lot_recu": 2}'::jsonb, 'effacement : ' || v::text;
    ASSERT (SELECT detail -> 'lignes_par_table' FROM journal_depart_officine WHERE action = 'effacement'
            AND officine_id = 'aaaaaaaa-0000-0000-0000-000000000021' AND operateur = 'Administrateur essai') = v, 'procès-verbal';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE officine_id = 'aaaaaaaa-0000-0000-0000-000000000021' AND evenement = 'officine_effacee'), 'journal de sécurité';
    ASSERT coalesce(current_setting('cloud.effacement_officine', true), '') = '', 'opération retirée après l''effacement';
END $$;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000021', true) \g /dev/null
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM alerte) + (SELECT count(*) FROM journal_vente) + (SELECT count(*) FROM ligne_remplacee) + (SELECT count(*) FROM lot_recu) = 0,
        'A : plus rien';
    ASSERT (SELECT count(*) FROM cloud_anomalie_integrite) = 1, 'seule l''anomalie de l''essai avant le délai (4), aucune notée par l''effacement';
END $$;
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000021', true) \g /dev/null
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM alerte) = 1 AND (SELECT count(*) FROM lot_recu) = 1, 'B intacte';
    BEGIN UPDATE journal_depart_officine SET operateur = 'x'; RAISE EXCEPTION 'procès-verbal modifiable'; EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE '%ajout seul%' THEN RAISE; END IF; END;
END $$;

\echo 'Tests du script central 021 (départ, conservation, effacement d''une officine) : tous réussis (transaction annulée).'
ROLLBACK;
