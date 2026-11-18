-- =====================================================================
-- Tests des ordres de référentiels (script 017), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/017_referentiels_vers_officines_tests.sql
-- =====================================================================
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000017', 'Essai référentiels');

DO $$
DECLARE a uuid := 'aaaaaaaa-0000-0000-0000-000000000017'; v uuid; o ordre_central;
BEGIN
    -- 1. Classe suivie : noms nettoyés, doublons (casse) retirés, triés
    v := cloud_creer_ordre(a, 'classe_suivie', 'antibiotiques', '{"dci":[" Amoxicilline ","amoxicilline","Azithromycine"]}', 'admin.essai');
    PERFORM set_config('app.officine_id', a::text, true);
    SELECT * INTO o FROM ordre_central WHERE id = v;
    ASSERT o.cible = 'antibiotiques' AND o.donnees = '{"dci":["Amoxicilline","Azithromycine"]}'::jsonb, 'données : ' || o.donnees::text;
    ASSERT (SELECT donnees ->> 'type' FROM descendant WHERE cle = v::text) = 'classe_suivie', 'publié';
    BEGIN PERFORM cloud_creer_ordre(a, 'classe_suivie', NULL, '{"dci":["A"]}', 'x'); RAISE EXCEPTION 'sans classe';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%Code de la classe%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'classe_suivie', 'antibiotiques', '{"dci":[]}', 'x'); RAISE EXCEPTION 'liste vide';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%1 à 2000%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'classe_suivie', 'antibiotiques', '{"dci":["A", 3]}', 'x'); RAISE EXCEPTION 'nombre accepté';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%Chaque DCI%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'classe_suivie', 'antibiotiques',
          jsonb_build_object('dci', (SELECT jsonb_agg(repeat('x', 150) || g) FROM generate_series(1, 500) g)), 'x'); RAISE EXCEPTION 'trop gros';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%trop volumineux%', SQLERRM; END;

    -- 2. Organismes : sigles en majuscules, centres triés, champs vides retirés
    v := cloud_creer_ordre(a, 'organismes', 'ignoré', '{"organismes":[{"code":"cnas","nom":" Caisse nationale ","delai_paiement_jours":45,"adresse":"",
        "centres":[{"code":"16","nom":"Alger"},{"code":"09","nom":"Blida"}]},{"code":"MDN","nom":"Ministère de la Défense nationale"}]}', 'admin.essai');
    PERFORM set_config('app.officine_id', a::text, true);
    SELECT * INTO o FROM ordre_central WHERE id = v;
    ASSERT o.cible IS NULL, 'pas de cible';
    ASSERT o.donnees = '{"organismes":[{"code":"CNAS","nom":"Caisse nationale","delai_paiement_jours":45,"centres":[{"code":"09","nom":"Blida"},{"code":"16","nom":"Alger"}]},
        {"code":"MDN","nom":"Ministère de la Défense nationale"}]}'::jsonb, 'données : ' || o.donnees::text;
    BEGIN PERFORM cloud_creer_ordre(a, 'organismes', NULL, '{"organismes":[{"code":"MDN","nom":"M","centres":[{"code":"1","nom":"X"}]}]}', 'x'); RAISE EXCEPTION 'centre hors CNAS';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%CNAS ou à la CASNOS%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'organismes', NULL, '{"organismes":[{"code":"CNAS","nom":"A"},{"code":"cnas","nom":"B"}]}', 'x'); RAISE EXCEPTION 'doublon';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%deux fois%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'organismes', NULL, '{"organismes":[{"code":"CNAS","nom":"A","centres":[{"code":"1","nom":"X"},{"code":"1","nom":"Y"}]}]}', 'x');
          RAISE EXCEPTION 'centre en double';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%centre payeur apparaît deux fois%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'organismes', NULL, '{"organismes":[{"code":"CNAS","nom":"A","delai_paiement_jours":400}]}', 'x'); RAISE EXCEPTION 'délai';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%Organisme invalide%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'organismes', NULL, '{"organismes":[{"code":"TROP-LONG-SIGLE","nom":"A"}]}', 'x'); RAISE EXCEPTION 'sigle long';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%Organisme invalide%', SQLERRM; END;

    -- 3. Les autres types restent acceptés
    PERFORM cloud_creer_ordre(a, 'essai', NULL, '{}', 'admin.essai');
END $$;

\echo 'Tests 017 : référentiels envoyés aux officines réussis'
ROLLBACK;
