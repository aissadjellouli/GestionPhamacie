-- =====================================================================
-- Tests du script 139 (contenance : flacons, seringues, stylos, cartouches, poches, tubes), en transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/139_contenance_flacons_seringues_stylos_tests.sql
-- Les exemples sont ceux de ContenanceDepuisNomenclatureTests (tests C#) : même résultat attendu.
-- =====================================================================
BEGIN;

CREATE TEMP TABLE t139_version AS
WITH v AS (
    INSERT INTO nomenclature_version (libelle, fichier, en_vigueur, non_renouveles, retraits, utilisateur_id)
    SELECT 'Test 139 ' || g, 'test139.xlsx', 0, 0, 0, (SELECT min(id) FROM utilisateur)
    FROM generate_series(1, 2) g
    RETURNING id)
SELECT min(id) AS ancienne, max(id) AS recente FROM v;

CREATE TEMP TABLE t139_cas (cas text, saisie int, conditionnements text[], attendu int);
INSERT INTO t139_cas VALUES
    ('saisi', 7, '{"B/1 FL"}', NULL),
    ('fl', NULL, '{"B/01 FL"}', 1),
    ('fl_taille', NULL, '{"B/10FL. DE 20ML"}', 10),
    ('fl_sans_de', NULL, '{"B/01 FL 2,5ML"}', 1),
    ('fl_barre', NULL, '{"B/1FL/5ML"}', 1),
    ('flacon_de', NULL, '{"B/01 FLACON DE 125ML"}', 1),
    ('flc', NULL, '{"B/01 FLC DE 60ML"}', 1),
    ('boite_flacon', NULL, '{"BOITE DE 1 FLACON DE 30 ML"}', 1),
    ('flacons_pluriel', NULL, '{"B/10 Flacons"}', 10),
    ('fl_parentheses', NULL, '{"B/10 FL (1 MG)"}', 10),
    ('seringue', NULL, '{"B/1 SERINGUE"}', 1),
    ('seringues', NULL, '{"B/02 SERINGUES"}', 2),
    ('seringue_preremplie', NULL, '{"B/2 SERINGUE PRE-REMPLIES DE 0.8ML"}', 2),
    ('sering_point', NULL, '{"B/1SERING. PREREMPL. DE 0,2ML"}', 1),
    ('seringue_minuscule', NULL, '{"B/6 Seringues préremplies"}', 6),
    ('serin_pre_rempli', NULL, '{"B/1 SERIN PRE REMPLI DE 0,30 ML"}', 1),
    ('stylos', NULL, '{"B/05 STYLOS PREREMPLIS DE 3ML"}', 5),
    ('stylo', NULL, '{"B/01 STYLO"}', 1),
    ('cart', NULL, '{"B/50CART. DE 1,8ML"}', 50),
    ('cartouches', NULL, '{"BOITE DE 05 CARTOUCHES DE 3ML"}', 5),
    ('poches', NULL, '{"B/04 POCHES DE 2L"}', 4),
    ('tube', NULL, '{"B/01 TUBE DE 50G"}', 1),
    ('amp_parentheses', NULL, '{"B/1AMP. (5ML)"}', 1),
    ('amp_barre', NULL, '{"B/20AMP./5ML"}', 20),
    ('amp_barre_collee', NULL, '{"B/1 AMP/5ML"}', 1),
    ('deux_egales', NULL, '{"B/10FL. DE 5ML","B/10FL. DE 5ML"}', 10),
    ('regression_b30', NULL, '{"B/30"}', 30),
    ('regression_amp', NULL, '{"B/5 AMP. DE 2ML"}', 5),
    ('regression_sach', NULL, '{"B/30 SACHETS"}', 30),
    ('fl_en_tete', NULL, '{"FL/05ML"}', NULL),
    ('f_en_tete', NULL, '{"F/125ML"}', NULL),
    ('tube_en_tete', NULL, '{"T/15G"}', NULL),
    ('contenant_avant_nombre', NULL, '{"B/FL 15 ML"}', NULL),
    ('poudre', NULL, '{"B/1FL. DE PDRE."}', NULL),
    ('poudre_solvant', NULL, '{"B/1FL. DE PDRE. + 1FL. DE SOLVT."}', NULL),
    ('taille_sans_unite', NULL, '{"B/01 FL DE 60"}', NULL),
    ('contenant_poudre', NULL, '{"BOITE DE 1 FLACON CONTENANT 500 MG DE POUDRE"}', NULL),
    ('pipette', NULL, '{"B/1FL DE 30ML+ PIPETTE GRADUEE EN ML (2ml)"}', NULL),
    ('stylo_cartouche_aiguille', NULL, '{"B/01STYLO. PREREMPLI DE 0,5ML (CARTOUCHE BI COMPARTIMENTEE)+UNE AIGUILLE"}', NULL),
    ('et', NULL, '{"B/10FL ET B/20FL"}', NULL),
    ('plus', NULL, '{"B/1+1"}', NULL),
    ('gelule_inhalateur', NULL, '{"B/60 GELULE + INHALATEUR"}', NULL),
    ('un_flacon', NULL, '{"BOITE D''UN FLACON"}', NULL),
    ('poche_sans_b', NULL, '{"POCHE DE 500ML"}', NULL),
    ('masse_seule', NULL, '{"B/400G"}', NULL),
    ('pilulier', NULL, '{"PILULIER/30"}', NULL),
    ('sans_unite', NULL, '{"B/10 DE 2ML"}', NULL),
    ('hors_limite', NULL, '{"B/1500 FLACONS"}', NULL),
    ('zero', NULL, '{"B/0 FL"}', NULL),
    ('divergentes', NULL, '{"B/5 FL","B/10 FL"}', NULL),
    ('une_douteuse', NULL, '{"B/5 FL","FL/10ML"}', NULL),
    ('vide', NULL, '{""}', NULL);

INSERT INTO produit (nom_commercial, num_enregistrement, contenance)
SELECT 'Produit test 139 ' || cas, 'T139-' || cas, saisie FROM t139_cas;

INSERT INTO nomenclature_produit (version_id, situation, num_enregistrement, conditionnement)
SELECT (SELECT recente FROM t139_version), 'en_vigueur', 'T139-' || c.cas, NULLIF(x, '')
FROM t139_cas c CROSS JOIN LATERAL unnest(c.conditionnements) x;

-- Version ancienne différente : ignorée au profit de la plus récente
INSERT INTO produit (nom_commercial, num_enregistrement) VALUES ('Produit test 139 versions', 'T139-versions');
INSERT INTO nomenclature_produit (version_id, situation, num_enregistrement, conditionnement) VALUES
    ((SELECT ancienne FROM t139_version), 'en_vigueur', 'T139-versions', 'B/10 FL. DE 2ML'),
    ((SELECT recente  FROM t139_version), 'en_vigueur', 'T139-versions', 'B/20 FL. DE 2ML');
INSERT INTO t139_cas VALUES ('versions', NULL, '{"B/20 FL. DE 2ML"}', 20);

\ir ../migration/139_contenance_flacons_seringues_stylos.sql

DO $$
DECLARE r record;
BEGIN
    ASSERT (SELECT contenance FROM produit WHERE num_enregistrement = 'T139-saisi') = 7, 'valeur saisie jamais écrasée';
    FOR r IN SELECT c.cas, c.attendu, p.contenance FROM t139_cas c JOIN produit p ON p.num_enregistrement = 'T139-' || c.cas
             WHERE c.cas <> 'saisi' LOOP
        ASSERT r.contenance IS NOT DISTINCT FROM r.attendu, format('cas %s : attendu %s, obtenu %s', r.cas, r.attendu, r.contenance);
    END LOOP;
    ASSERT contenance_depuis_conditionnement('b/30 gélules') = 30, 'minuscules accentuées';
    ASSERT contenance_depuis_conditionnement('BOÎTE DE 20 COMPRIMÉS') = 20, 'majuscules accentuées';
    ASSERT contenance_depuis_conditionnement('boîte de 05 ampoules de 2 ml') = 5, 'ampoules en minuscules';
    ASSERT contenance_depuis_conditionnement('FL/100ML') IS NULL, 'flacon exclu';
    -- Trace : une ligne par produit rempli, aucune pour les autres
    ASSERT (SELECT count(*) FROM journal_audit j JOIN produit p ON p.id = j.enregistrement_id
            WHERE p.num_enregistrement LIKE 'T139-%' AND j.action = 'produit_modifie'
              AND j.details->>'motif' = 'contenance depuis la nomenclature, script 139')
         = (SELECT count(*) FROM t139_cas WHERE attendu IS NOT NULL), 'une trace par produit rempli';
    ASSERT (SELECT bool_and((j.details->'contenance'->>'apres')::int = p.contenance AND j.details->'contenance'->'avant' = 'null'::jsonb)
            FROM journal_audit j JOIN produit p ON p.id = j.enregistrement_id
            WHERE p.num_enregistrement LIKE 'T139-%' AND j.details->>'motif' LIKE '%script 139'), 'détail avant/après';
END $$;

CREATE TEMP TABLE t139_apres AS
SELECT p.num_enregistrement, p.contenance FROM produit p WHERE p.num_enregistrement LIKE 'T139-%';
CREATE TEMP TABLE t139_journal AS SELECT count(*) AS n FROM journal_audit;
CREATE TEMP TABLE t139_remplis AS SELECT count(contenance) AS n FROM produit;

-- Idempotence : 2e passage sans effet
\ir ../migration/139_contenance_flacons_seringues_stylos.sql

DO $$
BEGIN
    ASSERT (SELECT count(*) FROM journal_audit) = (SELECT n FROM t139_journal), '2e passage : aucune nouvelle trace';
    ASSERT (SELECT count(contenance) FROM produit) = (SELECT n FROM t139_remplis), '2e passage : aucun produit rempli en plus';
    ASSERT NOT EXISTS (SELECT 1 FROM t139_apres a JOIN produit p ON p.num_enregistrement = a.num_enregistrement
                       WHERE p.contenance IS DISTINCT FROM a.contenance), '2e passage : aucune contenance changée';
END $$;

\echo 'Tests du script 139 : tous réussis (transaction annulée).'
ROLLBACK;
