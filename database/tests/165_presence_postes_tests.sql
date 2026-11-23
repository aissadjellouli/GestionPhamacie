-- =====================================================================
-- Tests du script 165 (présence des postes) et de son retour arrière, en transaction ANNULÉE,
-- depuis la racine du dépôt, après le script 164 :
--   psql -U pharma_app -d <base jetable> -v ON_ERROR_STOP=1 -f database/tests/165_presence_postes_tests.sql
-- Aller, retour, aller : la table, son index et son classement apparaissent, disparaissent, reviennent à l'identique.
-- =====================================================================
BEGIN;

-- Empreinte de la structure (fonction temporaire, annulée avec la transaction)
CREATE FUNCTION pg_temp.structure_presence() RETURNS text LANGUAGE sql AS $f$
    SELECT (SELECT coalesce(string_agg(column_name || ':' || data_type || ':' || is_nullable || ':' || coalesce(column_default, ''), ',' ORDER BY ordinal_position), '')
            FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'presence_poste')
        || '|' || (SELECT coalesce(string_agg(indexname || ':' || indexdef, ',' ORDER BY indexname), '')
                   FROM pg_indexes WHERE schemaname = current_schema() AND tablename = 'presence_poste')
        || '|' || (SELECT coalesce(string_agg(conname, ',' ORDER BY conname), '')
                   FROM pg_constraint WHERE conrelid = to_regclass('presence_poste'))
        || '|' || (SELECT coalesce(string_agg(sens || cle_colonnes::text, ','), '') FROM sync_table WHERE nom_table = 'presence_poste')
$f$;

\ir ../migration/165_presence_postes.sql
SELECT pg_temp.structure_presence() AS structure_aller \gset

-- Rejeu : même structure
\ir ../migration/165_presence_postes.sql
SELECT pg_temp.structure_presence() = :'structure_aller' AS identique \gset
\if :identique
\else
    DO $$ BEGIN RAISE EXCEPTION 'le second passage a changé la structure'; END $$;
\endif

-- Classement « jamais »
DO $$
BEGIN
    ASSERT (SELECT sens FROM sync_table WHERE nom_table = 'presence_poste') = 'jamais', 'presence_poste doit être classée « jamais »';
END $$;

-- Comportement : une ligne par poste, mise à jour de l'instant, nom vide ou trop long refusé
INSERT INTO presence_poste (poste) VALUES ('CAISSE-1');
DO $$
BEGIN
    ASSERT (SELECT count(*) FROM presence_poste) = 1, 'une ligne attendue';
    INSERT INTO presence_poste (poste, vu_le) VALUES ('CAISSE-1', now() - interval '1 hour')
        ON CONFLICT (poste) DO UPDATE SET vu_le = EXCLUDED.vu_le;
    ASSERT (SELECT count(*) FROM presence_poste) = 1, 'le même poste ne doit pas créer une seconde ligne';
    ASSERT (SELECT vu_le FROM presence_poste WHERE poste = 'CAISSE-1') < now() - interval '59 minutes', 'l''instant doit être mis à jour';
    BEGIN
        INSERT INTO presence_poste (poste) VALUES ('   ');
        RAISE EXCEPTION 'REFUS_ATTENDU : un nom de poste vide a été accepté';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO presence_poste (poste) VALUES (repeat('x', 101));
        RAISE EXCEPTION 'REFUS_ATTENDU : un nom de plus de 100 caractères a été accepté';
    EXCEPTION WHEN string_data_right_truncation THEN NULL;
    END;
END $$;

-- Retour (deux fois : rejouable)
\ir ../migration/retour/165_presence_postes_retour.sql
\ir ../migration/retour/165_presence_postes_retour.sql
DO $$
BEGIN
    ASSERT to_regclass('presence_poste') IS NULL, 'la table doit disparaître après le retour arrière';
    ASSERT NOT EXISTS (SELECT 1 FROM sync_table WHERE nom_table = 'presence_poste'), 'le classement doit disparaître après le retour arrière';
END $$;

-- Aller de nouveau : même structure qu'au premier passage
\ir ../migration/165_presence_postes.sql
SELECT pg_temp.structure_presence() = :'structure_aller' AS identique \gset
\if :identique
\else
    DO $$ BEGIN RAISE EXCEPTION 'aller, retour, aller : la structure n''est pas la même'; END $$;
\endif

ROLLBACK;
