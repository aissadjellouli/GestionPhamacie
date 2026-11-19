-- =====================================================================
-- Base centrale : n° de tickets réattribués après une restauration
-- (2026-11-19, choix « A + B » du titulaire ; docs/conception-synchronisation.md
-- § 31.10). Script de la base CENTRALE, à passer après 023 et 024 par le
-- propriétaire, AVANT le script officine 152.
-- Les ventes faites à l'officine entre la restauration et la détection
-- reprennent des n° de tickets déjà reçus ici (l'officine n'avance son
-- compteur qu'à la détection ; le script officine 152 réduit cette fenêtre
-- au démarrage). Ici :
--   * cloud_integrite_tickets_reattribues() : pour chaque restauration
--     constatée sur le journal des ventes, n° de ticket d'une vente de
--     l'ancienne suite (reçue avant la nouvelle époque, après le rang
--     commun) repris par une vente de la nouvelle suite : anomalie
--     « ticket_reattribue_restauration » (une par vente de la nouvelle
--     suite, n° dans « attendu »), RATTACHÉE à la restauration
--     (cloud_anomalie_classement), classée comme elle. Jamais en double.
--   * cloud_verifier_integrite : l'appelle, et chaque restauration du
--     résumé integrite-v1 porte « tickets_reattribues » (liste des n°).
-- Garde de rejeu : 023 ne recrée plus cloud_verifier_integrite si
-- cloud_integrite_tickets_reattribues existe.
-- Idempotent. Aucune donnée existante modifiée.
-- =====================================================================

SET client_encoding = 'UTF8';

DO $prerequis$ BEGIN
    IF to_regprocedure('public.cloud_integrite_verifier_branches(text, text, interval)') IS NULL THEN
        RAISE EXCEPTION 'Script central 025 : passer d''abord le script 023. Rien n''a été modifié.';
    END IF;
END $prerequis$;

-- ---- 1. N° de tickets repris par la nouvelle suite ----
CREATE OR REPLACE FUNCTION cloud_integrite_tickets_reattribues() RETURNS int
LANGUAGE plpgsql AS $$
DECLARE
    v_off    uuid := cloud_officine_courante();
    b        cloud_integrite_bifurcation;
    t        record;
    v_id     bigint;
    n        int := 0;
BEGIN
    IF v_off IS NULL THEN RAISE EXCEPTION 'Officine courante non posée (app.officine_id)'; END IF;
    IF to_regclass('public.journal_vente') IS NULL THEN RETURN 0; END IF;
    FOR b IN SELECT * FROM cloud_integrite_bifurcation WHERE officine_id = v_off AND journal = 'journal_vente' ORDER BY id LOOP
        FOR t IN
            WITH lignes AS (
                SELECT r.id::text AS id, r.rang, r.numero_ticket, r.sync_epoque AS epoque, r.survenu_le
                  FROM public.journal_vente r
                 WHERE r.officine_id = v_off AND r.cle_chaine = b.cle_chaine AND r.rang > b.rang_commun
                   AND r.evenement = 'vente' AND r.numero_ticket IS NOT NULL
                UNION ALL
                SELECT d.ligne ->> 'id', (d.ligne ->> 'rang')::bigint, d.ligne ->> 'numero_ticket', d.epoque, (d.ligne ->> 'survenu_le')::timestamptz
                  FROM cloud_ligne_divergente d
                 WHERE d.officine_id = v_off AND d.nom_table = 'journal_vente' AND d.cle_chaine = b.cle_chaine
                   AND (d.ligne ->> 'rang')::bigint > b.rang_commun
                   AND d.ligne ->> 'evenement' = 'vente' AND d.ligne ->> 'numero_ticket' IS NOT NULL)
            SELECT nv.id, nv.rang, nv.numero_ticket, nv.survenu_le,
                   min(an.rang) AS rang_ancien, min(an.survenu_le) AS le_ancien
              FROM lignes nv
              JOIN lignes an ON an.numero_ticket = nv.numero_ticket AND an.epoque <= b.ancienne_epoque
             WHERE nv.epoque >= b.nouvelle_epoque
             GROUP BY nv.id, nv.rang, nv.numero_ticket, nv.survenu_le
             ORDER BY nv.rang, nv.id
        LOOP
            IF cloud_integrite_noter('journal_vente', b.cle_chaine, t.rang, t.id, 'ticket_reattribue_restauration',
                    format('n° %s repris par une vente du %s (nouvelle époque %s) ; déjà attribué à une vente du %s (rang %s, époque %s), reçue avant la restauration',
                           t.numero_ticket, to_char(t.survenu_le, 'YYYY-MM-DD HH24:MI'), b.nouvelle_epoque,
                           to_char(t.le_ancien, 'YYYY-MM-DD HH24:MI'), t.rang_ancien, b.ancienne_epoque),
                    t.numero_ticket, t.id) THEN
                n := n + 1;
            END IF;
            -- Rattachement à la restauration (anomalie notée à ce passage ou à un passage précédent sans rattachement)
            SELECT a.id INTO v_id FROM cloud_anomalie_integrite a
             WHERE a.officine_id = v_off AND a.journal = 'journal_vente' AND a.nature = 'ticket_reattribue_restauration'
               AND a.cle_chaine = b.cle_chaine AND a.rang = t.rang AND a.cle_ligne = t.id;
            IF v_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM cloud_anomalie_classement c WHERE c.officine_id = v_off AND c.anomalie_id = v_id) THEN
                INSERT INTO cloud_anomalie_classement (officine_id, anomalie_id, bifurcation_id, classe)
                VALUES (v_off, v_id, b.id, CASE WHEN b.classe = 'restauration_constatee' THEN 'remplacee_apres_restauration' ELSE 'restauration_a_justifier' END);
            END IF;
        END LOOP;
    END LOOP;
    RETURN n;
END $$;
COMMENT ON FUNCTION cloud_integrite_tickets_reattribues() IS 'N° de tickets de l''ancienne suite repris par la nouvelle après une restauration : anomalie ticket_reattribue_restauration rattachée à la restauration (script 025).';

-- ---- 2. Vérification et résumé (remplace la définition du script 023) ----
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
    -- Script 025 : n° de tickets repris par les ventes faites avant la détection, signalés et rattachés à la restauration
    PERFORM cloud_integrite_tickets_reattribues();

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
                                                 'prise_acte', cloud_integrite_prise_acte(b.nouvelle_epoque),
                                                 'tickets_reattribues', (SELECT coalesce(jsonb_agg(a.attendu ORDER BY a.attendu), '[]'::jsonb)
                                                     FROM cloud_anomalie_classement c JOIN cloud_anomalie_integrite a
                                                       ON a.officine_id = c.officine_id AND a.id = c.anomalie_id AND a.nature = 'ticket_reattribue_restauration'
                                                     WHERE c.officine_id = v_off AND c.bifurcation_id = b.id)) ORDER BY b.id), '[]'::jsonb)
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

REVOKE EXECUTE ON FUNCTION cloud_integrite_tickets_reattribues(), cloud_verifier_integrite(boolean) FROM PUBLIC;
