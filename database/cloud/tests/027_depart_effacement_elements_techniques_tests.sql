-- =====================================================================
-- Tests du script 027 (effacement d'une officine partie, éléments techniques compris), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/027_depart_effacement_elements_techniques_tests.sql
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000027', 'Essai 027 A'), ('bbbbbbbb-0000-0000-0000-000000000027', 'Essai 027 B');
INSERT INTO certificat (empreinte, officine_id, serveur, numero_serie, echeance)
VALUES (repeat('7', 64), 'aaaaaaaa-0000-0000-0000-000000000027', 'principal', 'S7', now() + interval '1 year'),
       (repeat('8', 64), 'bbbbbbbb-0000-0000-0000-000000000027', 'principal', 'S8', now() + interval '1 year');
INSERT INTO code_activation (empreinte_code, officine_id, serveur, expire_le)
VALUES (repeat('c', 64), 'aaaaaaaa-0000-0000-0000-000000000027', 'secours', now() + interval '1 day');

-- Données reçues de A (époques 1 puis 2 : une ligne archivée ; un chargement) et de B
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000027', true) \g /dev/null
SELECT cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"produit","id":"1","op":"creation","donnees":{"id":1,"nom_commercial":"a"}},'
    '{"n":2,"table":"produit","id":"2","op":"creation","donnees":{"id":2,"nom_commercial":"b"}}]}', 'UTF8')) \g /dev/null
SELECT * FROM cloud_debut_chargement(1, 2, 1, 0, 'demande', ARRAY['produit']) \g /dev/null
SELECT cloud_appliquer_lot(2, 1, repeat('b', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"produit","id":"1","op":"instantane","donnees":{"id":1,"nom_commercial":"autre"}}]}', 'UTF8')) \g /dev/null
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000027', true) \g /dev/null
SELECT cloud_appliquer_lot(1, 1, repeat('c', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"produit","id":"7","op":"creation","donnees":{"id":7,"nom_commercial":"B"}}]}', 'UTF8')) \g /dev/null
RESET ROLE;

-- Éléments techniques des deux officines, écrits par le propriétaire
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000027', true) \g /dev/null
INSERT INTO journal_vente (officine_id, id, sync_epoque, sync_numero) VALUES ('aaaaaaaa-0000-0000-0000-000000000027', 1, 1, 9);
SELECT cloud_regler_famille('fournisseurs', false, 'aaaaaaaa-0000-0000-0000-000000000027') \g /dev/null
SELECT cloud_creer_ordre('aaaaaaaa-0000-0000-0000-000000000027', 'essai', NULL, '{}', 'Administrateur essai') \g /dev/null
SELECT cloud_publier_descendant('reglage_central', 'essai_027', '{"a":1}') \g /dev/null
INSERT INTO cloud_anomalie_integrite (officine_id, journal, nature, detail) VALUES ('aaaaaaaa-0000-0000-0000-000000000027', 'journal_vente', 'ligne_manquante', 'essai A');
INSERT INTO cloud_integrite_etat (officine_id, journal, cle_chaine, statut) VALUES ('aaaaaaaa-0000-0000-0000-000000000027', 'journal_vente', 'officine', 'ok');
INSERT INTO cloud_integrite_sceau (officine_id, sceau_id, statut) VALUES ('aaaaaaaa-0000-0000-0000-000000000027', 1, 'ok');
INSERT INTO cloud_integrite_officine (officine_id) VALUES ('aaaaaaaa-0000-0000-0000-000000000027');
INSERT INTO cloud_journal_reglage (officine_id, action, auteur) VALUES ('aaaaaaaa-0000-0000-0000-000000000027', 'publication', 'essai');
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000027', true) \g /dev/null
SELECT cloud_creer_ordre('bbbbbbbb-0000-0000-0000-000000000027', 'essai', NULL, '{}', 'Administrateur essai') \g /dev/null
SELECT cloud_publier_descendant('reglage_central', 'essai_027', '{"b":1}') \g /dev/null
INSERT INTO cloud_anomalie_integrite (officine_id, journal, nature, detail) VALUES ('bbbbbbbb-0000-0000-0000-000000000027', 'journal_vente', 'ligne_manquante', 'essai B');
INSERT INTO cloud_integrite_etat (officine_id, journal, cle_chaine, statut) VALUES ('bbbbbbbb-0000-0000-0000-000000000027', 'journal_vente', 'officine', 'ok');
INSERT INTO cloud_integrite_sceau (officine_id, sceau_id, statut) VALUES ('bbbbbbbb-0000-0000-0000-000000000027', 1, 'ok');
INSERT INTO cloud_integrite_officine (officine_id) VALUES ('bbbbbbbb-0000-0000-0000-000000000027');
SELECT set_config('app.officine_id', '', true) \g /dev/null

-- Comptes par table pour une officine (propriétaire, officine posée : la sécurité par ligne s'applique)
CREATE FUNCTION pg_temp.compte(p_officine uuid) RETURNS jsonb LANGUAGE plpgsql AS $$
DECLARE t text; n bigint; v jsonb := '{}';
BEGIN
    PERFORM set_config('app.officine_id', p_officine::text, true);
    FOREACH t IN ARRAY ARRAY['produit', 'journal_vente', 'ligne_remplacee', 'lot_recu', 'cloud_anomalie_integrite', 'cloud_integrite_sceau',
                             'cloud_integrite_etat', 'cloud_integrite_officine', 'ordre_central', 'descendant', 'officine_descendant', 'chargement',
                             'officine_volume_jour', 'officine_epoque'] LOOP
        EXECUTE format('SELECT count(*) FROM public.%I WHERE officine_id = $1', t) INTO n USING p_officine;
        IF n > 0 THEN v := v || jsonb_build_object(t, n); END IF;
    END LOOP;
    PERFORM set_config('app.officine_id', '', true);
    RETURN v;
END $$;

-- Nombre de lignes d'une table pour une officine (officine posée le temps du comptage : sécurité par ligne)
CREATE FUNCTION pg_temp.n(p_table text, p_officine uuid) RETURNS bigint LANGUAGE plpgsql AS $$
DECLARE n bigint;
BEGIN
    PERFORM set_config('app.officine_id', p_officine::text, true);
    EXECUTE format('SELECT count(*) FROM public.%I WHERE officine_id = $1', p_table) INTO n USING p_officine;
    PERFORM set_config('app.officine_id', '', true);
    RETURN n;
END $$;

-- 0. Les éléments techniques existent bien pour A et B (le test serait sinon sans valeur)
DO $$
DECLARE v jsonb := pg_temp.compte('aaaaaaaa-0000-0000-0000-000000000027');
BEGIN
    ASSERT (SELECT bool_and(v ? t) FROM unnest(cloud_effacement_tables_techniques()) t), 'A : tous les éléments techniques présents : ' || v::text;
    ASSERT pg_temp.n('ordre_central_journal', 'aaaaaaaa-0000-0000-0000-000000000027') >= 1, 'journal des ordres de A';
END $$;

-- 1. Refus avant le départ, puis avant le délai (simulation comprise)
DO $$ BEGIN
    BEGIN PERFORM * FROM cloud_effacer_officine('aaaaaaaa-0000-0000-0000-000000000027'); RAISE EXCEPTION 'simulation permise avant départ';
    EXCEPTION WHEN raise_exception THEN IF SQLERRM NOT LIKE '%pas partie%' THEN RAISE; END IF; END;
    PERFORM cloud_depart_officine('aaaaaaaa-0000-0000-0000-000000000027', 'Administrateur essai');
    BEGIN PERFORM * FROM cloud_effacer_officine('aaaaaaaa-0000-0000-0000-000000000027'); RAISE EXCEPTION 'simulation permise avant le délai';
    EXCEPTION WHEN raise_exception THEN IF SQLERRM NOT LIKE '%non écoulée%' THEN RAISE; END IF; END;
    BEGIN PERFORM * FROM cloud_effacer_officine('aaaaaaaa-0000-0000-0000-000000000027', true, 'Administrateur essai'); RAISE EXCEPTION 'effacement avant le délai';
    EXCEPTION WHEN raise_exception THEN IF SQLERRM NOT LIKE '%non écoulée%' THEN RAISE; END IF; END;
END $$;

-- 2. Anomalies : suppression refusée hors effacement (même après le délai, même pour le propriétaire)
UPDATE officine SET resiliee_le = now() - interval '7 years' WHERE id = 'aaaaaaaa-0000-0000-0000-000000000027';
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000027', true) \g /dev/null
DO $$ BEGIN
    BEGIN DELETE FROM cloud_anomalie_integrite; RAISE EXCEPTION 'anomalie supprimée hors effacement'; EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE '%ajout seul%' THEN RAISE; END IF; END;
    BEGIN UPDATE cloud_anomalie_integrite SET detail = 'x'; RAISE EXCEPTION 'anomalie modifiée'; EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE '%ajout seul%' THEN RAISE; END IF; END;
END $$;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000027', true) \g /dev/null
DO $$ BEGIN
    -- Pour A (officine partie, délai écoulé) mais SANS l'opération d'effacement posée : toujours refusé
    BEGIN DELETE FROM cloud_anomalie_integrite; RAISE EXCEPTION 'anomalie supprimée sans effacement en cours'; EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE '%ajout seul%' THEN RAISE; END IF; END;
END $$;
SELECT set_config('app.officine_id', '', true) \g /dev/null

-- 3. Simulation : le compte est exact, rien n'est effacé
DO $$
DECLARE avant jsonb := pg_temp.compte('aaaaaaaa-0000-0000-0000-000000000027'); v jsonb; v_tech bigint;
BEGIN
    SELECT jsonb_object_agg(nom_table, lignes) INTO v FROM cloud_effacer_officine('aaaaaaaa-0000-0000-0000-000000000027');
    ASSERT v @> avant AND (SELECT count(*) FROM jsonb_object_keys(v)) = (SELECT count(*) FROM jsonb_object_keys(avant)),
        'simulation : compte exact ; simulé ' || v::text || ' ; réel ' || avant::text;
    ASSERT pg_temp.compte('aaaaaaaa-0000-0000-0000-000000000027') = avant, 'simulation : rien n''est effacé';
    ASSERT NOT EXISTS (SELECT 1 FROM journal_depart_officine WHERE action = 'effacement'), 'simulation : pas de procès-verbal';
    -- ordre : les époques en dernier
    ASSERT (SELECT nom_table FROM cloud_effacer_officine('aaaaaaaa-0000-0000-0000-000000000027') OFFSET (SELECT count(*) FROM jsonb_object_keys(avant)) - 1) = 'officine_epoque',
        'officine_epoque en dernier';
END $$;

-- 4. Effacement confirmé
DO $$
DECLARE avant jsonb := pg_temp.compte('aaaaaaaa-0000-0000-0000-000000000027'); avant_b jsonb := pg_temp.compte('bbbbbbbb-0000-0000-0000-000000000027');
        v jsonb; v_tech bigint; pv jsonb; v_certs int; v_secu int;
BEGIN
    v_certs := pg_temp.n('certificat', 'aaaaaaaa-0000-0000-0000-000000000027');
    v_secu := pg_temp.n('journal_securite', 'aaaaaaaa-0000-0000-0000-000000000027');
    SELECT jsonb_object_agg(nom_table, lignes) INTO v FROM cloud_effacer_officine('aaaaaaaa-0000-0000-0000-000000000027', true, 'Administrateur essai');
    ASSERT v @> avant, 'effacement : mêmes comptes que la simulation : ' || v::text;
    ASSERT pg_temp.compte('aaaaaaaa-0000-0000-0000-000000000027') = '{}'::jsonb, 'A : tout effacé : ' || pg_temp.compte('aaaaaaaa-0000-0000-0000-000000000027')::text;
    -- Gardés : fiche, certificats, journaux, journal des ordres, code d'activation, réglage des familles, journal des réglages
    ASSERT EXISTS (SELECT 1 FROM officine WHERE id = 'aaaaaaaa-0000-0000-0000-000000000027'), 'fiche de l''officine gardée';
    ASSERT pg_temp.n('certificat', 'aaaaaaaa-0000-0000-0000-000000000027') = v_certs AND v_certs = 1, 'certificats gardés';
    ASSERT pg_temp.n('journal_securite', 'aaaaaaaa-0000-0000-0000-000000000027') >= v_secu + 1, 'journal de sécurité gardé et complété';
    ASSERT pg_temp.n('journal_depart_officine', 'aaaaaaaa-0000-0000-0000-000000000027') = 2, 'journal des départs : départ et effacement';
    ASSERT pg_temp.n('ordre_central_journal', 'aaaaaaaa-0000-0000-0000-000000000027') >= 1, 'journal des ordres gardé (option B)';
    ASSERT pg_temp.n('code_activation', 'aaaaaaaa-0000-0000-0000-000000000027') = 1, 'code d''activation gardé';
    ASSERT pg_temp.n('officine_famille', 'aaaaaaaa-0000-0000-0000-000000000027') = 1, 'réglage des familles gardé';
    ASSERT pg_temp.n('cloud_journal_reglage', 'aaaaaaaa-0000-0000-0000-000000000027') = 1, 'journal des réglages gardé';
    -- Procès-verbal
    SELECT detail INTO pv FROM journal_depart_officine WHERE action = 'effacement' AND officine_id = 'aaaaaaaa-0000-0000-0000-000000000027';
    SELECT coalesce(sum(value::bigint), 0) INTO v_tech FROM jsonb_each_text(avant) WHERE key = ANY (cloud_effacement_tables_techniques());
    ASSERT pv ->> 'script' = '027' AND (pv ->> 'techniques')::bigint = v_tech AND v_tech > 0, 'procès-verbal : script et sous-total techniques : ' || pv::text;
    ASSERT pv -> 'lignes_par_table' @> avant AND (pv ->> 'total')::bigint = (SELECT sum(value::bigint) FROM jsonb_each_text(v)), 'procès-verbal : lignes par table et total';
    -- Officine B intacte
    ASSERT pg_temp.compte('bbbbbbbb-0000-0000-0000-000000000027') = avant_b, 'B intacte';
    ASSERT pg_temp.n('ordre_central_journal', 'bbbbbbbb-0000-0000-0000-000000000027') >= 1, 'journal des ordres de B';
    ASSERT coalesce(current_setting('cloud.effacement_officine', true), '') = '' AND coalesce(current_setting('app.officine_id', true), '') = '', 'opération et officine retirées';
    -- Effacement rejoué : plus rien à effacer
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_effacer_officine('aaaaaaaa-0000-0000-0000-000000000027')), 'rejoué : rien à effacer';
END $$;

-- 5. Une officine effacée ne peut pas être réactivée (constat : cloud_activer refuse une officine résiliée)
DO $$ BEGIN
    ASSERT NOT cloud_activer(repeat('c', 64), 'aaaaaaaa-0000-0000-0000-000000000027', repeat('9', 64), 'S9', now() + interval '1 year'),
        'activation refusée pour une officine effacée';
    ASSERT NOT EXISTS (SELECT 1 FROM certificat WHERE empreinte = repeat('9', 64)), 'aucun certificat créé';
END $$;

-- 6. Rejeu : les scripts 012 et 022 repassés ne ramènent ni le déclencheur ni la fonction d'avant
\ir ../012_verification_integrite_central.sql
\ir ../022_integrite_restauration_tables.sql
\ir ../027_depart_effacement_elements_techniques.sql
DO $$ BEGIN
    ASSERT (SELECT tgfoid::regproc::text FROM pg_trigger WHERE tgname = 'trg_cloud_anomalie_integrite_ajout_seul') = 'cloud_integrite_restauration_garde',
        'rejeu de 012 : déclencheur des anomalies gardé';
    ASSERT pg_get_functiondef('cloud_effacer_officine(uuid, boolean, text)'::regprocedure) LIKE '%cloud_effacement_tables_techniques%', 'rejeu de 022 : fonction de 027 gardée';
    ASSERT NOT EXISTS (SELECT 1 FROM pg_constraint WHERE contype = 'f' AND conrelid = 'public.ordre_central_journal'::regclass), 'plus de clé étrangère sur le journal des ordres';
END $$;

\echo 'Tests du script central 027 (effacement d''une officine, éléments techniques) : tous réussis (transaction annulée).'
ROLLBACK;
