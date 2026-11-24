-- =====================================================================
-- Tests du script 167 (liste des postes autorisés) et de son retour arrière, en transaction ANNULÉE,
-- depuis la racine du dépôt, après le script 166 :
--   psql -U pharma_app -d <base jetable> -v ON_ERROR_STOP=1 -f database/tests/167_postes_autorises_tests.sql
-- Contraintes (statuts, clé d'un poste actif, code d'un poste en attente, révocation), unicité du nom sans la casse parmi les
-- postes non révoqués, réglage par défaut « transition », classement « jamais », puis aller, retour, aller à l'identique.
-- =====================================================================
BEGIN;

-- Empreinte de la structure (fonction temporaire, annulée avec la transaction)
CREATE FUNCTION pg_temp.structure_postes() RETURNS text LANGUAGE sql AS $f$
    SELECT (SELECT coalesce(string_agg(table_name || '.' || column_name || ':' || data_type || ':' || is_nullable || ':' || coalesce(column_default, ''), ','
                                       ORDER BY table_name, ordinal_position), '')
            FROM information_schema.columns WHERE table_schema = current_schema()
              AND (table_name IN ('poste_autorise', 'parametre_postes') OR (table_name = 'presence_poste' AND column_name = 'poste_id')))
        || '|' || (SELECT coalesce(string_agg(indexname || ':' || indexdef, ',' ORDER BY indexname), '')
                   FROM pg_indexes WHERE schemaname = current_schema() AND tablename IN ('poste_autorise', 'parametre_postes', 'presence_poste'))
        || '|' || (SELECT coalesce(string_agg(conname || ':' || pg_get_constraintdef(oid), ',' ORDER BY conname), '')
                   FROM pg_constraint WHERE conrelid IN (to_regclass('poste_autorise'), to_regclass('parametre_postes'), to_regclass('presence_poste')))
        || '|' || (SELECT coalesce(string_agg(nom_table || sens || cle_colonnes::text, ',' ORDER BY nom_table), '')
                   FROM sync_table WHERE nom_table IN ('poste_autorise', 'parametre_postes'))
$f$;

\ir ../migration/167_postes_autorises.sql
SELECT pg_temp.structure_postes() AS structure_aller \gset

-- Rejeu : même structure, une seule ligne de réglage
\ir ../migration/167_postes_autorises.sql
SELECT pg_temp.structure_postes() = :'structure_aller' AS identique \gset
\if :identique
\else
    DO $$ BEGIN RAISE EXCEPTION 'le second passage a changé la structure'; END $$;
\endif

DO $$
DECLARE v_id bigint;
BEGIN
    ASSERT (SELECT count(*) FROM parametre_postes) = 1, 'une seule ligne de réglage attendue';
    ASSERT (SELECT mode FROM parametre_postes) = 'transition', 'mode « transition » attendu par défaut';
    ASSERT (SELECT maximum_postes FROM parametre_postes) = 20, 'maximum de 20 postes attendu par défaut';
    ASSERT (SELECT duree_code_minutes FROM parametre_postes) = 15, 'code valable 15 minutes par défaut';
    ASSERT (SELECT count(*) FROM sync_table WHERE nom_table IN ('poste_autorise', 'parametre_postes') AND sens = 'jamais') = 2,
        'les deux tables doivent être classées « jamais »';

    -- Poste en attente : code obligatoire
    BEGIN
        INSERT INTO poste_autorise (nom) VALUES ('CAISSE-1');
        RAISE EXCEPTION 'REFUS_ATTENDU : un poste en attente sans code a été accepté';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    INSERT INTO poste_autorise (nom, code_hache, code_expire_le) VALUES ('CAISSE-1', '\x01', now() + interval '15 minutes') RETURNING id INTO v_id;

    -- Nom unique sans la casse parmi les postes non révoqués
    BEGIN
        INSERT INTO poste_autorise (nom, code_hache, code_expire_le) VALUES ('caisse-1', '\x02', now() + interval '15 minutes');
        RAISE EXCEPTION 'REFUS_ATTENDU : un nom déjà pris (autre casse) a été accepté';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;

    -- Actif : clé, empreinte et date d'enrôlement obligatoires
    BEGIN
        UPDATE poste_autorise SET statut = 'actif' WHERE id = v_id;
        RAISE EXCEPTION 'REFUS_ATTENDU : un poste actif sans clé a été accepté';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    UPDATE poste_autorise SET statut = 'actif', cle_publique = '\x3059', empreinte_cle = 'ABCDEF0123456789', enrole_le = now(),
        code_hache = NULL, code_expire_le = NULL WHERE id = v_id;

    -- Révocation : date obligatoire, et le nom redevient libre
    BEGIN
        UPDATE poste_autorise SET statut = 'revoque' WHERE id = v_id;
        RAISE EXCEPTION 'REFUS_ATTENDU : une révocation sans date a été acceptée';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    UPDATE poste_autorise SET statut = 'revoque', revoque_le = now(), motif_revocation = 'PC volé' WHERE id = v_id;
    INSERT INTO poste_autorise (nom, code_hache, code_expire_le) VALUES ('Caisse-1', '\x03', now() + interval '15 minutes');

    -- Statut inconnu, nom vide, réglage hors bornes : refusés
    BEGIN
        INSERT INTO poste_autorise (nom, statut, code_hache, code_expire_le) VALUES ('X', 'perdu', '\x04', now());
        RAISE EXCEPTION 'REFUS_ATTENDU : un statut inconnu a été accepté';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO poste_autorise (nom, code_hache, code_expire_le) VALUES ('  ', '\x05', now());
        RAISE EXCEPTION 'REFUS_ATTENDU : un nom vide a été accepté';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        UPDATE parametre_postes SET mode = 'ouvert';
        RAISE EXCEPTION 'REFUS_ATTENDU : un mode inconnu a été accepté';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        UPDATE parametre_postes SET maximum_postes = 0;
        RAISE EXCEPTION 'REFUS_ATTENDU : un maximum de 0 poste a été accepté';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    -- Présence : poste prouvé rattaché ; la suppression du poste laisse la présence (poste_id remis à NULL)
    INSERT INTO presence_poste (poste, poste_id) VALUES ('Caisse-1', v_id);
    ASSERT (SELECT poste_id FROM presence_poste WHERE poste = 'Caisse-1') = v_id, 'poste_id attendu';
    DELETE FROM poste_autorise WHERE id = v_id;
    ASSERT (SELECT poste_id FROM presence_poste WHERE poste = 'Caisse-1') IS NULL, 'poste_id doit être remis à NULL';
END $$;

-- Retour (deux fois : rejouable)
\ir ../migration/retour/167_postes_autorises_retour.sql
\ir ../migration/retour/167_postes_autorises_retour.sql
DO $$
BEGIN
    ASSERT to_regclass('poste_autorise') IS NULL, 'poste_autorise doit disparaître après le retour arrière';
    ASSERT to_regclass('parametre_postes') IS NULL, 'parametre_postes doit disparaître après le retour arrière';
    ASSERT NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'presence_poste' AND column_name = 'poste_id'),
        'presence_poste.poste_id doit disparaître après le retour arrière';
    ASSERT NOT EXISTS (SELECT 1 FROM sync_table WHERE nom_table IN ('poste_autorise', 'parametre_postes')), 'le classement doit disparaître après le retour arrière';
END $$;

-- Aller de nouveau : même structure qu'au premier passage
\ir ../migration/167_postes_autorises.sql
SELECT pg_temp.structure_postes() = :'structure_aller' AS identique \gset
\if :identique
\else
    DO $$ BEGIN RAISE EXCEPTION 'aller, retour, aller : la structure n''est pas la même'; END $$;
\endif

ROLLBACK;
