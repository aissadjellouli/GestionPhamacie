-- =====================================================================
-- Tests des ordres prestations, indications et DCI (script 018), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/018_referentiels_prestations_indications_dci_tests.sql
-- =====================================================================
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000018', 'Essai référentiels 3 à 5');

DO $$
DECLARE a uuid := 'aaaaaaaa-0000-0000-0000-000000000018'; v uuid; o ordre_central;
BEGIN
    -- 1. Prestations : codes en majuscules, triés, valeurs absentes non ajoutées
    v := cloud_creer_ordre(a, 'prestations', NULL, '{"prestations":[{"code":"tension","libelle":" Prise de tension ","prix":150},
        {"code":"INJ","libelle":"Injection","remboursable":true,"tarif_reference":80.5,"taux_tva":0}]}', 'admin.essai');
    PERFORM set_config('app.officine_id', a::text, true);
    SELECT * INTO o FROM ordre_central WHERE id = v;
    ASSERT o.donnees = '{"prestations":[{"code":"INJ","libelle":"Injection","taux_tva":0,"remboursable":true,"tarif_reference":80.5},
        {"code":"TENSION","libelle":"Prise de tension","prix":150}]}'::jsonb, 'données : ' || o.donnees::text;
    BEGIN PERFORM cloud_creer_ordre(a, 'prestations', NULL, '{"prestations":[{"code":"A","libelle":"A","remboursable":true}]}', 'x'); RAISE EXCEPTION 'remboursable sans tarif';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%Prestation invalide%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'prestations', NULL, '{"prestations":[{"code":"A","libelle":"A","tarif_reference":10}]}', 'x'); RAISE EXCEPTION 'tarif sans remboursable';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%Prestation invalide%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'prestations', NULL, '{"prestations":[{"code":"A","libelle":"A","prix":-1}]}', 'x'); RAISE EXCEPTION 'prix négatif';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%Prestation invalide%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'prestations', NULL, '{"prestations":[{"code":"A","libelle":"A","taux_tva":120}]}', 'x'); RAISE EXCEPTION 'TVA';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%Prestation invalide%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'prestations', NULL, '{"prestations":[{"code":"a","libelle":"A"},{"code":"A","libelle":"B"}]}', 'x'); RAISE EXCEPTION 'doublon';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%deux fois%', SQLERRM; END;

    -- 2. Indications et DCI : textes nettoyés, doublons retirés sans tenir compte de la casse
    v := cloud_creer_ordre(a, 'indications', NULL, '{"indications":[" Toux ","toux","Fièvre"]}', 'admin.essai');
    PERFORM set_config('app.officine_id', a::text, true);
    ASSERT (SELECT donnees FROM ordre_central WHERE id = v) = '{"indications":["Fièvre","Toux"]}'::jsonb, 'indications';
    v := cloud_creer_ordre(a, 'dci', NULL, '{"dci":["DAPTOMYCINE","daptomycine","LINEZOLIDE"]}', 'admin.essai');
    PERFORM set_config('app.officine_id', a::text, true);
    ASSERT (SELECT donnees FROM ordre_central WHERE id = v) = '{"dci":["DAPTOMYCINE","LINEZOLIDE"]}'::jsonb, 'dci';
    BEGIN PERFORM cloud_creer_ordre(a, 'indications', NULL, jsonb_build_object('indications', jsonb_build_array(repeat('x', 81))), 'x'); RAISE EXCEPTION 'libellé long';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%1 à 80%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'dci', NULL, '{"dci":[]}', 'x'); RAISE EXCEPTION 'liste vide';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%Liste attendue%', SQLERRM; END;

    -- 3. Les autres types restent acceptés
    PERFORM cloud_creer_ordre(a, 'classe_suivie', 'antibiotiques', '{"dci":["A"]}', 'admin.essai');
END $$;

\echo 'Tests 018 : prestations, indications et DCI réussis'
ROLLBACK;
