-- =====================================================================
-- Base centrale, 2026-11-20 (décisions du titulaire, docs/taches/
-- central-decisions-2026-11-19) : effacement d'une officine partie,
-- éléments techniques compris. Script de la base CENTRALE, passé par le
-- propriétaire, idempotent ; à passer après 026 (et 022).
--   1. cloud_effacement_tables_techniques() : liste ORDONNÉE des tables
--      techniques effacées en plus des tables du script 022 : anomalies
--      d'intégrité, sceaux, états et points de reprise de la vérification,
--      ordres, éléments descendants, chargements, volumes du jour, époques.
--      Sert aussi de marqueur de rejeu (gardes dans 012 et 022).
--   2. Le déclencheur d'ajout seul de cloud_anomalie_integrite est rebranché
--      sur cloud_integrite_restauration_garde() : le seul DELETE permis est
--      celui de l'effacement d'une officine partie, durée écoulée.
--   3. ordre_central_journal est GARDÉ (option B) : la clé étrangère vers
--      ordre_central est retirée (les ordres sont effacés, leur journal reste,
--      en ajout seul).
--   4. cloud_effacer_officine redéfinie (dernière définition : 022), même
--      signature (nom_table, lignes). Procès-verbal : « techniques » (sous-total
--      des tables techniques) et « script » = 027.
-- GARDÉS (jamais effacés) : officine (fiche), certificat, code_activation,
-- officine_famille, journal_securite, journal_depart_officine,
-- cloud_journal_reglage, ordre_central_journal.
-- PostgreSQL 18 exigé (comme 020 à 022). Aucune donnée n'est effacée par ce
-- script : seule la fonction d'effacement, soumise à son délai, le fait.
-- =====================================================================

SET client_encoding = 'UTF8';

DO $version$ BEGIN
    IF current_setting('server_version_num')::int < 180000 THEN
        RAISE EXCEPTION 'Script central 027 : PostgreSQL 18 ou plus récent exigé (comme les scripts 020 à 022) ; ce serveur est en version %. Rien n''a été modifié.',
            current_setting('server_version');
    END IF;
    IF to_regclass('public.cloud_epoque_changement') IS NULL THEN
        RAISE EXCEPTION 'Script central 027 : passer d''abord le script 022 (table cloud_epoque_changement absente). Rien n''a été modifié.';
    END IF;
END $version$;

-- ---- 1. Tables techniques, dans l'ordre d'effacement ----
-- Ordre : vérification d'intégrité, ordres, éléments descendants, chargements et volumes, époques en dernier.
CREATE OR REPLACE FUNCTION cloud_effacement_tables_techniques() RETURNS text[]
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT ARRAY['cloud_anomalie_integrite', 'cloud_integrite_sceau', 'cloud_integrite_etat', 'cloud_integrite_officine',
                 'ordre_central', 'descendant', 'officine_descendant', 'chargement', 'officine_volume_jour', 'officine_epoque']
$$;
REVOKE EXECUTE ON FUNCTION cloud_effacement_tables_techniques() FROM PUBLIC;

-- ---- 2. Anomalies : ajout seul, sauf effacement d'une officine partie ----
DO $trg$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'public.cloud_anomalie_integrite'::regclass
                      AND tgname = 'trg_cloud_anomalie_integrite_ajout_seul'
                      AND tgfoid = to_regprocedure('cloud_integrite_restauration_garde()')) THEN
        DROP TRIGGER IF EXISTS trg_cloud_anomalie_integrite_ajout_seul ON cloud_anomalie_integrite;
        CREATE TRIGGER trg_cloud_anomalie_integrite_ajout_seul BEFORE UPDATE OR DELETE ON cloud_anomalie_integrite
            FOR EACH ROW EXECUTE FUNCTION cloud_integrite_restauration_garde();
    END IF;
END $trg$;

-- ---- 3. Journal des ordres gardé : plus de clé étrangère vers ordre_central (nom lu dans le catalogue) ----
DO $fk$ DECLARE c text;
BEGIN
    FOR c IN SELECT conname FROM pg_constraint
             WHERE contype = 'f' AND conrelid = 'public.ordre_central_journal'::regclass AND confrelid = 'public.ordre_central'::regclass LOOP
        EXECUTE format('ALTER TABLE public.ordre_central_journal DROP CONSTRAINT %I', c);
    END LOOP;
END $fk$;

-- ---- 4. Effacement d'une officine partie (remplace la version de 022) : éléments techniques compris ----
CREATE OR REPLACE FUNCTION cloud_effacer_officine(p_officine uuid, p_confirmer boolean DEFAULT false, p_operateur text DEFAULT NULL)
RETURNS TABLE (nom_table text, lignes bigint)
LANGUAGE plpgsql AS $$
DECLARE o officine; v_ans int; t text; n bigint; v_reste bigint; v_detail jsonb := '{}'; v_total bigint := 0; v_tech bigint := 0;
        v_techniques text[] := cloud_effacement_tables_techniques(); v_tables text[];
BEGIN
    SELECT * INTO o FROM officine WHERE id = p_officine FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Officine inconnue : %', p_officine USING ERRCODE = 'P0001'; END IF;
    IF o.resiliee_le IS NULL THEN
        RAISE EXCEPTION 'Effacement refusé : l''officine % n''est pas partie (--depart-officine d''abord)', p_officine USING ERRCODE = 'P0001';
    END IF;
    v_ans := cloud_conservation_depart_ans(p_officine);
    IF NOT cloud_effacement_officine_permis(p_officine) THEN
        RAISE EXCEPTION 'Effacement refusé : durée de conservation après départ non écoulée (% ans, partie le %, effacement possible à partir du %)',
            v_ans, to_char(o.resiliee_le, 'YYYY-MM-DD'), to_char(o.resiliee_le + make_interval(years => v_ans), 'YYYY-MM-DD') USING ERRCODE = 'P0001';
    END IF;
    IF p_confirmer AND coalesce(length(btrim(p_operateur)), 0) NOT BETWEEN 1 AND 100 THEN
        RAISE EXCEPTION 'Opérateur attendu pour l''effacement (1 à 100 caractères)' USING ERRCODE = 'P0001';
    END IF;
    PERFORM set_config('app.officine_id', p_officine::text, true);
    IF p_confirmer THEN
        PERFORM set_config('cloud.effacement_officine', p_officine::text, true);
        PERFORM set_config('cloud.lot_recu_operation', 'effacer_officine', true);
    END IF;
    -- Ordre : tables miroir ; lignes remplacées et lots bruts ; tables du script 022 ; éléments techniques (époques en dernier)
    SELECT coalesce(array_agg(m.nom_table ORDER BY m.nom_table), '{}') INTO v_tables
    FROM cloud_table_miroir m WHERE to_regclass('public.' || quote_ident(m.nom_table)) IS NOT NULL;
    v_tables := v_tables || ARRAY['ligne_remplacee', 'lot_recu', 'cloud_ligne_divergente', 'cloud_anomalie_classement',
                                  'cloud_integrite_bifurcation', 'cloud_epoque_changement'] || v_techniques;
    FOREACH t IN ARRAY v_tables LOOP
        IF p_confirmer THEN
            EXECUTE format('WITH d AS (DELETE FROM public.%I WHERE officine_id = $1 RETURNING 1) SELECT count(*) FROM d', t) INTO n USING p_officine;
            -- Garde : une ligne restée (déclencheur d'ajout seul refermé) annule tout l'effacement
            EXECUTE format('SELECT count(*) FROM public.%I WHERE officine_id = $1', t) INTO v_reste USING p_officine;
            IF v_reste > 0 THEN
                RAISE EXCEPTION 'Effacement incomplet sur % (% ligne(s) restée(s)) : rien n''est effacé (repasser les scripts 021, 022 et 027)', t, v_reste USING ERRCODE = 'P0001';
            END IF;
        ELSE
            EXECUTE format('SELECT count(*) FROM public.%I WHERE officine_id = $1', t) INTO n USING p_officine;
        END IF;
        IF n > 0 THEN
            v_detail := v_detail || jsonb_build_object(t, n);
            v_total := v_total + n;
            IF t = ANY (v_techniques) THEN v_tech := v_tech + n; END IF;
            nom_table := t; lignes := n;
            RETURN NEXT;
        END IF;
    END LOOP;
    PERFORM set_config('cloud.effacement_officine', '', true);
    PERFORM set_config('cloud.lot_recu_operation', '', true);
    PERFORM set_config('app.officine_id', '', true);
    IF p_confirmer THEN
        INSERT INTO journal_depart_officine (officine_id, action, operateur, detail)
        VALUES (p_officine, 'effacement', btrim(p_operateur), jsonb_build_object('lignes_par_table', v_detail, 'total', v_total, 'techniques', v_tech,
                'parti_le', o.resiliee_le, 'conservation_ans', v_ans, 'script', '027'));
        INSERT INTO journal_securite (officine_id, evenement, detail)
        VALUES (p_officine, 'officine_effacee', format('officine partie le %s : %s ligne(s) effacée(s) (dont %s élément(s) technique(s)) après %s ans (procès-verbal : journal_depart_officine)',
                                                      to_char(o.resiliee_le, 'YYYY-MM-DD'), v_total, v_tech, v_ans));
    END IF;
END $$;
