-- =====================================================================
-- Tests du script 173 (une remise de 0 DA ne compte plus au contrôle interne) et de son retour arrière, en transaction ANNULÉE, depuis
-- la racine du dépôt, après 172 :
--   psql -U pharma_app -d <base jetable> -v ON_ERROR_STOP=1 -f database/tests/173_remise_nulle_hors_controle_tests.sql
-- Les deux comptages (remises par employé, remises_patient) exigent un montant de remise positif ; rejeu sans erreur ; aller, retour, aller.
-- =====================================================================
BEGIN;

\ir ../migration/173_remise_nulle_hors_controle.sql
\ir ../migration/173_remise_nulle_hors_controle.sql

SELECT (SELECT count(*) FROM regexp_matches(pg_get_functiondef('surveillance_candidats(date)'::regprocedure), 'montant_remise > 0', 'g')) = 2 AS ok \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'les deux comptages de remises doivent exiger un montant positif'; END $$;
\endif
-- La fonction s'exécute (aucune erreur de syntaxe ni de colonne)
SELECT count(*) >= 0 AS ok FROM surveillance_candidats(current_date) \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'surveillance_candidats en échec'; END $$;
\endif

\ir ../migration/retour/173_remise_nulle_hors_controle_retour.sql
SELECT position('montant_remise > 0' in pg_get_functiondef('surveillance_candidats(date)'::regprocedure)) = 0 AS ok \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'retour incomplet'; END $$;
\endif
\ir ../migration/173_remise_nulle_hors_controle.sql
SELECT position('montant_remise > 0' in pg_get_functiondef('surveillance_candidats(date)'::regprocedure)) > 0 AS ok \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'aller après retour en échec'; END $$;
\endif

ROLLBACK;
\echo '173_remise_nulle_hors_controle_tests : OK'
