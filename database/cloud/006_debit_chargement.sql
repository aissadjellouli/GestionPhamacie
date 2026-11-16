-- =====================================================================
-- Base centrale : débit pendant un chargement complet (2026-11-16,
-- docs/conception-synchronisation.md § 17). Script de la base CENTRALE.
--   * Limite de dépôts : 60 lots/min par certificat en temps normal,
--     relevée (600/min par défaut, appsettings Cloud:LotsParMinuteChargement)
--     quand l'officine a un chargement complet annoncé et non terminé
--     (cloud_chargement_en_cours, lu par l'API avant la limite de débit).
--   * Quota d'octets : pendant un chargement, les lots ne comptent pas dans
--     le quota du JOUR (512 Mio) mais dans un plafond PAR CHARGEMENT
--     (cloud_parametre.quota_chargement_octets, 8 Gio par défaut, réglable) :
--     un gros chargement n'est pas arrêté à mi-chemin par le quota du jour,
--     et un chargement anormal reste borné. Octets suivis dans
--     chargement.octets_recus.
-- Remplace cloud_appliquer_lot de 004. Idempotent ; à passer après 005.
-- =====================================================================

ALTER TABLE cloud_parametre ADD COLUMN IF NOT EXISTS quota_chargement_octets bigint NOT NULL DEFAULT 8589934592;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'cloud_parametre_quota_chargement_octets_check') THEN
        ALTER TABLE cloud_parametre ADD CONSTRAINT cloud_parametre_quota_chargement_octets_check CHECK (quota_chargement_octets > 0);
    END IF;
END $$;
ALTER TABLE chargement ADD COLUMN IF NOT EXISTS octets_recus bigint NOT NULL DEFAULT 0;

-- Chargement complet en cours pour l'officine d'un certificat (API, avant la limite de débit) ; aucune donnée renvoyée
CREATE OR REPLACE FUNCTION cloud_chargement_en_cours(p_empreinte text) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v_off uuid; v boolean;
BEGIN
    SELECT c.officine_id INTO v_off FROM certificat c WHERE c.empreinte = p_empreinte AND c.revoque_le IS NULL;
    IF v_off IS NULL THEN RETURN false; END IF;
    PERFORM set_config('app.officine_id', v_off::text, true);
    SELECT EXISTS (SELECT 1 FROM chargement WHERE officine_id = v_off AND etat = 'en_cours') INTO v;
    RETURN v;
END $$;
REVOKE EXECUTE ON FUNCTION cloud_chargement_en_cours(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_chargement_en_cours(text) TO pharma_cloud_api;

-- Dépôt d'un lot (remplace la version de 004) : quota par chargement pendant un chargement complet, quota du jour sinon
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
        IF v_ch.octets_recus + length(p_contenu) > coalesce(v_quota_ch, 8589934592) THEN
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
    v_effacees := cloud_colonnes_presentes(v_lot, cloud_colonnes_interdites());
    IF cardinality(v_effacees) > 0 THEN
        v_lot := cloud_lot_sans_colonnes(v_lot, v_effacees);
        v_contenu := convert_to(v_lot::text, 'UTF8');
        PERFORM cloud_journaliser(v_off, NULL, 'familles_effacees_reception',
            format('lot %s/%s : colonnes non autorisées effacées à la réception (%s)', p_epoque, p_numero, array_to_string(v_effacees, ', ')));
    END IF;
    v_nb := jsonb_array_length(v_lot -> 'lignes');
    FOR r IN SELECT x ->> 'table' AS nom, jsonb_agg(x) AS lignes
             FROM jsonb_array_elements(v_lot -> 'lignes') x GROUP BY 1 ORDER BY 1 LOOP
        SELECT * INTO m FROM cloud_table_miroir WHERE nom_table = r.nom;
        IF NOT FOUND THEN RAISE EXCEPTION 'Table non reçue par le serveur central : %', r.nom USING ERRCODE = 'P0001'; END IF;
        SELECT string_agg(format('%I', c), ', ' ORDER BY n) INTO v_cle FROM unnest(m.cle) WITH ORDINALITY k(c, n);
        SELECT string_agg(format('%L, split_part(f.x ->> ''id'', ''|'', %s)', c, n), ', ' ORDER BY n),
               string_agg(format('d.%I = k.%I', c, c), ' AND ' ORDER BY n),
               string_agg(format('coalesce(to_jsonb(d.%I) #>> ''{}'', '''')', c), ' || ''|'' || ' ORDER BY n)
            INTO v_objet, v_match, v_garde FROM unnest(m.cle) WITH ORDINALITY k(c, n);
        IF cardinality(m.cle) > 1 THEN v_match := v_match || ' AND (' || v_garde || ') = f.x ->> ''id'''; END IF;
        SELECT string_agg(format('%I = EXCLUDED.%I', c, c), ', ')
            INTO v_set FROM unnest(m.colonnes || ARRAY['sync_epoque', 'sync_numero', 'sync_recu_le', 'sync_absente_le']) c WHERE NOT c = ANY (m.cle);
        -- État final de chaque donnée dans ce lot (plus grand n° de ligne) ; « instantane » vaut création
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
COMMENT ON COLUMN cloud_parametre.quota_chargement_octets IS 'Plafond d''octets reçus par chargement complet (remplace le quota du jour pendant un chargement) (script 006).';
COMMENT ON COLUMN chargement.octets_recus IS 'Octets des lots reçus pendant ce chargement (script 006).';
