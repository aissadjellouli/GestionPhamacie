-- =====================================================================
-- Base centrale : intégrité après restauration, tables et réception
-- (lot S6.5.1, 2026-11-18 ; docs/conception-synchronisation.md § 31,
-- décisions S6.5-D1 à D9 validées par le titulaire le 2026-11-18).
-- Script de la base CENTRALE, à passer après 021 par le propriétaire.
--   * cloud_epoque_changement : déclaration d'un changement d'époque par
--     l'officine (« Repartir sur une nouvelle époque »), avant tout lot de
--     la nouvelle époque, avec les fins de chaîne locales ; date de
--     réception du central (horloge digne de foi). Ajout seul.
--   * cloud_ligne_divergente : ligne d'un journal chaîné (ou sceau,
--     clôture, vérification) reçue d'une époque DÉCLARÉE plus récente avec
--     un autre contenu : l'original reste en place (règle S4 inchangée,
--     S6.5-D2 a), la version reçue est gardée ici, sans anomalie à la
--     réception (la vérification par branches classe, script 023).
--   * cloud_integrite_bifurcation, cloud_anomalie_classement : constats de
--     restauration et rattachement des anomalies déjà notées (S6.5-D5),
--     remplis par le script 023. Ajout seul.
--   * Natures d'anomalie : remplacee_apres_restauration,
--     sceau_perdu_restauration, ticket_reattribue_restauration.
--   * Index (officine_id, cle_chaine, rang) des journaux miroir (non
--     unique : deux lignes du même rang après une restauration).
--   * cloud_integrite_ajout_seul redéfinie (dernière définition : 021).
--   * cloud_effacer_officine redéfinie (021) : efface aussi ces tables.
-- Gardes de rejeu : 021 ne recrée plus cloud_integrite_ajout_seul ni
-- cloud_effacer_officine si la table cloud_epoque_changement existe.
-- Rejouer 012 ou 021 après ce script ne ramène donc rien en arrière.
-- PostgreSQL 18 exigé (comme 020 et 021). Idempotent. Aucune donnée
-- existante modifiée.
-- =====================================================================

SET client_encoding = 'UTF8';

DO $version$ BEGIN
    IF current_setting('server_version_num')::int < 180000 THEN
        RAISE EXCEPTION 'Script central 022 : PostgreSQL 18 ou plus récent exigé (comme les scripts 020 et 021) ; ce serveur est en version %. Rien n''a été modifié.',
            current_setting('server_version');
    END IF;
    IF to_regclass('public.journal_depart_officine') IS NULL THEN
        RAISE EXCEPTION 'Script central 022 : passer d''abord le script 021 (table journal_depart_officine absente). Rien n''a été modifié.';
    END IF;
END $version$;

-- ---- 1. Garde commune d'ajout seul (seule exception : effacement d'une officine partie, script 021) ----
CREATE OR REPLACE FUNCTION cloud_integrite_restauration_garde() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' AND cloud_effacement_officine_en_cours(OLD.officine_id) THEN
        RETURN OLD;
    END IF;
    RAISE EXCEPTION '% : ajout seul', TG_TABLE_NAME;
END $$;

-- ---- 2. Déclaration d'un changement d'époque (S6.5-D1) ----
CREATE TABLE IF NOT EXISTS cloud_epoque_changement (
    id                    bigint      GENERATED ALWAYS AS IDENTITY,
    officine_id           uuid        NOT NULL,
    ancienne_epoque       int         NOT NULL CHECK (ancienne_epoque >= 1),
    nouvelle_epoque       int         NOT NULL,
    dernier_lot_local     bigint      NOT NULL CHECK (dernier_lot_local >= 0),
    dernier_lot_central   bigint      CHECK (dernier_lot_central >= 0),
    detecte_le            timestamptz,
    decide_le             timestamptz NOT NULL,
    titulaire_id          bigint,
    fins_chaine           jsonb       NOT NULL DEFAULT '[]',
    -- Condition 1 du § 31.2 : faux si un lot de la nouvelle époque était déjà arrivé à la réception de la déclaration
    avant_premier_lot     boolean     NOT NULL,
    recu_le               timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (officine_id, id),
    CONSTRAINT ck_cloud_epoque_changement_ordre CHECK (nouvelle_epoque > ancienne_epoque),
    CONSTRAINT ck_cloud_epoque_changement_fins CHECK (jsonb_typeof(fins_chaine) = 'array')
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_cloud_epoque_changement ON cloud_epoque_changement (officine_id, nouvelle_epoque);

-- ---- 3. Versions divergentes reçues après un changement d'époque déclaré (S6.5-D2 a) ----
CREATE TABLE IF NOT EXISTS cloud_ligne_divergente (
    id                bigint      GENERATED ALWAYS AS IDENTITY,
    officine_id       uuid        NOT NULL,
    nom_table         text        NOT NULL,
    cle               jsonb       NOT NULL,
    cle_chaine        text,
    rang              bigint,
    ligne             jsonb       NOT NULL,
    epoque            int         NOT NULL,
    numero            bigint      NOT NULL,
    epoque_existante  int         NOT NULL,
    changement_id     bigint      NOT NULL,
    recue_le          timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (officine_id, id),
    CONSTRAINT ck_cloud_ligne_divergente_epoque CHECK (epoque > epoque_existante)
);
-- Une même version renvoyée (lot rejoué, chargement repris) n'est gardée qu'une fois
CREATE UNIQUE INDEX IF NOT EXISTS ux_cloud_ligne_divergente ON cloud_ligne_divergente (officine_id, nom_table, cle, epoque, md5(ligne::text));
CREATE INDEX IF NOT EXISTS ix_cloud_ligne_divergente_chaine ON cloud_ligne_divergente (officine_id, nom_table, cle_chaine, rang);

-- ---- 4. Constats de restauration (remplis par le script 023) ----
CREATE TABLE IF NOT EXISTS cloud_integrite_bifurcation (
    id                    bigint      GENERATED ALWAYS AS IDENTITY,
    officine_id           uuid        NOT NULL,
    journal               text        NOT NULL,
    cle_chaine            text        NOT NULL,
    ancienne_epoque       int         NOT NULL,
    nouvelle_epoque       int         NOT NULL,
    changement_id         bigint,
    rang_commun           bigint      NOT NULL CHECK (rang_commun >= 0),
    hash_commun           bytea,
    fin_ancienne_rang     bigint,
    fin_ancienne_id       text,
    fin_ancienne_hash     bytea,
    lignes_ancienne       bigint      NOT NULL DEFAULT 0 CHECK (lignes_ancienne >= 0),
    premiere_date         timestamptz,
    derniere_date         timestamptz,
    montant_ttc           numeric(14,2),
    sceaux_perdus         int         NOT NULL DEFAULT 0 CHECK (sceaux_perdus >= 0),
    classe                text        NOT NULL,
    conditions            jsonb       NOT NULL DEFAULT '{}',
    constatee_le          timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (officine_id, id),
    CONSTRAINT ck_cloud_integrite_bifurcation_classe CHECK (classe IN ('restauration_constatee', 'restauration_a_justifier')),
    CONSTRAINT ck_cloud_integrite_bifurcation_epoques CHECK (nouvelle_epoque > ancienne_epoque)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_cloud_integrite_bifurcation ON cloud_integrite_bifurcation (officine_id, journal, cle_chaine, nouvelle_epoque);

-- Rattachement d'une anomalie déjà notée à une bifurcation (S6.5-D5 : jamais modifier ni cacher l'anomalie d'origine)
CREATE TABLE IF NOT EXISTS cloud_anomalie_classement (
    id               bigint      GENERATED ALWAYS AS IDENTITY,
    officine_id      uuid        NOT NULL,
    anomalie_id      bigint      NOT NULL,
    bifurcation_id   bigint      NOT NULL,
    classe           text        NOT NULL,
    classee_le       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (officine_id, id),
    CONSTRAINT ck_cloud_anomalie_classement_classe CHECK (classe IN ('remplacee_apres_restauration', 'restauration_a_justifier'))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_cloud_anomalie_classement ON cloud_anomalie_classement (officine_id, anomalie_id);

-- Cloisonnement et ajout seul des quatre tables
DO $tables$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['cloud_epoque_changement', 'cloud_ligne_divergente', 'cloud_integrite_bifurcation', 'cloud_anomalie_classement'] LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS cloisonnement ON %I', t);
        EXECUTE format('CREATE POLICY cloisonnement ON %I USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante())', t);
        EXECUTE format('DROP TRIGGER IF EXISTS trg_%s_ajout_seul ON %I', t, t);
        EXECUTE format('CREATE TRIGGER trg_%s_ajout_seul BEFORE UPDATE OR DELETE ON %I FOR EACH ROW EXECUTE FUNCTION cloud_integrite_restauration_garde()', t, t);
        EXECUTE format('DROP TRIGGER IF EXISTS trg_%s_non_vidable ON %I', t, t);
        EXECUTE format('CREATE TRIGGER trg_%s_non_vidable BEFORE TRUNCATE ON %I FOR EACH STATEMENT EXECUTE FUNCTION cloud_ajout_seul()', t, t);
        -- L'API n'y accède que par les fonctions SECURITY DEFINER ci-dessous (déclaration, déclencheur d'ajout seul)
        EXECUTE format('REVOKE ALL ON %I FROM pharma_cloud_api', t);
    END LOOP;
END $tables$;

-- ---- 5. Natures d'anomalie (§ 31.2 point 5) ----
DO $natures$ BEGIN
    ALTER TABLE cloud_anomalie_integrite DROP CONSTRAINT IF EXISTS ck_cloud_anomalie_integrite_nature;
    ALTER TABLE cloud_anomalie_integrite ADD CONSTRAINT ck_cloud_anomalie_integrite_nature CHECK (nature IN (
        'modification_refusee', 'suppression_refusee', 'ligne_manquante', 'algorithme_inconnu', 'chainage_rompu', 'ligne_modifiee',
        'sceau_sans_ligne', 'sceau_incoherent', 'ticket_manquant', 'ticket_hors_compteur', 'vente_sans_ligne', 'annulation_sans_ligne',
        'reecriture_apres_envoi', 'chaine_officine_plus_courte',
        'remplacee_apres_restauration', 'sceau_perdu_restauration', 'ticket_reattribue_restauration'));
END $natures$;

-- ---- 6. Index des journaux miroir par rang (non unique : deux branches après une restauration) ----
DO $index$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['journal_vente', 'journal_registre_psychotrope'] LOOP
        IF to_regclass('public.' || t) IS NOT NULL THEN
            EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON public.%I (officine_id, cle_chaine, rang)', 'ix_' || t || '_chaine_rang', t);
        END IF;
    END LOOP;
END $index$;

-- ---- 7. Réception de la déclaration (route POST /api/v1/epoque, officine du certificat) ----
-- Une déclaration par (officine, nouvelle époque). Renvoyée à l'identique (réessai de l'officine) : acceptée, rien n'est ajouté.
-- Renvoyée avec un autre contenu : refus (la première reçue fait foi).
CREATE OR REPLACE FUNCTION cloud_declarer_changement_epoque(p_ancienne int, p_nouvelle int, p_dernier_lot_local bigint, p_dernier_lot_central bigint,
                                                           p_detecte_le timestamptz, p_decide_le timestamptz, p_titulaire bigint, p_fins jsonb)
RETURNS TABLE (changement_id bigint, recu_le timestamptz, deja_declare boolean, avant_premier_lot boolean)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE
    v_off   uuid := cloud_officine_courante();
    v_fins  jsonb := coalesce(p_fins, '[]'::jsonb);
    x       cloud_epoque_changement;
    f       jsonb;
    v_avant boolean;
BEGIN
    IF v_off IS NULL THEN RAISE EXCEPTION 'Officine courante non posée (app.officine_id)'; END IF;
    IF p_ancienne IS NULL OR p_nouvelle IS NULL OR p_ancienne < 1 OR p_nouvelle <= p_ancienne THEN
        RAISE EXCEPTION 'Déclaration d''époque refusée : nouvelle époque (%) attendue plus grande que l''ancienne (%)', p_nouvelle, p_ancienne USING ERRCODE = '22023';
    END IF;
    IF p_decide_le IS NULL OR coalesce(p_dernier_lot_local, -1) < 0 OR coalesce(p_dernier_lot_central, 0) < 0 THEN
        RAISE EXCEPTION 'Déclaration d''époque refusée : date de décision et n° de lot attendus' USING ERRCODE = '22023';
    END IF;
    IF jsonb_typeof(v_fins) <> 'array' OR jsonb_array_length(v_fins) > 20 THEN
        RAISE EXCEPTION 'Déclaration d''époque refusée : fins de chaîne attendues (liste de 20 au plus)' USING ERRCODE = '22023';
    END IF;
    FOR f IN SELECT * FROM jsonb_array_elements(v_fins) LOOP
        IF jsonb_typeof(f) <> 'object' OR NOT (f ? 'journal') OR NOT (f ? 'cle_chaine') OR NOT (f ? 'rang')
           OR NOT EXISTS (SELECT 1 FROM cloud_chaine_reference r WHERE r.journal = f ->> 'journal')
           OR jsonb_typeof(f -> 'rang') <> 'number' OR (f ->> 'rang')::bigint < 0
           OR (f ? 'hash' AND f ->> 'hash' IS NOT NULL AND f ->> 'hash' !~ '^[0-9a-f]{64}([0-9a-f]{64})?$') THEN
            RAISE EXCEPTION 'Déclaration d''époque refusée : fin de chaîne invalide (%)', left(f::text, 200) USING ERRCODE = '22023';
        END IF;
    END LOOP;

    SELECT * INTO x FROM cloud_epoque_changement c WHERE c.officine_id = v_off AND c.nouvelle_epoque = p_nouvelle;
    IF FOUND THEN
        IF (x.ancienne_epoque, x.dernier_lot_local, x.dernier_lot_central, x.detecte_le, x.decide_le, x.titulaire_id, x.fins_chaine)
           IS NOT DISTINCT FROM (p_ancienne, p_dernier_lot_local, p_dernier_lot_central, p_detecte_le, p_decide_le, p_titulaire, v_fins) THEN
            RETURN QUERY SELECT x.id, x.recu_le, true, x.avant_premier_lot;
            RETURN;
        END IF;
        RAISE EXCEPTION 'Déclaration de l''époque % déjà reçue le % avec un autre contenu : la première fait foi', p_nouvelle,
            to_char(x.recu_le AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI "UTC"') USING ERRCODE = '23505';
    END IF;

    -- Condition 1 : aucun lot de la nouvelle époque ne doit être arrivé avant la déclaration
    v_avant := NOT EXISTS (SELECT 1 FROM officine_epoque e WHERE e.officine_id = v_off AND e.epoque = p_nouvelle AND e.dernier_lot > 0);
    INSERT INTO cloud_epoque_changement (officine_id, ancienne_epoque, nouvelle_epoque, dernier_lot_local, dernier_lot_central,
                                         detecte_le, decide_le, titulaire_id, fins_chaine, avant_premier_lot)
    VALUES (v_off, p_ancienne, p_nouvelle, p_dernier_lot_local, p_dernier_lot_central, p_detecte_le, p_decide_le, p_titulaire, v_fins, v_avant)
    RETURNING * INTO x;
    PERFORM cloud_journaliser(v_off, NULL, 'changement_epoque_declare',
        left(format('époque %s → %s déclarée (dernier lot local %s, vu au central %s)%s', p_ancienne, p_nouvelle, p_dernier_lot_local,
                    coalesce(p_dernier_lot_central::text, '?'),
                    CASE WHEN v_avant THEN '' ELSE ' : APRÈS un lot de la nouvelle époque (aucune indulgence)' END), 500));
    RETURN QUERY SELECT x.id, x.recu_le, false, v_avant;
END $$;
REVOKE EXECUTE ON FUNCTION cloud_declarer_changement_epoque(int, int, bigint, bigint, timestamptz, timestamptz, bigint, jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_declarer_changement_epoque(int, int, bigint, bigint, timestamptz, timestamptz, bigint, jsonb) TO pharma_cloud_api;

-- Déclaration qui couvre une époque reçue (la plus récente déclarée pour cette nouvelle époque), null sinon
CREATE OR REPLACE FUNCTION cloud_changement_epoque_pour(p_officine uuid, p_epoque int) RETURNS bigint
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
    SELECT c.id FROM cloud_epoque_changement c WHERE c.officine_id = p_officine AND c.nouvelle_epoque = p_epoque
$$;
REVOKE EXECUTE ON FUNCTION cloud_changement_epoque_pour(uuid, int) FROM PUBLIC;

-- ---- 8. Ajout seul des journaux chaînés (remplace la version de 021) ----
-- Inchangé, sauf : contenu différent reçu d'une époque PLUS RÉCENTE que la ligne en place, ET changement d'époque DÉCLARÉ pour
-- cette époque → l'original reste (règle S4), la version reçue est gardée dans cloud_ligne_divergente, sans anomalie à ce
-- moment (la vérification par branches, script 023, classe). Sans déclaration : modification_refusee comme avant.
CREATE OR REPLACE FUNCTION cloud_integrite_ajout_seul() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE
    v_sync    text[] := ARRAY['sync_epoque', 'sync_numero', 'sync_recu_le', 'sync_chargement', 'sync_absente_le'];
    v_avant   jsonb;
    v_apres   jsonb;
    v_diff    text[];
    v_filtres text[];
    v_chgt    bigint;
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
        IF (to_jsonb(NEW) ->> 'sync_epoque')::int > (to_jsonb(OLD) ->> 'sync_epoque')::int THEN
            v_chgt := cloud_changement_epoque_pour(OLD.officine_id, (to_jsonb(NEW) ->> 'sync_epoque')::int);
            IF v_chgt IS NOT NULL THEN
                -- Restauration déclarée (S6.5-D2 a) : original en place, version reçue gardée à part
                INSERT INTO cloud_ligne_divergente (officine_id, nom_table, cle, cle_chaine, rang, ligne, epoque, numero, epoque_existante, changement_id)
                VALUES (OLD.officine_id, TG_TABLE_NAME, jsonb_build_object('id', v_avant -> 'id'), v_apres ->> 'cle_chaine',
                        (v_apres ->> 'rang')::bigint, v_apres, (to_jsonb(NEW) ->> 'sync_epoque')::int, (to_jsonb(NEW) ->> 'sync_numero')::bigint,
                        (to_jsonb(OLD) ->> 'sync_epoque')::int, v_chgt)
                ON CONFLICT DO NOTHING;
                RETURN NULL;
            END IF;
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

-- ---- 9. Effacement d'une officine partie (remplace la version de 021) : nouvelles tables comprises ----
-- Garde de rejeu (2026-11-20) : définition remplacée par le script 027 ; rejouer ce script après 027 ne la
-- ramène pas en arrière (marqueur : fonction cloud_effacement_tables_techniques(), posée par 027).
DO $rejeu$ BEGIN
    IF to_regprocedure('cloud_effacement_tables_techniques()') IS NOT NULL THEN
        RAISE NOTICE 'cloud_effacer_officine : définition plus récente (script 027) gardée';
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
             UNION ALL SELECT 'ligne_remplacee' UNION ALL SELECT 'lot_recu'
             UNION ALL SELECT 'cloud_ligne_divergente' UNION ALL SELECT 'cloud_anomalie_classement'
             UNION ALL SELECT 'cloud_integrite_bifurcation' UNION ALL SELECT 'cloud_epoque_changement' LOOP
        IF p_confirmer THEN
            EXECUTE format('WITH d AS (DELETE FROM public.%I WHERE officine_id = $1 RETURNING 1) SELECT count(*) FROM d', t) INTO n USING p_officine;
            -- Garde : une ligne restée (déclencheur d'ajout seul refermé) annule tout l'effacement
            EXECUTE format('SELECT count(*) FROM public.%I WHERE officine_id = $1', t) INTO v_reste USING p_officine;
            IF v_reste > 0 THEN
                RAISE EXCEPTION 'Effacement incomplet sur % (% ligne(s) restée(s)) : rien n''est effacé (repasser les scripts 021 et 022)', t, v_reste USING ERRCODE = 'P0001';
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

COMMENT ON TABLE cloud_epoque_changement IS 'Changements d''époque déclarés par les officines (restauration, bascule), date de réception du central ; ajout seul (script 022, § 31).';
COMMENT ON TABLE cloud_ligne_divergente IS 'Version reçue d''une ligne de journal chaîné après un changement d''époque déclaré, l''original restant en place ; ajout seul (script 022).';
COMMENT ON TABLE cloud_integrite_bifurcation IS 'Restaurations constatées par la vérification par branches (script 023) ; ajout seul (script 022).';
COMMENT ON TABLE cloud_anomalie_classement IS 'Anomalies déjà notées rattachées à une restauration constatée, sans les modifier (S6.5-D5) ; ajout seul (script 022).';
