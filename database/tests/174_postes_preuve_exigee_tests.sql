-- =====================================================================
-- Tests du script 174 (postes autorisés : preuve toujours exigée) et de son retour arrière, en transaction ANNULÉE, depuis la racine du
-- dépôt, après 173 :
--   psql -U pharma_app -d <base jetable> -v ON_ERROR_STOP=1 -f database/tests/174_postes_preuve_exigee_tests.sql
-- Mode passé à 'exige' depuis 'transition' ou 'inactif', défaut 'exige', commentaire posé ; rejeu sans erreur ; aller, retour, aller ;
-- le retour remet le défaut 'transition' sans changer la valeur ; ligne absente recréée en 'exige'.
-- =====================================================================
BEGIN;

-- Ligne de réglage présente quel que soit l'état de la base d'essai (référentiel chargé ou non)
INSERT INTO parametre_postes (id) VALUES (1) ON CONFLICT (id) DO NOTHING;
UPDATE parametre_postes SET mode = 'transition', duree_code_minutes = 20 WHERE id = 1;
\ir ../migration/174_postes_preuve_exigee.sql
\ir ../migration/174_postes_preuve_exigee.sql

SELECT (SELECT mode FROM parametre_postes WHERE id = 1) = 'exige'
   AND (SELECT duree_code_minutes FROM parametre_postes WHERE id = 1) = 20 AS ok \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'mode non passé à exige, ou durée du code touchée'; END $$;
\endif
SELECT (SELECT column_default FROM information_schema.columns WHERE table_name = 'parametre_postes' AND column_name = 'mode') LIKE '''exige''%' AS ok \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'valeur par défaut du mode différente de exige'; END $$;
\endif
SELECT col_description('parametre_postes'::regclass,
       (SELECT attnum FROM pg_attribute WHERE attrelid = 'parametre_postes'::regclass AND attname = 'mode')) LIKE 'Plus lu depuis 174%' AS ok \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'commentaire de la colonne mode absent'; END $$;
\endif
-- Mode « inactif » écrit à la main : rejouer le script le remet à exige
UPDATE parametre_postes SET mode = 'inactif' WHERE id = 1;
\ir ../migration/174_postes_preuve_exigee.sql
SELECT (SELECT mode FROM parametre_postes WHERE id = 1) = 'exige' AS ok \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'mode inactif non remis à exige'; END $$;
\endif

-- Retour : défaut 'transition', valeur inchangée, commentaire retiré
\ir ../migration/retour/174_postes_preuve_exigee_retour.sql
\ir ../migration/retour/174_postes_preuve_exigee_retour.sql
SELECT (SELECT column_default FROM information_schema.columns WHERE table_name = 'parametre_postes' AND column_name = 'mode') LIKE '''transition''%'
   AND (SELECT mode FROM parametre_postes WHERE id = 1) = 'exige'
   AND col_description('parametre_postes'::regclass,
       (SELECT attnum FROM pg_attribute WHERE attrelid = 'parametre_postes'::regclass AND attname = 'mode')) IS NULL AS ok \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'retour incomplet'; END $$;
\endif

-- Aller après retour, ligne absente : recréée en exige
DELETE FROM parametre_postes;
\ir ../migration/174_postes_preuve_exigee.sql
SELECT (SELECT mode FROM parametre_postes WHERE id = 1) = 'exige'
   AND (SELECT column_default FROM information_schema.columns WHERE table_name = 'parametre_postes' AND column_name = 'mode') LIKE '''exige''%' AS ok \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'aller après retour en échec'; END $$;
\endif

ROLLBACK;
\echo '174_postes_preuve_exigee_tests : OK'
