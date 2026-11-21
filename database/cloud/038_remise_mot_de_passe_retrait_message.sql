-- =====================================================================
-- Base centrale, reste des lots S7.2 et S7.3 (2026-11-21, go du titulaire,
-- hors SMS) : docs/ecrans-central.md § 3 nonies. Script de la base
-- CENTRALE, passé par le propriétaire, idempotent ; à passer après 037.
--   1. Remise à zéro du mot de passe d'un compte distant par l'ÉDITEUR
--      (cloud_ecran_remise_mot_de_passe) : nouvelle empreinte BCrypt d'un
--      mot de passe provisoire tiré au sort par l'application (jamais le
--      mot de passe : il est montré UNE fois à l'éditeur par les écrans),
--      changement obligatoire à la connexion suivante, sessions et codes
--      en cours du compte fermés, verrouillage levé ; motif, ressaisie de
--      l'identifiant, code de confirmation ; jamais son propre compte.
--   2. Retrait d'un rattachement par le TITULAIRE lui-même
--      (cloud_ecran_titulaire_retirer_officine) : effet immédiat, ordres
--      encore ouverts de ce compte pour l'officine annulés et republiés
--      (même règle que le retrait depuis le poste, script 034) ; ressaisie
--      du nom de l'officine, code de confirmation. L'éditeur ne retire
--      aucun rattachement (fonction réservée au titulaire).
--   3. Ordre « message » du titulaire (liste fermée S7.3) : message court
--      affiché sur les postes de l'officine. Données message-v1 contrôlées
--      par cloud_message_poste_controle (texte simple de 1 à 200
--      caractères, sans balise, caractère invisible ni lien ; fin
--      d'affichage 1 à 7 jours après l'envoi), revérifiées par la
--      contrainte de ordre_central ; droit « ordres », code de
--      confirmation, 24 h pour être relevé, un seul message ouvert par
--      officine (cloud_ecran_titulaire_envoyer_message).
-- Aucune donnée existante n'est lue, modifiée ni supprimée par ce script
-- (fonctions ajoutées, contraintes élargies).
-- =====================================================================

SET client_encoding = 'UTF8';

DO $version$ BEGIN
    IF to_regprocedure('public.cloud_ecran_facteur_compte_contact(text,text,uuid,text,text)') IS NULL
       OR to_regprocedure('public.cloud_reglages_communs_controle(jsonb)') IS NULL
       OR to_regprocedure('public.cloud_ordres_compte_annuler(uuid,uuid)') IS NULL THEN
        RAISE EXCEPTION 'Script central 038 : passer d''abord les scripts 030 à 037 (écrans du central). Rien n''a été modifié.';
    END IF;
END $version$;

-- ---------------------------------------------------------------------
-- 1. Remise à zéro du mot de passe d'un compte distant (éditeur)
-- ---------------------------------------------------------------------

-- Statuts : ok, inconnu, soi_meme (son propre compte : changer son mot de passe par l'écran « Mot de passe »), inactif, motif (5 à 300
-- caractères), confirmation (identifiant du compte ressaisi), puis ceux du code (faux, expire, epuise, aucun). Contrôles AVANT le code.
-- Réussite : empreinte remplacée, mot de passe PROVISOIRE (étape « mot_de_passe » obligatoire à la connexion suivante), échecs et
-- verrouillage remis à zéro, sessions ouvertes du compte fermées (« revoquee »), codes en cours annulés. Journaux : accès (chaîné ;
-- une ligne pour l'éditeur, une pour le compte remis à zéro, visible dans son journal) et sécurité (n° du compte, motif) ; jamais le
-- mot de passe ni son empreinte.
CREATE OR REPLACE FUNCTION cloud_ecran_remise_mot_de_passe(p_empreinte_jeton text, p_empreinte_code text, p_compte uuid, p_motif text,
                                                           p_confirmation text, p_empreinte_mdp text)
RETURNS TABLE (statut text, sessions_fermees int)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; c compte_distant; r text; n int := 0; v_motif text := cloud_ecran_motif(p_motif);
BEGIN
    SELECT * INTO v FROM cloud_ecran_editeur(p_empreinte_jeton);
    IF p_empreinte_mdp IS NULL OR p_empreinte_mdp !~ '^\$2[aby]\$1[2-9]\$[./A-Za-z0-9]{53}$' THEN
        RAISE EXCEPTION 'empreinte BCrypt invalide (coût 12 au moins)' USING ERRCODE = '22023';
    END IF;
    SELECT * INTO c FROM compte_distant x WHERE x.id = p_compte FOR UPDATE;
    r := CASE WHEN c.id IS NULL THEN 'inconnu'
              WHEN c.id = v.compte_id THEN 'soi_meme'
              WHEN NOT c.actif THEN 'inactif'
              WHEN v_motif IS NULL THEN 'motif'
              WHEN lower(btrim(coalesce(p_confirmation, ''))) <> c.identifiant THEN 'confirmation' END;
    IF r IS NOT NULL THEN
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'mot_de_passe_remis', 'refus', r);
        RETURN QUERY SELECT r, 0; RETURN;
    END IF;
    r := cloud_ecran_controler_code(v.session_id, 'action', p_empreinte_code);
    IF r <> 'ok' THEN
        PERFORM cloud_ecran_noter_action(v.compte_id, v.session_id, NULL, 'mot_de_passe_remis', 'refus', r, NULL);
        RETURN QUERY SELECT r, 0; RETURN;
    END IF;
    UPDATE compte_distant x SET empreinte_mot_de_passe = p_empreinte_mdp, mot_de_passe_provisoire = true, mot_de_passe_change_le = now(),
           echecs = 0, verrouillages = 0, verrouille_jusqu_au = NULL
    WHERE x.id = c.id;
    UPDATE session_distante s SET fin_le = now(), motif_fin = 'revoquee' WHERE s.compte_id = c.id AND s.fin_le IS NULL;
    GET DIAGNOSTICS n = ROW_COUNT;
    UPDATE code_second_facteur k SET annule_le = now() WHERE k.compte_id = c.id AND k.utilise_le IS NULL AND k.annule_le IS NULL;
    PERFORM cloud_ecran_journaliser(c.id, NULL, NULL, 'mot_de_passe_remis', 'ok', 'editeur');
    PERFORM cloud_ecran_noter_action(v.compte_id, v.session_id, NULL, 'mot_de_passe_remis', 'ok', 'compte:' || left(c.id::text, 8),
        'mot de passe du compte n° ' || left(c.id::text, 8) || ' remis à zéro (provisoire, à changer à la connexion suivante ; '
        || n || ' session(s) fermée(s)) ; motif : ' || v_motif);
    RETURN QUERY SELECT 'ok'::text, n;
END $$;

-- ---------------------------------------------------------------------
-- 2. Retrait d'un rattachement par le titulaire lui-même
-- ---------------------------------------------------------------------

-- Statuts : ok, refuse (officine inconnue, d'un autre compte ou déjà retirée : refus noté par l'ouverture), confirmation (nom de
-- l'officine ressaisi, casse ignorée), puis ceux du code. Contrôles AVANT le code. Effet IMMÉDIAT (chaque fonction des écrans revérifie
-- le rattachement à chaque appel) ; ordres encore ouverts de CE compte pour l'officine annulés et republiés (comme le retrait depuis
-- le poste, 034). Aucun droit particulier exigé : un titulaire peut toujours retirer son propre accès.
CREATE OR REPLACE FUNCTION cloud_ecran_titulaire_retirer_officine(p_empreinte_jeton text, p_officine uuid, p_confirmation text, p_empreinte_code text)
RETURNS TABLE (statut text, ordres_annules int)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; o officine; r text; k int; n int := 0; x record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_titulaire(p_empreinte_jeton);
    -- Rattachement non retiré vérifié par la base (refus noté au journal des accès), officine posée par la base
    IF NOT cloud_ecran_ouvrir(p_empreinte_jeton, p_officine) THEN
        RETURN QUERY SELECT 'refuse'::text, 0; RETURN;
    END IF;
    SELECT * INTO o FROM officine f WHERE f.id = p_officine;
    IF lower(btrim(coalesce(p_confirmation, ''))) <> lower(btrim(o.libelle)) THEN
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, p_officine, 'retrait_rattachement', 'refus', 'confirmation');
        RETURN QUERY SELECT 'confirmation'::text, 0; RETURN;
    END IF;
    r := cloud_ecran_controler_code(v.session_id, 'action', p_empreinte_code);
    IF r <> 'ok' THEN
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, p_officine, 'retrait_rattachement', 'refus', 'code:' || r);
        RETURN QUERY SELECT r, 0; RETURN;
    END IF;
    UPDATE compte_officine c SET retire_le = now() WHERE c.compte_id = v.compte_id AND c.officine_id = p_officine AND c.retire_le IS NULL;
    GET DIAGNOSTICS k = ROW_COUNT;
    IF k = 0 THEN
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, p_officine, 'retrait_rattachement', 'refus', 'deja_retire');
        RETURN QUERY SELECT 'refuse'::text, 0; RETURN;
    END IF;
    FOR x IN SELECT z.id FROM ordre_central z WHERE z.officine_id = p_officine AND z.compte_id = v.compte_id
             AND z.statut IN ('en_attente', 'a_confirmer') ORDER BY z.cree_le LOOP
        PERFORM cloud_annuler_ordre(p_officine, x.id, 'titulaire à distance (rattachement retiré)');
        n := n + 1;
    END LOOP;
    PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, p_officine, 'retrait_rattachement', 'ok', 'ordres_annules:' || n);
    INSERT INTO journal_securite (officine_id, evenement, detail)
    VALUES (p_officine, 'retrait_compte_distant',
            left('écran du titulaire : le compte n° ' || left(v.compte_id::text, 8) || ' a retiré son rattachement'
                 || CASE WHEN n > 0 THEN ' ; ' || n || ' ordre(s) ouvert(s) annulé(s)' ELSE '' END, 500));
    RETURN QUERY SELECT 'ok'::text, n;
END $$;

-- ---------------------------------------------------------------------
-- 3. Ordre « message » du titulaire (affiché sur les postes de l'officine)
-- ---------------------------------------------------------------------

-- Contrôle des données d'un message (même règle que Pharma.Domain.Reglages.MessagePostes) : NULL si valable ; sinon format, vide,
-- trop_long, texte (ligne unique, sans balise ni caractère invisible ou de contrôle), lien, duree (fin 1 seconde à 7 jours après
-- p_cree). Pure (aucune table lue) : utilisée par la contrainte de ordre_central avec le rôle qui écrit la ligne.
CREATE OR REPLACE FUNCTION cloud_message_poste_controle(p jsonb, p_cree timestamptz) RETURNS text
LANGUAGE plpgsql IMMUTABLE SET search_path = public, pg_temp AS $$
DECLARE t text; f timestamptz;
BEGIN
    IF p IS NULL OR jsonb_typeof(p) <> 'object' OR length(p::text) > 2000 OR p ->> 'format' IS DISTINCT FROM 'message-v1'
       OR jsonb_typeof(p -> 'texte') IS DISTINCT FROM 'string' OR jsonb_typeof(p -> 'jusqu_au') IS DISTINCT FROM 'string'
       OR EXISTS (SELECT 1 FROM jsonb_object_keys(p) k WHERE k NOT IN ('format', 'texte', 'jusqu_au')) THEN
        RETURN 'format';
    END IF;
    t := p ->> 'texte';
    IF btrim(t) = '' THEN RETURN 'vide'; END IF;
    IF length(t) > 200 THEN RETURN 'trop_long'; END IF;
    IF t <> btrim(t) OR t ~ '[\u0001-\u001F\u007F-\u009F​-‏ -‮⁠-⁩﻿<>]' THEN RETURN 'texte'; END IF;
    IF t ~* '://|www\.|(https?|ftp|file|mailto|javascript|data|tel|sms|whatsapp):|[a-z0-9-]\.(com|net|org|fr|dz|info|io|app|biz|me|co|ly|gl|link|xyz)([^a-z0-9]|$)' THEN
        RETURN 'lien';
    END IF;
    IF p ->> 'jusqu_au' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6})?(Z|[+-][0-9]{2}:[0-9]{2})$' THEN
        RETURN 'format';
    END IF;
    BEGIN
        f := (p ->> 'jusqu_au')::timestamptz;
    EXCEPTION WHEN others THEN RETURN 'format';
    END;
    IF p_cree IS NULL OR f <= p_cree OR f > p_cree + interval '7 days' THEN RETURN 'duree'; END IF;
    RETURN NULL;
END $$;

-- Type « message » permis, au titulaire seulement, données contrôlées par la table elle-même (garde de rejeu : marqueur dans la définition)
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_ordre_central_type' AND conrelid = 'ordre_central'::regclass
                   AND pg_get_constraintdef(oid) LIKE '%''message''%') THEN
        ALTER TABLE ordre_central DROP CONSTRAINT IF EXISTS ck_ordre_central_type;
        ALTER TABLE ordre_central ADD CONSTRAINT ck_ordre_central_type CHECK (type IN ('essai', 'employe_creer', 'employe_modifier',
            'employe_bloquer', 'employe_debloquer', 'droits_role', 'prix_produit', 'nomenclature', 'commande', 'classe_suivie', 'organismes',
            'prestations', 'indications', 'dci', 'sauvegarde', 'verification_integrite', 'reglages', 'message'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_ordre_central_titulaire' AND conrelid = 'ordre_central'::regclass
                   AND pg_get_constraintdef(oid) LIKE '%cloud_message_poste_controle%') THEN
        ALTER TABLE ordre_central DROP CONSTRAINT IF EXISTS ck_ordre_central_titulaire;
        ALTER TABLE ordre_central ADD CONSTRAINT ck_ordre_central_titulaire
            CHECK ((type <> 'reglages' OR (origine = 'titulaire' AND envoi_id IS NOT NULL))
                   AND (type <> 'message' OR origine = 'titulaire')
                   AND (envoi_id IS NULL OR type = 'reglages')
                   AND (origine <> 'titulaire' OR (cible IS NULL AND expire_le <= cree_le + interval '24 hours'
                        AND ((type IN ('essai', 'sauvegarde', 'verification_integrite') AND donnees = '{}'::jsonb)
                             OR (type = 'reglages' AND cloud_reglages_communs_controle(donnees) IS NULL)
                             OR (type = 'message' AND cloud_message_poste_controle(donnees, cree_le) IS NULL)))));
    END IF;
END $$;

-- Envoi d'un message à une officine (droit « ordres »). Statuts : ok, trop_d_ordres (10 ordres par heure et par compte, messages
-- compris), vide, trop_long, texte, lien, duree (1 à 7 jours), format, refuse (officine non rattachée ou droit absent), partie,
-- deja_en_cours (un message encore ouvert pour l'officine), puis ceux du code. Contrôles AVANT le code ; chaque refus noté au journal
-- des accès. Ordre origine titulaire, 24 h pour être relevé ; le message reste affiché jusqu'à la date choisie (au plus 7 jours).
-- Journal de sécurité : longueur et fin d'affichage seulement (jamais le texte).
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

-- ---- Droits : rien pour PUBLIC ; écrans : les trois fonctions. Le contrôle du message (pur, sans lecture de table) reste exécutable
-- par tous : la contrainte de ordre_central l'appelle avec le rôle qui écrit la ligne (retour du résultat par l'API centrale) ----
REVOKE ALL ON FUNCTION cloud_ecran_remise_mot_de_passe(text, text, uuid, text, text, text),
    cloud_ecran_titulaire_retirer_officine(text, uuid, text, text),
    cloud_ecran_titulaire_envoyer_message(text, uuid, text, int, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_ecran_remise_mot_de_passe(text, text, uuid, text, text, text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_titulaire_retirer_officine(text, uuid, text, text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_titulaire_envoyer_message(text, uuid, text, int, text) TO pharma_cloud_ecrans;

COMMENT ON FUNCTION cloud_ecran_remise_mot_de_passe(text, text, uuid, text, text, text) IS 'Reste S7.2 : mot de passe d''un autre compte remis à zéro par l''éditeur (provisoire, sessions fermées), script 038.';
COMMENT ON FUNCTION cloud_ecran_titulaire_retirer_officine(text, uuid, text, text) IS 'Reste S7.2 : retrait d''un rattachement par le titulaire lui-même (ordres ouverts annulés), script 038.';
COMMENT ON FUNCTION cloud_message_poste_controle(jsonb, timestamptz) IS 'Reste S7.3 : données d''un ordre « message » (message-v1) ; NULL si valable, script 038.';
COMMENT ON FUNCTION cloud_ecran_titulaire_envoyer_message(text, uuid, text, int, text) IS 'Reste S7.3 : message affiché sur les postes d''une officine (droit « ordres »), script 038.';
