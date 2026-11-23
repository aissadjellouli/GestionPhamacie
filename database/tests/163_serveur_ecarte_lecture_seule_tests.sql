-- =====================================================================
-- Tests du script 163 (état « serveur écarté ») et de son retour arrière, en transaction ANNULÉE,
-- depuis la racine du dépôt, après le script 162 :
--   psql -U pharma_app -d <base jetable> -v ON_ERROR_STOP=1 -f database/tests/163_serveur_ecarte_lecture_seule_tests.sql
-- Aller, retour, aller : la table, ses index, ses déclencheurs et son classement apparaissent, disparaissent, reviennent à l'identique.
-- =====================================================================
BEGIN;

-- Empreinte de la structure (fonction temporaire, annulée avec la transaction)
CREATE FUNCTION pg_temp.structure_ecarte() RETURNS text LANGUAGE sql AS $f$
    SELECT (SELECT coalesce(string_agg(column_name || ':' || data_type || ':' || is_nullable || ':' || coalesce(column_default, ''), ',' ORDER BY ordinal_position), '')
            FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'serveur_ecarte')
        || '|' || (SELECT coalesce(string_agg(indexname || ':' || indexdef, ',' ORDER BY indexname), '')
                   FROM pg_indexes WHERE schemaname = current_schema() AND tablename = 'serveur_ecarte')
        || '|' || (SELECT coalesce(string_agg(tgname, ',' ORDER BY tgname), '')
                   FROM pg_trigger WHERE tgrelid = to_regclass('serveur_ecarte') AND NOT tgisinternal)
        || '|' || (SELECT coalesce(string_agg(sens || cle_colonnes::text, ','), '') FROM sync_table WHERE nom_table = 'serveur_ecarte')
$f$;

\ir ../migration/163_serveur_ecarte_lecture_seule.sql
SELECT pg_temp.structure_ecarte() AS structure_aller \gset

-- Rejeu : même structure
\ir ../migration/163_serveur_ecarte_lecture_seule.sql
SELECT pg_temp.structure_ecarte() = :'structure_aller' AS identique \gset
\if :identique
\else
    DO $$ BEGIN RAISE EXCEPTION 'le second passage a changé la structure'; END $$;
\endif

-- Classement « jamais »
DO $$
BEGIN
    ASSERT (SELECT sens FROM sync_table WHERE nom_table = 'serveur_ecarte') = 'jamais', 'serveur_ecarte doit être classée « jamais »';
END $$;

-- Comportement : une seule ligne active, ajout seul, levée permise une fois
INSERT INTO serveur_ecarte (serveur_retenu, timeline_locale, timeline_retenue, identifiant_systeme, motif)
VALUES ('SERVEUR-B', 1, 2, '7000000000000000001', 'essai');
DO $$
BEGIN
    BEGIN
        INSERT INTO serveur_ecarte (serveur_retenu, timeline_locale, timeline_retenue, identifiant_systeme, motif)
        VALUES ('SERVEUR-B', 1, 2, '7000000000000000001', 'doublon');
        RAISE EXCEPTION 'REFUS_ATTENDU : une seconde ligne active a été acceptée';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    BEGIN
        UPDATE serveur_ecarte SET motif = 'modifié';
        RAISE EXCEPTION 'REFUS_ATTENDU : la modification d''un écart a été acceptée';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM LIKE 'REFUS_ATTENDU%' THEN RAISE; END IF;
    END;
    BEGIN
        DELETE FROM serveur_ecarte;
        RAISE EXCEPTION 'REFUS_ATTENDU : la suppression d''un écart a été acceptée';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM LIKE 'REFUS_ATTENDU%' THEN RAISE; END IF;
    END;
    BEGIN
        TRUNCATE serveur_ecarte;
        RAISE EXCEPTION 'REFUS_ATTENDU : le vidage de la table a été accepté';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM LIKE 'REFUS_ATTENDU%' THEN RAISE; END IF;
    END;
END $$;
UPDATE serveur_ecarte SET leve_le = now(), leve_par = 'titulaire', leve_motif = 'faux positif';
DO $$
BEGIN
    ASSERT (SELECT count(*) FROM serveur_ecarte WHERE leve_le IS NULL) = 0, 'la levée doit désactiver la ligne';
    BEGIN
        UPDATE serveur_ecarte SET leve_motif = 'autre';
        RAISE EXCEPTION 'REFUS_ATTENDU : la modification d''une levée a été acceptée';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM LIKE 'REFUS_ATTENDU%' THEN RAISE; END IF;
    END;
END $$;
-- Après la levée, une nouvelle ligne active est permise
INSERT INTO serveur_ecarte (serveur_retenu, timeline_locale, timeline_retenue, identifiant_systeme, motif)
VALUES ('SERVEUR-C', 2, 3, '7000000000000000001', 'second écart');

-- Retour (deux fois : rejouable)
\ir ../migration/retour/163_serveur_ecarte_lecture_seule_retour.sql
\ir ../migration/retour/163_serveur_ecarte_lecture_seule_retour.sql
DO $$
BEGIN
    ASSERT to_regclass('serveur_ecarte') IS NULL, 'la table doit disparaître après le retour arrière';
    ASSERT NOT EXISTS (SELECT 1 FROM sync_table WHERE nom_table = 'serveur_ecarte'), 'le classement doit disparaître après le retour arrière';
    ASSERT NOT EXISTS (SELECT 1 FROM pg_proc WHERE proname = 'serveur_ecarte_proteger'), 'la fonction doit disparaître après le retour arrière';
END $$;

-- Aller de nouveau : même structure qu'au premier passage
\ir ../migration/163_serveur_ecarte_lecture_seule.sql
SELECT pg_temp.structure_ecarte() = :'structure_aller' AS identique \gset
\if :identique
\else
    DO $$ BEGIN RAISE EXCEPTION 'aller, retour, aller : la structure n''est pas la même'; END $$;
\endif

ROLLBACK;
