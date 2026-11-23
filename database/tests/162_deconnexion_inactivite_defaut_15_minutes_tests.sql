-- =====================================================================
-- Tests du script 162 (déconnexion après inactivité : 15 minutes par défaut) et de son retour arrière, en transaction ANNULÉE,
-- depuis la racine du dépôt, après le script 161 :
--   psql -U pharma_app -d <base jetable> -v ON_ERROR_STOP=1 -f database/tests/162_deconnexion_inactivite_defaut_15_minutes_tests.sql
-- Aller, retour, aller : le défaut passe à 15, revient à 0, repasse à 15 ; la valeur de la ligne existante ne change jamais.
-- =====================================================================
BEGIN;

SELECT deconnexion_minutes AS ligne_avant FROM parametre_caisse WHERE id = 1 \gset

\ir ../migration/162_deconnexion_inactivite_defaut_15_minutes.sql
DO $$
BEGIN
    ASSERT (SELECT column_default FROM information_schema.columns
            WHERE table_schema = current_schema() AND table_name = 'parametre_caisse' AND column_name = 'deconnexion_minutes') = '15',
        'le défaut doit être 15 après le script 162';
END $$;
SELECT string_agg(column_name || ':' || data_type || ':' || is_nullable || ':' || COALESCE(column_default, ''), ',' ORDER BY ordinal_position) AS structure_aller
FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'parametre_caisse' \gset

-- Rejeu : même structure
\ir ../migration/162_deconnexion_inactivite_defaut_15_minutes.sql
SELECT string_agg(column_name || ':' || data_type || ':' || is_nullable || ':' || COALESCE(column_default, ''), ',' ORDER BY ordinal_position) = :'structure_aller' AS identique
FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'parametre_caisse' \gset
\if :identique
\else
    DO $$ BEGIN RAISE EXCEPTION 'le second passage a changé la structure'; END $$;
\endif

-- Retour (deux fois : rejouable), puis aller de nouveau
\ir ../migration/retour/162_deconnexion_inactivite_defaut_15_minutes_retour.sql
\ir ../migration/retour/162_deconnexion_inactivite_defaut_15_minutes_retour.sql
DO $$
BEGIN
    ASSERT (SELECT column_default FROM information_schema.columns
            WHERE table_schema = current_schema() AND table_name = 'parametre_caisse' AND column_name = 'deconnexion_minutes') = '0',
        'le défaut doit revenir à 0 après le retour arrière';
END $$;
\ir ../migration/162_deconnexion_inactivite_defaut_15_minutes.sql
SELECT string_agg(column_name || ':' || data_type || ':' || is_nullable || ':' || COALESCE(column_default, ''), ',' ORDER BY ordinal_position) = :'structure_aller' AS identique
FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'parametre_caisse' \gset
\if :identique
\else
    DO $$ BEGIN RAISE EXCEPTION 'aller, retour, aller : la structure n''est pas la même'; END $$;
\endif

-- Aucune donnée touchée : la ligne existante garde sa valeur
SELECT deconnexion_minutes = :ligne_avant AS inchangee FROM parametre_caisse WHERE id = 1 \gset
\if :inchangee
\else
    DO $$ BEGIN RAISE EXCEPTION 'la valeur réglée de la ligne existante a changé'; END $$;
\endif

ROLLBACK;
