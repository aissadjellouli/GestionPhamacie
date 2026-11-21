-- =====================================================================
-- Base centrale, lot S7.6 (2026-11-21, go du titulaire « go codes gmail ») :
-- ENVOI DES CODES de second facteur réglé dans l'administration du site
-- central (docs/ecrans-central.md § 3 septies). Script de la base
-- CENTRALE, passé par le propriétaire, idempotent ; à passer après 036.
--   1. parametre_envoi_codes : réglage UNIQUE (une ligne, id = 1) :
--      canal (courriel | sms | les_deux), SMTP (serveur, port, TLS
--      starttls | ssl, identifiant, expéditeur), mot de passe SMTP
--      CHIFFRÉ par l'application (protection des données du central,
--      clés dans le dossier du central) : la base ne voit jamais le mot
--      de passe en clair ; modifié par (n° du compte) et le.
--   2. compte_distant : e-mail ET téléphone possibles, au moins un.
--      Constaté : la contrainte ck_compte_distant_contact du 030 le
--      permet déjà ; elle est seulement reposée si elle manquait. Aucune
--      ligne existante n'est lue, modifiée ni supprimée.
--   3. code_second_facteur.canal : « les_deux » permis (contrainte
--      élargie, aucune ligne modifiée).
--   4. Fonctions des écrans (SECURITY DEFINER, search_path fixé, EXECUTE
--      retiré à PUBLIC, accordé à pharma_cloud_ecrans seulement), toutes
--      préfixées cloud_ecran_facteur_ :
--        reglage_lire        éditeur : réglage SANS le mot de passe
--                            (seulement « renseigné oui/non ») ;
--        reglage_enregistrer éditeur, code de second facteur : mot de
--                            passe remplacé seulement s'il est saisi ;
--        reglage_envoi       toute session valable : réglage pour l'envoi
--                            d'un code (mot de passe CHIFFRÉ seulement) ;
--        demander            nouveau code (comme cloud_ecran_creer_code du
--                            030, canal « les_deux » en plus) ;
--        noter_envoi         résultat de l'envoi au journal des accès
--                            (liste fermée, jamais le code ni l'adresse) ;
--        comptes             éditeur : comptes distants, contact MASQUÉ ;
--        compte_contact      éditeur, code de second facteur : e-mail et
--                            téléphone d'un compte (au moins un).
--      Tout est noté au journal des accès (chaîné) et, pour les
--      modifications, au journal de sécurité ; jamais le mot de passe,
--      jamais une adresse ni un numéro.
-- =====================================================================

SET client_encoding = 'UTF8';

DO $version$ BEGIN
    IF to_regprocedure('public.cloud_ecran_noter_action(uuid,bigint,uuid,text,text,text,text)') IS NULL
       OR to_regprocedure('public.cloud_ecran_lignes_absentes_resume(text)') IS NULL THEN
        RAISE EXCEPTION 'Script central 037 : passer d''abord les scripts 030 à 036 (écrans du central). Rien n''a été modifié.';
    END IF;
END $version$;

-- ---- 1. Réglage unique de l'envoi des codes ----
CREATE TABLE IF NOT EXISTS parametre_envoi_codes (
    id                         int         PRIMARY KEY DEFAULT 1 CONSTRAINT ck_parametre_envoi_codes_unique CHECK (id = 1),
    canal                      text        NOT NULL DEFAULT 'courriel' CONSTRAINT ck_parametre_envoi_codes_canal CHECK (canal IN ('courriel', 'sms', 'les_deux')),
    smtp_serveur               text        CONSTRAINT ck_parametre_envoi_codes_serveur CHECK (smtp_serveur ~ '^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$'),
    smtp_port                  int         CONSTRAINT ck_parametre_envoi_codes_port CHECK (smtp_port BETWEEN 1 AND 65535),
    smtp_tls                   text        NOT NULL DEFAULT 'starttls' CONSTRAINT ck_parametre_envoi_codes_tls CHECK (smtp_tls IN ('starttls', 'ssl')),
    smtp_identifiant           text        CONSTRAINT ck_parametre_envoi_codes_identifiant CHECK (length(smtp_identifiant) BETWEEN 1 AND 254 AND smtp_identifiant !~ '[[:cntrl:][:space:]]'),
    smtp_expediteur            text        CONSTRAINT ck_parametre_envoi_codes_expediteur CHECK (length(smtp_expediteur) <= 254 AND smtp_expediteur ~ '^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$'),
    -- Texte chiffré par l'application (protection des données, base64url) : jamais le mot de passe en clair
    smtp_mot_de_passe_chiffre  text        CONSTRAINT ck_parametre_envoi_codes_chiffre CHECK (length(smtp_mot_de_passe_chiffre) BETWEEN 40 AND 4000 AND smtp_mot_de_passe_chiffre ~ '^[A-Za-z0-9_-]+$'),
    modifie_par                uuid        REFERENCES compte_distant (id),
    modifie_le                 timestamptz
);
INSERT INTO parametre_envoi_codes (id) VALUES (1) ON CONFLICT (id) DO NOTHING;
REVOKE ALL ON parametre_envoi_codes FROM PUBLIC, pharma_cloud_api, pharma_cloud_etat, pharma_cloud_ecrans;
COMMENT ON TABLE parametre_envoi_codes IS 'Lot S7.6 : réglage unique de l''envoi des codes de second facteur des écrans (script 037) ; mot de passe SMTP chiffré par l''application seulement.';

-- ---- 2. Comptes distants : e-mail ET téléphone possibles, au moins un (contrainte du 030 reposée si absente) ----
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public.compte_distant'::regclass AND conname = 'ck_compte_distant_contact') THEN
        ALTER TABLE compte_distant ADD CONSTRAINT ck_compte_distant_contact CHECK (telephone IS NOT NULL OR email IS NOT NULL);
    END IF;
END $$;

-- ---- 3. Canal « les_deux » noté pour un code (contrainte élargie, aucune ligne modifiée) ----
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public.code_second_facteur'::regclass AND conname = 'code_second_facteur_canal_check') THEN
        ALTER TABLE code_second_facteur DROP CONSTRAINT code_second_facteur_canal_check;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public.code_second_facteur'::regclass AND conname = 'ck_code_second_facteur_canal') THEN
        ALTER TABLE code_second_facteur ADD CONSTRAINT ck_code_second_facteur_canal CHECK (canal IN ('fichier', 'email', 'sms', 'les_deux'));
    END IF;
END $$;

-- ---------------------------------------------------------------------
-- 4. Fonctions des écrans
-- ---------------------------------------------------------------------

-- Réglage complet pour le canal choisi : SMTP entier (serveur, port, identifiant, expéditeur, mot de passe) pour courriel et
-- les_deux ; rien de plus pour sms (aucun fournisseur : l'envoi le dira)
CREATE OR REPLACE FUNCTION cloud_ecran_facteur_reglage_complet(p parametre_envoi_codes) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path = public, pg_temp AS $$
    SELECT p.canal = 'sms' OR (p.smtp_serveur IS NOT NULL AND p.smtp_port IS NOT NULL AND p.smtp_identifiant IS NOT NULL
                               AND p.smtp_expediteur IS NOT NULL AND p.smtp_mot_de_passe_chiffre IS NOT NULL)
$$;

-- Lecture par l'éditeur : jamais le mot de passe (ni chiffré) ; seulement s'il est renseigné
CREATE OR REPLACE FUNCTION cloud_ecran_facteur_reglage_lire(p_empreinte_jeton text)
RETURNS TABLE (canal text, smtp_serveur text, smtp_port int, smtp_tls text, smtp_identifiant text, smtp_expediteur text,
               mot_de_passe_renseigne boolean, complet boolean, modifie_le timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_editeur(p_empreinte_jeton);
    PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'envoi_codes_lu', 'ok', NULL);
    RETURN QUERY SELECT p.canal, p.smtp_serveur, p.smtp_port, p.smtp_tls, p.smtp_identifiant, p.smtp_expediteur,
                        p.smtp_mot_de_passe_chiffre IS NOT NULL, cloud_ecran_facteur_reglage_complet(p), p.modifie_le
                 FROM parametre_envoi_codes p WHERE p.id = 1;
END $$;

-- Enregistrement par l'éditeur (code de second facteur « action »). Champ SMTP vide : effacé ; mot de passe chiffré NULL ou
-- vide : INCHANGÉ (remplacé seulement s'il est saisi). Courriel ou les_deux exigent un SMTP complet après l'enregistrement
-- (sinon plus aucun code ne partirait). Contrôles de saisie AVANT le code (un code n'est pas consommé pour une faute de frappe).
-- Statuts : ok, canal, serveur, port, tls, identifiant, expediteur, incomplet, puis ceux du code (faux, expire, epuise, aucun).
CREATE OR REPLACE FUNCTION cloud_ecran_facteur_reglage_enregistrer(p_empreinte_jeton text, p_empreinte_code text, p_canal text, p_serveur text,
                                                                 p_port int, p_tls text, p_identifiant text, p_expediteur text,
                                                                 p_mot_de_passe_chiffre text)
RETURNS TABLE (statut text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; p parametre_envoi_codes; r text; v_serveur text := nullif(btrim(coalesce(p_serveur, '')), '');
        v_ident text := nullif(btrim(coalesce(p_identifiant, '')), ''); v_exp text := nullif(btrim(coalesce(p_expediteur, '')), '');
        v_chiffre text := nullif(btrim(coalesce(p_mot_de_passe_chiffre, '')), ''); v_remplace boolean;
BEGIN
    SELECT * INTO v FROM cloud_ecran_editeur(p_empreinte_jeton);
    IF p_canal IS NULL OR p_canal NOT IN ('courriel', 'sms', 'les_deux') THEN RETURN QUERY SELECT 'canal'::text; RETURN; END IF;
    IF v_serveur IS NOT NULL AND v_serveur !~ '^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$' THEN RETURN QUERY SELECT 'serveur'::text; RETURN; END IF;
    IF p_port IS NOT NULL AND p_port NOT BETWEEN 1 AND 65535 THEN RETURN QUERY SELECT 'port'::text; RETURN; END IF;
    IF p_tls IS NULL OR p_tls NOT IN ('starttls', 'ssl') THEN RETURN QUERY SELECT 'tls'::text; RETURN; END IF;
    IF v_ident IS NOT NULL AND (length(v_ident) > 254 OR v_ident ~ '[[:cntrl:][:space:]]') THEN RETURN QUERY SELECT 'identifiant'::text; RETURN; END IF;
    IF v_exp IS NOT NULL AND (length(v_exp) > 254 OR v_exp !~ '^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$') THEN
        RETURN QUERY SELECT 'expediteur'::text; RETURN;
    END IF;
    IF v_chiffre IS NOT NULL AND (length(v_chiffre) NOT BETWEEN 40 AND 4000 OR v_chiffre !~ '^[A-Za-z0-9_-]+$') THEN
        RAISE EXCEPTION 'mot de passe chiffré de forme invalide' USING ERRCODE = '22023';
    END IF;
    SELECT * INTO p FROM parametre_envoi_codes x WHERE x.id = 1 FOR UPDATE;
    v_remplace := v_chiffre IS NOT NULL;
    p.canal := p_canal; p.smtp_serveur := v_serveur; p.smtp_port := p_port; p.smtp_tls := p_tls; p.smtp_identifiant := v_ident;
    p.smtp_expediteur := v_exp; p.smtp_mot_de_passe_chiffre := coalesce(v_chiffre, p.smtp_mot_de_passe_chiffre);
    IF NOT cloud_ecran_facteur_reglage_complet(p) THEN RETURN QUERY SELECT 'incomplet'::text; RETURN; END IF;
    r := cloud_ecran_controler_code(v.session_id, 'action', p_empreinte_code);
    IF r <> 'ok' THEN
        PERFORM cloud_ecran_noter_action(v.compte_id, v.session_id, NULL, 'envoi_codes_modifie', 'refus', r, NULL);
        RETURN QUERY SELECT r; RETURN;
    END IF;
    UPDATE parametre_envoi_codes x SET canal = p.canal, smtp_serveur = p.smtp_serveur, smtp_port = p.smtp_port, smtp_tls = p.smtp_tls,
           smtp_identifiant = p.smtp_identifiant, smtp_expediteur = p.smtp_expediteur, smtp_mot_de_passe_chiffre = p.smtp_mot_de_passe_chiffre,
           modifie_par = v.compte_id, modifie_le = now()
    WHERE x.id = 1;
    -- Journal de sécurité : canal, serveur et port seulement ; jamais l'identifiant, l'expéditeur ni le mot de passe
    PERFORM cloud_ecran_noter_action(v.compte_id, v.session_id, NULL, 'envoi_codes_modifie', 'ok', p.canal,
        'envoi des codes : canal ' || p.canal || coalesce(', SMTP ' || p.smtp_serveur || ':' || p.smtp_port || ' ' || p.smtp_tls, '')
        || CASE WHEN v_remplace THEN ', mot de passe remplacé' ELSE ', mot de passe inchangé' END);
    RETURN QUERY SELECT 'ok'::text;
END $$;

-- Réglage pour l'envoi d'un code (toute session valable : étape « code » à la connexion, session ouverte pour une action ou
-- l'e-mail d'essai) : mot de passe CHIFFRÉ seulement, déchiffré par l'application. Aucune ligne sans session valable.
CREATE OR REPLACE FUNCTION cloud_ecran_facteur_reglage_envoi(p_empreinte_jeton text)
RETURNS TABLE (canal text, smtp_serveur text, smtp_port int, smtp_tls text, smtp_identifiant text, smtp_expediteur text,
               smtp_mot_de_passe_chiffre text, complet boolean)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_session_valable(p_empreinte_jeton);
    IF v.session_id IS NULL THEN RETURN; END IF;
    RETURN QUERY SELECT p.canal, p.smtp_serveur, p.smtp_port, p.smtp_tls, p.smtp_identifiant, p.smtp_expediteur, p.smtp_mot_de_passe_chiffre,
                        cloud_ecran_facteur_reglage_complet(p)
                 FROM parametre_envoi_codes p WHERE p.id = 1;
END $$;

-- Nouveau code de second facteur : mêmes règles que cloud_ecran_creer_code (030 : codes précédents du même objet annulés, 5 codes
-- au plus par compte et par quart d'heure), canal « les_deux » en plus (contact exigé : au moins un des deux).
-- Statuts : envoye, session, trop_de_codes, sans_contact.
CREATE OR REPLACE FUNCTION cloud_ecran_facteur_demander_code(p_empreinte_jeton text, p_objet text, p_empreinte_code text, p_canal text)
RETURNS TABLE (statut text, identifiant text, email text, telephone text, expire_le timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; c compte_distant; v_expire timestamptz := now() + interval '5 minutes'; v_etape text;
BEGIN
    IF p_objet NOT IN ('connexion', 'action') OR p_canal NOT IN ('fichier', 'email', 'sms', 'les_deux') OR p_empreinte_code !~ '^[0-9a-f]{64}$' THEN
        RAISE EXCEPTION 'paramètres du code invalides' USING ERRCODE = '22023';
    END IF;
    SELECT * INTO v FROM cloud_ecran_session_valable(p_empreinte_jeton);
    v_etape := CASE p_objet WHEN 'connexion' THEN 'code' ELSE 'ouverte' END;
    IF v.session_id IS NULL OR v.etape <> v_etape THEN
        RETURN QUERY SELECT 'session'::text, NULL::text, NULL::text, NULL::text, NULL::timestamptz; RETURN;
    END IF;
    SELECT * INTO c FROM compte_distant x WHERE x.id = v.compte_id;
    IF (SELECT count(*) FROM code_second_facteur k WHERE k.compte_id = c.id AND k.cree_le > now() - interval '15 minutes') >= 5 THEN
        PERFORM cloud_ecran_journaliser(c.id, v.session_id, NULL, 'code_demande', 'refus', 'trop_de_codes');
        RETURN QUERY SELECT 'trop_de_codes'::text, NULL::text, NULL::text, NULL::text, NULL::timestamptz; RETURN;
    END IF;
    IF (p_canal = 'email' AND c.email IS NULL) OR (p_canal = 'sms' AND c.telephone IS NULL)
       OR (p_canal = 'les_deux' AND c.email IS NULL AND c.telephone IS NULL) THEN
        PERFORM cloud_ecran_journaliser(c.id, v.session_id, NULL, 'code_demande', 'refus', 'sans_contact');
        RETURN QUERY SELECT 'sans_contact'::text, NULL::text, NULL::text, NULL::text, NULL::timestamptz; RETURN;
    END IF;
    UPDATE code_second_facteur k SET annule_le = now()
    WHERE k.session_id = v.session_id AND k.objet = p_objet AND k.utilise_le IS NULL AND k.annule_le IS NULL;
    INSERT INTO code_second_facteur (compte_id, session_id, objet, empreinte_code, canal, expire_le)
    VALUES (c.id, v.session_id, p_objet, p_empreinte_code, p_canal, v_expire);
    PERFORM cloud_ecran_journaliser(c.id, v.session_id, NULL, 'code_demande', 'ok', p_objet || ':' || p_canal);
    RETURN QUERY SELECT 'envoye'::text, c.identifiant, c.email, c.telephone, v_expire;
END $$;

-- Résultat de l'envoi d'un code ou de l'e-mail d'essai, au journal des accès (liste fermée : canal:résultat ; jamais le code,
-- l'adresse ni le numéro). Rien sans session valable.
CREATE OR REPLACE FUNCTION cloud_ecran_facteur_noter_envoi(p_empreinte_jeton text, p_evenement text, p_objet text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record;
BEGIN
    IF p_evenement IS NULL OR p_evenement NOT IN ('code_envoi', 'envoi_codes_essai')
       OR p_objet IS NULL OR p_objet !~ '^(fichier|email|sms|reglage):(ok|echec|indisponible|sans_contact|incomplet)$' THEN
        RAISE EXCEPTION 'résultat d''envoi invalide' USING ERRCODE = '22023';
    END IF;
    SELECT * INTO v FROM cloud_ecran_session_valable(p_empreinte_jeton);
    IF v.session_id IS NULL THEN RETURN; END IF;
    IF p_evenement = 'envoi_codes_essai' AND v.role <> 'editeur' THEN
        RAISE EXCEPTION 'écran réservé à l''éditeur' USING ERRCODE = '42501';
    END IF;
    PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, p_evenement, CASE WHEN p_objet LIKE '%:ok' THEN 'ok' ELSE 'refus' END, p_objet);
END $$;

-- Comptes distants vus par l'éditeur (gestion du contact) : identifiant, rôle, état, contact MASQUÉ (jamais l'adresse ni le
-- numéro complets), page de 10 à 100
CREATE OR REPLACE FUNCTION cloud_ecran_facteur_comptes(p_empreinte_jeton text, p_page int, p_taille int)
RETURNS TABLE (total bigint, id uuid, identifiant text, role text, actif boolean, email_masque text, telephone_masque text,
               derniere_connexion_le timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; v_taille int := least(greatest(coalesce(p_taille, 20), 10), 100); v_page int := greatest(coalesce(p_page, 1), 1);
BEGIN
    SELECT * INTO v FROM cloud_ecran_editeur(p_empreinte_jeton);
    PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'liste_comptes', 'ok', NULL);
    RETURN QUERY SELECT count(*) OVER (), c.id, c.identifiant, c.role, c.actif,
                        CASE WHEN c.email IS NOT NULL THEN left(c.email, 1) || '***' || substr(c.email, strpos(c.email, '@')) END,
                        CASE WHEN c.telephone IS NOT NULL THEN '***' || right(c.telephone, 2) END,
                        c.derniere_connexion_le
                 FROM compte_distant c ORDER BY c.role, c.identifiant OFFSET (v_page - 1) * v_taille LIMIT v_taille;
END $$;

-- Contact d'un compte modifié par l'éditeur (code de second facteur « action »). Saisie vide : INCHANGÉ ; « - » : retiré.
-- Au moins un contact reste (e-mail ou téléphone). Statuts : ok, inconnu, email, telephone, sans_contact, inchange, puis ceux du
-- code. Journaux : jamais l'adresse ni le numéro (seulement modifié / retiré / inchangé).
CREATE OR REPLACE FUNCTION cloud_ecran_facteur_compte_contact(p_empreinte_jeton text, p_empreinte_code text, p_compte uuid, p_email text, p_telephone text)
RETURNS TABLE (statut text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; c compte_distant; r text; v_email text := btrim(coalesce(p_email, '')); v_tel text := replace(btrim(coalesce(p_telephone, '')), ' ', '');
        n_email text; n_tel text;
BEGIN
    SELECT * INTO v FROM cloud_ecran_editeur(p_empreinte_jeton);
    SELECT * INTO c FROM compte_distant x WHERE x.id = p_compte FOR UPDATE;
    IF NOT FOUND THEN RETURN QUERY SELECT 'inconnu'::text; RETURN; END IF;
    IF v_email <> '' AND v_email <> '-' AND (length(v_email) > 254 OR v_email !~ '^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$') THEN
        RETURN QUERY SELECT 'email'::text; RETURN;
    END IF;
    IF v_tel <> '' AND v_tel <> '-' AND v_tel !~ '^\+?[0-9]{8,15}$' THEN RETURN QUERY SELECT 'telephone'::text; RETURN; END IF;
    n_email := CASE v_email WHEN '' THEN c.email WHEN '-' THEN NULL ELSE v_email END;
    n_tel := CASE v_tel WHEN '' THEN c.telephone WHEN '-' THEN NULL ELSE v_tel END;
    IF n_email IS NULL AND n_tel IS NULL THEN RETURN QUERY SELECT 'sans_contact'::text; RETURN; END IF;
    IF n_email IS NOT DISTINCT FROM c.email AND n_tel IS NOT DISTINCT FROM c.telephone THEN RETURN QUERY SELECT 'inchange'::text; RETURN; END IF;
    r := cloud_ecran_controler_code(v.session_id, 'action', p_empreinte_code);
    IF r <> 'ok' THEN
        PERFORM cloud_ecran_noter_action(v.compte_id, v.session_id, NULL, 'compte_contact_modifie', 'refus', r, NULL);
        RETURN QUERY SELECT r; RETURN;
    END IF;
    UPDATE compte_distant x SET email = n_email, telephone = n_tel WHERE x.id = c.id;
    PERFORM cloud_ecran_noter_action(v.compte_id, v.session_id, NULL, 'compte_contact_modifie', 'ok', 'compte:' || left(c.id::text, 8),
        'contact du compte n° ' || left(c.id::text, 8) || ' : e-mail '
        || CASE WHEN n_email IS NOT DISTINCT FROM c.email THEN 'inchangé' WHEN n_email IS NULL THEN 'retiré' ELSE 'modifié' END
        || ', téléphone ' || CASE WHEN n_tel IS NOT DISTINCT FROM c.telephone THEN 'inchangé' WHEN n_tel IS NULL THEN 'retiré' ELSE 'modifié' END);
    RETURN QUERY SELECT 'ok'::text;
END $$;

-- ---- Droits : rien pour PUBLIC ; écrans : les fonctions cloud_ecran_facteur_* sauf le contrôle interne de complétude ----
REVOKE ALL ON FUNCTION cloud_ecran_facteur_reglage_complet(parametre_envoi_codes), cloud_ecran_facteur_reglage_lire(text),
    cloud_ecran_facteur_reglage_enregistrer(text, text, text, text, int, text, text, text, text), cloud_ecran_facteur_reglage_envoi(text),
    cloud_ecran_facteur_demander_code(text, text, text, text), cloud_ecran_facteur_noter_envoi(text, text, text), cloud_ecran_facteur_comptes(text, int, int),
    cloud_ecran_facteur_compte_contact(text, text, uuid, text, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_ecran_facteur_reglage_lire(text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_facteur_reglage_enregistrer(text, text, text, text, int, text, text, text, text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_facteur_reglage_envoi(text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_facteur_demander_code(text, text, text, text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_facteur_noter_envoi(text, text, text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_facteur_comptes(text, int, int) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_facteur_compte_contact(text, text, uuid, text, text) TO pharma_cloud_ecrans;

COMMENT ON FUNCTION cloud_ecran_facteur_reglage_lire(text) IS 'Lot S7.6 : réglage de l''envoi des codes lu par l''éditeur, sans le mot de passe (script 037).';
COMMENT ON FUNCTION cloud_ecran_facteur_reglage_envoi(text) IS 'Lot S7.6 : réglage pour l''envoi d''un code, mot de passe chiffré seulement (script 037).';
