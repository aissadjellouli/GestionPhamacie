-- =====================================================================
-- Base centrale, règles des écrans du titulaire (revue de code du 2026-11-23). Script de la base CENTRALE, passé par le
-- propriétaire, idempotent ; à passer après 042. Aucune donnée modifiée. Trois corrections, fonctions redéfinies à l'identique sauf :
--   1. cloud_ecran_rattacher (032) : un compte déjà rattaché qui perd le droit « ordres » (le rattachement par code remet consulter
--      et journal des accès) voit ses ordres encore ouverts annulés, comme au retrait du droit par le poste (034). Le nombre d'ordres
--      annulés est noté au journal de sécurité de l'officine.
--   2. cloud_ecran_rattacher : les essais bloqués « trop_d_essais » ne comptent plus dans la fenêtre d'un quart d'heure (seuls les
--      vrais refus comptent) ; sans cela, chaque essai bloqué faisait glisser la fenêtre et le blocage ne se terminait jamais.
--   3. cloud_ecran_titulaire_creer_ordre (034) et cloud_ecran_titulaire_envoyer_message (038) : verrou pris AUSSI par officine (en plus
--      du verrou par compte), avant de chercher un ordre ouvert du même type : deux comptes d'une même officine ne peuvent plus créer
--      ensemble deux ordres ouverts du même type (ni deux messages en attente). Pas d'index unique partiel : la notion d'ordre
--      « ouvert » dépend de expire_le > now() (non immuable, donc impossible dans un index), et des ordres échus non encore marqués
--      « expiré » bloqueraient à tort ; le verrou par officine suffit.
-- =====================================================================
SET client_encoding = 'UTF8';

DO $version$ BEGIN
    IF to_regprocedure('public.cloud_ordres_compte_annuler(uuid,uuid)') IS NULL
       OR to_regprocedure('public.cloud_ecran_titulaire_envoyer_message(text,uuid,text,integer,text)') IS NULL THEN
        RAISE EXCEPTION 'Script central 043 : passer d''abord les scripts 034 et 038. Rien n''a été modifié.';
    END IF;
END $version$;

CREATE OR REPLACE FUNCTION cloud_ecran_rattacher(p_empreinte_jeton text, p_empreinte_code text)
RETURNS TABLE (statut text, officine_id uuid)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; k code_rattachement; o officine; r text; v_avant text[]; n int := 0;
BEGIN
    SELECT * INTO v FROM cloud_ecran_titulaire(p_empreinte_jeton);
    IF (SELECT count(*) FROM journal_acces_distant j WHERE j.compte_id = v.compte_id AND j.evenement = 'rattachement' AND j.resultat = 'refus'
        AND j.objet IS DISTINCT FROM 'trop_d_essais' AND j.le > now() - interval '15 minutes') >= 5 THEN
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'rattachement', 'refus', 'trop_d_essais');
        RETURN QUERY SELECT 'trop_d_essais'::text, NULL::uuid; RETURN;
    END IF;
    IF p_empreinte_code IS NOT NULL AND p_empreinte_code ~ '^[0-9a-f]{64}$' THEN
        SELECT * INTO k FROM code_rattachement x WHERE x.empreinte_code = p_empreinte_code FOR UPDATE;
    END IF;
    r := CASE WHEN k.id IS NULL THEN 'inconnu'
              WHEN k.compte_id <> v.compte_id THEN 'autre_compte'
              WHEN k.utilise_le IS NOT NULL OR k.annule_le IS NOT NULL THEN 'utilise'
              WHEN k.expire_le <= now() THEN 'expire' END;
    IF r IS NULL THEN
        SELECT * INTO o FROM officine x WHERE x.id = k.officine_id;
        IF o.resiliee_le IS NOT NULL THEN r := 'partie'; END IF;
    END IF;
    IF r IS NOT NULL THEN
        -- Le journal garde le motif ; un code d'un autre compte n'est pas noté contre l'officine de ce compte
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, CASE WHEN r IN ('utilise', 'expire', 'partie') THEN k.officine_id END,
                                        'rattachement', 'refus', r);
        RETURN QUERY SELECT CASE WHEN r = 'autre_compte' THEN 'inconnu' ELSE r END, NULL::uuid; RETURN;
    END IF;
    UPDATE code_rattachement SET utilise_le = now() WHERE id = k.id;
    -- Droits d'un rattachement déjà en cours (verrouillé) : le rattachement par code les ramène à consulter et journal des accès
    SELECT x.droits INTO v_avant FROM compte_officine x WHERE x.compte_id = v.compte_id AND x.officine_id = k.officine_id AND x.retire_le IS NULL FOR UPDATE;
    INSERT INTO compte_officine AS x (compte_id, officine_id, accorde_par, droits)
    VALUES (v.compte_id, k.officine_id, 'code de rattachement n° ' || k.id, ARRAY['consulter', 'journal_acces'])
    ON CONFLICT ON CONSTRAINT compte_officine_pkey DO UPDATE SET retire_le = NULL, droits = EXCLUDED.droits, accorde_le = now(), accorde_par = EXCLUDED.accorde_par;
    -- Un compte déjà rattaché qui perd le droit « ordres » : ses ordres encore ouverts sont annulés
    -- ordre_central est cloisonnée (RLS forcée) : l'officine du code est posée avant la recherche des ordres ouverts
    IF v_avant IS NOT NULL AND 'ordres' = ANY (v_avant) THEN
        PERFORM set_config('app.officine_id', k.officine_id::text, true);
        n := cloud_ordres_compte_annuler(k.officine_id, v.compte_id);
    END IF;
    PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, k.officine_id, 'rattachement', 'ok', NULL);
    INSERT INTO journal_securite (officine_id, evenement, detail)
    VALUES (k.officine_id, 'rattachement_titulaire',
            left('écran titulaire, compte n° ' || left(v.compte_id::text, 8) || ' : officine rattachée (consulter, journal des accès) par le code n° '
                 || k.id || ' créé par ' || k.cree_par
                 || CASE WHEN n > 0 THEN ' ; ' || n || ' ordre(s) ouvert(s) annulé(s) (droit « ordres » perdu)' ELSE '' END, 500));
    RETURN QUERY SELECT 'ok'::text, k.officine_id;
END $$;

CREATE OR REPLACE FUNCTION cloud_ecran_titulaire_creer_ordre(p_empreinte_jeton text, p_officine uuid, p_type text, p_empreinte_code text)
RETURNS TABLE (statut text, ordre_id uuid)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; c compte_distant; o officine; r text; v_id uuid; v_auteur text;
BEGIN
    SELECT * INTO v FROM cloud_ecran_titulaire(p_empreinte_jeton);
    -- Deux demandes simultanées du même compte : comptées l'une après l'autre
    PERFORM pg_advisory_xact_lock(hashtextextended('ordre_titulaire:' || v.compte_id::text, 0));
    IF (SELECT count(*) FROM journal_acces_distant j WHERE j.compte_id = v.compte_id AND j.evenement = 'ordre_titulaire'
        AND j.resultat = 'ok' AND j.le > now() - interval '1 hour') >= 10 THEN
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'ordre_titulaire', 'refus', 'trop_d_ordres');
        RETURN QUERY SELECT 'trop_d_ordres'::text, NULL::uuid; RETURN;
    END IF;
    IF NOT cloud_ordre_titulaire_permis(p_type) THEN
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'ordre_titulaire', 'refus', 'hors_liste');
        RETURN QUERY SELECT 'hors_liste'::text, NULL::uuid; RETURN;
    END IF;
    -- Rattachement non retiré et droit « ordres » (refus noté par l'ouverture), officine posée par la base
    IF NOT (SELECT x.ouverte FROM cloud_ecran_ouvrir_titulaire(p_empreinte_jeton, p_officine, 'ordres', 'ordre_titulaire') x) THEN
        RETURN QUERY SELECT 'refuse'::text, NULL::uuid; RETURN;
    END IF;
    -- Un seul ordre ouvert du même type par officine : deux demandes simultanées de comptes différents passent l'une après l'autre
    PERFORM pg_advisory_xact_lock(hashtextextended('ordre_officine:' || p_officine::text, 0));
    SELECT * INTO o FROM officine x WHERE x.id = p_officine;
    r := CASE WHEN o.resiliee_le IS NOT NULL THEN 'partie'
              WHEN EXISTS (SELECT 1 FROM ordre_central x WHERE x.officine_id = p_officine AND x.origine = 'titulaire' AND x.type = p_type
                           AND x.statut IN ('en_attente', 'a_confirmer') AND x.expire_le > now()) THEN 'deja_en_cours' END;
    IF r IS NULL THEN
        r := nullif(cloud_ecran_controler_code(v.session_id, 'action', p_empreinte_code), 'ok');
    END IF;
    IF r IS NOT NULL THEN
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, p_officine, 'ordre_titulaire', 'refus', p_type || ':' || r);
        RETURN QUERY SELECT r, NULL::uuid; RETURN;
    END IF;
    SELECT * INTO c FROM compte_distant x WHERE x.id = v.compte_id;
    v_auteur := left('titulaire à distance (' || c.identifiant || ')', 100);
    INSERT INTO ordre_central (officine_id, type, cible, attendu, donnees, auteur, expire_le, origine, compte_id)
    VALUES (p_officine, p_type, NULL, '{}'::jsonb, '{}'::jsonb, v_auteur, now() + interval '24 hours', 'titulaire', v.compte_id)
    RETURNING id INTO v_id;
    INSERT INTO ordre_central_journal (officine_id, ordre_id, statut, par) VALUES (p_officine, v_id, 'en_attente', v_auteur);
    PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, p_officine, 'ordre_titulaire', 'ok', p_type);
    PERFORM cloud_journaliser(p_officine, NULL, 'ordre_cree',
        left(format('ordre %s (%s) par l''écran du titulaire, compte n° %s, échéance 24 h', v_id, p_type, left(v.compte_id::text, 8)), 500));
    PERFORM cloud_ordre_publier(v_id);
    RETURN QUERY SELECT 'ok'::text, v_id;
END $$;

CREATE OR REPLACE FUNCTION cloud_ecran_titulaire_envoyer_message(p_empreinte_jeton text, p_officine uuid, p_texte text, p_jours int, p_empreinte_code text)
RETURNS TABLE (statut text, ordre_id uuid)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; c compte_distant; o officine; r text; v_id uuid; v_auteur text; v_texte text := btrim(coalesce(p_texte, ''));
        v_fin timestamptz; v_donnees jsonb;
BEGIN
    SELECT * INTO v FROM cloud_ecran_titulaire(p_empreinte_jeton);
    -- Même verrou et même limite que les autres ordres du titulaire (034)
    PERFORM pg_advisory_xact_lock(hashtextextended('ordre_titulaire:' || v.compte_id::text, 0));
    IF (SELECT count(*) FROM journal_acces_distant j WHERE j.compte_id = v.compte_id AND j.evenement = 'ordre_titulaire'
        AND j.resultat = 'ok' AND j.le > now() - interval '1 hour') >= 10 THEN
        r := 'trop_d_ordres';
    ELSIF p_jours IS NULL OR p_jours NOT BETWEEN 1 AND 7 THEN
        r := 'duree';
    ELSE
        v_fin := date_trunc('second', now() + make_interval(days => p_jours));
        v_donnees := jsonb_build_object('format', 'message-v1', 'texte', v_texte,
                                        'jusqu_au', to_char(v_fin AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'));
        r := cloud_message_poste_controle(v_donnees, now());
    END IF;
    IF r IS NOT NULL THEN
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'ordre_titulaire', 'refus', 'message:' || r);
        RETURN QUERY SELECT r, NULL::uuid; RETURN;
    END IF;
    -- Rattachement non retiré et droit « ordres » (refus noté par l'ouverture), officine posée par la base
    IF NOT (SELECT x.ouverte FROM cloud_ecran_ouvrir_titulaire(p_empreinte_jeton, p_officine, 'ordres', 'ordre_titulaire') x) THEN
        RETURN QUERY SELECT 'refuse'::text, NULL::uuid; RETURN;
    END IF;
    -- Un seul ordre ouvert du même type par officine : deux demandes simultanées de comptes différents passent l'une après l'autre
    PERFORM pg_advisory_xact_lock(hashtextextended('ordre_officine:' || p_officine::text, 0));
    SELECT * INTO o FROM officine x WHERE x.id = p_officine;
    r := CASE WHEN o.resiliee_le IS NOT NULL THEN 'partie'
              WHEN EXISTS (SELECT 1 FROM ordre_central x WHERE x.officine_id = p_officine AND x.origine = 'titulaire' AND x.type = 'message'
                           AND x.statut IN ('en_attente', 'a_confirmer') AND x.expire_le > now()) THEN 'deja_en_cours' END;
    IF r IS NULL THEN
        r := nullif(cloud_ecran_controler_code(v.session_id, 'action', p_empreinte_code), 'ok');
    END IF;
    IF r IS NOT NULL THEN
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, p_officine, 'ordre_titulaire', 'refus', 'message:' || r);
        RETURN QUERY SELECT r, NULL::uuid; RETURN;
    END IF;
    SELECT * INTO c FROM compte_distant x WHERE x.id = v.compte_id;
    v_auteur := left('titulaire à distance (' || c.identifiant || ')', 100);
    INSERT INTO ordre_central (officine_id, type, cible, attendu, donnees, auteur, expire_le, origine, compte_id)
    VALUES (p_officine, 'message', NULL, '{}'::jsonb, v_donnees, v_auteur, now() + interval '24 hours', 'titulaire', v.compte_id)
    RETURNING id INTO v_id;
    INSERT INTO ordre_central_journal (officine_id, ordre_id, statut, par) VALUES (p_officine, v_id, 'en_attente', v_auteur);
    PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, p_officine, 'ordre_titulaire', 'ok', 'message');
    PERFORM cloud_journaliser(p_officine, NULL, 'ordre_cree',
        left(format('ordre %s (message de %s caractère(s), affiché jusqu''au %s UTC) par l''écran du titulaire, compte n° %s, échéance 24 h',
                    v_id, length(v_texte), to_char(v_fin AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI'), left(v.compte_id::text, 8)), 500));
    PERFORM cloud_ordre_publier(v_id);
    RETURN QUERY SELECT 'ok'::text, v_id;
END $$;

-- Droits inchangés (CREATE OR REPLACE les conserve) ; rappel pour un rejeu sur une base neuve
REVOKE ALL ON FUNCTION cloud_ecran_rattacher(text, text), cloud_ecran_titulaire_creer_ordre(text, uuid, text, text),
    cloud_ecran_titulaire_envoyer_message(text, uuid, text, int, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_ecran_rattacher(text, text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_titulaire_creer_ordre(text, uuid, text, text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_titulaire_envoyer_message(text, uuid, text, int, text) TO pharma_cloud_ecrans;
COMMENT ON FUNCTION cloud_ecran_rattacher(text, text) IS 'Rattachement par code (032) ; script 043 : ordres annulés si le droit « ordres » est perdu, essais bloqués non comptés.';
