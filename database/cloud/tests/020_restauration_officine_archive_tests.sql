-- =====================================================================
-- Tests du script 020 (lot S6.1 : archive avant remplacement, maximums, fin de chargement, époque maximale), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/020_restauration_officine_archive_tests.sql
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000020', 'Essai 020 A'), ('bbbbbbbb-0000-0000-0000-000000000020', 'Essai 020 B');
SET LOCAL ROLE pharma_cloud_api;

-- Officine A, époque 1 : quatre produits, puis une modification dans la MÊME époque (pas d'archive)
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000020', true) \g /dev/null
SELECT cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"produit","id":"1","op":"creation","donnees":{"id":1,"nom_commercial":"v1"}},'
    '{"n":2,"table":"produit","id":"2","op":"creation","donnees":{"id":2,"nom_commercial":"identique"}},'
    '{"n":3,"table":"produit","id":"3","op":"creation","donnees":{"id":3,"nom_commercial":"a supprimer"}},'
    '{"n":4,"table":"produit","id":"10","op":"creation","donnees":{"id":10,"nom_commercial":"perdue"}}]}', 'UTF8')) \g /dev/null
SELECT cloud_appliquer_lot(1, 2, repeat('b', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":5,"table":"produit","id":"1","op":"modification","donnees":{"id":1,"nom_commercial":"v1 modifiee"}},'
    '{"n":6,"table":"produit","id":"2","op":"suppression","donnees":null},'
    '{"n":7,"table":"produit","id":"2","op":"creation","donnees":{"id":2,"nom_commercial":"identique"}}]}', 'UTF8')) \g /dev/null
DO $$ BEGIN
    ASSERT cloud_lignes_remplacees(1) = 0, 'même époque : aucune archive';
    ASSERT (SELECT nom_commercial FROM produit WHERE id = 1) = 'v1 modifiee', 'même époque : remplacement comme avant';
END $$;

-- Officine B : un grand id (ne doit jamais compter pour A)
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000020', true) \g /dev/null
SELECT cloud_appliquer_lot(1, 1, repeat('c', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"produit","id":"999","op":"creation","donnees":{"id":999,"nom_commercial":"B"}}]}', 'UTF8')) \g /dev/null

-- Officine A repartie sur l'époque 2 (restauration) : id 1 repris avec un autre contenu, id 2 renvoyé à l'identique,
-- id 3 et 10 supprimés ; chargement complet en cours pour compter les archives à la fin
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000020', true) \g /dev/null
SELECT * FROM cloud_debut_chargement(1, 2, 1, 0, 'demande', ARRAY['produit']) \g /dev/null
SELECT cloud_appliquer_lot(2, 1, repeat('d', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"produit","id":"1","op":"instantane","donnees":{"id":1,"nom_commercial":"nouvelle vente"}},'
    '{"n":1,"table":"produit","id":"2","op":"instantane","donnees":{"id":2,"nom_commercial":"identique"}},'
    '{"n":2,"table":"produit","id":"3","op":"suppression","donnees":null},'
    '{"n":3,"table":"produit","id":"10","op":"suppression","donnees":null}]}', 'UTF8')) \g /dev/null
DO $$ BEGIN
    ASSERT (SELECT nom_commercial FROM produit WHERE id = 1) = 'nouvelle vente', 'époque plus récente : remplacement appliqué';
    ASSERT NOT EXISTS (SELECT 1 FROM produit WHERE id IN (3, 10)), 'époque plus récente : suppressions appliquées';
    ASSERT cloud_lignes_remplacees(2) = 3, 'trois archives (1 remplacée, 3 et 10 supprimées ; 2 identique non archivée) : ' || cloud_lignes_remplacees(2);
END $$;

-- Maximums : A voit 10 (archivée), jamais 999 (officine B) ; B voit 999
DO $$ BEGIN
    ASSERT (SELECT maximum FROM cloud_maximums_officine() WHERE nom_table = 'produit') = 10,
        'maximum de A (archive comprise) : ' || (SELECT maximum FROM cloud_maximums_officine() WHERE nom_table = 'produit');
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_maximums_officine() WHERE nom_table = 'chifa_code_produit'), 'table à clé non entière jamais renvoyée';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_maximums_officine() WHERE nom_table = 'vente'), 'table sans ligne non renvoyée';
END $$;

-- Comptage depuis une date (début du chargement) : rien après « maintenant + 1 s » (correction 2026-11-18)
DO $$ BEGIN
    ASSERT cloud_lignes_remplacees(2, now()) = 3, 'archives depuis le début du chargement : 3';
    ASSERT cloud_lignes_remplacees(2, now() + interval '1 second') = 0, 'archives antérieures à la date exclues';
END $$;

-- Plus grande époque connue (route /etat) : A a les époques 1 et 2 ; officine sans lot : 0
DO $$ BEGIN
    ASSERT cloud_epoque_max() = 2, 'époque maximale de A : ' || cloud_epoque_max();
END $$;

-- Fin du chargement : les archives depuis le début du chargement sont comptées
SELECT * FROM cloud_fin_chargement(1, '{"produit":2}') \g /dev/null
DO $$ BEGIN
    ASSERT (SELECT lignes_remplacees FROM chargement WHERE numero = 1) = 3, 'compte rendu : 3 lignes archivées';
END $$;

-- Droits de l'API : ni lecture, ni modification, ni suppression directe des archives
DO $$ BEGIN
    BEGIN PERFORM count(*) FROM ligne_remplacee; RAISE EXCEPTION 'lecture permise'; EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN UPDATE ligne_remplacee SET operation = 'suppression'; RAISE EXCEPTION 'modification permise'; EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN DELETE FROM ligne_remplacee; RAISE EXCEPTION 'suppression permise'; EXCEPTION WHEN insufficient_privilege THEN NULL; END;
END $$;

SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000020', true) \g /dev/null
DO $$ BEGIN
    ASSERT cloud_lignes_remplacees(2) = 0, 'cloisonnement : B ne compte pas les archives de A';
    ASSERT (SELECT maximum FROM cloud_maximums_officine() WHERE nom_table = 'produit') = 999, 'maximum de B';
    ASSERT cloud_epoque_max() = 1, 'cloisonnement : époque maximale de B';
END $$;
SELECT set_config('app.officine_id', 'cccccccc-0000-0000-0000-000000000020', true) \g /dev/null
DO $$ BEGIN ASSERT cloud_epoque_max() = 0, 'officine sans lot : époque 0'; END $$;
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000020', true) \g /dev/null
RESET ROLE;

-- Propriétaire : contenu de l'archive, cloisonnement et ajout seul (même pour lui)
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000020', true) \g /dev/null
DO $$ BEGIN ASSERT (SELECT count(*) FROM ligne_remplacee) = 0, 'cloisonnement : B ne lit rien'; END $$;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000020', true) \g /dev/null
DO $$
DECLARE l ligne_remplacee;
BEGIN
    SELECT * INTO l FROM ligne_remplacee WHERE cle = '{"id": 1}';
    ASSERT l.operation = 'remplacement' AND l.ancienne ->> 'nom_commercial' = 'v1 modifiee' AND l.ancienne_epoque = 1 AND l.ancien_numero = 5
       AND l.nouvelle_epoque = 2 AND l.nouveau_numero = 1, 'ancienne version de l''id 1 archivée : ' || row_to_json(l)::text;
    SELECT * INTO l FROM ligne_remplacee WHERE cle = '{"id": 10}';
    ASSERT l.operation = 'suppression' AND l.ancienne ->> 'nom_commercial' = 'perdue' AND l.nouveau_numero = 3, 'id 10 supprimé archivé';
    ASSERT NOT EXISTS (SELECT 1 FROM ligne_remplacee WHERE cle = '{"id": 2}'), 'renvoi identique non archivé';
    BEGIN UPDATE ligne_remplacee SET operation = 'suppression'; RAISE EXCEPTION 'modification permise'; EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE '%ajout seul%' THEN RAISE; END IF; END;
    BEGIN DELETE FROM ligne_remplacee; RAISE EXCEPTION 'suppression permise'; EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE '%ajout seul%' THEN RAISE; END IF; END;
    ASSERT (SELECT count(*) FROM ligne_remplacee) = 3, 'archives intactes';
END $$;

\echo 'Tests du script central 020 (archive avant remplacement, maximums, fin de chargement) : tous réussis (transaction annulée).'
ROLLBACK;
