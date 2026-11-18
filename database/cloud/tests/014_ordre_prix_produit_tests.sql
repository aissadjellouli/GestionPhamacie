-- =====================================================================
-- Tests de l'ordre de prix (script 014), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/014_ordre_prix_produit_tests.sql
-- =====================================================================
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000014', 'Essai S5.2');
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000014', true);
SELECT cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"produit","id":"2","op":"creation","donnees":{"id":2,"nom_commercial":"Amoxicilline 500","prix_public":350.00,"tarif_reference":300.00}},'
    '{"n":2,"table":"produit","id":"3","op":"creation","donnees":{"id":3,"nom_commercial":"Crème","prix_public":1000.00}}]}', 'UTF8'));
RESET ROLE;

DO $$
DECLARE a uuid := 'aaaaaaaa-0000-0000-0000-000000000014'; v uuid; o ordre_central;
BEGIN
    v := cloud_creer_ordre(a, 'prix_produit', '2', '{"prix_public":380}', 'admin.essai');
    PERFORM set_config('app.officine_id', a::text, true);
    SELECT * INTO o FROM ordre_central WHERE id = v;
    ASSERT o.attendu = '{"prix_public":350.00,"tarif_reference":300.00}'::jsonb, 'attendu : ' || o.attendu::text;
    ASSERT o.donnees = '{"nom":"Amoxicilline 500","prix_public":380}'::jsonb, 'données : ' || o.donnees::text;
    ASSERT (SELECT donnees ->> 'type' FROM descendant WHERE cle = v::text) = 'prix_produit', 'publié';
    -- Tarif seul, sur un produit sans tarif
    v := cloud_creer_ordre(a, 'prix_produit', '3', '{"tarif_reference":900.5}', 'admin.essai');
    PERFORM set_config('app.officine_id', a::text, true);
    ASSERT (SELECT attendu FROM ordre_central WHERE id = v) = '{"prix_public":1000.00,"tarif_reference":null}'::jsonb, 'tarif attendu vide';

    BEGIN PERFORM cloud_creer_ordre(a, 'prix_produit', '2', '{"prix_public":0}', 'x'); RAISE EXCEPTION 'prix nul accepté';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%supérieurs à 0%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'prix_produit', '2', '{"prix_public":12.345}', 'x'); RAISE EXCEPTION 'trois décimales acceptées';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%deux décimales%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'prix_produit', '2', '{"prix_public":"380"}', 'x'); RAISE EXCEPTION 'texte accepté';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%supérieurs à 0%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'prix_produit', '2', '{"prix_public":350}', 'x'); RAISE EXCEPTION 'prix inchangé accepté';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%Rien ne change%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'prix_produit', '2', '{}', 'x'); RAISE EXCEPTION 'ordre vide accepté';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%Rien à modifier%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'prix_produit', '99', '{"prix_public":10}', 'x'); RAISE EXCEPTION 'produit inconnu accepté';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%inconnu%', SQLERRM; END;
    -- Les autres types restent acceptés (version 013 reprise)
    BEGIN PERFORM cloud_creer_ordre(a, 'employe_bloquer', '5', '{}', 'x'); RAISE EXCEPTION 'employé inconnu accepté';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%inconnu%', SQLERRM; END;
    PERFORM cloud_creer_ordre(a, 'essai', NULL, '{}', 'admin.essai');
END $$;

\echo 'Tests 014 : ordre de prix réussis'
ROLLBACK;
