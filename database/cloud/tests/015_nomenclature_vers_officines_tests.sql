-- =====================================================================
-- Tests de la nomenclature envoyée aux officines (script 015), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/015_nomenclature_vers_officines_tests.sql
-- =====================================================================
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000015', 'Essai S5.3 A'), ('bbbbbbbb-0000-0000-0000-000000000015', 'Essai S5.3 B');

DO $$
DECLARE a uuid := 'aaaaaaaa-0000-0000-0000-000000000015'; f bigint; f2 bigint; v uuid; o ordre_central;
BEGIN
    -- 1. Enregistrement : classeur .xlsx seulement, un même contenu gardé une fois
    BEGIN PERFORM cloud_importer_referentiel('nomenclature', 'Essai', 'essai.txt', convert_to('bonjour', 'UTF8'), 'admin.essai'); RAISE EXCEPTION 'texte accepté';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%classeur Excel%', SQLERRM; END;
    BEGIN PERFORM cloud_importer_referentiel('nomenclature', 'Essai', 'vide.xlsx', ''::bytea, 'admin.essai'); RAISE EXCEPTION 'vide accepté';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%vide%', SQLERRM; END;
    f := cloud_importer_referentiel('nomenclature', ' Nomenclature Juin 2026 ', 'nomenclature.xlsx', '\x504b030401020304'::bytea, 'admin.essai');
    f2 := cloud_importer_referentiel('nomenclature', 'Autre libellé', 'copie.xlsx', '\x504b030401020304'::bytea, 'admin.essai');
    ASSERT f = f2, 'même contenu, même fichier';
    ASSERT (SELECT libelle || '|' || octets || '|' || length(empreinte) FROM referentiel_fichier WHERE id = f) = 'Nomenclature Juin 2026|8|64', 'fichier enregistré';

    -- 2. Ordre « nomenclature » : désigne le fichier, avec son empreinte et sa taille
    v := cloud_creer_ordre(a, 'nomenclature', NULL, jsonb_build_object('fichier', f), 'admin.essai');
    PERFORM set_config('app.officine_id', a::text, true);
    SELECT * INTO o FROM ordre_central WHERE id = v;
    ASSERT o.donnees ->> 'libelle' = 'Nomenclature Juin 2026' AND (o.donnees ->> 'octets')::int = 8
           AND o.donnees ->> 'empreinte' = (SELECT empreinte FROM referentiel_fichier WHERE id = f), 'données de l''ordre : ' || o.donnees::text;
    BEGIN PERFORM cloud_creer_ordre(a, 'nomenclature', NULL, '{"fichier":999999}', 'x'); RAISE EXCEPTION 'fichier inconnu accepté';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%inconnu%', SQLERRM; END;
    PERFORM cloud_creer_ordre(a, 'essai', NULL, '{}', 'admin.essai');   -- les autres types restent acceptés
END $$;

-- 3. Téléchargement : seulement par l'officine qui a reçu l'ordre (rôle de l'API)
SELECT id AS fichier FROM referentiel_fichier WHERE libelle = 'Nomenclature Juin 2026' \gset
SELECT set_config('essai.fichier', :'fichier', true) \g /dev/null
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000015', true) \g /dev/null
DO $$ BEGIN ASSERT cloud_referentiel_contenu(current_setting('essai.fichier')::bigint) = '\x504b030401020304'::bytea, 'officine A : contenu remis'; END $$;
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000015', true) \g /dev/null
DO $$ BEGIN ASSERT cloud_referentiel_contenu(current_setting('essai.fichier')::bigint) IS NULL, 'officine B sans ordre : rien'; END $$;
DO $$
BEGIN
    BEGIN PERFORM 1 FROM referentiel_fichier; RAISE EXCEPTION 'API lit la table';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
END $$;
RESET ROLE;

\echo 'Tests 015 : nomenclature envoyée aux officines réussis'
ROLLBACK;
