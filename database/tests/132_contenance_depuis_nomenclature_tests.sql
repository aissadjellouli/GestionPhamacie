-- =====================================================================
-- Tests du script 132 (contenance depuis la nomenclature), en transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/132_contenance_depuis_nomenclature_tests.sql
-- Les exemples sont ceux de ContenanceDepuisNomenclatureTests (tests C#) : même résultat attendu.
-- =====================================================================
BEGIN;

-- Deux versions de nomenclature de test (la plus récente seule compte)
CREATE TEMP TABLE t132_version AS
WITH v AS (
    INSERT INTO nomenclature_version (libelle, fichier, en_vigueur, non_renouveles, retraits, utilisateur_id)
    SELECT 'Test 132 ' || g, 'test132.xlsx', 0, 0, 0, (SELECT min(id) FROM utilisateur)
    FROM generate_series(1, 2) g
    RETURNING id)
SELECT min(id) AS ancienne, max(id) AS recente FROM v;

-- Produits de test : n° T132-<cas>, contenance vide sauf « saisi »
CREATE TEMP TABLE t132_cas (cas text, saisie int, conditionnements text[], attendu int);
INSERT INTO t132_cas VALUES
    ('saisi',          7,    '{B/30}',                NULL),  -- attendu : 7 inchangé (vérifié à part)
    ('b30',            NULL, '{B/30}',                30),
    ('b030',           NULL, '{B/030}',               30),
    ('boite',          NULL, '{"BOITE DE 20"}',       20),
    ('boite_accent',   NULL, '{"boîte de 14"}',       14),
    ('espaces',        NULL, '{" b / 012 "}',         12),
    ('bte',            NULL, '{BTE/28}',              28),
    ('deux_egales',    NULL, '{B/20,B/20}',           20),
    ('plus',           NULL, '{B/1+1}',               NULL),
    ('et',             NULL, '{"B/30 ET B/90"}',      NULL),
    ('flacon',         NULL, '{FL/10ML}',             NULL),
    ('tube',           NULL, '{T/15G}',               NULL),
    ('b_flacon',       NULL, '{"B/2 FL DE 100 ML"}',  NULL),
    ('b1fl',           NULL, '{"B/1FL. DE PDRE."}',   NULL),
    ('sachets',        NULL, '{"B/30 SACHETS"}',      NULL),
    ('zero',           NULL, '{B/0}',                 NULL),
    ('plus_de_mille',  NULL, '{B/5000}',              NULL),
    ('mille',          NULL, '{B/1000}',              1000),
    ('divergentes',    NULL, '{B/20,B/30}',           NULL),
    ('une_douteuse',   NULL, '{B/20,FL/10ML}',        NULL),
    ('vide',           NULL, '{""}',                  NULL),
    ('sans_ligne',     NULL, '{}',                    NULL);

INSERT INTO produit (nom_commercial, num_enregistrement, contenance)
SELECT 'Produit test 132 ' || cas, 'T132-' || cas, saisie FROM t132_cas;

INSERT INTO nomenclature_produit (version_id, situation, num_enregistrement, conditionnement)
SELECT (SELECT recente FROM t132_version), 'en_vigueur', 'T132-' || c.cas, NULLIF(x, '')
FROM t132_cas c CROSS JOIN LATERAL unnest(c.conditionnements) x;

-- Version ancienne différente : ignorée au profit de la plus récente
INSERT INTO produit (nom_commercial, num_enregistrement) VALUES ('Produit test 132 versions', 'T132-versions');
INSERT INTO nomenclature_produit (version_id, situation, num_enregistrement, conditionnement) VALUES
    ((SELECT ancienne FROM t132_version), 'en_vigueur', 'T132-versions', 'B/10'),
    ((SELECT recente  FROM t132_version), 'en_vigueur', 'T132-versions', 'B/20');
INSERT INTO t132_cas VALUES ('versions', NULL, '{B/20}', 20);

\ir ../migration/132_contenance_depuis_nomenclature.sql

DO $$
DECLARE r record;
BEGIN
    ASSERT (SELECT contenance FROM produit WHERE num_enregistrement = 'T132-saisi') = 7, 'valeur saisie jamais écrasée';
    FOR r IN SELECT c.cas, c.attendu, p.contenance FROM t132_cas c JOIN produit p ON p.num_enregistrement = 'T132-' || c.cas
             WHERE c.cas <> 'saisi' LOOP
        ASSERT r.contenance IS NOT DISTINCT FROM r.attendu, format('cas %s : attendu %s, obtenu %s', r.cas, r.attendu, r.contenance);
    END LOOP;
    -- Trace : une ligne par produit rempli, aucune pour les autres
    ASSERT (SELECT count(*) FROM journal_audit j JOIN produit p ON p.id = j.enregistrement_id
            WHERE p.num_enregistrement LIKE 'T132-%' AND j.action = 'produit_modifie'
              AND j.details->>'motif' = 'contenance depuis la nomenclature, script 132')
         = (SELECT count(*) FROM t132_cas WHERE attendu IS NOT NULL), 'une trace par produit rempli';
    ASSERT (SELECT bool_and((j.details->'contenance'->>'apres')::int = p.contenance AND j.details->'contenance'->'avant' = 'null'::jsonb)
            FROM journal_audit j JOIN produit p ON p.id = j.enregistrement_id
            WHERE p.num_enregistrement LIKE 'T132-%' AND j.details->>'motif' LIKE '%script 132'), 'détail avant/après';
END $$;

CREATE TEMP TABLE t132_apres AS
SELECT p.num_enregistrement, p.contenance FROM produit p WHERE p.num_enregistrement LIKE 'T132-%';
CREATE TEMP TABLE t132_journal AS SELECT count(*) AS n FROM journal_audit;

-- Idempotence : 2e passage sans effet
\ir ../migration/132_contenance_depuis_nomenclature.sql

DO $$
BEGIN
    ASSERT (SELECT count(*) FROM journal_audit) = (SELECT n FROM t132_journal), '2e passage : aucune nouvelle trace';
    ASSERT NOT EXISTS (SELECT 1 FROM t132_apres a JOIN produit p ON p.num_enregistrement = a.num_enregistrement
                       WHERE p.contenance IS DISTINCT FROM a.contenance), '2e passage : aucune contenance changée';
END $$;

\echo 'Tests du script 132 : tous réussis (transaction annulée).'
ROLLBACK;
