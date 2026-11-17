-- =====================================================================
-- Tests du script central 011 (ajouts_locaux seulement pour motifs et remises, défaut D-M8-1), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/011_descendant_ajouts_locaux_motifs_remises_tests.sql
-- =====================================================================
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000031', 'Essai 011 A'), ('bbbbbbbb-0000-0000-0000-000000000032', 'Essai 011 B');

-- ---- 1. Les 11 tables sont publiées (valeur quelconque : le central ne valide pas le contenu, l'outil d'administration le fait) ----
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY cloud_reglage_tables() LOOP
        PERFORM cloud_publier_reglage(t, '{"x":1}'::jsonb, 'admin-011', 'propose', true);
    END LOOP;
    ASSERT (SELECT count(*) FROM cloud_reglage_modele WHERE valeur IS NOT NULL) = 11, '11 tables publiées';
END $$;

-- ---- 2. Élément descendu : « ajouts_locaux » seulement pour motifs et remises ----
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000031', true);
DO $$
DECLARE r record; n int := 0;
BEGIN
    FOR r IN SELECT d.cle, d.donnees FROM cloud_descendants(0) d WHERE d.cle LIKE 'mutu\_%' LOOP
        n := n + 1;
        IF r.cle IN ('mutu_motif_reglable', 'mutu_remise_automatique') THEN
            ASSERT r.donnees ? 'ajouts_locaux' AND jsonb_typeof(r.donnees -> 'ajouts_locaux') = 'boolean', 'ajouts_locaux attendu pour ' || r.cle;
        ELSE
            ASSERT NOT (r.donnees ? 'ajouts_locaux'), 'ajouts_locaux interdit pour ' || r.cle || ' : ' || r.donnees::text;
        END IF;
        ASSERT r.donnees ->> 'format' = 'mutu-v1' AND r.donnees ->> 'mode' = 'propose' AND r.donnees -> 'valeur' = '{"x":1}'
               AND r.donnees ->> 'auteur' = 'admin-011' AND r.donnees ->> 'publie_le' IS NOT NULL, 'enveloppe complète pour ' || r.cle;
    END LOOP;
    ASSERT n = 11, 'un élément par table : ' || n;
END $$;
RESET ROLE;

-- ---- 3. Officine libérée : mode « local », jamais d'ajouts_locaux ----
SELECT cloud_liberer_reglages('bbbbbbbb-0000-0000-0000-000000000032', 'admin-011');
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000032', true);
DO $$
BEGIN
    ASSERT (SELECT count(*) FROM cloud_descendants(0) d WHERE d.cle LIKE 'mutu\_%') = 11, '11 éléments locaux';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_descendants(0) d WHERE d.cle LIKE 'mutu\_%' AND (d.donnees ->> 'mode' <> 'local' OR d.donnees ? 'ajouts_locaux')), 'libérée : mode local sans ajouts_locaux';
END $$;
RESET ROLE;

-- ---- 4. Droits conservés ----
DO $$
BEGIN
    ASSERT has_function_privilege('pharma_cloud_api', 'cloud_descendants(bigint)', 'EXECUTE'), 'cloud_descendants appelable par l''API';
    ASSERT NOT has_function_privilege('public', 'cloud_descendants(bigint)', 'EXECUTE'), 'pas d''EXECUTE pour PUBLIC';
    ASSERT (SELECT prosecdef FROM pg_proc WHERE oid = 'cloud_descendants(bigint)'::regprocedure), 'SECURITY DEFINER conservé';
    ASSERT pg_get_functiondef('cloud_descendants(bigint)'::regprocedure) LIKE '%m.nom_table IN (%', 'version 011 en place (repère de la garde du script 010)';
END $$;

\echo 'Tests du script central 011 : tous réussis (transaction annulée).'
ROLLBACK;
