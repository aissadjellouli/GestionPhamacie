-- =====================================================================
-- Base centrale, 2026-11-20 (décisions du titulaire, docs/taches/
-- central-decisions-2026-11-19) : tables sans archive. Script de la base
-- CENTRALE, passé par le propriétaire, idempotent ; à passer après 025.
--   1. cloud_table_miroir.archive_remplacee (booléen, vrai par défaut) :
--      règle si une ligne remplacée ou supprimée par une époque plus récente
--      est copiée dans ligne_remplacee. Mise à faux, UNE FOIS (à la création
--      de la colonne), pour alerte et alerte_surveillance : ces alertes sont
--      recalculées par l'officine, leur archive n'apporte rien. Un rejeu ne
--      réécrit jamais un réglage posé ensuite. Le script 002 régénéré ne
--      touche que la clé et les colonnes du catalogue.
--   2. cloud_appliquer_lot redéfinie (dernière définition : 020) : copie
--      exacte de 020, seul changement « IF v_archive AND m.archive_remplacee ».
--      Le miroir est mis à jour comme avant ; seule l'archive est omise.
--      Les alertes déjà archivées sont gardées (rien n'est supprimé).
--   Garde de rejeu dans 020 (marqueur : la colonne archive_remplacee).
-- PostgreSQL 18 exigé (RETURNING old/new, comme 020).
-- =====================================================================

SET client_encoding = 'UTF8';

DO $version$ BEGIN
    IF current_setting('server_version_num')::int < 180000 THEN
        RAISE EXCEPTION 'Script central 026 : PostgreSQL 18 ou plus récent exigé (comme le script 020) ; ce serveur est en version %. Rien n''a été modifié.',
            current_setting('server_version');
    END IF;
    IF to_regclass('public.ligne_remplacee') IS NULL THEN
        RAISE EXCEPTION 'Script central 026 : passer d''abord le script 020 (table ligne_remplacee absente). Rien n''a été modifié.';
    END IF;
END $version$;

-- ---- 1. Colonne réglable ----
DO $col$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'public.cloud_table_miroir'::regclass AND attname = 'archive_remplacee' AND NOT attisdropped) THEN
        ALTER TABLE cloud_table_miroir ADD COLUMN archive_remplacee boolean NOT NULL DEFAULT true;
        UPDATE cloud_table_miroir SET archive_remplacee = false WHERE nom_table IN ('alerte', 'alerte_surveillance');
    END IF;
END $col$;
COMMENT ON COLUMN cloud_table_miroir.archive_remplacee IS 'Vrai : une ligne remplacée ou supprimée par une époque plus récente est copiée dans ligne_remplacee ; faux : miroir mis à jour sans archive (alertes recalculées). Réglable par le propriétaire (script 026).';

-- ---- 2. Dépôt d'un lot (remplace la version de 020) ----
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
        IF v_archive AND m.archive_remplacee THEN
            -- Table à archive réglable (cloud_table_miroir.archive_remplacee, script 026)
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
