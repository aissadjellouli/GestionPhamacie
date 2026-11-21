-- =====================================================================
-- Tests du script 029 (rôle de lecture pharma_cloud_etat), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/029_role_lecture_etat_tests.sql
-- Lit les colonnes permises, cloisonnement des lots, époques et chargements ; refus sur code_activation.empreinte_code,
-- lot_recu.contenu, certificat.motif_revocation, une table miroir, ligne_remplacee, toute écriture directe et toute fonction
-- SECURITY DEFINER ; droits de pharma_cloud_api gardés.
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

DO $$ BEGIN
    ASSERT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pharma_cloud_etat' AND NOT rolsuper AND NOT rolbypassrls),
        'rôle pharma_cloud_etat présent, ni superutilisateur ni BYPASSRLS';
    ASSERT NOT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'public' AND tableowner = 'pharma_cloud_etat'), 'propriétaire d''aucune table';
END $$;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000029', 'Essai 029 A <b>'), ('bbbbbbbb-0000-0000-0000-000000000029', 'Essai 029 B');
INSERT INTO certificat (empreinte, officine_id, serveur, numero_serie, echeance, revoque_le, motif_revocation)
VALUES (repeat('e', 64), 'aaaaaaaa-0000-0000-0000-000000000029', 'principal', '29', now() + interval '1 year', now(), 'essai 029');
INSERT INTO code_activation (empreinte_code, officine_id, serveur, expire_le)
VALUES (repeat('c', 64), 'aaaaaaaa-0000-0000-0000-000000000029', 'principal', now() + interval '1 day');
INSERT INTO journal_securite (officine_id, evenement, detail) VALUES ('aaaaaaaa-0000-0000-0000-000000000029', 'essai_lecture_etat', 'essai');

-- Un lot reçu par A (par l'API, comme en vrai)
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000029', true) \g /dev/null
SELECT cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"vente","id":"1","op":"creation","donnees":{"id":1,"numero":"T-000029"}}]}', 'UTF8')) \g /dev/null
RESET ROLE;

SET LOCAL ROLE pharma_cloud_etat;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000029', true) \g /dev/null

-- Colonnes permises
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM officine WHERE id IN ('aaaaaaaa-0000-0000-0000-000000000029', 'bbbbbbbb-0000-0000-0000-000000000029')) = 2, 'officines lues';
    ASSERT (SELECT left(empreinte, 8) FROM certificat WHERE officine_id = 'aaaaaaaa-0000-0000-0000-000000000029') = 'eeeeeeee', 'certificat lu';
    ASSERT (SELECT revoque_le IS NOT NULL FROM certificat WHERE officine_id = 'aaaaaaaa-0000-0000-0000-000000000029'), 'révocation lue';
    ASSERT (SELECT count(*) FROM code_activation WHERE officine_id = 'aaaaaaaa-0000-0000-0000-000000000029' AND utilise_le IS NULL AND expire_le > now()) = 1, 'code lu (sans empreinte)';
    ASSERT (SELECT count(*) FROM journal_securite WHERE evenement = 'essai_lecture_etat') = 1, 'journal lu';
    ASSERT (SELECT count(*) FROM lot_recu WHERE officine_id = 'aaaaaaaa-0000-0000-0000-000000000029') = 1, 'lot de A lu';
    ASSERT (SELECT nb_lignes FROM lot_recu WHERE officine_id = 'aaaaaaaa-0000-0000-0000-000000000029') = 1, 'nb_lignes lu';
    ASSERT (SELECT dernier_lot FROM officine_epoque WHERE officine_id = 'aaaaaaaa-0000-0000-0000-000000000029' AND epoque = 1) = 1, 'époque lue';
    ASSERT (SELECT count(*) FROM chargement) >= 0, 'chargement lisible';
END $$;

-- Cloisonnement : B ne voit pas le lot de A, même sans filtre ; sans officine posée, rien
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000029', true) \g /dev/null
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM lot_recu) = 0, 'B ne voit aucun lot de A';
    ASSERT (SELECT count(*) FROM officine_epoque) = 0, 'B ne voit aucune époque de A';
END $$;
SELECT set_config('app.officine_id', '', true) \g /dev/null
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM lot_recu) = 0, 'sans officine posée : aucun lot';
    ASSERT (SELECT count(*) FROM officine_epoque) = 0, 'sans officine posée : aucune époque';
    ASSERT (SELECT count(*) FROM chargement) = 0, 'sans officine posée : aucun chargement';
END $$;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000029', true) \g /dev/null

-- Refus : colonnes sensibles, tables hors liste, écritures, fonctions SECURITY DEFINER
DO $$ BEGIN PERFORM empreinte_code FROM code_activation; RAISE EXCEPTION 'empreinte_code devait être refusée';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;
DO $$ BEGIN PERFORM * FROM code_activation; RAISE EXCEPTION 'SELECT * sur code_activation devait être refusé';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;
DO $$ BEGIN PERFORM contenu FROM lot_recu; RAISE EXCEPTION 'lot_recu.contenu devait être refusé';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;
DO $$ BEGIN PERFORM motif_revocation FROM certificat; RAISE EXCEPTION 'certificat.motif_revocation devait être refusé';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;
DO $$ BEGIN PERFORM 1 FROM vente; RAISE EXCEPTION 'table miroir vente devait être refusée';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;
DO $$ BEGIN PERFORM 1 FROM ligne_remplacee; RAISE EXCEPTION 'ligne_remplacee devait être refusée';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;
DO $$ BEGIN PERFORM 1 FROM journal_depart_officine; RAISE EXCEPTION 'journal_depart_officine devait être refusé';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;
DO $$ BEGIN PERFORM 1 FROM cloud_parametre; RAISE EXCEPTION 'cloud_parametre devait être refusé';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;
DO $$ BEGIN INSERT INTO officine (libelle) VALUES ('Essai 029 refus'); RAISE EXCEPTION 'INSERT officine devait être refusé';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;
DO $$ BEGIN UPDATE officine SET libelle = libelle WHERE id = 'aaaaaaaa-0000-0000-0000-000000000029'; RAISE EXCEPTION 'UPDATE officine devait être refusé';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;
DO $$ BEGIN DELETE FROM code_activation WHERE officine_id = 'aaaaaaaa-0000-0000-0000-000000000029'; RAISE EXCEPTION 'DELETE code_activation devait être refusé';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;
DO $$ BEGIN INSERT INTO journal_securite (evenement) VALUES ('essai_refus'); RAISE EXCEPTION 'INSERT journal_securite devait être refusé';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;
DO $$ BEGIN UPDATE officine_epoque SET dernier_lot = dernier_lot; RAISE EXCEPTION 'UPDATE officine_epoque devait être refusé';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;
DO $$ BEGIN UPDATE chargement SET etat = etat; RAISE EXCEPTION 'UPDATE chargement devait être refusé';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;
DO $$ BEGIN INSERT INTO lot_recu (officine_id, epoque, numero, empreinte, version_contrat, nb_lignes, octets, contenu)
    VALUES ('aaaaaaaa-0000-0000-0000-000000000029', 1, 2, repeat('f', 64), 'sync-v1', 0, 0, '\x00'); RAISE EXCEPTION 'INSERT lot_recu devait être refusé';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;
DO $$ BEGIN PERFORM cloud_appliquer_lot(1, 2, repeat('b', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"vente","id":"2","op":"creation","donnees":{"id":2,"numero":"T-000030"}}]}', 'UTF8')); RAISE EXCEPTION 'dépôt de lot devait être refusé';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;
DO $$ BEGIN PERFORM cloud_journaliser(NULL, NULL, 'essai_refus', NULL); RAISE EXCEPTION 'cloud_journaliser devait être refusée';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;
DO $$ BEGIN PERFORM cloud_activer(repeat('c', 64), 'aaaaaaaa-0000-0000-0000-000000000029', repeat('d', 64), '1', now()); RAISE EXCEPTION 'cloud_activer devait être refusée';
EXCEPTION WHEN insufficient_privilege THEN NULL; END $$;
RESET ROLE;

-- Aucune fonction SECURITY DEFINER (hors déclencheurs) exécutable par pharma_cloud_etat ni par PUBLIC ; l'API garde les siennes
DO $$ BEGIN
    ASSERT NOT EXISTS (SELECT 1 FROM pg_proc p WHERE p.pronamespace = 'public'::regnamespace AND p.prokind = 'f' AND p.prosecdef
        AND p.prorettype <> 'trigger'::regtype AND (has_function_privilege('pharma_cloud_etat', p.oid, 'EXECUTE') OR has_function_privilege('public', p.oid, 'EXECUTE'))),
        'fonction SECURITY DEFINER exécutable par pharma_cloud_etat ou PUBLIC';
    ASSERT has_function_privilege('pharma_cloud_api', 'cloud_activer(text, uuid, text, text, timestamptz)', 'EXECUTE'), 'API : cloud_activer';
    ASSERT has_function_privilege('pharma_cloud_api', 'cloud_renouveler(text, text, text, timestamptz)', 'EXECUTE'), 'API : cloud_renouveler';
    ASSERT has_function_privilege('pharma_cloud_api', 'cloud_journaliser(uuid, text, text, text)', 'EXECUTE'), 'API : cloud_journaliser';
    ASSERT has_function_privilege('pharma_cloud_api', 'cloud_code_valable(text)', 'EXECUTE'), 'API : cloud_code_valable';
    ASSERT has_function_privilege('pharma_cloud_api', 'cloud_certificat(text)', 'EXECUTE'), 'API : cloud_certificat';
    ASSERT has_function_privilege('pharma_cloud_api', 'cloud_declarer_changement_epoque(int, int, bigint, bigint, timestamptz, timestamptz, bigint, jsonb)', 'EXECUTE'), 'API : déclaration d''époque';
    -- Aucun droit d'écriture sur aucune table du schéma
    ASSERT NOT EXISTS (SELECT 1 FROM pg_class c WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r', 'p')
        AND has_table_privilege('pharma_cloud_etat', c.oid, 'INSERT, UPDATE, DELETE, TRUNCATE')), 'droit d''écriture sur une table';
    -- Lecture : seulement les 7 tables techniques (par colonnes)
    ASSERT NOT EXISTS (SELECT 1 FROM pg_class c WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r', 'p', 'v')
        AND has_any_column_privilege('pharma_cloud_etat', c.oid, 'SELECT')
        AND c.relname NOT IN ('officine', 'certificat', 'code_activation', 'journal_securite', 'lot_recu', 'officine_epoque', 'chargement')),
        'lecture hors des 7 tables techniques';
END $$;

ROLLBACK;
\echo '029 : tous réussis'
