-- =====================================================================
-- Tests du script 137 (contenance : unité de délivrance suivie de sa taille unitaire), en transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/137_contenance_unite_taille_unitaire_tests.sql
-- Les exemples sont ceux de ContenanceDepuisNomenclatureTests (tests C#) : même résultat attendu.
-- =====================================================================
BEGIN;

CREATE TEMP TABLE t137_version AS
WITH v AS (
    INSERT INTO nomenclature_version (libelle, fichier, en_vigueur, non_renouveles, retraits, utilisateur_id)
    SELECT 'Test 137 ' || g, 'test137.xlsx', 0, 0, 0, (SELECT min(id) FROM utilisateur)
    FROM generate_series(1, 2) g
    RETURNING id)
SELECT min(id) AS ancienne, max(id) AS recente FROM v;

CREATE TEMP TABLE t137_cas (cas text, saisie int, conditionnements text[], attendu int);
INSERT INTO t137_cas VALUES
    ('saisi',           7,    '{"B/5 AMP. DE 2ML"}',                  NULL),  -- attendu : 7 inchangé (vérifié à part)
    ('amp_de_ml',       NULL, '{"B/5 AMP. DE 2ML"}',                  5),
    ('amp_collee',      NULL, '{"B/05AMP. DE 2ML"}',                  5),
    ('amp_sans_de',     NULL, '{"B/05 AMP 2ML"}',                    5),
    ('amp_point_sans',  NULL, '{"B/10 AMP.2ML"}',                    10),
    ('amp_virgule',     NULL, '{"B/10 AMP. DE 0,5 ML"}',             10),
    ('boite_ampoules',  NULL, '{"BOITE DE 05 AMPOULES DE 01ML"}',     5),
    ('sachets_masse',   NULL, '{"B/30 SACHETS DE 5 G"}',             30),
    ('sachets_ml',      NULL, '{"B/20 SACHETS DE 15ML"}',            20),
    ('sach_masse',      NULL, '{"B/14SACH. DE 5G"}',                 14),
    ('unidoses_ml',     NULL, '{"B/20 RECIPIENTS UNIDOSES DE 2ML"}', 20),
    ('cp_mg',           NULL, '{"B/30 CP DE 500 MG"}',               30),
    ('deux_egales',     NULL, '{"B/5 AMP. DE 2ML","B/5 AMP. DE 2ML"}', 5),
    ('regression_b30',  NULL, '{B/30}',                              30),
    ('regression_sach', NULL, '{"B/30 SACHETS"}',                    30),
    ('sans_unite',      NULL, '{"B/10 DE 2ML"}',                     NULL),
    ('complement',      NULL, '{"B/5 AMPOULES EN POLYPROPYLENE"}',   NULL),
    ('autocassables',   NULL, '{"B/20AMP.AUTOCASSABLES"}',           NULL),
    ('deux_nombres',    NULL, '{"B/5 AMP. DE 2ML ET B/50 AMP. DE 2ML"}', NULL),
    ('poudre_solvant',  NULL, '{"B/1FL. DE PDRE. + 1AMP. DE 2ML"}',  NULL),
    ('flacon_ml',       NULL, '{"B/2 FL DE 100 ML"}',                NULL),
    ('seringue_ml',     NULL, '{"B/1 SERINGUE PRE-REMPLIE DE 1ML"}', NULL),
    ('flacon_seul',     NULL, '{FL/10ML}',                           NULL),
    ('tube',            NULL, '{T/15G}',                             NULL),
    ('amp_sans_taille', NULL, '{"B/5 AMP. DE 2"}',                   NULL),
    ('amp_barre',       NULL, '{"B/20AMP./5ML"}',                    NULL),
    ('amp_1500',        NULL, '{"B/1500 AMP DE 2ML"}',               NULL),
    ('divergentes',     NULL, '{"B/5 AMP. DE 2ML","B/10 AMP. DE 2ML"}', NULL),
    ('une_douteuse',    NULL, '{"B/5 AMP. DE 2ML",FL/10ML}',         NULL),
    ('vide',            NULL, '{""}',                                NULL);

INSERT INTO produit (nom_commercial, num_enregistrement, contenance)
SELECT 'Produit test 137 ' || cas, 'T137-' || cas, saisie FROM t137_cas;

INSERT INTO nomenclature_produit (version_id, situation, num_enregistrement, conditionnement)
SELECT (SELECT recente FROM t137_version), 'en_vigueur', 'T137-' || c.cas, NULLIF(x, '')
FROM t137_cas c CROSS JOIN LATERAL unnest(c.conditionnements) x;

-- Version ancienne différente : ignorée au profit de la plus récente
INSERT INTO produit (nom_commercial, num_enregistrement) VALUES ('Produit test 137 versions', 'T137-versions');
INSERT INTO nomenclature_produit (version_id, situation, num_enregistrement, conditionnement) VALUES
    ((SELECT ancienne FROM t137_version), 'en_vigueur', 'T137-versions', 'B/10 AMP. DE 2ML'),
    ((SELECT recente  FROM t137_version), 'en_vigueur', 'T137-versions', 'B/20 AMP. DE 2ML');
INSERT INTO t137_cas VALUES ('versions', NULL, '{"B/20 AMP. DE 2ML"}', 20);

\ir ../migration/137_contenance_unite_taille_unitaire.sql

DO $$
DECLARE r record;
BEGIN
    ASSERT (SELECT contenance FROM produit WHERE num_enregistrement = 'T137-saisi') = 7, 'valeur saisie jamais écrasée';
    FOR r IN SELECT c.cas, c.attendu, p.contenance FROM t137_cas c JOIN produit p ON p.num_enregistrement = 'T137-' || c.cas
             WHERE c.cas <> 'saisi' LOOP
        ASSERT r.contenance IS NOT DISTINCT FROM r.attendu, format('cas %s : attendu %s, obtenu %s', r.cas, r.attendu, r.contenance);
    END LOOP;
    ASSERT contenance_depuis_conditionnement('b/30 gélules') = 30, 'minuscules accentuées';
    ASSERT contenance_depuis_conditionnement('BOÎTE DE 20 COMPRIMÉS') = 20, 'majuscules accentuées';
    ASSERT contenance_depuis_conditionnement('boîte de 05 ampoules de 2 ml') = 5, 'ampoules en minuscules';
    ASSERT contenance_depuis_conditionnement('FL/100ML') IS NULL, 'flacon exclu';
    -- Trace : une ligne par produit rempli, aucune pour les autres
    ASSERT (SELECT count(*) FROM journal_audit j JOIN produit p ON p.id = j.enregistrement_id
            WHERE p.num_enregistrement LIKE 'T137-%' AND j.action = 'produit_modifie'
              AND j.details->>'motif' = 'contenance depuis la nomenclature, script 137')
         = (SELECT count(*) FROM t137_cas WHERE attendu IS NOT NULL), 'une trace par produit rempli';
    ASSERT (SELECT bool_and((j.details->'contenance'->>'apres')::int = p.contenance AND j.details->'contenance'->'avant' = 'null'::jsonb)
            FROM journal_audit j JOIN produit p ON p.id = j.enregistrement_id
            WHERE p.num_enregistrement LIKE 'T137-%' AND j.details->>'motif' LIKE '%script 137'), 'détail avant/après';
END $$;

CREATE TEMP TABLE t137_apres AS
SELECT p.num_enregistrement, p.contenance FROM produit p WHERE p.num_enregistrement LIKE 'T137-%';
CREATE TEMP TABLE t137_journal AS SELECT count(*) AS n FROM journal_audit;
CREATE TEMP TABLE t137_remplis AS SELECT count(contenance) AS n FROM produit;

-- Idempotence : 2e passage sans effet
\ir ../migration/137_contenance_unite_taille_unitaire.sql

DO $$
BEGIN
    ASSERT (SELECT count(*) FROM journal_audit) = (SELECT n FROM t137_journal), '2e passage : aucune nouvelle trace';
    ASSERT (SELECT count(contenance) FROM produit) = (SELECT n FROM t137_remplis), '2e passage : aucun produit rempli en plus';
    ASSERT NOT EXISTS (SELECT 1 FROM t137_apres a JOIN produit p ON p.num_enregistrement = a.num_enregistrement
                       WHERE p.contenance IS DISTINCT FROM a.contenance), '2e passage : aucune contenance changée';
END $$;

\echo 'Tests du script 137 : tous réussis (transaction annulée).'
ROLLBACK;
