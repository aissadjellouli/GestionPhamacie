-- =====================================================================
-- Base centrale, lot S6.3 (2026-11-18, docs/conception-synchronisation.md
-- § 30.3 points 7 à 9, réalisation § 30.5) : départ d'une officine.
-- Script de la base CENTRALE, passé par le propriétaire, idempotent ; à
-- passer après 020.
--   1. Durée de conservation après départ : cloud_parametre.
--      conservation_depart_ans (6 ans par défaut, 6 minimum), surchargeable
--      par officine (officine.conservation_depart_ans), comme
--      conservation_lots_ans (script 004).
--   2. journal_depart_officine : départ, export remis, procès-verbal
--      d'effacement (date, officine, opérateur, lignes par table). Ajout
--      seul ; l'API n'y a aucun accès.
--   3. cloud_depart_officine : révocation de tous les certificats de
--      l'officine, officine.resiliee_le posée (gardée si déjà posée),
--      journaux. L'export (CSV dans un zip, empreintes SHA-256) est écrit
--      par la console d'administration (--depart-officine).
--   4. cloud_effacer_officine : SUPPRESSION DE DONNÉES. Refusée (même en
--      simulation) si l'officine n'est pas partie ou si la durée de
--      conservation après départ n'est pas écoulée ; simulation (nombre de
--      lignes par table) tant que p_confirmer est faux ; effacement réel
--      des tables miroir, de ligne_remplacee et des lots bruts lot_recu de
--      l'officine, procès-verbal. Jamais automatique.
--   5. Gardes d'ajout seul ouvertes pour ce seul effacement (lot_recu,
--      ligne_remplacee, journaux chaînés du script 012) : l'opération posée
--      dans la transaction (cloud.effacement_officine = code de l'officine)
--      ne suffit pas, l'officine doit être partie ET sa durée écoulée.
--      Redéfinit cloud_lot_recu_garde (004), cloud_ligne_remplacee_garde
--      (020) et cloud_integrite_ajout_seul (012). Depuis le 2026-11-18,
--      rejouer 004, 012 ou 020 après ce script ne referme plus ces gardes
--      (004 et 012 ne les redéfinissent pas si journal_depart_officine
--      existe ; 020 ne crée la sienne que si elle est absente).
-- Fonctions d'administration : aucun EXECUTE pour pharma_cloud_api.
-- PostgreSQL 18 exigé (contrôle en tête, comme le script 020).
-- =====================================================================

SET client_encoding = 'UTF8';

DO $version$ BEGIN
    IF current_setting('server_version_num')::int < 180000 THEN
        RAISE EXCEPTION 'Script central 021 : PostgreSQL 18 ou plus récent exigé (comme le script 020) ; ce serveur est en version %. Rien n''a été modifié.',
            current_setting('server_version');
    END IF;
END $version$;

-- ---- 1. Durée de conservation après départ ----
ALTER TABLE cloud_parametre ADD COLUMN IF NOT EXISTS conservation_depart_ans int NOT NULL DEFAULT 6;
ALTER TABLE officine ADD COLUMN IF NOT EXISTS conservation_depart_ans int;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'cloud_parametre_conservation_depart_ans_check') THEN
        ALTER TABLE cloud_parametre ADD CONSTRAINT cloud_parametre_conservation_depart_ans_check CHECK (conservation_depart_ans >= 6);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'officine_conservation_depart_ans_check') THEN
        ALTER TABLE officine ADD CONSTRAINT officine_conservation_depart_ans_check CHECK (conservation_depart_ans IS NULL OR conservation_depart_ans >= 6);
    END IF;
END $$;

-- Durée de conservation après départ d'une officine (surcharge, sinon globale ; jamais moins de 6 ans)
CREATE OR REPLACE FUNCTION cloud_conservation_depart_ans(p_officine uuid) RETURNS int
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
    SELECT greatest(6, coalesce((SELECT conservation_depart_ans FROM officine WHERE id = p_officine),
                                (SELECT conservation_depart_ans FROM cloud_parametre WHERE id = 1), 6))
$$;

-- Réglage (ans) : 6 minimum, pas de maximum ; p_ans NULL pour une officine = valeur globale
CREATE OR REPLACE FUNCTION cloud_regler_conservation_depart(p_ans int, p_officine uuid DEFAULT NULL) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    IF p_ans IS NOT NULL AND p_ans < 6 THEN
        RAISE EXCEPTION 'Durée de conservation après départ : 6 ans minimum (demandé : % an(s))', p_ans USING ERRCODE = 'P0001';
    END IF;
    IF p_officine IS NULL THEN
        IF p_ans IS NULL THEN RAISE EXCEPTION 'Durée globale attendue (6 ans minimum)'; END IF;
        UPDATE cloud_parametre SET conservation_depart_ans = p_ans WHERE id = 1;
    ELSE
        UPDATE officine SET conservation_depart_ans = p_ans WHERE id = p_officine;
        IF NOT FOUND THEN RAISE EXCEPTION 'Officine inconnue : %', p_officine; END IF;
    END IF;
    INSERT INTO journal_securite (officine_id, evenement, detail)
    VALUES (p_officine, 'reglage_conservation_depart', format('conservation après départ : %s', coalesce(p_ans || ' ans', 'valeur globale')));
END $$;

-- ---- 2. Journal des départs et procès-verbaux d'effacement (ajout seul) ----
CREATE TABLE IF NOT EXISTS journal_depart_officine (
    id           bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    le           timestamptz NOT NULL DEFAULT now(),
    officine_id  uuid        NOT NULL,
    action       text        NOT NULL,
    operateur    text        NOT NULL,
    detail       jsonb       NOT NULL DEFAULT '{}'
);
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'journal_depart_officine_action_check') THEN
        ALTER TABLE journal_depart_officine ADD CONSTRAINT journal_depart_officine_action_check CHECK (action IN ('depart', 'export', 'effacement'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'journal_depart_officine_operateur_check') THEN
        ALTER TABLE journal_depart_officine ADD CONSTRAINT journal_depart_officine_operateur_check CHECK (length(btrim(operateur)) BETWEEN 1 AND 100);
    END IF;
END $$;
CREATE INDEX IF NOT EXISTS ix_journal_depart_officine ON journal_depart_officine (officine_id, le);
DROP TRIGGER IF EXISTS trg_journal_depart_officine_ajout_seul ON journal_depart_officine;
CREATE TRIGGER trg_journal_depart_officine_ajout_seul BEFORE UPDATE OR DELETE ON journal_depart_officine FOR EACH ROW EXECUTE FUNCTION cloud_ajout_seul();
DROP TRIGGER IF EXISTS trg_journal_depart_officine_non_vidable ON journal_depart_officine;
CREATE TRIGGER trg_journal_depart_officine_non_vidable BEFORE TRUNCATE ON journal_depart_officine FOR EACH STATEMENT EXECUTE FUNCTION cloud_ajout_seul();
REVOKE ALL ON journal_depart_officine FROM pharma_cloud_api;

-- ---- 3. Départ ----
-- Révoque tous les certificats encore valables, pose resiliee_le (gardée si déjà posée : départ rejoué pour refaire l'export)
CREATE OR REPLACE FUNCTION cloud_depart_officine(p_officine uuid, p_operateur text)
RETURNS TABLE (certificats_revoques int, resiliee_le timestamptz, deja_partie boolean)
LANGUAGE plpgsql AS $$
DECLARE o officine; n int;
BEGIN
    IF coalesce(length(btrim(p_operateur)), 0) NOT BETWEEN 1 AND 100 THEN RAISE EXCEPTION 'Opérateur attendu (1 à 100 caractères)' USING ERRCODE = 'P0001'; END IF;
    SELECT * INTO o FROM officine WHERE id = p_officine FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Officine inconnue : %', p_officine USING ERRCODE = 'P0001'; END IF;
    UPDATE certificat SET revoque_le = now(), motif_revocation = 'départ de l''officine'
    WHERE officine_id = p_officine AND revoque_le IS NULL;
    GET DIAGNOSTICS n = ROW_COUNT;
    UPDATE officine x SET resiliee_le = coalesce(x.resiliee_le, now()) WHERE x.id = p_officine;
    INSERT INTO journal_securite (officine_id, evenement, detail)
    VALUES (p_officine, 'depart_officine', format('départ de l''officine : %s certificat(s) révoqué(s)%s', n,
                                                  CASE WHEN o.resiliee_le IS NOT NULL THEN ' (départ déjà enregistré, date gardée)' ELSE '' END));
    INSERT INTO journal_depart_officine (officine_id, action, operateur, detail)
    VALUES (p_officine, 'depart', btrim(p_operateur), jsonb_build_object('certificats_revoques', n, 'deja_partie', o.resiliee_le IS NOT NULL));
    RETURN QUERY SELECT n, (SELECT x.resiliee_le FROM officine x WHERE x.id = p_officine), o.resiliee_le IS NOT NULL;
END $$;

-- Export remis au titulaire (écrit par la console) : fichier, empreinte de l'archive, lignes par table
CREATE OR REPLACE FUNCTION cloud_noter_export_depart(p_officine uuid, p_operateur text, p_detail jsonb) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM officine WHERE id = p_officine AND resiliee_le IS NOT NULL) THEN
        RAISE EXCEPTION 'Export de départ : officine inconnue ou non partie : %', p_officine USING ERRCODE = 'P0001';
    END IF;
    INSERT INTO journal_depart_officine (officine_id, action, operateur, detail) VALUES (p_officine, 'export', btrim(p_operateur), coalesce(p_detail, '{}'));
    INSERT INTO journal_securite (officine_id, evenement, detail)
    VALUES (p_officine, 'export_depart', left(format('export complet remis : archive SHA-256 %s', coalesce(p_detail ->> 'empreinte_archive', '?')), 500));
END $$;

-- ---- 4. Effacement après la durée de conservation ----
-- Vrai seulement si l'officine est partie et que sa durée de conservation après départ est écoulée
CREATE OR REPLACE FUNCTION cloud_effacement_officine_permis(p_officine uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
    SELECT coalesce((SELECT o.resiliee_le IS NOT NULL AND o.resiliee_le + make_interval(years => cloud_conservation_depart_ans(o.id)) <= now()
                     FROM officine o WHERE o.id = p_officine), false)
$$;

-- Effacement de cette officine en cours dans la transaction (opération posée par cloud_effacer_officine) ET permis
CREATE OR REPLACE FUNCTION cloud_effacement_officine_en_cours(p_officine uuid) RETURNS boolean
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
BEGIN
    IF p_officine IS NULL OR coalesce(current_setting('cloud.effacement_officine', true), '') <> p_officine::text THEN RETURN false; END IF;
    RETURN cloud_effacement_officine_permis(p_officine);
END $$;

-- Lignes de l'officine dans ses tables miroir, ligne_remplacee et lot_recu : simulation (compte) ou effacement (p_confirmer).
-- Une ligne par table non vide. Refus : officine inconnue, non partie, durée non écoulée, opérateur absent à la confirmation.
-- Garde de rejeu (2026-11-18, lot S6.5) : définition remplacée par le script 022 ; rejouer ce script après 022 ne la
-- ramène pas en arrière (marqueur : table cloud_epoque_changement, posée par 022).
DO $rejeu$ BEGIN
    IF to_regclass('public.cloud_epoque_changement') IS NOT NULL THEN
        RAISE NOTICE 'cloud_effacer_officine : définition plus récente (script 022) gardée';
    ELSE
        EXECUTE $def$
CREATE OR REPLACE FUNCTION cloud_effacer_officine(p_officine uuid, p_confirmer boolean DEFAULT false, p_operateur text DEFAULT NULL)
RETURNS TABLE (nom_table text, lignes bigint)
LANGUAGE plpgsql AS $$
DECLARE o officine; v_ans int; t text; n bigint; v_reste bigint; v_detail jsonb := '{}'; v_total bigint := 0;
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
    FOR t IN SELECT m.nom_table FROM cloud_table_miroir m WHERE to_regclass('public.' || quote_ident(m.nom_table)) IS NOT NULL
             UNION ALL SELECT 'ligne_remplacee' UNION ALL SELECT 'lot_recu' LOOP
        IF p_confirmer THEN
            EXECUTE format('WITH d AS (DELETE FROM public.%I WHERE officine_id = $1 RETURNING 1) SELECT count(*) FROM d', t) INTO n USING p_officine;
            -- Garde : une ligne restée (déclencheur d'ajout seul refermé) annule tout l'effacement
            EXECUTE format('SELECT count(*) FROM public.%I WHERE officine_id = $1', t) INTO v_reste USING p_officine;
            IF v_reste > 0 THEN
                RAISE EXCEPTION 'Effacement incomplet sur % (% ligne(s) restée(s)) : rien n''est effacé (repasser le script 021)', t, v_reste USING ERRCODE = 'P0001';
            END IF;
        ELSE
            EXECUTE format('SELECT count(*) FROM public.%I WHERE officine_id = $1', t) INTO n USING p_officine;
        END IF;
        IF n > 0 THEN
            v_detail := v_detail || jsonb_build_object(t, n);
            v_total := v_total + n;
            nom_table := t; lignes := n;
            RETURN NEXT;
        END IF;
    END LOOP;
    PERFORM set_config('cloud.effacement_officine', '', true);
    PERFORM set_config('cloud.lot_recu_operation', '', true);
    PERFORM set_config('app.officine_id', '', true);
    IF p_confirmer THEN
        INSERT INTO journal_depart_officine (officine_id, action, operateur, detail)
        VALUES (p_officine, 'effacement', btrim(p_operateur), jsonb_build_object('lignes_par_table', v_detail, 'total', v_total,
                'parti_le', o.resiliee_le, 'conservation_ans', v_ans));
        INSERT INTO journal_securite (officine_id, evenement, detail)
        VALUES (p_officine, 'officine_effacee', format('officine partie le %s : %s ligne(s) effacée(s) après %s ans (procès-verbal : journal_depart_officine)',
                                                      to_char(o.resiliee_le, 'YYYY-MM-DD'), v_total, v_ans));
    END IF;
END $$;
$def$;
    END IF;
END $rejeu$;

-- ---- 5. Gardes d'ajout seul : passage pour le seul effacement permis ----
-- lot_recu (remplace la version de 004) : purge, expurgation (004) et effacement d'une officine partie
CREATE OR REPLACE FUNCTION cloud_lot_recu_garde() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE v_op text := coalesce(current_setting('cloud.lot_recu_operation', true), '');
BEGIN
    IF TG_OP = 'DELETE' AND v_op = 'purge'
       AND (SELECT purge_lots_active FROM cloud_parametre WHERE id = 1)
       AND OLD.recu_le < now() - make_interval(years => cloud_conservation_ans(OLD.officine_id)) THEN
        RETURN OLD;
    END IF;
    IF TG_OP = 'DELETE' AND v_op = 'effacer_officine' AND cloud_effacement_officine_en_cours(OLD.officine_id) THEN
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND v_op = 'expurger' AND NEW.expurge_le IS NOT NULL
       AND coalesce(OLD.colonnes_effacees, '{}') <@ coalesce(NEW.colonnes_effacees, '{}')
       AND (to_jsonb(NEW) - ARRAY['contenu', 'expurge_le', 'colonnes_effacees'])
         = (to_jsonb(OLD) - ARRAY['contenu', 'expurge_le', 'colonnes_effacees'])
       AND convert_from(NEW.contenu, 'UTF8')::jsonb
         = cloud_lot_sans_colonnes(convert_from(OLD.contenu, 'UTF8')::jsonb, NEW.colonnes_effacees) THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION '% : ajout seul', TG_TABLE_NAME;
END $$;

-- ligne_remplacee (remplace la version de 020)
-- Garde de rejeu (2026-11-20) : définition remplacée par le script 028 ; rejouer ce script après 028 ne la
-- ramène pas en arrière (marqueur : colonne ligne_remplacee.colonnes_effacees, posée par 028).
DO $rejeu$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass('public.ligne_remplacee') AND attname = 'colonnes_effacees' AND NOT attisdropped) THEN
        RAISE NOTICE 'cloud_ligne_remplacee_garde : définition plus récente (script 028) gardée';
    ELSE
        EXECUTE $def$
CREATE OR REPLACE FUNCTION cloud_ligne_remplacee_garde() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' AND cloud_effacement_officine_en_cours(OLD.officine_id) THEN
        RETURN OLD;
    END IF;
    RAISE EXCEPTION '% : ajout seul', TG_TABLE_NAME;
END $$;
$def$;
    END IF;
END $rejeu$;

-- Journaux chaînés (remplace la version de 012, identique sauf la première condition)
-- Garde de rejeu (2026-11-18, lot S6.5) : définition remplacée par le script 022 ; rejouer ce script après 022 ne la
-- ramène pas en arrière (marqueur : table cloud_epoque_changement, posée par 022).
DO $rejeu$ BEGIN
    IF to_regclass('public.cloud_epoque_changement') IS NOT NULL THEN
        RAISE NOTICE 'cloud_integrite_ajout_seul : définition plus récente (script 022) gardée';
    ELSE
        EXECUTE $def$
CREATE OR REPLACE FUNCTION cloud_integrite_ajout_seul() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE
    v_sync    text[] := ARRAY['sync_epoque', 'sync_numero', 'sync_recu_le', 'sync_chargement', 'sync_absente_le'];
    v_avant   jsonb;
    v_apres   jsonb;
    v_diff    text[];
    v_filtres text[];
BEGIN
    IF TG_OP = 'DELETE' AND cloud_effacement_officine_en_cours(OLD.officine_id) THEN
        RETURN OLD;   -- effacement d'une officine partie, durée de conservation écoulée (script 021)
    END IF;
    IF TG_OP = 'UPDATE' THEN
        v_avant := to_jsonb(OLD) - v_sync;
        v_apres := to_jsonb(NEW) - v_sync;
        IF v_avant = v_apres THEN RETURN NEW; END IF;   -- renvoi identique (chargement complet), marques du chargement
        SELECT array_agg(k ORDER BY k) INTO v_diff FROM jsonb_object_keys(v_avant || v_apres) k
        WHERE v_avant -> k IS DISTINCT FROM v_apres -> k;
        SELECT colonnes_filtrees INTO v_filtres FROM cloud_chaine_reference WHERE journal = TG_TABLE_NAME;
        IF v_filtres IS NOT NULL AND v_diff <@ v_filtres THEN
            RETURN NULL;   -- seul un texte filtré diffère (listes de l'officine changées) : l'original est gardé, sans anomalie
        END IF;
        PERFORM cloud_integrite_noter(TG_TABLE_NAME, v_avant ->> 'cle_chaine', (v_avant ->> 'rang')::bigint,
            (v_avant ->> 'id') || '@' || NEW.sync_epoque || '/' || NEW.sync_numero, 'modification_refusee',
            format('ligne %s reçue avec un autre contenu (colonnes : %s) : version d''origine gardée', v_avant ->> 'id', array_to_string(v_diff, ', ')));
        RETURN NULL;
    END IF;
    -- DELETE
    v_avant := to_jsonb(OLD);
    PERFORM cloud_integrite_noter(TG_TABLE_NAME, v_avant ->> 'cle_chaine', (v_avant ->> 'rang')::bigint, v_avant ->> 'id', 'suppression_refusee',
        format('suppression de la ligne %s reçue : ligne gardée', v_avant ->> 'id'));
    RETURN NULL;
END $$;
$def$;
    END IF;
END $rejeu$;

-- ---- Droits : aucune fonction d'administration pour l'API ----
REVOKE EXECUTE ON FUNCTION cloud_conservation_depart_ans(uuid), cloud_regler_conservation_depart(int, uuid),
    cloud_depart_officine(uuid, text), cloud_noter_export_depart(uuid, text, jsonb), cloud_effacement_officine_permis(uuid),
    cloud_effacement_officine_en_cours(uuid), cloud_effacer_officine(uuid, boolean, text) FROM PUBLIC;
REVOKE EXECUTE ON FUNCTION cloud_conservation_depart_ans(uuid), cloud_regler_conservation_depart(int, uuid),
    cloud_depart_officine(uuid, text), cloud_noter_export_depart(uuid, text, jsonb), cloud_effacement_officine_permis(uuid),
    cloud_effacement_officine_en_cours(uuid), cloud_effacer_officine(uuid, boolean, text) FROM pharma_cloud_api;

COMMENT ON COLUMN cloud_parametre.conservation_depart_ans IS 'Durée de conservation des données d''une officine partie (ans), 6 minimum, avant tout effacement (script 021).';
COMMENT ON COLUMN officine.conservation_depart_ans IS 'Surcharge de la durée de conservation après départ (ans, 6 minimum) ; NULL = valeur globale (script 021).';
COMMENT ON TABLE journal_depart_officine IS 'Serveur central : départs d''officine, exports remis, procès-verbaux d''effacement (opérateur, lignes par table), ajout seul (script 021).';
