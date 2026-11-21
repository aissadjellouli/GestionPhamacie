-- =====================================================================
-- Base centrale, lot S7.2b (2026-11-21, go du titulaire, recommandations
-- acceptées) : le POSTE de l'officine demande lui-même un code de
-- rattachement (Administration › Synchronisation › Accès à distance),
-- par la route POST /api/v1/rattachement de l'API centrale (certificat
-- client de l'officine + jeton, comme l'échange). Script de la base
-- CENTRALE, passé par le propriétaire, idempotent ; à passer après 032.
--   1. cloud_code_rattachement_enregistrer : enregistrement commun à la
--      console (--code-rattachement) et au poste : mêmes règles (un
--      compte, une officine, 30 minutes, usage unique, code précédent du
--      même compte et de la même officine annulé, empreinte SHA-256
--      seule), journal des accès ET journal de sécurité.
--   2. cloud_compte_distant_code_rattachement (console) : inchangée pour
--      l'appelant, passe désormais par l'enregistrement commun.
--   3. cloud_poste_code_rattachement (rôle pharma_cloud_api) : officine
--      = celle posée par l'API après contrôle du certificat (app.officine_id)
--      ET celle du certificat présenté (revérifiée ici : valable, non
--      révoqué, officine non partie) ; jamais un paramètre de la demande.
--      Compte inconnu, non titulaire, désactivé ou identifiant mal formé :
--      MÊME réponse « refus » (aucune énumération des comptes ; le motif
--      réel reste au journal de sécurité du central, sans l'identifiant).
--      Limite : 5 demandes (délivrées ou refusées) par heure et par
--      officine, puis « trop_de_codes ».
--   4. cloud_ecran_titulaire_journal : acteur « poste » (Poste de
--      l'officine) pour les demandes du poste.
-- Aucune donnée existante n'est lue, modifiée ni supprimée par ce script.
-- =====================================================================

SET client_encoding = 'UTF8';

DO $version$ BEGIN
    IF to_regclass('public.code_rattachement') IS NULL
       OR to_regprocedure('public.cloud_compte_distant_code_rattachement(text,uuid,text,text)') IS NULL
       OR to_regprocedure('public.cloud_ecran_titulaire_journal(text,uuid,integer,integer)') IS NULL THEN
        RAISE EXCEPTION 'Script central 033 : passer d''abord le script 032 (écrans du titulaire). Rien n''a été modifié.';
    END IF;
END $version$;

-- ---- 1. Enregistrement commun d'un code de rattachement (console et poste ; jamais accordé) ----
-- Le compte (titulaire actif) et l'officine (non partie) sont contrôlés par l'appelant ; seule l'empreinte du code arrive ici
-- (code tiré par l'application, ComptesDistants.TirerCodeRattachement, affiché une fois). Rend l'échéance (30 minutes).
CREATE OR REPLACE FUNCTION cloud_code_rattachement_enregistrer(p_compte uuid, p_officine uuid, p_empreinte_code text, p_par text, p_source text)
RETURNS timestamptz
LANGUAGE plpgsql SET search_path = public, pg_temp AS $$
DECLARE v_expire timestamptz := now() + interval '30 minutes'; v_id bigint;
BEGIN
    IF p_source IS NULL OR p_source NOT IN ('console', 'poste') THEN
        RAISE EXCEPTION 'origine du code de rattachement inconnue' USING ERRCODE = '22023';
    END IF;
    IF p_empreinte_code IS NULL OR p_empreinte_code !~ '^[0-9a-f]{64}$' THEN
        RAISE EXCEPTION 'empreinte du code de rattachement invalide' USING ERRCODE = '22023';
    END IF;
    UPDATE code_rattachement x SET annule_le = now()
    WHERE x.compte_id = p_compte AND x.officine_id = p_officine AND x.utilise_le IS NULL AND x.annule_le IS NULL;
    INSERT INTO code_rattachement (empreinte_code, compte_id, officine_id, expire_le, cree_par)
    VALUES (p_empreinte_code, p_compte, p_officine, v_expire, btrim(p_par)) RETURNING id INTO v_id;
    PERFORM cloud_ecran_journaliser(p_compte, NULL, p_officine, 'code_rattachement_cree', 'ok', p_source);
    INSERT INTO journal_securite (officine_id, evenement, detail)
    VALUES (p_officine, 'code_rattachement_cree',
            left(CASE p_source WHEN 'console' THEN 'console' ELSE 'poste de l''officine' END || ' : code de rattachement n° ' || v_id
                 || ' (30 min) pour le compte n° ' || left(p_compte::text, 8) || ', par ' || btrim(p_par), 500));
    RETURN v_expire;
END $$;

-- ---- 2. Console du central (propriétaire) : mêmes statuts qu'en 032, enregistrement commun ----
-- Statuts : ok, compte_inconnu, pas_titulaire, compte_inactif, officine_inconnue, partie.
CREATE OR REPLACE FUNCTION cloud_compte_distant_code_rattachement(p_identifiant text, p_officine uuid, p_empreinte_code text, p_par text)
RETURNS TABLE (statut text, expire_le timestamptz)
LANGUAGE plpgsql SET search_path = public, pg_temp AS $$
DECLARE c compte_distant; o officine;
BEGIN
    IF p_empreinte_code IS NULL OR p_empreinte_code !~ '^[0-9a-f]{64}$' THEN
        RAISE EXCEPTION 'empreinte du code de rattachement invalide' USING ERRCODE = '22023';
    END IF;
    SELECT * INTO c FROM compte_distant x WHERE x.identifiant = lower(btrim(p_identifiant));
    IF NOT FOUND THEN RETURN QUERY SELECT 'compte_inconnu'::text, NULL::timestamptz; RETURN; END IF;
    IF c.role <> 'titulaire' THEN RETURN QUERY SELECT 'pas_titulaire'::text, NULL::timestamptz; RETURN; END IF;
    IF NOT c.actif THEN RETURN QUERY SELECT 'compte_inactif'::text, NULL::timestamptz; RETURN; END IF;
    SELECT * INTO o FROM officine x WHERE x.id = p_officine;
    IF NOT FOUND THEN RETURN QUERY SELECT 'officine_inconnue'::text, NULL::timestamptz; RETURN; END IF;
    IF o.resiliee_le IS NOT NULL THEN RETURN QUERY SELECT 'partie'::text, NULL::timestamptz; RETURN; END IF;
    RETURN QUERY SELECT 'ok'::text, cloud_code_rattachement_enregistrer(c.id, o.id, p_empreinte_code, p_par, 'console');
END $$;

-- ---- 3. Poste de l'officine (API centrale, rôle pharma_cloud_api) ----
-- Statuts : ok, refus (compte inconnu, non titulaire, désactivé, identifiant mal formé : même réponse), trop_de_codes.
-- Certificat absent, d'une autre officine que celle posée, révoqué, échu ou officine partie : erreur 42501 (rien n'est noté
-- au journal des accès ; l'API a déjà refusé ces cas avant d'appeler).
CREATE OR REPLACE FUNCTION cloud_poste_code_rattachement(p_certificat text, p_identifiant text, p_empreinte_code text)
RETURNS TABLE (statut text, expire_le timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v_off uuid := cloud_officine_courante(); c compte_distant; v_identifiant text := lower(btrim(coalesce(p_identifiant, '')));
        v_motif text;
BEGIN
    IF v_off IS NULL OR p_certificat IS NULL OR NOT EXISTS (
        SELECT 1 FROM certificat k JOIN officine o ON o.id = k.officine_id
        WHERE k.empreinte = p_certificat AND k.officine_id = v_off AND k.revoque_le IS NULL AND k.echeance > now() AND o.resiliee_le IS NULL) THEN
        RAISE EXCEPTION 'certificat valable de l''officine exigé' USING ERRCODE = '42501';
    END IF;
    IF p_empreinte_code IS NULL OR p_empreinte_code !~ '^[0-9a-f]{64}$' THEN
        RAISE EXCEPTION 'empreinte du code de rattachement invalide' USING ERRCODE = '22023';
    END IF;
    -- Limite par officine (demandes délivrées ET refusées, pour ne pas servir d'oracle) ; deux demandes simultanées comptées l'une après l'autre
    PERFORM pg_advisory_xact_lock(hashtextextended('code_rattachement_poste:' || v_off::text, 0));
    IF (SELECT count(*) FROM journal_acces_distant j
        WHERE j.officine_id = v_off AND j.evenement = 'code_rattachement_cree' AND j.objet = 'poste' AND j.le > now() - interval '1 hour') >= 5 THEN
        PERFORM cloud_ecran_journaliser(NULL, NULL, v_off, 'code_rattachement_cree', 'refus', 'poste:limite');
        RETURN QUERY SELECT 'trop_de_codes'::text, NULL::timestamptz; RETURN;
    END IF;
    IF v_identifiant ~ '^[a-z0-9][a-z0-9._-]{2,59}$' THEN
        SELECT * INTO c FROM compte_distant x WHERE x.identifiant = v_identifiant;
    END IF;
    v_motif := CASE WHEN c.id IS NULL THEN 'compte_inconnu' WHEN c.role <> 'titulaire' THEN 'pas_titulaire' WHEN NOT c.actif THEN 'compte_inactif' END;
    IF v_motif IS NOT NULL THEN
        -- Refus noté contre l'officine, jamais contre un compte (le compte visé n'est pas révélé) ; motif réel au journal de sécurité
        PERFORM cloud_ecran_journaliser(NULL, NULL, v_off, 'code_rattachement_cree', 'refus', 'poste');
        INSERT INTO journal_securite (officine_id, certificat_empreinte, evenement, detail)
        VALUES (v_off, p_certificat, 'code_rattachement_refuse', 'poste de l''officine : code de rattachement non délivré (' || v_motif || ')');
        RETURN QUERY SELECT 'refus'::text, NULL::timestamptz; RETURN;
    END IF;
    RETURN QUERY SELECT 'ok'::text, cloud_code_rattachement_enregistrer(c.id, v_off, p_empreinte_code, 'poste de l''officine', 'poste');
END $$;

-- ---- 4. Journal des accès d'une officine du titulaire (remplace la version de 032 : acteur « poste ») ----
-- Acteur : « vous », « editeur », « titulaire » (un autre compte titulaire), « poste » (demande du poste de l'officine) ou
-- « console », sans identifiant ni nom. Page de 10 à 100 lignes.
CREATE OR REPLACE FUNCTION cloud_ecran_titulaire_journal(p_empreinte_jeton text, p_officine uuid, p_page int, p_taille int)
RETURNS TABLE (total bigint, id bigint, le timestamptz, acteur text, evenement text, resultat text, objet text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; v_taille int := least(greatest(coalesce(p_taille, 20), 10), 100); v_page int := greatest(coalesce(p_page, 1), 1);
BEGIN
    SELECT * INTO v FROM cloud_ecran_ouvrir_titulaire(p_empreinte_jeton, p_officine, 'journal_acces', 'journal_officine_titulaire');
    IF NOT v.ouverte THEN RETURN; END IF;
    RETURN QUERY SELECT count(*) OVER (), j.id, j.le,
                        -- Demande du poste (objet « poste… ») ; commande de la console (objet « console ») : notée au compte concerné,
                        -- mais faite par le propriétaire du central
                        CASE WHEN j.objet LIKE 'poste%' THEN 'poste'
                             WHEN j.objet = 'console' OR j.compte_id IS NULL THEN 'console' WHEN j.compte_id = v.compte_id THEN 'vous'
                             WHEN c.role = 'editeur' THEN 'editeur' WHEN c.role = 'titulaire' THEN 'titulaire' ELSE 'console' END,
                        j.evenement, j.resultat, j.objet
                 FROM journal_acces_distant j LEFT JOIN compte_distant c ON c.id = j.compte_id
                 WHERE j.officine_id = p_officine ORDER BY j.id DESC OFFSET (v_page - 1) * v_taille LIMIT v_taille;
END $$;

-- ---- Droits : rien pour PUBLIC ; l'API centrale n'exécute que la fonction du poste ----
REVOKE ALL ON FUNCTION cloud_code_rattachement_enregistrer(uuid, uuid, text, text, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION cloud_compte_distant_code_rattachement(text, uuid, text, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION cloud_poste_code_rattachement(text, text, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION cloud_ecran_titulaire_journal(text, uuid, int, int) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_poste_code_rattachement(text, text, text) TO pharma_cloud_api;
GRANT EXECUTE ON FUNCTION cloud_ecran_titulaire_journal(text, uuid, int, int) TO pharma_cloud_ecrans;
