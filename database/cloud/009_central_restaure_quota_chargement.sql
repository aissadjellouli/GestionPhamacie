-- =====================================================================
-- Base centrale : corrections après revue et audit de sécurité
-- (2026-11-16, docs/conception-synchronisation.md § 15 et § 17).
-- Script de la base CENTRALE. Idempotent ; à passer après 008.
--   1. Central restauré (cloud_descendants) : si l'officine annonce une
--      version appliquée SUPÉRIEURE à la version maximale connue ici, la
--      version maximale est remontée à celle de l'officine et les éléments
--      de l'officine sont republiés au-dessus (versions p_apres + 1, ...) :
--      la version suivante dépasse toujours ce que l'officine a déjà vu et
--      elle redescend le réglage actuel. Version annoncée au-delà d'un
--      plafond (10^12) : ignorée (jamais de débordement).
--   2. Plafond de chargement par officine sur 24 h glissantes, TOUS
--      chargements additionnés (terminés, en cours, remplacés), et nombre
--      maximal de débuts de chargement par 24 h (cloud_parametre, réglables).
--      Dépassement : refus « quota_depasse » (début : erreur dont le message
--      commence par « quota_depasse », rendue en 429 et journalisée par l'API ;
--      lot : statut quota_depasse, journalisé par l'API).
--   3. cloud_chargement_en_cours : le positionnement de app.officine_id
--      (local à la transaction) est CONSERVÉ : la table chargement est sous
--      sécurité par ligne forcée, y compris pour le propriétaire de la
--      fonction ; sans lui la fonction ne verrait jamais le chargement
--      (preuve : test 009).
-- =====================================================================

-- ---- 1. Central restauré ----
-- Garde de rejeu (2026-11-18) : définition remplacée par le script 010 ; rejouer ce script après 010 ne la
-- ramène pas en arrière (marqueur : table cloud_reglage_table, posé par 010).
DO $rejeu$ BEGIN
    IF to_regclass('public.cloud_reglage_table') IS NOT NULL THEN
        RAISE NOTICE 'cloud_descendants : définition plus récente (script 010) gardée';
    ELSE
        EXECUTE $def$
CREATE OR REPLACE FUNCTION cloud_descendants(p_apres bigint)
RETURNS TABLE (nom_table text, cle text, version bigint, donnees jsonb, version_max bigint)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v_off uuid := cloud_officine_courante(); v_max bigint; v_n bigint;
BEGIN
    IF v_off IS NULL THEN RAISE EXCEPTION 'Officine courante non posée (app.officine_id)'; END IF;
    PERFORM cloud_publier_descendant('reglage_central', 'familles_envoyees',
        jsonb_build_object('familles', to_jsonb(cloud_familles_envoyees(v_off))));
    SELECT o.version_max INTO v_max FROM officine_descendant o WHERE o.officine_id = v_off;
    IF p_apres IS NOT NULL AND p_apres > v_max AND p_apres <= 1000000000000 THEN
        -- Serveur central restauré : l'officine a vu des versions que ce serveur ne connaît plus. Tout est republié au-dessus.
        WITH o AS (SELECT x.nom_table AS t, x.cle AS c, row_number() OVER (ORDER BY x.version, x.nom_table, x.cle) AS rn
                   FROM descendant x WHERE x.officine_id = v_off)
        UPDATE descendant d SET version = p_apres + o.rn, modifie_le = now() FROM o
        WHERE d.officine_id = v_off AND d.nom_table = o.t AND d.cle = o.c;
        GET DIAGNOSTICS v_n = ROW_COUNT;
        UPDATE officine_descendant o SET version_max = p_apres + v_n WHERE o.officine_id = v_off;
        PERFORM cloud_journaliser(v_off, NULL, 'descendant_remonte', 'version descendante de l''officine supérieure à celle du serveur central : versions remontées');
        v_max := p_apres + v_n;
    END IF;
    IF p_apres IS NOT NULL AND p_apres BETWEEN 0 AND v_max THEN
        UPDATE officine_descendant o SET version_acquittee = greatest(o.version_acquittee, p_apres), acquittee_le = now()
        WHERE o.officine_id = v_off AND o.version_acquittee < p_apres;
    END IF;
    RETURN QUERY SELECT d.nom_table, d.cle, d.version, d.donnees, v_max FROM descendant d
                 WHERE d.officine_id = v_off AND d.version > coalesce(p_apres, 0) ORDER BY d.version;
END $$;
$def$;
    END IF;
END $rejeu$;
REVOKE EXECUTE ON FUNCTION cloud_descendants(bigint) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_descendants(bigint) TO pharma_cloud_api;

-- ---- 2. Plafonds de chargement sur 24 h ----
ALTER TABLE cloud_parametre ADD COLUMN IF NOT EXISTS quota_chargements_octets_24h bigint NOT NULL DEFAULT 17179869184;
ALTER TABLE cloud_parametre ADD COLUMN IF NOT EXISTS chargements_max_24h int NOT NULL DEFAULT 5;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'cloud_parametre_quota_chargements_octets_24h_check') THEN
        ALTER TABLE cloud_parametre ADD CONSTRAINT cloud_parametre_quota_chargements_octets_24h_check CHECK (quota_chargements_octets_24h > 0);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'cloud_parametre_chargements_max_24h_check') THEN
        ALTER TABLE cloud_parametre ADD CONSTRAINT cloud_parametre_chargements_max_24h_check CHECK (chargements_max_24h >= 1);
    END IF;
END $$;

-- Début d'un chargement (remplace la version de 003) : au plus chargements_max_24h débuts sur 24 h glissantes par officine
-- (la répétition d'un même n° ne compte pas)
CREATE OR REPLACE FUNCTION cloud_debut_chargement(p_numero int, p_epoque int, p_repere bigint, p_reprendre_apres bigint,
    p_motif text, p_tables text[])
RETURNS TABLE (dernier bigint, abandonnes bigint)
LANGUAGE plpgsql AS $$
DECLARE v_off uuid := cloud_officine_courante(); c chargement; e officine_epoque; v_inconnue text; v_ab bigint := 0; v_max int;
BEGIN
    IF v_off IS NULL THEN RAISE EXCEPTION 'Officine courante non posée (app.officine_id)'; END IF;
    SELECT * INTO c FROM chargement WHERE officine_id = v_off AND numero = p_numero;
    IF FOUND THEN
        IF c.epoque <> p_epoque OR c.repere <> p_repere THEN
            RAISE EXCEPTION 'Chargement % déjà annoncé avec une autre époque ou un autre repère', p_numero USING ERRCODE = 'P0001';
        END IF;
        RETURN QUERY SELECT greatest(c.dernier_avant, c.reprendre_apres), c.lots_abandonnes; RETURN;
    END IF;
    SELECT string_agg(x, ', ') INTO v_inconnue FROM unnest(p_tables) x WHERE NOT EXISTS (SELECT 1 FROM cloud_table_miroir m WHERE m.nom_table = x);
    IF v_inconnue IS NOT NULL THEN RAISE EXCEPTION 'Table non reçue par le serveur central : %', v_inconnue USING ERRCODE = 'P0001'; END IF;
    SELECT chargements_max_24h INTO v_max FROM cloud_parametre WHERE id = 1;
    IF (SELECT count(*) FROM chargement WHERE officine_id = v_off AND debut_le > now() - interval '24 hours') >= coalesce(v_max, 5) THEN
        RAISE EXCEPTION 'quota_depasse : trop de chargements complets sur 24 heures' USING ERRCODE = 'P0001';
    END IF;
    UPDATE chargement SET etat = 'remplace', fin_le = now() WHERE officine_id = v_off AND etat = 'en_cours';
    INSERT INTO officine_epoque (officine_id, epoque) VALUES (v_off, p_epoque) ON CONFLICT DO NOTHING;
    SELECT * INTO e FROM officine_epoque WHERE officine_id = v_off AND epoque = p_epoque FOR UPDATE;
    IF p_reprendre_apres > e.dernier_lot THEN
        v_ab := p_reprendre_apres - e.dernier_lot;
        UPDATE officine_epoque SET dernier_lot = p_reprendre_apres WHERE officine_id = v_off AND epoque = p_epoque;
        PERFORM cloud_journaliser(v_off, NULL, 'lots_abandonnes',
            format('chargement %s : lots %s à %s de l''époque %s absents du serveur central, remplacés par le chargement complet',
                   p_numero, e.dernier_lot + 1, p_reprendre_apres, p_epoque));
    END IF;
    INSERT INTO chargement (officine_id, numero, epoque, repere, motif, tables, reprendre_apres, lots_abandonnes, dernier_avant)
    VALUES (v_off, p_numero, p_epoque, p_repere, p_motif, p_tables, p_reprendre_apres, v_ab, e.dernier_lot);
    PERFORM cloud_journaliser(v_off, NULL, 'chargement_debut', format('chargement %s (%s), %s table(s)', p_numero, p_motif, cardinality(p_tables)));
    RETURN QUERY SELECT greatest(e.dernier_lot, p_reprendre_apres), v_ab;
END $$;
GRANT EXECUTE ON FUNCTION cloud_debut_chargement(int, int, bigint, bigint, text, text[]) TO pharma_cloud_api;

-- Dépôt d'un lot (remplace la version de 006) : plafond cumulé sur 24 h (tous chargements, remplacés compris), puis quota par chargement pendant un chargement complet, quota du jour sinon
-- Garde de rejeu (2026-11-18) : définition remplacée par le script 020 ; rejouer ce script après 020 ne la
-- ramène pas en arrière (marqueur : table ligne_remplacee, posé par 020).
DO $rejeu$ BEGIN
    IF to_regclass('public.ligne_remplacee') IS NOT NULL THEN
        RAISE NOTICE 'cloud_appliquer_lot : définition plus récente (script 020) gardée';
    ELSE
        EXECUTE $def$
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
$def$;
    END IF;
END $rejeu$;

GRANT EXECUTE ON FUNCTION cloud_appliquer_lot(int, bigint, text, text, bytea) TO pharma_cloud_api;

COMMENT ON COLUMN cloud_parametre.quota_chargements_octets_24h IS 'Octets reçus au plus par officine sur 24 h glissantes, tous chargements complets additionnés, remplacés compris (script 009).';
COMMENT ON COLUMN cloud_parametre.chargements_max_24h IS 'Débuts de chargement complet au plus par officine sur 24 h glissantes (script 009).';
