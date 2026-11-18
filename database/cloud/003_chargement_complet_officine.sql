-- =====================================================================
-- Base centrale, lot S6 (2026-11-16, docs/conception-synchronisation.md
-- § 13) : chargement complet d'une officine (activation, demande du
-- titulaire, réactivation, lots perdus par le serveur central).
--   * chargement : un par officine en cours ; début (n°, époque, repère,
--     lots à abandonner), fin (lignes absentes de l'officine).
--   * Lignes « instantane » d'un lot : appliquées comme une création
--     (plus grand (époque, n°) gagne) ET marquées vues par le chargement en
--     cours (sync_chargement), même si une version plus récente est déjà là.
--     Seules les lignes portant le repère du chargement en cours sont
--     marquées (une ligne d'un chargement remplacé ne compte pas).
--   * Fin : toute ligne de l'officine non vue par le chargement et non
--     modifiée depuis le repère ((époque, n°) <= (époque, repère)) est
--     marquée absente (sync_absente_le), JAMAIS effacée (décision 8 :
--     signaler et garder) ; journal de sécurité (compte, sans donnée).
--   * Une ligne absente reçue de nouveau perd sa marque.
-- Remplace cloud_appliquer_lot de 001. Idempotent. À passer après 002
-- (colonnes sync_chargement et sync_absente_le des tables miroir).
-- =====================================================================

CREATE TABLE IF NOT EXISTS chargement (
    officine_id        uuid        NOT NULL,
    numero             int         NOT NULL CHECK (numero >= 1),
    epoque             int         NOT NULL CHECK (epoque >= 1),
    repere             bigint      NOT NULL CHECK (repere >= 0),
    motif              text        NOT NULL CHECK (motif IN ('activation', 'demande', 'reactivation', 'lots_manquants')),
    tables             text[]      NOT NULL,
    reprendre_apres    bigint      NOT NULL DEFAULT 0,
    lots_abandonnes    bigint      NOT NULL DEFAULT 0,
    dernier_avant      bigint      NOT NULL DEFAULT 0,
    etat               text        NOT NULL DEFAULT 'en_cours' CHECK (etat IN ('en_cours', 'termine', 'remplace')),
    debut_le           timestamptz NOT NULL DEFAULT now(),
    fin_le             timestamptz,
    lignes_attendues   jsonb,
    lignes_vues        jsonb,
    absentes           bigint,
    absentes_par_table jsonb,
    PRIMARY KEY (officine_id, numero)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_chargement_en_cours ON chargement (officine_id) WHERE etat = 'en_cours';
ALTER TABLE chargement ENABLE ROW LEVEL SECURITY;
ALTER TABLE chargement FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON chargement;
CREATE POLICY cloisonnement ON chargement USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE ON chargement TO pharma_cloud_api;

-- Début d'un chargement pour l'officine courante. Idempotent (même n° : réponse enregistrée). Un chargement en cours plus
-- ancien est remplacé. Si l'officine a des lots accusés que le serveur central n'a plus (p_reprendre_apres > dernier reçu),
-- ces lots sont abandonnés : le n° attendu saute à p_reprendre_apres + 1 et le chargement remplace leur contenu.
-- Garde de rejeu (2026-11-18) : définition remplacée par le script 009 ; rejouer ce script après 009 ne la
-- ramène pas en arrière (marqueur : colonne cloud_parametre.quota_chargements_octets_24h, posé par 009).
DO $rejeu$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'public.cloud_parametre'::regclass AND attname = 'quota_chargements_octets_24h' AND NOT attisdropped) THEN
        RAISE NOTICE 'cloud_debut_chargement : définition plus récente (script 009) gardée';
    ELSE
        EXECUTE $def$
CREATE OR REPLACE FUNCTION cloud_debut_chargement(p_numero int, p_epoque int, p_repere bigint, p_reprendre_apres bigint,
    p_motif text, p_tables text[])
RETURNS TABLE (dernier bigint, abandonnes bigint)
LANGUAGE plpgsql AS $$
DECLARE v_off uuid := cloud_officine_courante(); c chargement; e officine_epoque; v_inconnue text; v_ab bigint := 0;
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
$def$;
    END IF;
END $rejeu$;

-- Fin d'un chargement : lignes vues, lignes absentes de l'officine marquées (jamais effacées). Idempotent.
-- Garde de rejeu (2026-11-18) : définition remplacée par le script 020 ; rejouer ce script après 020 ne la
-- ramène pas en arrière (marqueur : table ligne_remplacee, posé par 020).
DO $rejeu$ BEGIN
    IF to_regclass('public.ligne_remplacee') IS NOT NULL THEN
        RAISE NOTICE 'cloud_fin_chargement : définition plus récente (script 020) gardée';
    ELSE
        EXECUTE $def$
CREATE OR REPLACE FUNCTION cloud_fin_chargement(p_numero int, p_lignes jsonb)
RETURNS TABLE (absentes bigint, absentes_par_table jsonb, lignes_vues jsonb)
LANGUAGE plpgsql AS $$
DECLARE v_off uuid := cloud_officine_courante(); c chargement; t text; n bigint; v bigint; v_abs jsonb := '{}'; v_vues jsonb := '{}'; v_total bigint := 0;
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
    UPDATE chargement SET etat = 'termine', fin_le = now(), lignes_attendues = p_lignes, lignes_vues = v_vues,
                          absentes = v_total, absentes_par_table = v_abs
    WHERE officine_id = v_off AND numero = p_numero;
    PERFORM cloud_journaliser(v_off, NULL, CASE WHEN v_total > 0 THEN 'chargement_absentes' ELSE 'chargement_termine' END,
        format('chargement %s : %s ligne(s) du serveur central absente(s) de l''officine (gardées)', p_numero, v_total));
    RETURN QUERY SELECT v_total, v_abs, v_vues;
END $$;
$def$;
    END IF;
END $rejeu$;

-- Dépôt d'un lot (remplace la version de 001) : en plus, lignes « instantane » marquées vues par le chargement en cours,
-- marque « absente » retirée d'une ligne reçue de nouveau.
-- Garde de rejeu (2026-11-18) : définition remplacée par le script 004 ; rejouer ce script après 004 ne la
-- ramène pas en arrière (marqueur : fonction cloud_lot_sans_colonnes, posé par 004).
DO $rejeu$ BEGIN
    IF to_regprocedure('public.cloud_lot_sans_colonnes(jsonb,text[])') IS NOT NULL THEN
        RAISE NOTICE 'cloud_appliquer_lot : définition plus récente (script 004) gardée';
    ELSE
        EXECUTE $def$
CREATE OR REPLACE FUNCTION cloud_appliquer_lot(p_epoque int, p_numero bigint, p_empreinte text, p_version text, p_contenu bytea)
RETURNS TABLE (statut text, dernier bigint)
LANGUAGE plpgsql AS $$
DECLARE
    v_off     uuid := cloud_officine_courante();
    e         officine_epoque;
    v_lot     jsonb;
    r         record;
    m         cloud_table_miroir;
    v_ch      chargement;
    v_cle     text;
    v_objet   text;
    v_match   text;
    v_garde   text;
    v_set     text;
    v_nb      int;
    v_quota   bigint;
    v_jour    bigint;
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
    SELECT quota_octets_jour INTO v_quota FROM cloud_parametre WHERE id = 1;
    SELECT coalesce((SELECT octets FROM officine_volume_jour WHERE officine_id = v_off AND jour = (now() AT TIME ZONE 'UTC')::date), 0) INTO v_jour;
    IF v_jour + length(p_contenu) > coalesce(v_quota, 536870912) THEN
        RETURN QUERY SELECT 'quota_depasse'::text, e.dernier_lot; RETURN;
    END IF;
    SELECT * INTO v_ch FROM chargement WHERE officine_id = v_off AND etat = 'en_cours' AND epoque = p_epoque;

    v_lot := convert_from(p_contenu, 'UTF8')::jsonb;
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

    INSERT INTO lot_recu (officine_id, epoque, numero, empreinte, version_contrat, nb_lignes, octets, contenu)
    VALUES (v_off, p_epoque, p_numero, p_empreinte, p_version, v_nb, length(p_contenu), p_contenu);
    INSERT INTO officine_volume_jour (officine_id, jour, octets) VALUES (v_off, (now() AT TIME ZONE 'UTC')::date, length(p_contenu))
    ON CONFLICT (officine_id, jour) DO UPDATE SET octets = officine_volume_jour.octets + EXCLUDED.octets;
    UPDATE officine_epoque SET dernier_lot = p_numero, dernier_recu_le = now() WHERE officine_id = v_off AND epoque = p_epoque;
    RETURN QUERY SELECT 'accepte'::text, p_numero;
END $$;
$def$;
    END IF;
END $rejeu$;

GRANT EXECUTE ON FUNCTION cloud_debut_chargement(int, int, bigint, bigint, text, text[]), cloud_fin_chargement(int, jsonb),
    cloud_appliquer_lot(int, bigint, text, text, bytea) TO pharma_cloud_api;

COMMENT ON TABLE chargement IS 'Serveur central : chargements complets des officines (début, fin, lignes absentes de l''officine signalées et gardées), cloisonnés par officine.';
