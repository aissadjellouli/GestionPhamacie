-- =====================================================================
-- Base centrale : intégrité après restauration, vérification par branches
-- (lot S6.5.2, 2026-11-18 ; docs/conception-synchronisation.md § 31,
-- décisions S6.5-D1 à D9 validées par le titulaire le 2026-11-18).
-- Script de la base CENTRALE, à passer après 022 par le propriétaire.
--   * Sans changement d'époque déclaré, ou sans deux versions d'un même
--     rang : vérification d'origine (script 012), inchangée
--     (cloud_integrite_verifier_chaine_lineaire, copie exacte).
--   * Sinon : vérification par LIENS D'EMPREINTE, toujours depuis le rang
--     1, sur les lignes de la table miroir et les versions divergentes
--     (022). Deux lignes du même rang chaînées sur la même ligne =
--     bifurcation au rang R ; elle est classée « remplacée après
--     restauration » seulement si les conditions 1 à 8 du § 31.2 sont
--     toutes remplies (déclaration reçue avant le premier lot, chargement
--     terminé, rangs 1 à R communs, fin de chaîne déclarée cohérente,
--     ancienne suite reçue avant la déclaration, nouvelle suite
--     postérieure et sans reprise de n° hors collision, deux suites
--     vérifiées de bout en bout, sceaux perdus comptés). Sinon : anomalie
--     d'aujourd'hui (chainage_rompu) avec la condition non remplie.
--   * Sceau perdu ou plus de 2 restaurations en 12 mois : classe
--     « restauration à justifier » (S6.5-D3, D7).
--   * Anomalies déjà notées sur la suite remplacée : rattachées
--     (cloud_anomalie_classement), jamais modifiées (S6.5-D5).
--   * Sceaux et vérification croisée : ligne cherchée par (rang, n°,
--     empreinte) dans les deux suites, jamais par le rang seul.
--   * Résumé integrite-v1 : champs restaurations et anomalies_classees,
--     statut « restauration » tant que le titulaire n'a pas pris acte
--     (S6.5-D4, table officine integrite_prise_acte).
--   * Tickets (S6.5-D6 a) : l'officine avance son compteur au-delà du plus
--     grand n° reçu par le central ; aucun changement ici.
-- Gardes de rejeu : 012 ne recrée plus les fonctions remplacées ici si
-- cloud_integrite_verifier_branches existe. PostgreSQL 18 exigé.
-- Idempotent. Aucune donnée existante modifiée.
-- =====================================================================

SET client_encoding = 'UTF8';

DO $version$ BEGIN
    IF current_setting('server_version_num')::int < 180000 THEN
        RAISE EXCEPTION 'Script central 023 : PostgreSQL 18 ou plus récent exigé ; ce serveur est en version %. Rien n''a été modifié.',
            current_setting('server_version');
    END IF;
    IF to_regclass('public.cloud_epoque_changement') IS NULL THEN
        RAISE EXCEPTION 'Script central 023 : passer d''abord le script 022 (table cloud_epoque_changement absente). Rien n''a été modifié.';
    END IF;
END $version$;

-- ---- 1. Point de reprise : époque de la suite vivante ----
ALTER TABLE cloud_integrite_etat ADD COLUMN IF NOT EXISTS epoque_branche int;
COMMENT ON COLUMN cloud_integrite_etat.epoque_branche IS 'Époque de la suite vivante quand la chaîne a bifurqué après une restauration (script 023).';

-- ---- 2. Vérification d'origine, par rang (copie exacte de la définition du script 012, sous un autre nom) ----
CREATE OR REPLACE FUNCTION cloud_integrite_verifier_chaine_lineaire(p_journal text, p_cle text, p_complete boolean, p_delai interval) RETURNS text
LANGUAGE plpgsql AS $$
DECLARE
    v_off     uuid := cloud_officine_courante();
    ref       cloud_chaine_reference;
    e         cloud_integrite_etat;
    v_rang    bigint := 0;
    v_prec    bytea;
    v_algo    text;
    v_nv      bigint := 0;
    v_statut  text := 'ok';
    v_rupture bigint;
    v_marque  text;
    l         record;
    v_calc    bytea;
BEGIN
    SELECT * INTO ref FROM cloud_chaine_reference WHERE journal = p_journal;
    SELECT * INTO e FROM cloud_integrite_etat WHERE officine_id = v_off AND journal = p_journal AND cle_chaine = p_cle FOR UPDATE;
    IF FOUND AND NOT p_complete THEN
        IF e.statut = 'rupture' THEN RETURN 'rupture'; END IF;   -- la vérification complète repart du rang 1
        v_rang := e.dernier_rang; v_prec := e.dernier_hash; v_algo := e.algorithme; v_nv := e.non_verifiables;
    END IF;
    SELECT coalesce(string_agg(format('coalesce(r.%I::text, '''') ~ %L', c, cloud_integrite_marqueurs()), ' OR '), 'false')
      INTO v_marque FROM unnest(ref.colonnes_filtrees) c;
    FOR l IN EXECUTE format(
        'SELECT r.id::text AS id, r.rang, r.hash, r.hash_precedent, r.sync_recu_le, %s AS canonique, (%s) AS marque
         FROM public.%I r WHERE r.officine_id = $1 AND r.cle_chaine = $2 AND r.rang > $3 ORDER BY r.rang',
        CASE WHEN ref.mode = 'complet' THEN cloud_integrite_expression(p_journal) ELSE 'NULL::text' END, v_marque, p_journal)
        USING v_off, p_cle, v_rang
    LOOP
        IF l.rang IS DISTINCT FROM v_rang + 1 THEN
            -- Trou : les lignes suivantes sont arrivées ; anomalie seulement si le manque dure plus que le délai
            IF l.sync_recu_le < now() - p_delai THEN
                PERFORM cloud_integrite_noter(p_journal, p_cle, v_rang + 1, NULL, 'ligne_manquante',
                    format('rang %s jamais reçu (rang %s reçu le %s)', v_rang + 1, l.rang, to_char(l.sync_recu_le AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI "UTC"')));
                v_statut := 'rupture'; v_rupture := v_rang + 1;
            ELSE
                v_statut := 'attente';
            END IF;
            EXIT;
        END IF;
        IF v_rang = 0 THEN
            v_algo := CASE length(l.hash) WHEN 32 THEN 'sha256' WHEN 64 THEN 'sha512' END;
            IF v_algo IS NULL THEN
                PERFORM cloud_integrite_noter(p_journal, p_cle, l.rang, l.id, 'algorithme_inconnu',
                    format('empreinte de %s octets : algorithme inconnu', coalesce(length(l.hash), 0)));
                v_statut := 'rupture'; v_rupture := l.rang; EXIT;
            END IF;
            v_prec := decode(repeat('00', length(l.hash)), 'hex');
        END IF;
        IF l.hash_precedent IS DISTINCT FROM v_prec OR length(l.hash) IS DISTINCT FROM length(v_prec) THEN
            PERFORM cloud_integrite_noter(p_journal, p_cle, l.rang, l.id, 'chainage_rompu', 'empreinte précédente différente de celle de la ligne d''avant',
                encode(v_prec, 'hex'), coalesce(encode(l.hash_precedent, 'hex'), 'vide'));
            v_statut := 'rupture'; v_rupture := l.rang; EXIT;
        END IF;
        IF ref.mode = 'complet' THEN
            v_calc := CASE v_algo WHEN 'sha512' THEN sha512(convert_to(l.canonique, 'UTF8') || v_prec)
                                  ELSE sha256(convert_to(l.canonique, 'UTF8') || v_prec) END;
            IF v_calc IS DISTINCT FROM l.hash THEN
                IF l.marque THEN
                    v_nv := v_nv + 1;   -- texte filtré avant l'envoi : contenu non recalculable, chaînage vérifié
                ELSE
                    PERFORM cloud_integrite_noter(p_journal, p_cle, l.rang, l.id, 'ligne_modifiee', 'empreinte recalculée différente de l''empreinte reçue',
                        encode(v_calc, 'hex'), coalesce(encode(l.hash, 'hex'), 'vide'));
                    v_statut := 'rupture'; v_rupture := l.rang; EXIT;
                END IF;
            END IF;
        END IF;
        v_prec := l.hash;
        v_rang := l.rang;
    END LOOP;
    INSERT INTO cloud_integrite_etat AS x (officine_id, journal, cle_chaine, algorithme, dernier_rang, dernier_hash, statut, rang_rupture,
                                          non_verifiables, verifie_le, verification_complete_le)
    VALUES (v_off, p_journal, p_cle, v_algo, v_rang, v_prec, v_statut, v_rupture, v_nv, now(), CASE WHEN p_complete THEN now() END)
    ON CONFLICT (officine_id, journal, cle_chaine) DO UPDATE SET algorithme = EXCLUDED.algorithme, dernier_rang = EXCLUDED.dernier_rang,
        dernier_hash = EXCLUDED.dernier_hash, statut = EXCLUDED.statut, rang_rupture = EXCLUDED.rang_rupture,
        non_verifiables = EXCLUDED.non_verifiables, verifie_le = EXCLUDED.verifie_le,
        verification_complete_le = coalesce(EXCLUDED.verification_complete_le, x.verification_complete_le);
    RETURN v_statut;
END $$;

-- ---- 3. Nœuds d'une chaîne : lignes de la table miroir et versions divergentes (script 022), dans une table temporaire ----
-- Une ligne par version reçue : source, n°, rang, empreintes, époque, réception (horloge du central), absente du dernier
-- chargement, date de la ligne (colonne_date du référentiel), forme canonique (mode complet), texte filtré, montant et événement.
CREATE OR REPLACE FUNCTION cloud_integrite_charger_noeuds(p_journal text, p_cle text) RETURNS bigint
LANGUAGE plpgsql AS $$
DECLARE
    v_off     uuid := cloud_officine_courante();
    ref       cloud_chaine_reference;
    v_canon   text;
    v_marque  text;
    v_montant text;
    v_evt     text;
    n         bigint;
BEGIN
    SELECT * INTO ref FROM cloud_chaine_reference WHERE journal = p_journal;
    IF to_regclass('pg_temp.cloud_noeud') IS NULL THEN
        CREATE TEMP TABLE cloud_noeud (
            source text, id text, rang bigint, hash bytea, hash_precedent bytea, epoque int, recu_le timestamptz, absente boolean,
            date_ligne timestamptz, canonique text, marque boolean, montant numeric, evenement text) ON COMMIT DROP;
    END IF;
    TRUNCATE cloud_noeud;
    v_canon := CASE WHEN ref.mode = 'complet' THEN cloud_integrite_expression(p_journal) ELSE 'NULL::text' END;
    SELECT coalesce(string_agg(format('coalesce(r.%I::text, '''') ~ %L', c, cloud_integrite_marqueurs()), ' OR '), 'false')
      INTO v_marque FROM unnest(ref.colonnes_filtrees) c;
    v_montant := CASE WHEN p_journal = 'journal_vente' THEN 'r.montant_ttc::numeric' ELSE 'NULL::numeric' END;
    v_evt := CASE WHEN p_journal = 'journal_vente' THEN 'r.evenement::text' ELSE 'NULL::text' END;
    EXECUTE format(
        'INSERT INTO cloud_noeud SELECT ''miroir'', r.id::text, r.rang, r.hash, r.hash_precedent, r.sync_epoque, r.sync_recu_le,
                r.sync_absente_le IS NOT NULL, r.%I::timestamptz, %s, (%s), %s, %s
         FROM public.%I r WHERE r.officine_id = $1 AND r.cle_chaine = $2',
        ref.colonne_date, v_canon, v_marque, v_montant, v_evt, p_journal) USING v_off, p_cle;
    EXECUTE format(
        'INSERT INTO cloud_noeud SELECT ''divergente'', r.id::text, r.rang, r.hash, r.hash_precedent, d.epoque, d.recue_le,
                false, r.%I::timestamptz, %s, (%s), %s, %s
         FROM cloud_ligne_divergente d CROSS JOIN LATERAL jsonb_populate_record(NULL::public.%I, d.ligne) r
         WHERE d.officine_id = $1 AND d.nom_table = %L AND d.cle_chaine = $2',
        ref.colonne_date, v_canon, v_marque, v_montant, v_evt, p_journal, p_journal) USING v_off, p_cle;
    SELECT count(*) INTO n FROM cloud_noeud;
    RETURN n;
END $$;

-- Empreinte recalculée d'un nœud (mode complet) ; NULL en mode « liaison »
CREATE OR REPLACE FUNCTION cloud_integrite_empreinte(p_canonique text, p_prec bytea, p_algo text) RETURNS bytea
LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE WHEN p_canonique IS NULL THEN NULL
                WHEN p_algo = 'sha512' THEN sha512(convert_to(p_canonique, 'UTF8') || p_prec)
                ELSE sha256(convert_to(p_canonique, 'UTF8') || p_prec) END
$$;

-- Suit une branche depuis un nœud (déjà choisi) par les liens d'empreinte, sans choisir à une nouvelle bifurcation.
-- Rend : nombre de lignes, fin (rang, n°, empreinte), dates, montant TTC des ventes et annulations, plus grande réception,
-- empreintes vérifiées (mode complet), nœuds non vérifiables (texte filtré), époque la plus grande, et la première erreur.
CREATE OR REPLACE FUNCTION cloud_integrite_suivre_branche(p_rang bigint, p_hash bytea, p_prec bytea, p_algo text, p_mode text, p_epoque_max int)
RETURNS jsonb
LANGUAGE plpgsql AS $$
DECLARE
    n        record;
    v_rang   bigint := p_rang;
    v_hash   bytea := p_hash;
    v_prec   bytea := p_prec;
    v_lignes bigint := 0;
    v_nv     bigint := 0;
    v_cands  int;
    v_res    jsonb := '{}';
    v_debut  timestamptz;
    v_fin    timestamptz;
    v_mont   numeric := 0;
    v_recu   timestamptz;
    v_ep     int := 0;
    v_id     text;
    v_erreur text;
    v_calc   bytea;
BEGIN
    SELECT * INTO n FROM cloud_noeud WHERE rang = p_rang AND hash = p_hash AND hash_precedent IS NOT DISTINCT FROM p_prec
        AND (p_epoque_max IS NULL OR epoque <= p_epoque_max) ORDER BY epoque DESC LIMIT 1;
    IF NOT FOUND THEN
        RETURN jsonb_build_object('lignes', 0, 'erreur', format('aucune ligne au rang %s avec cette empreinte', p_rang));
    END IF;
    LOOP
        IF p_mode = 'complet' THEN
            v_calc := cloud_integrite_empreinte(n.canonique, v_prec, p_algo);
            IF v_calc IS DISTINCT FROM n.hash THEN
                IF n.marque THEN v_nv := v_nv + 1;
                ELSE v_erreur := format('empreinte recalculée différente au rang %s (ligne %s)', n.rang, n.id); EXIT; END IF;
            END IF;
        END IF;
        v_lignes := v_lignes + 1;
        v_debut := least(v_debut, n.date_ligne); v_fin := greatest(v_fin, n.date_ligne);
        v_recu := greatest(v_recu, n.recu_le); v_ep := greatest(v_ep, n.epoque);
        IF n.evenement IN ('vente', 'annulation') THEN v_mont := v_mont + coalesce(n.montant, 0); END IF;
        v_rang := n.rang; v_hash := n.hash; v_id := n.id; v_prec := n.hash;
        SELECT count(*) INTO v_cands FROM cloud_noeud c WHERE c.rang = v_rang + 1 AND c.hash_precedent = v_hash
            AND (p_epoque_max IS NULL OR c.epoque <= p_epoque_max);
        EXIT WHEN v_cands = 0;
        IF v_cands > 1 THEN v_erreur := format('nouvelle bifurcation au rang %s dans cette branche', v_rang); EXIT; END IF;
        SELECT * INTO n FROM cloud_noeud c WHERE c.rang = v_rang + 1 AND c.hash_precedent = v_hash
            AND (p_epoque_max IS NULL OR c.epoque <= p_epoque_max);
    END LOOP;
    RETURN jsonb_build_object('lignes', v_lignes, 'fin_rang', v_rang, 'fin_id', v_id, 'fin_hash', encode(v_hash, 'hex'),
        'debut', v_debut, 'fin', v_fin, 'montant', v_mont, 'recu_max', v_recu, 'epoque_max', v_ep, 'non_verifiables', v_nv, 'erreur', v_erreur);
END $$;

-- ---- 4. Constat d'une bifurcation au rang p_rang (dernier rang commun), conditions 1 à 8 du § 31.2 ----
-- Rend {"etat": "reconnue" | "attente" | "refusee", "vivant_hash": …, "detail": …, "bifurcation_id": …}.
CREATE OR REPLACE FUNCTION cloud_integrite_constater_bifurcation(p_journal text, p_cle text, p_rang bigint, p_prec bytea, p_algo text)
RETURNS jsonb
LANGUAGE plpgsql AS $$
DECLARE
    v_off      uuid := cloud_officine_courante();
    ref        cloud_chaine_reference;
    v_vivant   record;
    v_ancien   record;
    v_nb_anc   int;
    d          cloud_epoque_changement;
    v_fin_decl bigint;
    v_colli    bigint;
    a          jsonb;
    b          jsonb;
    v_echec    text;
    v_classe   text := 'restauration_constatee';
    v_perdus   int := 0;
    v_bif      bigint;
    v_max_id   bigint;
    v_tol      interval := interval '10 minutes';   -- S6.5-D7
    v_cond     jsonb;
    s          record;
BEGIN
    SELECT * INTO ref FROM cloud_chaine_reference WHERE journal = p_journal;
    -- Suite vivante : la version d'époque la plus récente au rang R + 1 ; anciennes suites : les autres
    SELECT * INTO v_vivant FROM cloud_noeud WHERE rang = p_rang + 1 AND hash_precedent = p_prec
        ORDER BY epoque DESC, (source = 'divergente') DESC LIMIT 1;
    SELECT count(*) INTO v_nb_anc FROM cloud_noeud WHERE rang = p_rang + 1 AND hash_precedent = p_prec AND epoque < v_vivant.epoque;
    IF v_nb_anc <> 1 OR (SELECT count(*) FROM cloud_noeud WHERE rang = p_rang + 1 AND hash_precedent = p_prec) <> 2 THEN
        RETURN jsonb_build_object('etat', 'refusee', 'detail', format('bifurcation au rang %s : %s suites, deux attendues d''époques différentes',
            p_rang, (SELECT count(*) FROM cloud_noeud WHERE rang = p_rang + 1 AND hash_precedent = p_prec)));
    END IF;
    SELECT * INTO v_ancien FROM cloud_noeud WHERE rang = p_rang + 1 AND hash_precedent = p_prec AND epoque < v_vivant.epoque;

    -- Condition 1 : changement d'époque déclaré, reçu avant le premier lot de la nouvelle époque
    SELECT * INTO d FROM cloud_epoque_changement c WHERE c.officine_id = v_off AND c.ancienne_epoque >= v_ancien.epoque
        AND c.nouvelle_epoque <= v_vivant.epoque ORDER BY c.nouvelle_epoque LIMIT 1;
    IF NOT FOUND THEN
        RETURN jsonb_build_object('etat', 'refusee', 'detail', format('condition 1 : aucun changement d''époque déclaré après l''époque %s', v_ancien.epoque));
    END IF;
    IF NOT d.avant_premier_lot THEN
        v_echec := format('condition 1 : déclaration de l''époque %s reçue après un lot de cette époque', d.nouvelle_epoque);
    END IF;
    -- Condition 2 : chargement complet de la nouvelle époque terminé ; avant, rien n'est classé
    IF v_echec IS NULL AND NOT EXISTS (SELECT 1 FROM chargement g WHERE g.officine_id = v_off AND g.epoque >= d.nouvelle_epoque AND g.etat = 'termine') THEN
        RETURN jsonb_build_object('etat', 'attente', 'detail', format('chargement complet de l''époque %s non terminé', d.nouvelle_epoque));
    END IF;
    -- Condition 3 : rangs 1 à R communs (renvoyés à l'identique, aucune version divergente, aucune ligne absente)
    IF v_echec IS NULL AND EXISTS (SELECT 1 FROM cloud_noeud WHERE rang <= p_rang AND (source = 'divergente' OR absente)) THEN
        v_echec := format('condition 3 : une ligne de rang inférieur ou égal à %s a été renvoyée différente ou n''a pas été renvoyée', p_rang);
    END IF;
    -- Condition 4 : fin de chaîne déclarée entre R et R + lignes en collision
    SELECT (f ->> 'rang')::bigint INTO v_fin_decl FROM jsonb_array_elements(d.fins_chaine) f
        WHERE f ->> 'journal' = p_journal AND f ->> 'cle_chaine' = p_cle LIMIT 1;
    SELECT count(*) INTO v_colli FROM cloud_noeud WHERE source = 'divergente' AND rang > p_rang;
    IF v_echec IS NULL AND (v_fin_decl IS NULL OR v_fin_decl < p_rang OR v_fin_decl > p_rang + v_colli) THEN
        v_echec := format('condition 4 : fin de chaîne déclarée %s hors de [%s, %s]', coalesce(v_fin_decl::text, 'absente'), p_rang, p_rang + v_colli);
    END IF;
    -- Conditions 5 et 7 (ancienne suite) : vérifiée de bout en bout, toute reçue avant la déclaration et d'une époque antérieure
    a := cloud_integrite_suivre_branche(p_rang + 1, v_ancien.hash, p_prec, p_algo, ref.mode, d.nouvelle_epoque - 1);
    IF v_echec IS NULL AND a ->> 'erreur' IS NOT NULL THEN v_echec := 'condition 7 (ancienne suite) : ' || (a ->> 'erreur'); END IF;
    IF v_echec IS NULL AND ((a ->> 'recu_max')::timestamptz >= d.recu_le OR (a ->> 'epoque_max')::int >= d.nouvelle_epoque) THEN
        v_echec := 'condition 5 : une ligne de l''ancienne suite a été reçue après la déclaration ou dans la nouvelle époque';
    END IF;
    -- Conditions 6 et 7 (nouvelle suite) : vérifiée jusqu'à sa fin ou sa prochaine bifurcation ; lignes postérieures à la
    -- dernière réception de l'ancienne suite (tolérance d'horloge), n° hors collision au-delà du plus grand n° connu à la déclaration
    b := cloud_integrite_suivre_branche(p_rang + 1, v_vivant.hash, p_prec, p_algo, ref.mode, NULL);
    IF v_echec IS NULL AND b ->> 'erreur' IS NOT NULL AND (b ->> 'erreur') NOT LIKE 'nouvelle bifurcation%' THEN
        v_echec := 'condition 7 (nouvelle suite) : ' || (b ->> 'erreur');
    END IF;
    IF v_echec IS NULL AND EXISTS (SELECT 1 FROM cloud_noeud c WHERE c.rang > p_rang AND c.epoque >= d.nouvelle_epoque
                                     AND c.date_ligne < (a ->> 'recu_max')::timestamptz - v_tol) THEN
        v_echec := 'condition 6 : une ligne de la nouvelle suite est datée avant la dernière réception de l''ancienne (réécriture du passé)';
    END IF;
    SELECT max(id::bigint) INTO v_max_id FROM cloud_noeud WHERE recu_le < d.recu_le AND id ~ '^[0-9]+$';
    IF v_echec IS NULL AND EXISTS (SELECT 1 FROM cloud_noeud c WHERE c.source = 'miroir' AND c.rang > p_rang AND c.epoque >= d.nouvelle_epoque
                                     AND c.id ~ '^[0-9]+$' AND c.id::bigint <= coalesce(v_max_id, 0)
                                     AND NOT EXISTS (SELECT 1 FROM cloud_noeud x WHERE x.source = 'divergente' AND x.id = c.id)) THEN
        v_echec := 'condition 6 : un n° de la nouvelle suite reprend un n° déjà connu du central sans collision déclarée';
    END IF;

    v_cond := jsonb_build_object('declaration', d.id, 'declaree_le', d.recu_le, 'fin_declaree', v_fin_decl, 'collisions', v_colli,
                                 'ancienne', a, 'nouvelle', b);
    IF v_echec IS NOT NULL THEN
        RETURN jsonb_build_object('etat', 'refusee', 'detail', v_echec, 'conditions', v_cond);
    END IF;

    -- Condition 8 et S6.5-D3 : sceaux reçus avant la déclaration, ancrés dans l'ancienne suite = perdus ; classe « à justifier »
    FOR s IN SELECT x.id, x.dernier_rang FROM sceau_integrite x
             WHERE x.officine_id = v_off AND x.journal = p_journal AND x.cle_chaine = p_cle AND x.dernier_rang > p_rang
               AND x.sync_recu_le < d.recu_le
               AND EXISTS (SELECT 1 FROM cloud_noeud c WHERE c.rang = x.dernier_rang AND c.hash = x.dernier_hash AND c.epoque < d.nouvelle_epoque
                           AND c.rang <= (a ->> 'fin_rang')::bigint) LOOP
        v_perdus := v_perdus + 1;
    END LOOP;
    IF v_perdus > 0 THEN v_classe := 'restauration_a_justifier'; END IF;
    -- S6.5-D7 : plus de 2 restaurations déclarées sur 12 mois → à justifier
    IF (SELECT count(*) FROM cloud_epoque_changement c WHERE c.officine_id = v_off AND c.recu_le > now() - interval '12 months') > 2 THEN
        v_classe := 'restauration_a_justifier';
    END IF;

    INSERT INTO cloud_integrite_bifurcation (officine_id, journal, cle_chaine, ancienne_epoque, nouvelle_epoque, changement_id, rang_commun, hash_commun,
        fin_ancienne_rang, fin_ancienne_id, fin_ancienne_hash, lignes_ancienne, premiere_date, derniere_date, montant_ttc, sceaux_perdus, classe, conditions)
    VALUES (v_off, p_journal, p_cle, v_ancien.epoque, d.nouvelle_epoque, d.id, p_rang, p_prec,
        (a ->> 'fin_rang')::bigint, a ->> 'fin_id', decode(a ->> 'fin_hash', 'hex'), (a ->> 'lignes')::bigint,
        (a ->> 'debut')::timestamptz, (a ->> 'fin')::timestamptz, CASE WHEN p_journal = 'journal_vente' THEN (a ->> 'montant')::numeric END,
        v_perdus, v_classe, v_cond)
    ON CONFLICT DO NOTHING;
    SELECT id INTO v_bif FROM cloud_integrite_bifurcation WHERE officine_id = v_off AND journal = p_journal AND cle_chaine = p_cle
        AND nouvelle_epoque = d.nouvelle_epoque;

    -- Une anomalie par bifurcation (le détail est dans cloud_integrite_bifurcation), une par sceau perdu
    PERFORM cloud_integrite_noter(p_journal, p_cle, p_rang + 1, format('époque %s → %s', v_ancien.epoque, d.nouvelle_epoque),
        'remplacee_apres_restauration',
        format('%s ligne(s) des rangs %s à %s (du %s au %s) remplacées après restauration%s ; classe : %s', a ->> 'lignes', p_rang + 1, a ->> 'fin_rang',
               to_char((a ->> 'debut')::timestamptz AT TIME ZONE 'UTC', 'YYYY-MM-DD'), to_char((a ->> 'fin')::timestamptz AT TIME ZONE 'UTC', 'YYYY-MM-DD'),
               CASE WHEN p_journal = 'journal_vente' THEN format(', montant TTC %s', a ->> 'montant') ELSE '' END,
               CASE v_classe WHEN 'restauration_constatee' THEN 'remplacée après restauration' ELSE 'restauration à justifier' END));
    FOR s IN SELECT x.id, x.dernier_rang FROM sceau_integrite x
             WHERE x.officine_id = v_off AND x.journal = p_journal AND x.cle_chaine = p_cle AND x.dernier_rang > p_rang
               AND x.dernier_rang <= (a ->> 'fin_rang')::bigint AND x.sync_recu_le < d.recu_le LOOP
        PERFORM cloud_integrite_noter(p_journal, p_cle, s.dernier_rang, 'sceau ' || s.id, 'sceau_perdu_restauration',
            format('sceau n° %s : ligne scellée (rang %s) dans la suite remplacée après restauration', s.id, s.dernier_rang));
    END LOOP;
    -- S6.5-D5 : anomalies déjà notées sur la suite remplacée (rangs > R), rattachées sans être modifiées
    INSERT INTO cloud_anomalie_classement (officine_id, anomalie_id, bifurcation_id, classe)
    SELECT v_off, x.id, v_bif, CASE WHEN v_classe = 'restauration_constatee' THEN 'remplacee_apres_restauration' ELSE 'restauration_a_justifier' END
    FROM cloud_anomalie_integrite x
    WHERE x.officine_id = v_off AND x.journal = p_journal AND coalesce(x.cle_chaine, p_cle) = p_cle AND x.rang > p_rang
      AND x.nature IN ('chainage_rompu', 'ligne_manquante', 'modification_refusee', 'sceau_incoherent', 'sceau_sans_ligne',
                       'reecriture_apres_envoi', 'chaine_officine_plus_courte')
      AND x.detectee_le >= (a ->> 'recu_max')::timestamptz
    ON CONFLICT DO NOTHING;
    RETURN jsonb_build_object('etat', 'reconnue', 'bifurcation_id', v_bif, 'classe', v_classe);
END $$;

-- ---- 5. Vérification par liens d'empreinte d'une chaîne (officine courante), toujours depuis le rang 1 ----
-- Essai S6.5.5 (2026-11-19) : ligne d'une ancienne branche déjà reconnue (au-delà du rang commun, jusqu'à la fin de l'ancienne branche,
-- reçue avant la nouvelle époque) alors que la suite vivante est celle de cette nouvelle époque ou d'une plus récente. Une nouvelle
-- branche plus courte que l'ancienne (restauration suivie de peu de ventes) faisait noter l'ancienne ligne suivante en chaînage rompu.
CREATE OR REPLACE FUNCTION cloud_integrite_ancienne_branche(p_journal text, p_cle text, p_rang bigint, p_epoque int, p_epoque_vivante int)
RETURNS boolean LANGUAGE sql STABLE AS $$
    SELECT EXISTS (SELECT 1 FROM cloud_integrite_bifurcation b
                   WHERE b.officine_id = cloud_officine_courante() AND b.journal = p_journal AND b.cle_chaine = p_cle
                     AND p_rang > b.rang_commun AND p_rang <= coalesce(b.fin_ancienne_rang, p_rang)
                     AND p_epoque < b.nouvelle_epoque AND b.nouvelle_epoque <= coalesce(p_epoque_vivante, 0))
$$;

CREATE OR REPLACE FUNCTION cloud_integrite_verifier_branches(p_journal text, p_cle text, p_delai interval) RETURNS text
LANGUAGE plpgsql AS $$
DECLARE
    v_off     uuid := cloud_officine_courante();
    ref       cloud_chaine_reference;
    v_rang    bigint := 0;
    v_prec    bytea;
    v_algo    text;
    v_nv      bigint := 0;
    v_statut  text := 'ok';
    v_rupture bigint;
    v_ep      int;
    v_cands   int;
    n         record;
    l         record;
    v_res     jsonb;
    v_calc    bytea;
BEGIN
    SELECT * INTO ref FROM cloud_chaine_reference WHERE journal = p_journal;
    PERFORM cloud_integrite_charger_noeuds(p_journal, p_cle);
    SELECT * INTO l FROM cloud_noeud WHERE rang = 1 ORDER BY epoque LIMIT 1;
    IF FOUND THEN
        v_algo := CASE length(l.hash) WHEN 32 THEN 'sha256' WHEN 64 THEN 'sha512' END;
        IF v_algo IS NULL THEN
            PERFORM cloud_integrite_noter(p_journal, p_cle, 1, l.id, 'algorithme_inconnu', format('empreinte de %s octets : algorithme inconnu', coalesce(length(l.hash), 0)));
            v_statut := 'rupture'; v_rupture := 1;
        ELSE
            v_prec := decode(repeat('00', length(l.hash)), 'hex');
        END IF;
    END IF;
    WHILE v_statut = 'ok' LOOP
        SELECT count(*) INTO v_cands FROM cloud_noeud WHERE rang = v_rang + 1 AND hash_precedent = v_prec;
        IF v_cands = 0 THEN
            SELECT * INTO l FROM cloud_noeud x WHERE x.rang = v_rang + 1
                AND NOT cloud_integrite_ancienne_branche(p_journal, p_cle, x.rang, x.epoque, v_ep) ORDER BY x.epoque DESC LIMIT 1;
            IF FOUND THEN
                PERFORM cloud_integrite_noter(p_journal, p_cle, l.rang, l.id, 'chainage_rompu', 'empreinte précédente différente de celle de la ligne d''avant',
                    encode(v_prec, 'hex'), coalesce(encode(l.hash_precedent, 'hex'), 'vide'));
                v_statut := 'rupture'; v_rupture := l.rang;
            ELSE
                SELECT * INTO l FROM cloud_noeud x WHERE x.rang > v_rang + 1
                    AND NOT cloud_integrite_ancienne_branche(p_journal, p_cle, x.rang, x.epoque, v_ep) ORDER BY x.rang, x.recu_le LIMIT 1;
                IF FOUND THEN
                    IF l.recu_le < now() - p_delai THEN
                        PERFORM cloud_integrite_noter(p_journal, p_cle, v_rang + 1, NULL, 'ligne_manquante',
                            format('rang %s jamais reçu (rang %s reçu le %s)', v_rang + 1, l.rang, to_char(l.recu_le AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI "UTC"')));
                        v_statut := 'rupture'; v_rupture := v_rang + 1;
                    ELSE
                        v_statut := 'attente';
                    END IF;
                END IF;
            END IF;
            EXIT;
        END IF;
        IF v_cands > 1 THEN
            v_res := cloud_integrite_constater_bifurcation(p_journal, p_cle, v_rang, v_prec, v_algo);
            IF v_res ->> 'etat' = 'attente' THEN v_statut := 'attente'; EXIT; END IF;
            IF v_res ->> 'etat' = 'refusee' THEN
                SELECT * INTO l FROM cloud_noeud WHERE rang = v_rang + 1 AND hash_precedent = v_prec ORDER BY epoque DESC LIMIT 1;
                PERFORM cloud_integrite_noter(p_journal, p_cle, v_rang + 1, l.id || '@' || l.epoque, 'chainage_rompu',
                    left('deux suites au rang ' || (v_rang + 1) || ', non reconnues comme une restauration : ' || (v_res ->> 'detail'), 500));
                v_statut := 'rupture'; v_rupture := v_rang + 1;
                EXIT;
            END IF;
            -- Restauration reconnue : la vérification continue sur la suite vivante (version d'époque la plus récente)
            SELECT * INTO n FROM cloud_noeud WHERE rang = v_rang + 1 AND hash_precedent = v_prec ORDER BY epoque DESC, (source = 'divergente') DESC LIMIT 1;
        ELSE
            SELECT * INTO n FROM cloud_noeud WHERE rang = v_rang + 1 AND hash_precedent = v_prec;
        END IF;
        IF ref.mode = 'complet' THEN
            v_calc := cloud_integrite_empreinte(n.canonique, v_prec, v_algo);
            IF v_calc IS DISTINCT FROM n.hash THEN
                IF n.marque THEN
                    v_nv := v_nv + 1;
                ELSE
                    PERFORM cloud_integrite_noter(p_journal, p_cle, n.rang, n.id, 'ligne_modifiee', 'empreinte recalculée différente de l''empreinte reçue',
                        encode(v_calc, 'hex'), coalesce(encode(n.hash, 'hex'), 'vide'));
                    v_statut := 'rupture'; v_rupture := n.rang; EXIT;
                END IF;
            END IF;
        END IF;
        v_prec := n.hash; v_rang := n.rang; v_ep := n.epoque;
    END LOOP;
    INSERT INTO cloud_integrite_etat AS x (officine_id, journal, cle_chaine, algorithme, dernier_rang, dernier_hash, statut, rang_rupture,
                                          non_verifiables, verifie_le, verification_complete_le, epoque_branche)
    VALUES (v_off, p_journal, p_cle, v_algo, v_rang, v_prec, v_statut, v_rupture, v_nv, now(), now(), v_ep)
    ON CONFLICT (officine_id, journal, cle_chaine) DO UPDATE SET algorithme = EXCLUDED.algorithme, dernier_rang = EXCLUDED.dernier_rang,
        dernier_hash = EXCLUDED.dernier_hash, statut = EXCLUDED.statut, rang_rupture = EXCLUDED.rang_rupture,
        non_verifiables = EXCLUDED.non_verifiables, verifie_le = EXCLUDED.verifie_le,
        verification_complete_le = EXCLUDED.verification_complete_le, epoque_branche = EXCLUDED.epoque_branche;
    RETURN v_statut;
END $$;

-- ---- 6. Aiguillage : sans changement d'époque déclaré ni deux versions d'un même rang, vérification d'origine (script 012) ----
CREATE OR REPLACE FUNCTION cloud_integrite_verifier_chaine(p_journal text, p_cle text, p_complete boolean, p_delai interval) RETURNS text
LANGUAGE plpgsql AS $$
DECLARE v_off uuid := cloud_officine_courante(); v_double boolean;
BEGIN
    IF EXISTS (SELECT 1 FROM cloud_epoque_changement WHERE officine_id = v_off) THEN
        v_double := EXISTS (SELECT 1 FROM cloud_ligne_divergente WHERE officine_id = v_off AND nom_table = p_journal AND cle_chaine = p_cle);
        IF NOT v_double THEN
            EXECUTE format('SELECT EXISTS (SELECT 1 FROM public.%I WHERE officine_id = $1 AND cle_chaine = $2 GROUP BY rang HAVING count(*) > 1)', p_journal)
                INTO v_double USING v_off, p_cle;
        END IF;
        IF v_double THEN RETURN cloud_integrite_verifier_branches(p_journal, p_cle, p_delai); END IF;
    END IF;
    RETURN cloud_integrite_verifier_chaine_lineaire(p_journal, p_cle, p_complete, p_delai);
END $$;

-- ---- 7. Ligne « vivante » d'un rang (suite la plus récente) et recherche d'une ligne par (rang, n°, empreinte) ----
-- Utilisées par les sceaux et la vérification croisée : jamais par le rang seul, qui peut porter deux lignes après une restauration.
CREATE OR REPLACE FUNCTION cloud_integrite_ligne_vivante(p_journal text, p_cle text, p_rang bigint)
RETURNS TABLE (id text, hash bytea, date_ligne timestamptz)
LANGUAGE plpgsql AS $$
DECLARE v_off uuid := cloud_officine_courante(); ref cloud_chaine_reference;
BEGIN
    SELECT * INTO ref FROM cloud_chaine_reference r WHERE r.journal = p_journal;
    RETURN QUERY EXECUTE format(
        'SELECT x.id, x.hash, x.date_ligne FROM (
             SELECT r.id::text AS id, r.hash, r.%1$I::timestamptz AS date_ligne, r.sync_epoque AS epoque, (r.sync_absente_le IS NULL) AS presente, 0 AS ordre
             FROM public.%2$I r WHERE r.officine_id = $1 AND r.cle_chaine = $2 AND r.rang = $3
             UNION ALL
             SELECT r.id::text, r.hash, r.%1$I::timestamptz, d.epoque, true, 1
             FROM cloud_ligne_divergente d CROSS JOIN LATERAL jsonb_populate_record(NULL::public.%2$I, d.ligne) r
             WHERE d.officine_id = $1 AND d.nom_table = %3$L AND d.cle_chaine = $2 AND d.rang = $3) x
         ORDER BY x.presente DESC, x.epoque DESC, x.ordre DESC LIMIT 1',
        ref.colonne_date, p_journal, p_journal) USING v_off, p_cle, p_rang;
END $$;

CREATE OR REPLACE FUNCTION cloud_integrite_ligne_existe(p_journal text, p_cle text, p_rang bigint, p_id bigint, p_hash bytea) RETURNS boolean
LANGUAGE plpgsql AS $$
DECLARE v_off uuid := cloud_officine_courante(); v boolean;
BEGIN
    EXECUTE format(
        'SELECT EXISTS (SELECT 1 FROM public.%I r WHERE r.officine_id = $1 AND r.cle_chaine = $2 AND r.rang = $3 AND r.hash = $5 AND ($4 IS NULL OR r.id = $4))
             OR EXISTS (SELECT 1 FROM cloud_ligne_divergente d CROSS JOIN LATERAL jsonb_populate_record(NULL::public.%I, d.ligne) r
                        WHERE d.officine_id = $1 AND d.nom_table = %L AND d.cle_chaine = $2 AND d.rang = $3 AND r.hash = $5 AND ($4 IS NULL OR r.id = $4))',
        p_journal, p_journal, p_journal) INTO v USING v_off, p_cle, p_rang, p_id, p_hash;
    RETURN v;
END $$;

-- ---- 8. Ancrages des sceaux (remplace la version de 012) : ligne cherchée par (rang, n°, empreinte) dans les deux suites ----
CREATE OR REPLACE FUNCTION cloud_integrite_verifier_sceaux(p_complete boolean, p_delai interval) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    v_off    uuid := cloud_officine_courante();
    s        record;
    v_statut text;
    v_vivant record;
BEGIN
    FOR s IN SELECT x.id, x.journal, x.cle_chaine, x.dernier_rang, x.dernier_id, x.dernier_hash, x.sync_recu_le FROM sceau_integrite x
             WHERE x.officine_id = v_off
               AND (p_complete OR NOT EXISTS (SELECT 1 FROM cloud_integrite_sceau c WHERE c.officine_id = v_off AND c.sceau_id = x.id AND c.statut <> 'attente'))
             ORDER BY x.id LOOP
        IF NOT EXISTS (SELECT 1 FROM cloud_chaine_reference WHERE journal = s.journal) THEN
            v_statut := 'non_verifiable';
        ELSIF s.dernier_rang = 0 THEN
            v_statut := 'ok';   -- journal vide au moment du sceau
        ELSIF cloud_integrite_ligne_existe(s.journal, s.cle_chaine, s.dernier_rang, s.dernier_id, s.dernier_hash) THEN
            v_statut := 'ok';   -- ligne scellée reçue telle quelle (suite vivante ou suite remplacée après restauration)
        ELSE
            SELECT * INTO v_vivant FROM cloud_integrite_ligne_vivante(s.journal, s.cle_chaine, s.dernier_rang);
            IF v_vivant.hash IS NULL THEN
                IF s.sync_recu_le < now() - p_delai THEN
                    PERFORM cloud_integrite_noter(s.journal, s.cle_chaine, s.dernier_rang, 'sceau ' || s.id, 'sceau_sans_ligne',
                        format('sceau n° %s : ligne scellée (rang %s) jamais reçue', s.id, s.dernier_rang));
                    v_statut := 'incoherent';
                ELSE
                    v_statut := 'attente';
                END IF;
            ELSE
                PERFORM cloud_integrite_noter(s.journal, s.cle_chaine, s.dernier_rang, 'sceau ' || s.id, 'sceau_incoherent',
                    format('sceau n° %s : la ligne scellée reçue ne correspond pas au sceau', s.id),
                    encode(s.dernier_hash, 'hex'), encode(v_vivant.hash, 'hex'));
                v_statut := 'incoherent';
            END IF;
        END IF;
        INSERT INTO cloud_integrite_sceau (officine_id, sceau_id, statut) VALUES (v_off, s.id, v_statut)
        ON CONFLICT (officine_id, sceau_id) DO UPDATE SET statut = EXCLUDED.statut, verifie_le = now();
        v_vivant := NULL;
    END LOOP;
END $$;

-- ---- 9. Vérification croisée (remplace la version de 012) : lignes cherchées dans la suite vivante ----
CREATE OR REPLACE FUNCTION cloud_integrite_verifier_croisee(p_complete boolean, p_delai interval) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    v_off   uuid := cloud_officine_courante();
    o       cloud_integrite_officine;
    v       record;
    ref     cloud_chaine_reference;
    w       record;
    x       record;
    v_point bigint;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass('public.verification_integrite') AND attname = 'dernier_hash' AND NOT attisdropped) THEN
        RETURN;   -- tables miroir pas encore régénérées après le script officine 144
    END IF;
    SELECT * INTO o FROM cloud_integrite_officine WHERE officine_id = v_off FOR UPDATE;
    v_point := CASE WHEN p_complete THEN 0 ELSE coalesce(o.derniere_verification_id, 0) END;
    FOR v IN EXECUTE
        'SELECT x.id, x.journal, x.cle_chaine, x.statut, x.verifie_le, x.dernier_rang, x.dernier_hash, x.sync_recu_le FROM public.verification_integrite x
         WHERE x.officine_id = $1 AND x.id > $2 ORDER BY x.id'
        USING v_off, v_point
    LOOP
        SELECT * INTO ref FROM cloud_chaine_reference WHERE journal = v.journal;
        IF NOT FOUND OR v.cle_chaine IS NULL OR v.statut IS DISTINCT FROM 'ok' OR v.dernier_rang IS NULL THEN v_point := v.id; CONTINUE; END IF;
        IF v.dernier_rang > 0 AND NOT cloud_integrite_ligne_existe(v.journal, v.cle_chaine, v.dernier_rang, NULL, v.dernier_hash) THEN
            SELECT * INTO w FROM cloud_integrite_ligne_vivante(v.journal, v.cle_chaine, v.dernier_rang);
            IF w.hash IS NULL THEN
                IF v.sync_recu_le < now() - p_delai THEN
                    PERFORM cloud_integrite_noter(v.journal, v.cle_chaine, v.dernier_rang, NULL, 'ligne_manquante',
                        format('rang %s vérifié par l''officine le %s, jamais reçu', v.dernier_rang, to_char(v.verifie_le AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI "UTC"')));
                ELSE
                    EXIT;
                END IF;
            ELSE
                PERFORM cloud_integrite_noter(v.journal, v.cle_chaine, v.dernier_rang, NULL, 'reecriture_apres_envoi',
                    format('empreinte du rang %s dans l''officine (vérification du %s) différente de celle reçue', v.dernier_rang,
                           to_char(v.verifie_le AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI "UTC"')),
                    encode(w.hash, 'hex'), encode(v.dernier_hash, 'hex'));
            END IF;
        END IF;
        -- Ligne de la suite VIVANTE au-delà de la fin de chaîne de l'officine, enregistrée avant sa vérification : lignes perdues
        -- (une ligne de la suite remplacée après restauration, absente du dernier chargement, n'est pas prise ici)
        SELECT * INTO x FROM cloud_integrite_ligne_vivante(v.journal, v.cle_chaine, v.dernier_rang + 1);
        IF x.id IS NOT NULL AND x.date_ligne < v.verifie_le - interval '1 minute'
           AND NOT EXISTS (SELECT 1 FROM cloud_integrite_bifurcation b WHERE b.officine_id = v_off AND b.journal = v.journal
                           AND b.cle_chaine = v.cle_chaine AND v.dernier_rang + 1 BETWEEN b.rang_commun + 1 AND coalesce(b.fin_ancienne_rang, b.rang_commun)) THEN
            PERFORM cloud_integrite_noter(v.journal, v.cle_chaine, v.dernier_rang + 1, NULL, 'chaine_officine_plus_courte',
                format('l''officine s''arrête au rang %s le %s alors que le rang %s, enregistré avant, a été reçu', v.dernier_rang,
                       to_char(v.verifie_le AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI "UTC"'), v.dernier_rang + 1));
        END IF;
        w := NULL; x := NULL;
        v_point := v.id;
    END LOOP;
    INSERT INTO cloud_integrite_officine AS z (officine_id, derniere_verification_id) VALUES (v_off, v_point)
    ON CONFLICT (officine_id) DO UPDATE SET derniere_verification_id = EXCLUDED.derniere_verification_id;
END $$;

-- ---- 10. Prise d'acte du titulaire (S6.5-D4) : table montante de l'officine, présente au central après régénération du 002 ----
CREATE OR REPLACE FUNCTION cloud_integrite_prise_acte(p_epoque int) RETURNS boolean
LANGUAGE plpgsql STABLE AS $$
DECLARE v boolean;
BEGIN
    IF to_regclass('public.integrite_prise_acte') IS NULL THEN RETURN false; END IF;
    EXECUTE 'SELECT EXISTS (SELECT 1 FROM public.integrite_prise_acte p WHERE p.officine_id = $1 AND p.nouvelle_epoque = $2)'
        INTO v USING cloud_officine_courante(), p_epoque;
    RETURN v;
END $$;

-- ---- 11. Passage complet pour l'officine courante (remplace la version de 012) ----
-- Résumé integrite-v1 étendu (compatible : une officine non mise à jour ignore les champs nouveaux) :
--   * restaurations : bifurcations constatées (journal, époques, rang commun, lignes, période, montant, sceaux perdus, classe, prise d'acte) ;
--   * anomalies_classees : anomalies rattachées à une restauration (S6.5-D5), chaque anomalie de la liste porte « classee » ;
--   * statut (S6.5-D4) : « anomalie » s'il reste une anomalie NON rattachée ou une chaîne en rupture ; sinon « restauration » tant
--     qu'une restauration n'a pas fait l'objet d'une prise d'acte du titulaire ; sinon « attente » ou « ok » comme avant.
CREATE OR REPLACE FUNCTION cloud_verifier_integrite(p_complete boolean DEFAULT false) RETURNS jsonb
LANGUAGE plpgsql AS $$
DECLARE
    v_off      uuid := cloud_officine_courante();
    v_delai    interval;
    v_avant    bigint;
    r          record;
    v_resume   jsonb;
    v_statut   text;
    v_chaines  jsonb;
    v_sceaux   jsonb;
    v_anom     jsonb;
    v_rest     jsonb;
    v_total    bigint;
    v_classees bigint;
    v_vraies   bigint;
    v_dernier  bigint;
    v_nouv     bigint;
BEGIN
    IF v_off IS NULL THEN RAISE EXCEPTION 'Officine courante non posée (app.officine_id)'; END IF;
    SELECT make_interval(hours => integrite_delai_heures) INTO v_delai FROM cloud_parametre WHERE id = 1;
    v_delai := coalesce(v_delai, interval '24 hours');
    SELECT coalesce(max(id), 0) INTO v_avant FROM cloud_anomalie_integrite WHERE officine_id = v_off;

    FOR r IN SELECT ref.journal FROM cloud_chaine_reference ref ORDER BY ref.journal LOOP
        IF to_regclass('public.' || r.journal) IS NULL THEN CONTINUE; END IF;
        FOR v_statut IN EXECUTE format('SELECT DISTINCT cle_chaine FROM public.%I WHERE officine_id = $1 AND cle_chaine IS NOT NULL ORDER BY 1', r.journal) USING v_off LOOP
            PERFORM cloud_integrite_verifier_chaine(r.journal, v_statut, p_complete, v_delai);
        END LOOP;
    END LOOP;
    PERFORM cloud_integrite_verifier_sceaux(p_complete, v_delai);
    PERFORM cloud_integrite_verifier_tickets(p_complete, v_delai);
    PERFORM cloud_integrite_verifier_croisee(p_complete, v_delai);

    INSERT INTO cloud_integrite_officine AS x (officine_id, verifie_le, verification_complete_le)
    VALUES (v_off, now(), CASE WHEN p_complete THEN now() END)
    ON CONFLICT (officine_id) DO UPDATE SET verifie_le = EXCLUDED.verifie_le,
        verification_complete_le = coalesce(EXCLUDED.verification_complete_le, x.verification_complete_le);

    SELECT coalesce(jsonb_agg(jsonb_build_object('journal', e.journal, 'cle', e.cle_chaine, 'mode', ref.mode, 'statut', e.statut,
                                                 'dernier_rang', e.dernier_rang, 'non_verifiables', e.non_verifiables,
                                                 'epoque_branche', e.epoque_branche) ORDER BY e.journal, e.cle_chaine), '[]'::jsonb)
      INTO v_chaines FROM cloud_integrite_etat e JOIN cloud_chaine_reference ref ON ref.journal = e.journal WHERE e.officine_id = v_off;
    SELECT jsonb_build_object('ok', count(*) FILTER (WHERE statut = 'ok'), 'attente', count(*) FILTER (WHERE statut = 'attente'),
                              'incoherents', count(*) FILTER (WHERE statut = 'incoherent'), 'non_verifiables', count(*) FILTER (WHERE statut = 'non_verifiable'))
      INTO v_sceaux FROM cloud_integrite_sceau WHERE officine_id = v_off;
    SELECT count(*), coalesce(max(id), 0) INTO v_total, v_dernier FROM cloud_anomalie_integrite WHERE officine_id = v_off;
    SELECT count(*) INTO v_classees FROM cloud_anomalie_integrite a WHERE a.officine_id = v_off
        AND (a.nature IN ('remplacee_apres_restauration', 'sceau_perdu_restauration', 'ticket_reattribue_restauration')
             OR EXISTS (SELECT 1 FROM cloud_anomalie_classement c WHERE c.officine_id = v_off AND c.anomalie_id = a.id));
    v_vraies := v_total - v_classees;
    SELECT coalesce(jsonb_agg(jsonb_build_object('id', a.id, 'nature', a.nature, 'journal', a.journal, 'rang', a.rang, 'ligne', a.cle_ligne,
                                                 'detail', a.detail, 'detectee_le', a.detectee_le,
                                                 'classee', a.nature IN ('remplacee_apres_restauration', 'sceau_perdu_restauration', 'ticket_reattribue_restauration')
                                                            OR EXISTS (SELECT 1 FROM cloud_anomalie_classement c WHERE c.officine_id = v_off AND c.anomalie_id = a.id))
                              ORDER BY a.id DESC), '[]'::jsonb)
      INTO v_anom FROM (SELECT * FROM cloud_anomalie_integrite WHERE officine_id = v_off ORDER BY id DESC LIMIT 20) a;
    SELECT coalesce(jsonb_agg(jsonb_build_object('id', b.id, 'journal', b.journal, 'cle', b.cle_chaine, 'ancienne_epoque', b.ancienne_epoque,
                                                 'nouvelle_epoque', b.nouvelle_epoque, 'rang_commun', b.rang_commun, 'fin_ancienne_rang', b.fin_ancienne_rang,
                                                 'lignes', b.lignes_ancienne, 'du', b.premiere_date, 'au', b.derniere_date, 'montant_ttc', b.montant_ttc,
                                                 'sceaux_perdus', b.sceaux_perdus, 'classe', b.classe, 'constatee_le', b.constatee_le,
                                                 'prise_acte', cloud_integrite_prise_acte(b.nouvelle_epoque)) ORDER BY b.id), '[]'::jsonb)
      INTO v_rest FROM cloud_integrite_bifurcation b WHERE b.officine_id = v_off;
    v_nouv := (SELECT count(*) FROM cloud_anomalie_integrite WHERE officine_id = v_off AND id > v_avant);
    v_statut := CASE WHEN v_vraies > 0 OR EXISTS (SELECT 1 FROM cloud_integrite_etat WHERE officine_id = v_off AND statut = 'rupture') THEN 'anomalie'
                     WHEN EXISTS (SELECT 1 FROM jsonb_array_elements(v_rest) x WHERE NOT (x ->> 'prise_acte')::boolean) THEN 'restauration'
                     WHEN EXISTS (SELECT 1 FROM cloud_integrite_etat WHERE officine_id = v_off AND statut = 'attente')
                          OR (v_sceaux ->> 'attente')::bigint > 0 THEN 'attente'
                     ELSE 'ok' END;
    v_resume := jsonb_build_object(
        'format', 'integrite-v1',
        'statut', v_statut,
        'verifie_le', (SELECT verifie_le FROM cloud_integrite_officine WHERE officine_id = v_off),
        'complete_le', (SELECT verification_complete_le FROM cloud_integrite_officine WHERE officine_id = v_off),
        'chaines', v_chaines,
        'sceaux', v_sceaux,
        'tickets_verifies_jusqu_a', (SELECT tickets_jusqu_a FROM cloud_integrite_officine WHERE officine_id = v_off),
        'anomalies_total', v_total,
        'anomalies_classees', v_classees,
        'derniere_anomalie_id', v_dernier,
        'anomalies', v_anom,
        'restaurations', v_rest);
    PERFORM cloud_publier_descendant('reglage_central', 'integrite_centrale', v_resume);
    RETURN v_resume || jsonb_build_object('nouvelles', v_nouv);
END $$;

-- Restaurations constatées de l'officine courante (console --restaurations-integrite, export « restauration »)
CREATE OR REPLACE FUNCTION cloud_restaurations_integrite() RETURNS SETOF cloud_integrite_bifurcation
LANGUAGE sql STABLE AS $$
    SELECT * FROM cloud_integrite_bifurcation WHERE officine_id = cloud_officine_courante() ORDER BY id
$$;

-- Droits : vérification et lecture réservées au propriétaire (outil d'administration)
REVOKE EXECUTE ON FUNCTION cloud_integrite_verifier_chaine(text, text, boolean, interval), cloud_integrite_verifier_chaine_lineaire(text, text, boolean, interval),
    cloud_integrite_verifier_branches(text, text, interval), cloud_integrite_constater_bifurcation(text, text, bigint, bytea, text),
    cloud_integrite_suivre_branche(bigint, bytea, bytea, text, text, int), cloud_integrite_charger_noeuds(text, text),
    cloud_integrite_ligne_vivante(text, text, bigint), cloud_integrite_ligne_existe(text, text, bigint, bigint, bytea),
    cloud_integrite_verifier_sceaux(boolean, interval), cloud_integrite_verifier_croisee(boolean, interval), cloud_verifier_integrite(boolean),
    cloud_integrite_prise_acte(int), cloud_restaurations_integrite() FROM PUBLIC;

COMMENT ON FUNCTION cloud_integrite_verifier_branches(text, text, interval) IS 'Vérification par liens d''empreinte, bifurcations constatées (script 023, § 31).';
