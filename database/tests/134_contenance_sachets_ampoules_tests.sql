-- =====================================================================
-- Tests du script 134 (contenance : sachets, ampoules, comprimés…), en transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/134_contenance_sachets_ampoules_tests.sql
-- Les exemples sont ceux de ContenanceDepuisNomenclatureTests (tests C#) : même résultat attendu.
-- =====================================================================
BEGIN;

-- Deux versions de nomenclature de test (la plus récente seule compte)
CREATE TEMP TABLE t134_version AS
WITH v AS (
    INSERT INTO nomenclature_version (libelle, fichier, en_vigueur, non_renouveles, retraits, utilisateur_id)
    SELECT 'Test 134 ' || g, 'test134.xlsx', 0, 0, 0, (SELECT min(id) FROM utilisateur)
    FROM generate_series(1, 2) g
    RETURNING id)
SELECT min(id) AS ancienne, max(id) AS recente FROM v;

-- Produits de test : n° T134-<cas>, contenance vide sauf « saisi »
CREATE TEMP TABLE t134_cas (cas text, saisie int, conditionnements text[], attendu int);
INSERT INTO t134_cas VALUES
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
    ('sachets',        NULL, '{"B/30 SACHETS"}',      30),
    ('sach_point',     NULL, '{"B/20 SACH."}',        20),
    ('sachets_doses',  NULL, '{"B/10 SACHETS-DOSES"}', 10),
    ('amp',            NULL, '{"B/6 AMP."}',          6),
    ('amp_buv',        NULL, '{"B/10 AMP BUV"}',      10),
    ('ampoules',       NULL, '{"B/5 AMPOULES"}',      5),
    ('cp',             NULL, '{"B/30 CP"}',           30),
    ('cpr',            NULL, '{"B/20CPR"}',           20),
    ('cp_eff',         NULL, '{"B/20 COMPRIMÉS EFFERVESCENTS"}', 20),
    ('gelules',        NULL, '{"B/30 GELULES"}',      30),
    ('suppo',          NULL, '{"B/10 SUPPOSITOIRES"}', 10),
    ('ovules',         NULL, '{"B/6 OVULES"}',        6),
    ('capsules',       NULL, '{"B/30 CAPSULES"}',     30),
    ('patchs',         NULL, '{"B/4 PATCHS"}',        4),
    ('transderm',      NULL, '{"B/8 DISPOSITIFS TRANSDERMIQUES"}', 8),
    ('unidoses',       NULL, '{"B/20 RECIPIENTS UNIDOSES"}', 20),
    ('sachets_masse',  NULL, '{"B/30 SACHETS DE 5 G"}', NULL),
    ('amp_volume',     NULL, '{"B/5 AMP. DE 2ML"}',   NULL),
    ('amp_complement', NULL, '{"B/5 AMPOULES EN POLYPROPYLENE"}', NULL),
    ('seringue',       NULL, '{"B/1 SERINGUE"}',      NULL),
    ('sachets_1500',   NULL, '{"B/1500 SACHETS"}',    NULL),
    ('zero',           NULL, '{B/0}',                 NULL),
    ('plus_de_mille',  NULL, '{B/5000}',              NULL),
    ('mille',          NULL, '{B/1000}',              1000),
    ('divergentes',    NULL, '{B/20,B/30}',           NULL),
    ('une_douteuse',   NULL, '{B/20,FL/10ML}',        NULL),
    ('vide',           NULL, '{""}',                  NULL),
    ('sans_ligne',     NULL, '{}',                    NULL);

INSERT INTO produit (nom_commercial, num_enregistrement, contenance)
SELECT 'Produit test 134 ' || cas, 'T134-' || cas, saisie FROM t134_cas;

INSERT INTO nomenclature_produit (version_id, situation, num_enregistrement, conditionnement)
SELECT (SELECT recente FROM t134_version), 'en_vigueur', 'T134-' || c.cas, NULLIF(x, '')
FROM t134_cas c CROSS JOIN LATERAL unnest(c.conditionnements) x;

-- Version ancienne différente : ignorée au profit de la plus récente
INSERT INTO produit (nom_commercial, num_enregistrement) VALUES ('Produit test 134 versions', 'T134-versions');
INSERT INTO nomenclature_produit (version_id, situation, num_enregistrement, conditionnement) VALUES
    ((SELECT ancienne FROM t134_version), 'en_vigueur', 'T134-versions', 'B/10'),
    ((SELECT recente  FROM t134_version), 'en_vigueur', 'T134-versions', 'B/20');
INSERT INTO t134_cas VALUES ('versions', NULL, '{B/20}', 20);

\ir ../migration/134_contenance_sachets_ampoules.sql

DO $$
DECLARE r record;
BEGIN
    ASSERT (SELECT contenance FROM produit WHERE num_enregistrement = 'T134-saisi') = 7, 'valeur saisie jamais écrasée';
    FOR r IN SELECT c.cas, c.attendu, p.contenance FROM t134_cas c JOIN produit p ON p.num_enregistrement = 'T134-' || c.cas
             WHERE c.cas <> 'saisi' LOOP
        ASSERT r.contenance IS NOT DISTINCT FROM r.attendu, format('cas %s : attendu %s, obtenu %s', r.cas, r.attendu, r.contenance);
    END LOOP;
    -- Trace : une ligne par produit rempli, aucune pour les autres
    ASSERT (SELECT count(*) FROM journal_audit j JOIN produit p ON p.id = j.enregistrement_id
            WHERE p.num_enregistrement LIKE 'T134-%' AND j.action = 'produit_modifie'
              AND j.details->>'motif' = 'contenance depuis la nomenclature, script 134')
         = (SELECT count(*) FROM t134_cas WHERE attendu IS NOT NULL), 'une trace par produit rempli';
    ASSERT (SELECT bool_and((j.details->'contenance'->>'apres')::int = p.contenance AND j.details->'contenance'->'avant' = 'null'::jsonb)
            FROM journal_audit j JOIN produit p ON p.id = j.enregistrement_id
            WHERE p.num_enregistrement LIKE 'T134-%' AND j.details->>'motif' LIKE '%script 134'), 'détail avant/après';
END $$;

CREATE TEMP TABLE t134_apres AS
SELECT p.num_enregistrement, p.contenance FROM produit p WHERE p.num_enregistrement LIKE 'T134-%';
CREATE TEMP TABLE t134_journal AS SELECT count(*) AS n FROM journal_audit;

-- Idempotence : 2e passage sans effet
\ir ../migration/134_contenance_sachets_ampoules.sql

DO $$
BEGIN
    ASSERT (SELECT count(*) FROM journal_audit) = (SELECT n FROM t134_journal), '2e passage : aucune nouvelle trace';
    ASSERT NOT EXISTS (SELECT 1 FROM t134_apres a JOIN produit p ON p.num_enregistrement = a.num_enregistrement
                       WHERE p.contenance IS DISTINCT FROM a.contenance), '2e passage : aucune contenance changée';
END $$;

\echo 'Tests du script 134 : tous réussis (transaction annulée).'
ROLLBACK;
