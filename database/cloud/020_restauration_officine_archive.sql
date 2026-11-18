-- =====================================================================
-- Base centrale, lot S6.1 (2026-11-18, docs/conception-synchronisation.md
-- § 30.3 points 4 et 5, réalisation § 30.5). Script de la base CENTRALE,
-- passé par le propriétaire, idempotent ; à passer après 019 et AVANT le
-- script 149 des officines (la route GET /api/v1/maximums lit
-- cloud_maximums_officine).
--   1. ligne_remplacee : version d'une ligne miroir REMPLACÉE ou SUPPRIMÉE
--      par une ligne d'une époque plus récente (officine revenue en arrière
--      puis repartie sur une nouvelle époque : collision des n°). Ajout
--      seul, cloisonnée (RLS forcée) ; l'API n'y fait que des INSERT (par
--      cloud_appliquer_lot) et n'en lit rien directement.
--   2. cloud_appliquer_lot (dernière définition : script 009) : seul
--      changement, l'ancienne version est copiée dans ligne_remplacee quand
--      une ligne d'une époque plus récente la remplace avec un AUTRE contenu
--      (colonnes sync_* exclues) ou la supprime. Même époque : inchangé.
--      Une ligne renvoyée à l'identique (chargement complet) n'est pas
--      archivée : rien n'est perdu. Colonnes des familles nominatives non
--      autorisées pour l'officine : retirées de la copie (comme à la
--      réception). Utilise RETURNING old/new (PostgreSQL 18).
--   3. cloud_maximums_officine : plus grand id par table miroir à clé
--      « id » entière, pour l'officine courante (toutes époques, lignes
--      absentes et archivées comprises). Aucune donnée de ligne.
--   4. cloud_fin_chargement (dernière définition : script 003) : en plus,
--      nombre de lignes archivées depuis le DÉBUT DU CHARGEMENT
--      (remplacee_le >= chargement.debut_le, époque du chargement)
--      (chargement.lignes_remplacees, journal de sécurité) ; l'alerte
--      « chargement_absentes » est levée aussi s'il y en a. Retour inchangé.
--   5. cloud_epoque_max : plus grande époque connue pour l'officine courante
--      (route GET /api/v1/etat, champ facultatif EpoqueMax) : la nouvelle
--      époque d'une officine revenue en arrière la dépasse toujours.
-- Corrections après revue (2026-11-18, conception § 30.8) : PostgreSQL 18
-- exigé (contrôle en tête) ; cloud_appliquer_lot garde le chemin de 009
-- (sans to_jsonb ni archive) quand l'officine n'a aucune époque antérieure
-- à celle du lot (cas courant) ; fin de chargement comptée depuis son début ;
-- cloud_epoque_max. Rejouable : un nouveau passage sur une base à jour
-- recrée ces fonctions ; les scripts 001 à 012 ne les écrasent plus.
-- =====================================================================

SET client_encoding = 'UTF8';

-- PostgreSQL 18 exigé : RETURNING old/new (archive avant remplacement)
DO $version$ BEGIN
    IF current_setting('server_version_num')::int < 180000 THEN
        RAISE EXCEPTION 'Script central 020 : PostgreSQL 18 ou plus récent exigé (RETURNING old/new) ; ce serveur est en version %. Rien n''a été modifié.',
            current_setting('server_version');
    END IF;
END $version$;

-- ---- 1. Lignes remplacées ----
CREATE TABLE IF NOT EXISTS ligne_remplacee (
    id               bigint      GENERATED ALWAYS AS IDENTITY,
    officine_id      uuid        NOT NULL,
    nom_table        text        NOT NULL,
    cle              jsonb       NOT NULL,
    ancienne         jsonb       NOT NULL,
    ancienne_epoque  int         NOT NULL,
    ancien_numero    bigint      NOT NULL,
    nouvelle_epoque  int         NOT NULL,
    nouveau_numero   bigint      NOT NULL,
    operation        text        NOT NULL,
    remplacee_le     timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (officine_id, id)
);
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ligne_remplacee_operation_check') THEN
        ALTER TABLE ligne_remplacee ADD CONSTRAINT ligne_remplacee_operation_check CHECK (operation IN ('remplacement', 'suppression'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ligne_remplacee_epoque_check') THEN
        ALTER TABLE ligne_remplacee ADD CONSTRAINT ligne_remplacee_epoque_check CHECK (ancienne_epoque < nouvelle_epoque);
    END IF;
END $$;
CREATE INDEX IF NOT EXISTS ix_ligne_remplacee_table ON ligne_remplacee (officine_id, nom_table);
CREATE INDEX IF NOT EXISTS ix_ligne_remplacee_epoque ON ligne_remplacee (officine_id, nouvelle_epoque);
ALTER TABLE ligne_remplacee ENABLE ROW LEVEL SECURITY;
ALTER TABLE ligne_remplacee FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON ligne_remplacee;
CREATE POLICY cloisonnement ON ligne_remplacee USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());

-- Ajout seul. Seule exception : l'effacement d'une officine partie, après la durée de conservation (script 021, qui
-- remplace cette fonction). Garde : créée ici seulement si elle n'existe pas (rejouer 020 après 021 ne la rétrécit pas).
DO $garde$ BEGIN
    IF to_regprocedure('cloud_ligne_remplacee_garde()') IS NULL THEN
        EXECUTE $f$
            CREATE FUNCTION cloud_ligne_remplacee_garde() RETURNS trigger
            LANGUAGE plpgsql AS $c$
            BEGIN
                RAISE EXCEPTION '% : ajout seul', TG_TABLE_NAME;
            END $c$$f$;
    END IF;
END $garde$;
DROP TRIGGER IF EXISTS trg_ligne_remplacee_ajout_seul ON ligne_remplacee;
CREATE TRIGGER trg_ligne_remplacee_ajout_seul BEFORE UPDATE OR DELETE ON ligne_remplacee FOR EACH ROW EXECUTE FUNCTION cloud_ligne_remplacee_garde();
DROP TRIGGER IF EXISTS trg_ligne_remplacee_non_vidable ON ligne_remplacee;
CREATE TRIGGER trg_ligne_remplacee_non_vidable BEFORE TRUNCATE ON ligne_remplacee FOR EACH STATEMENT EXECUTE FUNCTION cloud_ajout_seul();

-- Droits : l'API ne fait qu'ajouter (par cloud_appliquer_lot) ; lecture par les fonctions ci-dessous seulement
REVOKE ALL ON ligne_remplacee FROM pharma_cloud_api;
GRANT INSERT ON ligne_remplacee TO pharma_cloud_api;

-- ---- 2. Dépôt d'un lot (remplace la version de 009) : archive avant remplacement par une époque plus récente ----
CREATE OR REPLACE FUNCTION cloud_appliquer_lot(p_epoque int, p_numero bigint, p_empreinte text, p_version text, p_contenu bytea)
RETURNS TABLE (statut text, dernier bigint)
LANGUAGE plpgsql AS $$
DECLARE
    v_off      uuid := cloud_officine_courante();
    e          officine_epoque;
    v_lot      jsonb;
    r          record;
    m          cloud_table_miroir;
    v_ch       chargement;
    v_cle      text;
    v_objet    text;
    v_match    text;
    v_garde    text;
    v_set      text;
    v_nb       int;
    v_quota    bigint;
    v_jour     bigint;
    v_effacees text[];
    v_contenu  bytea := p_contenu;
    v_quota_ch bigint;
    v_interdites text[] := cloud_colonnes_interdites();
    v_retirer  text[];
    v_cle_new  text;
    v_cle_old  text;
    v_archive  boolean;
BEGIN
    IF v_off IS NULL THEN RAISE EXCEPTION 'Officine courante non posée (app.officine_id)'; END IF;
    INSERT INTO officine_epoque (officine_id, epoque) VALUES (v_off, p_epoque) ON CONFLICT DO NOTHING;
    SELECT * INTO e FROM officine_epoque WHERE officine_id = v_off AND epoque = p_epoque FOR UPDATE;
    IF p_numero <= e.dernier_lot THEN
        IF EXISTS (SELECT 1 FROM lot_recu l WHERE l.officine_id = v_off AND l.epoque = p_epoque AND l.numero = p_numero AND l.empreinte = p_empreinte) THEN
            RETURN QUERY SELECT 'deja_recu'::text, e.dernier_lot; RETURN;
        END IF;
        RETURN QUERY SELECT 'empreinte_differente'::text, e.dernier_lot; RETURN;
    END IF;
    IF p_numero > e.dernier_lot + 1 THEN
        RETURN QUERY SELECT 'trou'::text, e.dernier_lot; RETURN;
    END IF;
    SELECT * INTO v_ch FROM chargement WHERE officine_id = v_off AND etat = 'en_cours' AND epoque = p_epoque;
    IF v_ch.numero IS NOT NULL THEN
        -- Chargement complet en cours : plafond propre au chargement (pas le quota du jour, pour ne pas l'arrêter à mi-chemin)
        SELECT quota_chargement_octets INTO v_quota_ch FROM cloud_parametre WHERE id = 1;
        IF v_ch.octets_recus + length(p_contenu) > coalesce(v_quota_ch, 8589934592)
           OR (SELECT coalesce(sum(c.octets_recus), 0) FROM chargement c WHERE c.officine_id = v_off AND c.debut_le > now() - interval '24 hours')
              + length(p_contenu) > (SELECT quota_chargements_octets_24h FROM cloud_parametre WHERE id = 1) THEN
            RETURN QUERY SELECT 'quota_depasse'::text, e.dernier_lot; RETURN;
        END IF;
    ELSE
        SELECT quota_octets_jour INTO v_quota FROM cloud_parametre WHERE id = 1;
        SELECT coalesce((SELECT octets FROM officine_volume_jour WHERE officine_id = v_off AND jour = (now() AT TIME ZONE 'UTC')::date), 0) INTO v_jour;
        IF v_jour + length(p_contenu) > coalesce(v_quota, 536870912) THEN
            RETURN QUERY SELECT 'quota_depasse'::text, e.dernier_lot; RETURN;
        END IF;
    END IF;

    v_lot := convert_from(p_contenu, 'UTF8')::jsonb;
    -- Double sécurité : familles non autorisées effacées avant toute écriture
    v_effacees := cloud_colonnes_presentes(v_lot, v_interdites);
    IF cardinality(v_effacees) > 0 THEN
        v_lot := cloud_lot_sans_colonnes(v_lot, v_effacees);
        v_contenu := convert_to(v_lot::text, 'UTF8');
        PERFORM cloud_journaliser(v_off, NULL, 'familles_effacees_reception',
            format('lot %s/%s : colonnes non autorisées effacées à la réception (%s)', p_epoque, p_numero, array_to_string(v_effacees, ', ')));
    END IF;
    v_nb := jsonb_array_length(v_lot -> 'lignes');
    -- Archive possible seulement si l'officine a une époque antérieure à celle du lot (repartie après un retour en arrière) ;
    -- sinon (cas courant) chemin de 009, sans to_jsonb ni RETURNING old/new
    v_archive := EXISTS (SELECT 1 FROM officine_epoque oe WHERE oe.officine_id = v_off AND oe.epoque < p_epoque);
    FOR r IN SELECT x ->> 'table' AS nom, jsonb_agg(x) AS lignes
             FROM jsonb_array_elements(v_lot -> 'lignes') x GROUP BY 1 ORDER BY 1 LOOP
        SELECT * INTO m FROM cloud_table_miroir WHERE nom_table = r.nom;
        IF NOT FOUND THEN RAISE EXCEPTION 'Table non reçue par le serveur central : %', r.nom USING ERRCODE = 'P0001'; END IF;
        SELECT string_agg(format('%I', c), ', ' ORDER BY n) INTO v_cle FROM unnest(m.cle) WITH ORDINALITY k(c, n);
        SELECT string_agg(format('%L, split_part(f.x ->> ''id'', ''|'', %s)', c, n), ', ' ORDER BY n),
               string_agg(format('d.%I = k.%I', c, c), ' AND ' ORDER BY n),
               string_agg(format('coalesce(to_jsonb(d.%I) #>> ''{}'', '''')', c), ' || ''|'' || ' ORDER BY n),
               string_agg(format('%L, new.%I', c, c), ', ' ORDER BY n),
               string_agg(format('%L, d.%I', c, c), ', ' ORDER BY n)
            INTO v_objet, v_match, v_garde, v_cle_new, v_cle_old FROM unnest(m.cle) WITH ORDINALITY k(c, n);
        IF cardinality(m.cle) > 1 THEN v_match := v_match || ' AND (' || v_garde || ') = f.x ->> ''id'''; END IF;
        SELECT string_agg(format('%I = EXCLUDED.%I', c, c), ', ')
            INTO v_set FROM unnest(m.colonnes || ARRAY['sync_epoque', 'sync_numero', 'sync_recu_le', 'sync_absente_le']) c WHERE NOT c = ANY (m.cle);
        IF v_archive THEN
            -- Colonnes retirées de la copie archivée : colonnes sync (comparaison) et familles non autorisées pour l'officine
            SELECT coalesce(array_agg(split_part(i, '.', 2)), '{}') INTO v_retirer FROM unnest(v_interdites) i WHERE split_part(i, '.', 1) = r.nom;
            -- État final de chaque donnée dans ce lot (plus grand n° de ligne) ; « instantane » vaut création.
            -- Remplacement par une époque plus récente avec un autre contenu : l'ancienne version est archivée (S6.1).
            EXECUTE format($f$
                WITH maj AS (
                    INSERT INTO %1$I AS d
                    SELECT p.* FROM (SELECT DISTINCT ON (x ->> 'id') x FROM jsonb_array_elements($1) x
                                     ORDER BY x ->> 'id', (x ->> 'n')::bigint DESC) f,
                         LATERAL jsonb_populate_record(NULL::%1$I, (f.x -> 'donnees') || jsonb_build_object(
                             'officine_id', $2, 'sync_epoque', $3, 'sync_numero', (f.x ->> 'n')::bigint, 'sync_recu_le', now())) p
                    WHERE f.x ->> 'op' <> 'suppression'
                    ON CONFLICT (officine_id, %2$s) DO UPDATE SET %3$s
                    WHERE (d.sync_epoque, d.sync_numero) < (EXCLUDED.sync_epoque, EXCLUDED.sync_numero)
                    RETURNING old.sync_epoque AS a_epoque, old.sync_numero AS a_numero, to_jsonb(old) - $4 AS a_ligne, to_jsonb(new) - $4 AS n_ligne,
                              new.sync_epoque AS n_epoque, new.sync_numero AS n_numero, jsonb_build_object(%4$s) AS cle)
                INSERT INTO ligne_remplacee (officine_id, nom_table, cle, ancienne, ancienne_epoque, ancien_numero, nouvelle_epoque, nouveau_numero, operation)
                SELECT $2, %5$L, maj.cle, maj.a_ligne, maj.a_epoque, maj.a_numero, maj.n_epoque, maj.n_numero, 'remplacement' FROM maj
                WHERE maj.a_epoque IS NOT NULL AND maj.a_epoque < maj.n_epoque
                  AND (maj.a_ligne - ARRAY['sync_epoque', 'sync_numero', 'sync_recu_le', 'sync_chargement', 'sync_absente_le'])
                      IS DISTINCT FROM (maj.n_ligne - ARRAY['sync_epoque', 'sync_numero', 'sync_recu_le', 'sync_chargement', 'sync_absente_le'])$f$,
                r.nom, v_cle, v_set, v_cle_new, r.nom)
            USING r.lignes, v_off, p_epoque, v_retirer;
            -- Suppression par une époque plus récente : la ligne supprimée est archivée (S6.1)
            EXECUTE format($f$
                WITH sup AS (
                    DELETE FROM %1$I d USING (SELECT DISTINCT ON (x ->> 'id') x FROM jsonb_array_elements($1) x
                                              ORDER BY x ->> 'id', (x ->> 'n')::bigint DESC) f,
                         LATERAL jsonb_populate_record(NULL::%1$I, jsonb_build_object(%2$s)) k
                    WHERE f.x ->> 'op' = 'suppression' AND d.officine_id = $2 AND %3$s
                      AND (d.sync_epoque, d.sync_numero) < ($3, (f.x ->> 'n')::bigint)
                    RETURNING d.sync_epoque AS a_epoque, d.sync_numero AS a_numero, to_jsonb(d) - $4 AS a_ligne,
                              (f.x ->> 'n')::bigint AS n_numero, jsonb_build_object(%4$s) AS cle)
                INSERT INTO ligne_remplacee (officine_id, nom_table, cle, ancienne, ancienne_epoque, ancien_numero, nouvelle_epoque, nouveau_numero, operation)
                SELECT $2, %5$L, sup.cle, sup.a_ligne, sup.a_epoque, sup.a_numero, $3, sup.n_numero, 'suppression' FROM sup
                WHERE sup.a_epoque < $3$f$, r.nom, v_objet, v_match, v_cle_old, r.nom)
            USING r.lignes, v_off, p_epoque, v_retirer;
        ELSE
            -- Aucune époque antérieure : rien à archiver (chemin de 009)
            EXECUTE format($f$
                INSERT INTO %1$I AS d
                SELECT p.* FROM (SELECT DISTINCT ON (x ->> 'id') x FROM jsonb_array_elements($1) x
                                 ORDER BY x ->> 'id', (x ->> 'n')::bigint DESC) f,
                     LATERAL jsonb_populate_record(NULL::%1$I, (f.x -> 'donnees') || jsonb_build_object(
                         'officine_id', $2, 'sync_epoque', $3, 'sync_numero', (f.x ->> 'n')::bigint, 'sync_recu_le', now())) p
                WHERE f.x ->> 'op' <> 'suppression'
                ON CONFLICT (officine_id, %2$s) DO UPDATE SET %3$s
                WHERE (d.sync_epoque, d.sync_numero) < (EXCLUDED.sync_epoque, EXCLUDED.sync_numero)$f$, r.nom, v_cle, v_set)
            USING r.lignes, v_off, p_epoque;
            EXECUTE format($f$
                DELETE FROM %1$I d USING (SELECT DISTINCT ON (x ->> 'id') x FROM jsonb_array_elements($1) x
                                          ORDER BY x ->> 'id', (x ->> 'n')::bigint DESC) f,
                     LATERAL jsonb_populate_record(NULL::%1$I, jsonb_build_object(%2$s)) k
                WHERE f.x ->> 'op' = 'suppression' AND d.officine_id = $2 AND %3$s
                  AND (d.sync_epoque, d.sync_numero) < ($3, (f.x ->> 'n')::bigint)$f$, r.nom, v_objet, v_match)
            USING r.lignes, v_off, p_epoque;
        END IF;
        -- Lignes de l'instantané du chargement en cours : vues (même si une version plus récente est déjà là)
        IF v_ch.numero IS NOT NULL THEN
            EXECUTE format($f$
                UPDATE %1$I d SET sync_chargement = $4, sync_absente_le = NULL
                FROM (SELECT x FROM jsonb_array_elements($1) x WHERE x ->> 'op' = 'instantane' AND (x ->> 'n')::bigint = $3) f,
                     LATERAL jsonb_populate_record(NULL::%1$I, jsonb_build_object(%2$s)) k
                WHERE d.officine_id = $2 AND %3$s$f$, r.nom, v_objet, v_match)
            USING r.lignes, v_off, v_ch.repere, v_ch.numero;
        END IF;
    END LOOP;

    INSERT INTO lot_recu (officine_id, epoque, numero, empreinte, version_contrat, nb_lignes, octets, contenu, expurge_le, colonnes_effacees)
    VALUES (v_off, p_epoque, p_numero, p_empreinte, p_version, v_nb, length(p_contenu), v_contenu,
            CASE WHEN cardinality(v_effacees) > 0 THEN now() END, CASE WHEN cardinality(v_effacees) > 0 THEN v_effacees END);
    IF v_ch.numero IS NOT NULL THEN
        UPDATE chargement SET octets_recus = octets_recus + length(p_contenu) WHERE officine_id = v_off AND numero = v_ch.numero;
    ELSE
        INSERT INTO officine_volume_jour (officine_id, jour, octets) VALUES (v_off, (now() AT TIME ZONE 'UTC')::date, length(p_contenu))
        ON CONFLICT (officine_id, jour) DO UPDATE SET octets = officine_volume_jour.octets + EXCLUDED.octets;
    END IF;
    UPDATE officine_epoque SET dernier_lot = p_numero, dernier_recu_le = now() WHERE officine_id = v_off AND epoque = p_epoque;
    RETURN QUERY SELECT 'accepte'::text, p_numero;
END $$;
GRANT EXECUTE ON FUNCTION cloud_appliquer_lot(int, bigint, text, text, bytea) TO pharma_cloud_api;

-- ---- 3. Maximums des identifiants (route GET /api/v1/maximums) ----
-- Tables miroir à clé « id » entière (smallint, integer, bigint) : plus grand id reçu de l'officine courante, lignes présentes
-- (absentes comprises) et lignes archivées. Une table sans aucune ligne n'est pas renvoyée.
CREATE OR REPLACE FUNCTION cloud_maximums_officine()
RETURNS TABLE (nom_table text, maximum bigint)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v_off uuid := cloud_officine_courante(); t text; v_max bigint; v_arch bigint;
BEGIN
    IF v_off IS NULL THEN RAISE EXCEPTION 'Officine courante non posée (app.officine_id)'; END IF;
    FOR t IN SELECT m.nom_table FROM cloud_table_miroir m
             JOIN pg_attribute a ON a.attrelid = to_regclass('public.' || quote_ident(m.nom_table)) AND a.attname = 'id'
                                AND a.attnum > 0 AND NOT a.attisdropped
             WHERE m.cle = ARRAY['id'] AND a.atttypid IN ('int2'::regtype, 'int4'::regtype, 'int8'::regtype)
             ORDER BY m.nom_table LOOP
        EXECUTE format('SELECT max(id)::bigint FROM public.%I WHERE officine_id = $1', t) INTO v_max USING v_off;
        SELECT max((l.cle ->> 'id')::bigint) INTO v_arch FROM ligne_remplacee l
        WHERE l.officine_id = v_off AND l.nom_table = t AND (l.cle ->> 'id') ~ '^-?[0-9]{1,18}$';
        IF v_max IS NOT NULL OR v_arch IS NOT NULL THEN
            nom_table := t;
            maximum := greatest(v_max, v_arch);
            RETURN NEXT;
        END IF;
    END LOOP;
END $$;
REVOKE EXECUTE ON FUNCTION cloud_maximums_officine() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_maximums_officine() TO pharma_cloud_api;

-- ---- 4. Fin d'un chargement (remplace la version de 003) : lignes archivées depuis le début du chargement comptées ----
ALTER TABLE chargement ADD COLUMN IF NOT EXISTS lignes_remplacees bigint;

-- Nombre de lignes archivées pour l'officine courante par des lots de l'époque donnée (lecture sans droit sur la table)
CREATE OR REPLACE FUNCTION cloud_lignes_remplacees(p_epoque int) RETURNS bigint
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
    SELECT count(*) FROM ligne_remplacee WHERE officine_id = cloud_officine_courante() AND nouvelle_epoque = p_epoque
$$;
REVOKE EXECUTE ON FUNCTION cloud_lignes_remplacees(int) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_lignes_remplacees(int) TO pharma_cloud_api;

-- Même compte, limité aux lignes archivées depuis une date (début du chargement : correction 2026-11-18)
CREATE OR REPLACE FUNCTION cloud_lignes_remplacees(p_epoque int, p_depuis timestamptz) RETURNS bigint
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
    SELECT count(*) FROM ligne_remplacee WHERE officine_id = cloud_officine_courante() AND nouvelle_epoque = p_epoque
                                           AND remplacee_le >= p_depuis
$$;
REVOKE EXECUTE ON FUNCTION cloud_lignes_remplacees(int, timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_lignes_remplacees(int, timestamptz) TO pharma_cloud_api;

CREATE OR REPLACE FUNCTION cloud_fin_chargement(p_numero int, p_lignes jsonb)
RETURNS TABLE (absentes bigint, absentes_par_table jsonb, lignes_vues jsonb)
LANGUAGE plpgsql AS $$
DECLARE v_off uuid := cloud_officine_courante(); c chargement; t text; n bigint; v bigint; v_abs jsonb := '{}'; v_vues jsonb := '{}'; v_total bigint := 0;
        v_remplacees bigint;
BEGIN
    IF v_off IS NULL THEN RAISE EXCEPTION 'Officine courante non posée (app.officine_id)'; END IF;
    SELECT * INTO c FROM chargement WHERE officine_id = v_off AND numero = p_numero FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Chargement % inconnu', p_numero USING ERRCODE = 'P0001'; END IF;
    IF c.etat = 'termine' THEN RETURN QUERY SELECT c.absentes, c.absentes_par_table, c.lignes_vues; RETURN; END IF;
    IF c.etat = 'remplace' THEN RAISE EXCEPTION 'Chargement % remplacé par un plus récent', p_numero USING ERRCODE = 'P0001'; END IF;
    FOREACH t IN ARRAY c.tables LOOP
        CONTINUE WHEN NOT EXISTS (SELECT 1 FROM cloud_table_miroir m WHERE m.nom_table = t);
        EXECUTE format($f$
            WITH m AS (UPDATE %1$I SET sync_absente_le = coalesce(sync_absente_le, now())
                       WHERE officine_id = $1 AND sync_chargement IS DISTINCT FROM $2
                         AND (sync_epoque, sync_numero) <= ($3, $4) RETURNING 1)
            SELECT count(*) FROM m$f$, t) INTO n USING v_off, p_numero, c.epoque, c.repere;
        EXECUTE format('SELECT count(*) FROM %I WHERE officine_id = $1 AND sync_chargement = $2', t) INTO v USING v_off, p_numero;
        IF n > 0 THEN v_abs := v_abs || jsonb_build_object(t, n); END IF;
        v_vues := v_vues || jsonb_build_object(t, v);
        v_total := v_total + n;
    END LOOP;
    -- Lignes archivées depuis le début de CE chargement (pas celles d'un chargement précédent de la même époque)
    v_remplacees := cloud_lignes_remplacees(c.epoque, c.debut_le);
    UPDATE chargement SET etat = 'termine', fin_le = now(), lignes_attendues = p_lignes, lignes_vues = v_vues,
                          absentes = v_total, absentes_par_table = v_abs, lignes_remplacees = v_remplacees
    WHERE officine_id = v_off AND numero = p_numero;
    PERFORM cloud_journaliser(v_off, NULL, CASE WHEN v_total > 0 OR v_remplacees > 0 THEN 'chargement_absentes' ELSE 'chargement_termine' END,
        format('chargement %s : %s ligne(s) du serveur central absente(s) de l''officine (gardées), %s ligne(s) archivée(s) depuis le début du chargement (époque %s ; remplacées ou supprimées, gardées)',
               p_numero, v_total, v_remplacees, c.epoque));
    RETURN QUERY SELECT v_total, v_abs, v_vues;
END $$;
GRANT EXECUTE ON FUNCTION cloud_fin_chargement(int, jsonb) TO pharma_cloud_api;

-- ---- 5. Plus grande époque connue pour l'officine courante (route GET /api/v1/etat, champ EpoqueMax) ----
CREATE OR REPLACE FUNCTION cloud_epoque_max() RETURNS int
LANGUAGE sql STABLE AS $$
    SELECT coalesce((SELECT max(epoque) FROM officine_epoque WHERE officine_id = cloud_officine_courante()), 0)
$$;
REVOKE EXECUTE ON FUNCTION cloud_epoque_max() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_epoque_max() TO pharma_cloud_api;

COMMENT ON TABLE ligne_remplacee IS 'Serveur central : version d''une ligne miroir remplacée ou supprimée par une époque plus récente (officine revenue en arrière), ajout seul, cloisonnée (script 020).';
COMMENT ON COLUMN chargement.lignes_remplacees IS 'Lignes archivées dans ligne_remplacee depuis le début du chargement (époque du chargement), comptées à la fin (script 020).';
COMMENT ON FUNCTION cloud_epoque_max() IS 'Plus grande époque reçue de l''officine courante (0 : aucune), rendue par GET /api/v1/etat (script 020).';
