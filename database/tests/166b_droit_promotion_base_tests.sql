-- =====================================================================
-- Tests du script administrateur 166b (droit d'exécuter pg_promote(boolean, integer)) et de son retour,
-- depuis la racine du dépôt, sur une base JETABLE, avec un SUPERUTILISATEUR (le script l'exige) :
--   psql -U postgres -d <base jetable> -v ON_ERROR_STOP=1 -f database/tests/166b_droit_promotion_base_tests.sql
-- Avec un compte non superutilisateur (pharma_app), le test est IGNORÉ avec un message, sans échec.
-- Aller, retour, aller : le droit apparaît, disparaît, revient ; le rejeu ne change rien.
-- =====================================================================
SELECT (SELECT rolsuper FROM pg_roles WHERE rolname = current_user)
       AND EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pharma_app') AS peut_tester \gset
\if :peut_tester
BEGIN;

\ir ../init/166b_droit_promotion_base.sql
SELECT has_function_privilege('pharma_app', 'pg_promote(boolean, integer)', 'EXECUTE') AS droit_aller \gset
\ir ../init/166b_droit_promotion_base.sql
SELECT has_function_privilege('pharma_app', 'pg_promote(boolean, integer)', 'EXECUTE') AS droit_rejeu \gset

\ir ../migration/retour/166b_droit_promotion_base_retour.sql
SELECT has_function_privilege('pharma_app', 'pg_promote(boolean, integer)', 'EXECUTE') AS droit_retour \gset
\ir ../migration/retour/166b_droit_promotion_base_retour.sql
SELECT has_function_privilege('pharma_app', 'pg_promote(boolean, integer)', 'EXECUTE') AS droit_retour_rejeu \gset

\ir ../init/166b_droit_promotion_base.sql
SELECT has_function_privilege('pharma_app', 'pg_promote(boolean, integer)', 'EXECUTE') AS droit_second_aller \gset

-- Seul ce droit est accordé : pas de droit sur une autre fonction système réservée
SELECT has_function_privilege('pharma_app', 'pg_control_checkpoint()', 'EXECUTE') AS autre_droit \gset

SELECT :'droit_aller' = 't' AND :'droit_rejeu' = 't' AND :'droit_retour' = 'f' AND :'droit_retour_rejeu' = 'f'
       AND :'droit_second_aller' = 't' AND :'autre_droit' = 'f' AS ok \gset
\if :ok
    \echo 'OK : 166b aller, retour, aller'
\else
    DO $$ BEGIN RAISE EXCEPTION '166b : droit attendu (aller t, retour f, aller t, aucun autre droit)'; END $$;
\endif
ROLLBACK;
\else
    \echo 'IGNORÉ : 166b exige un superutilisateur et le rôle pharma_app (compte courant non superutilisateur ou rôle absent)'
\endif
