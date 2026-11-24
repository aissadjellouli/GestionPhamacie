-- =====================================================================
-- Tests du script 166 (mise hors jeu du serveur 1, décisions de promotion) et de son retour arrière, en transaction ANNULÉE,
-- depuis la racine du dépôt, après le script 165 :
--   psql -U pharma_app -d <base jetable> -v ON_ERROR_STOP=1 -f database/tests/166_bascule_retrait_decision_tests.sql
-- Aller, rejeu, retour (deux fois), aller : même structure ; règles des contraintes ; ajout seul ; retour refusé après un retrait.
-- =====================================================================
BEGIN;

-- Empreinte de la structure (fonction temporaire, annulée avec la transaction)
CREATE FUNCTION pg_temp.structure_166() RETURNS text LANGUAGE sql AS $f$
    SELECT (SELECT coalesce(string_agg(table_name || '.' || column_name || ':' || data_type || ':' || is_nullable || ':' || coalesce(column_default, ''), ','
                                       ORDER BY table_name, ordinal_position), '')
            FROM information_schema.columns WHERE table_schema = current_schema() AND table_name IN ('serveur_ecarte', 'bascule_decision'))
        || '|' || (SELECT coalesce(string_agg(conname || ':' || pg_get_constraintdef(oid), ',' ORDER BY conname), '')
                   FROM pg_constraint WHERE conrelid IN (to_regclass('serveur_ecarte'), coalesce(to_regclass('bascule_decision'), 0::regclass)))
        || '|' || (SELECT coalesce(string_agg(tgname, ',' ORDER BY tgname), '')
                   FROM pg_trigger WHERE NOT tgisinternal AND tgrelid IN (to_regclass('serveur_ecarte'), coalesce(to_regclass('bascule_decision'), 0::regclass)))
        || '|' || (SELECT coalesce(string_agg(nom_table || ':' || sens, ',' ORDER BY nom_table), '') FROM sync_table WHERE nom_table = 'bascule_decision')
$f$;

\ir ../migration/166_bascule_retrait_decision.sql
SELECT pg_temp.structure_166() AS structure_aller \gset

-- Rejeu : même structure
\ir ../migration/166_bascule_retrait_decision.sql
SELECT pg_temp.structure_166() = :'structure_aller' AS identique \gset
\if :identique
\else
    DO $$ BEGIN RAISE EXCEPTION 'le second passage a changé la structure'; END $$;
\endif

SAVEPOINT sans_lignes;

DO $$
BEGIN
    ASSERT (SELECT sens FROM sync_table WHERE nom_table = 'bascule_decision') = 'jamais', 'bascule_decision doit être classée « jamais »';

    -- Écart « bascule » : timeline du retenu toujours obligatoire et plus grande
    BEGIN
        INSERT INTO serveur_ecarte (serveur_retenu, timeline_locale, timeline_retenue, identifiant_systeme, motif)
        VALUES ('srv2', 3, NULL, 'x', 'essai');
        RAISE EXCEPTION 'REFUS_ATTENDU : un écart « bascule » sans timeline retenue a été accepté';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    -- Écart « retrait » : timeline du retenu facultative, nature inconnue refusée
    INSERT INTO serveur_ecarte (serveur_retenu, timeline_locale, timeline_retenue, identifiant_systeme, motif, nature)
    VALUES ('srv2', 3, NULL, 'x', 'retrait demandé par le 2e serveur', 'retrait');
    ASSERT (SELECT count(*) FROM serveur_ecarte WHERE nature = 'retrait') = 1, 'un retrait sans timeline retenue doit être accepté';
    BEGIN
        INSERT INTO serveur_ecarte (serveur_retenu, timeline_locale, timeline_retenue, identifiant_systeme, motif, nature)
        VALUES ('srv2', 3, 4, 'x', 'essai', 'autre');
        RAISE EXCEPTION 'REFUS_ATTENDU : une nature inconnue a été acceptée';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    -- Décision : cas contrôlé, ajout seul
    INSERT INTO bascule_decision (decidee_le, cas, timeline_avant, timeline_apres, motif) VALUES (now(), 'promotion', 3, 4, 'essai');
    BEGIN
        INSERT INTO bascule_decision (decidee_le, cas, motif) VALUES (now(), 'autre', 'essai');
        RAISE EXCEPTION 'REFUS_ATTENDU : un cas inconnu a été accepté';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        UPDATE bascule_decision SET motif = 'modifié';
        RAISE EXCEPTION 'REFUS_ATTENDU : une décision a été modifiée';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM LIKE 'REFUS_ATTENDU%' THEN RAISE; END IF;
    END;
    BEGIN
        DELETE FROM bascule_decision;
        RAISE EXCEPTION 'REFUS_ATTENDU : une décision a été supprimée';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM LIKE 'REFUS_ATTENDU%' THEN RAISE; END IF;
    END;
END $$;

-- Retour refusé tant qu'un retrait ou une décision existe : rien n'est modifié (l'erreur annule seulement jusqu'au point de sauvegarde)
SAVEPOINT retour_refuse;
\set ON_ERROR_STOP 0
\ir ../migration/retour/166_bascule_retrait_decision_retour.sql
\set ON_ERROR_STOP 1
ROLLBACK TO SAVEPOINT retour_refuse;
DO $$
BEGIN
    ASSERT to_regclass('bascule_decision') IS NOT NULL, 'le retour refusé ne doit rien supprimer';
    ASSERT (SELECT count(*) FROM bascule_decision) = 1, 'le retour refusé ne doit rien supprimer';
    ASSERT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'serveur_ecarte' AND column_name = 'nature'), 'la colonne nature doit rester';
END $$;

-- Sans retrait ni décision (lignes d'essai retirées en revenant avant elles) : retour deux fois, puis aller : même structure
ROLLBACK TO SAVEPOINT sans_lignes;
\ir ../migration/retour/166_bascule_retrait_decision_retour.sql
\ir ../migration/retour/166_bascule_retrait_decision_retour.sql
DO $$
BEGIN
    ASSERT to_regclass('bascule_decision') IS NULL, 'bascule_decision doit disparaître après le retour arrière';
    ASSERT NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'serveur_ecarte' AND column_name = 'nature'), 'la colonne nature doit disparaître';
    ASSERT NOT EXISTS (SELECT 1 FROM sync_table WHERE nom_table = 'bascule_decision'), 'le classement doit disparaître';
END $$;
\ir ../migration/166_bascule_retrait_decision.sql
SELECT pg_temp.structure_166() = :'structure_aller' AS identique \gset
\if :identique
\else
    DO $$ BEGIN RAISE EXCEPTION 'aller, retour, aller : la structure n''est pas la même'; END $$;
\endif

ROLLBACK;
