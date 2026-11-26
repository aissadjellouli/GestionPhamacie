-- =====================================================================
-- Tests du script 169 (contenance : contenant seul et pilulier) et de son retour arrière, en transaction ANNULÉE,
-- depuis la racine du dépôt, après le script 168 :
--   psql -U pharma_app -d <base jetable> -v ON_ERROR_STOP=1 -f database/tests/169_contenance_contenant_seul_pilulier_tests.sql
-- Exemples de la fonction (mêmes que ContenanceDepuisNomenclatureTests), puis aller, retour, aller : même fonction.
-- =====================================================================
BEGIN;

\ir ../migration/169_contenance_contenant_seul_pilulier.sql

SELECT bool_and(contenance_depuis_conditionnement(c) IS NOT DISTINCT FROM v) AS ok,
       string_agg(c || ' -> ' || coalesce(contenance_depuis_conditionnement(c)::text, 'vide'), ' ; ')
           FILTER (WHERE contenance_depuis_conditionnement(c) IS DISTINCT FROM v) AS ecarts
FROM (VALUES ('FL/120ML', 1), ('FL./120ML', 1), ('FL/120 ML', 1), ('F/30ML', 1), ('FL/20DOSES', 1), ('FL./20 DOSES', 1), ('FL/30G', 1),
             ('T/30G', 1), ('TUBE/30G', 1), ('FLACON DE 100ML', 1), ('B/FL 15 ML', 1), ('B/TUBE DE 30G', 1), ('FL/05ML', 1),
             ('PILULIER/30', 30), ('PILULIER DE 60', 60),
             ('B/30', 30), ('B/10FL. DE 20ML', 10), ('B/30 ET B/60', NULL), ('B/28+7', NULL), ('B/1FL. DE PDRE.', NULL),
             ('POCHE DE 500ML', NULL), ('B/400G', NULL), ('BIDON DE 5L EN PEHD', NULL), ('100 ML', NULL), ('B/01 FL DE 60', NULL),
             ('B/1FL DE 30ML+ PIPETTE GRADUEE EN ML (2ml)', NULL), ('', NULL)) AS t(c, v) \gset
\if :ok
\else
    \echo :ecarts
    DO $$ BEGIN RAISE EXCEPTION 'contenance_depuis_conditionnement : valeurs inattendues'; END $$;
\endif

SELECT md5(prosrc) AS fonction_aller FROM pg_proc WHERE proname = 'contenance_depuis_conditionnement' \gset
\ir ../migration/retour/169_contenance_contenant_seul_pilulier_retour.sql
SELECT contenance_depuis_conditionnement('FL/120ML') IS NULL AND contenance_depuis_conditionnement('B/30') = 30 AS ok \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'retour : la fonction du script 139 n''est pas revenue'; END $$;
\endif
\ir ../migration/169_contenance_contenant_seul_pilulier.sql
SELECT md5(prosrc) = :'fonction_aller' AS identique FROM pg_proc WHERE proname = 'contenance_depuis_conditionnement' \gset
\if :identique
\else
    DO $$ BEGIN RAISE EXCEPTION 'aller, retour, aller : la fonction n''est pas la même'; END $$;
\endif

ROLLBACK;
