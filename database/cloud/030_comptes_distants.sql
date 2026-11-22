-- =====================================================================
-- Base centrale, lot S7.1 (2026-11-21, go du titulaire) : comptes
-- distants des écrans du serveur central (docs/ecrans-central.md,
-- docs/conception-synchronisation.md § 6, § 6.1, § 6.2). Script de la
-- base CENTRALE, passé par le propriétaire, idempotent ; à passer après
-- 029.
--   1. Rôle pharma_cloud_ecrans créé SANS connexion (NOLOGIN), jamais
--      propriétaire, AUCUN droit sur les tables : seulement EXECUTE sur
--      les fonctions cloud_ecran_* (SECURITY DEFINER, search_path fixé).
--      Une connexion s'y rattache par GRANT (poste de développement :
--      pharma_cloud_ecrans_dev_vs, scripts/dev/preparer_central_vs.ps1).
--   2. compte_distant : rôle « editeur » ou « titulaire », mot de passe
--      BCrypt (coût 12, calculé par l'application ; jamais en clair),
--      mot de passe provisoire à changer, téléphone ou e-mail pour le
--      second facteur, verrouillage (5 échecs : 15 min, puis double à
--      chaque verrouillage, 24 h au plus).
--   3. compte_officine : officines d'un compte titulaire (lot S7.2 ;
--      créée vide ici, l'éditeur n'en a pas besoin).
--   4. session_distante : empreinte SHA-256 du jeton (jamais le jeton) ;
--      éditeur 2 h ; titulaire 30 min d'inactivité, 8 h au plus ;
--      étapes « code » puis « mot_de_passe » (provisoire) puis « ouverte ».
--   5. code_second_facteur : 6 chiffres, seule l'empreinte HMAC (clé du
--      processus des écrans) est gardée ; 5 min ; 5 essais ; usage unique.
--   6. journal_acces_distant : ajout seul (déclencheurs existants),
--      chaîné par SHA-256 (§ 6.1 point 8), sans texte libre ni donnée
--      nominative (compte par son n°, jamais l'identifiant ni le nom).
-- Aucune donnée existante n'est lue, modifiée ni supprimée.
-- =====================================================================

SET client_encoding = 'UTF8';

DO $version$ BEGIN
    IF current_setting('server_version_num')::int < 180000 THEN
        RAISE EXCEPTION 'Script central 030 : PostgreSQL 18 ou plus récent exigé (comme les scripts 020 à 029) ; ce serveur est en version %. Rien n''a été modifié.',
            current_setting('server_version');
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pharma_cloud_etat')
       OR to_regclass('public.chargement') IS NULL OR to_regclass('public.cloud_integrite_bifurcation') IS NULL THEN
        RAISE EXCEPTION 'Script central 030 : passer d''abord les scripts 001 à 029 (rôle pharma_cloud_etat ou tables du central absents). Rien n''a été modifié.';
    END IF;
END $version$;

-- ---- 1. Rôle des écrans (sans connexion, sans droit sur les tables) ----
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pharma_cloud_ecrans') THEN
        CREATE ROLE pharma_cloud_ecrans NOLOGIN NOINHERIT;
    END IF;
END $$;
-- Le propriétaire des objets peut prendre le rôle (tests en transaction annulée), comme pour pharma_cloud_api (001) et pharma_cloud_etat (029)
DO $$ BEGIN
    EXECUTE format('GRANT pharma_cloud_ecrans TO %I WITH SET TRUE', current_user);
EXCEPTION WHEN others THEN
    RAISE NOTICE 'Droit SET sur pharma_cloud_ecrans non accordé à % : %', current_user, SQLERRM;
END $$;

-- ---- 2. Comptes distants ----
CREATE TABLE IF NOT EXISTS compte_distant (
    id                       uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    identifiant              text        NOT NULL UNIQUE CHECK (identifiant ~ '^[a-z0-9][a-z0-9._-]{2,59}$'),
    nom                      text        NOT NULL CHECK (length(btrim(nom)) BETWEEN 1 AND 120),
    role                     text        NOT NULL CHECK (role IN ('editeur', 'titulaire')),
    empreinte_mot_de_passe   text        NOT NULL CHECK (empreinte_mot_de_passe ~ '^\$2[aby]\$1[2-9]\$[./A-Za-z0-9]{53}$'),
    mot_de_passe_provisoire  boolean     NOT NULL DEFAULT true,
    mot_de_passe_change_le   timestamptz,
    telephone                text        CHECK (telephone ~ '^\+?[0-9]{8,15}$'),
    email                    text        CHECK (length(email) <= 254 AND email ~ '^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$'),
    actif                    boolean     NOT NULL DEFAULT true,
    cree_le                  timestamptz NOT NULL DEFAULT now(),
    cree_par                 text        NOT NULL CHECK (length(btrim(cree_par)) BETWEEN 1 AND 100),
    desactive_le             timestamptz,
    echecs                   int         NOT NULL DEFAULT 0 CHECK (echecs >= 0),
    verrouillages            int         NOT NULL DEFAULT 0 CHECK (verrouillages >= 0),
    verrouille_jusqu_au      timestamptz,
    derniere_connexion_le    timestamptz,
    CONSTRAINT ck_compte_distant_contact CHECK (telephone IS NOT NULL OR email IS NOT NULL)
);

-- ---- 3. Officines d'un compte titulaire (lot S7.2 ; vide en S7.1) ----
CREATE TABLE IF NOT EXISTS compte_officine (
    compte_id    uuid        NOT NULL REFERENCES compte_distant (id),
    officine_id  uuid        NOT NULL REFERENCES officine (id) ON UPDATE CASCADE,
    accorde_le   timestamptz NOT NULL DEFAULT now(),
    accorde_par  text        NOT NULL CHECK (length(btrim(accorde_par)) BETWEEN 1 AND 100),
    retire_le    timestamptz,
    PRIMARY KEY (compte_id, officine_id)
);

-- ---- 4. Sessions (empreinte du jeton seulement) ----
CREATE TABLE IF NOT EXISTS session_distante (
    id                    bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    empreinte_jeton       text        NOT NULL UNIQUE CHECK (empreinte_jeton ~ '^[0-9a-f]{64}$'),
    compte_id             uuid        NOT NULL REFERENCES compte_distant (id),
    etape                 text        NOT NULL CHECK (etape IN ('code', 'mot_de_passe', 'ouverte')),
    creee_le              timestamptz NOT NULL DEFAULT now(),
    derniere_activite_le  timestamptz NOT NULL DEFAULT now(),
    expire_le             timestamptz NOT NULL,
    fin_le                timestamptz,
    motif_fin             text        CHECK (motif_fin IN ('deconnexion', 'expiree', 'revoquee', 'remplacee', 'compte_desactive', 'codes_epuises'))
);
CREATE INDEX IF NOT EXISTS ix_session_distante_compte ON session_distante (compte_id) WHERE fin_le IS NULL;

-- ---- 5. Codes de second facteur (empreinte HMAC seulement) ----
CREATE TABLE IF NOT EXISTS code_second_facteur (
    id              bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    compte_id       uuid        NOT NULL REFERENCES compte_distant (id),
    session_id      bigint      NOT NULL REFERENCES session_distante (id),
    objet           text        NOT NULL CHECK (objet IN ('connexion', 'action')),
    empreinte_code  text        NOT NULL CHECK (empreinte_code ~ '^[0-9a-f]{64}$'),
    canal           text        NOT NULL CHECK (canal IN ('fichier', 'email', 'sms')),
    cree_le         timestamptz NOT NULL DEFAULT now(),
    expire_le       timestamptz NOT NULL,
    essais          int         NOT NULL DEFAULT 0 CHECK (essais BETWEEN 0 AND 5),
    utilise_le      timestamptz,
    annule_le       timestamptz,
    CONSTRAINT ck_code_second_facteur_duree CHECK (expire_le > cree_le)
);
CREATE INDEX IF NOT EXISTS ix_code_second_facteur_session ON code_second_facteur (session_id, objet, id DESC);
CREATE INDEX IF NOT EXISTS ix_code_second_facteur_compte ON code_second_facteur (compte_id, cree_le);

-- ---- 6. Journal des accès : ajout seul, chaîné ----
CREATE TABLE IF NOT EXISTS journal_acces_distant (
    id                    bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    le                    timestamptz NOT NULL DEFAULT clock_timestamp(),
    compte_id             uuid,
    session_id            bigint,
    officine_id           uuid,
    evenement             text        NOT NULL CHECK (evenement ~ '^[a-z_]{1,60}$'),
    resultat              text        NOT NULL CHECK (resultat IN ('ok', 'refus')),
    objet                 text        CHECK (objet ~ '^[a-z0-9._:-]{1,80}$'),
    empreinte_precedente  text        CHECK (empreinte_precedente ~ '^[0-9a-f]{64}$'),
    empreinte             text        NOT NULL CHECK (empreinte ~ '^[0-9a-f]{64}$')
);
CREATE INDEX IF NOT EXISTS ix_journal_acces_distant_compte ON journal_acces_distant (compte_id, id DESC);
CREATE INDEX IF NOT EXISTS ix_journal_acces_distant_officine ON journal_acces_distant (officine_id, id DESC);
DROP TRIGGER IF EXISTS trg_journal_acces_distant_ajout_seul ON journal_acces_distant;
CREATE TRIGGER trg_journal_acces_distant_ajout_seul BEFORE UPDATE OR DELETE ON journal_acces_distant FOR EACH ROW EXECUTE FUNCTION cloud_ajout_seul();
DROP TRIGGER IF EXISTS trg_journal_acces_distant_non_vidable ON journal_acces_distant;
CREATE TRIGGER trg_journal_acces_distant_non_vidable BEFORE TRUNCATE ON journal_acces_distant FOR EACH STATEMENT EXECUTE FUNCTION cloud_ajout_seul();

-- Aucun droit direct, pour aucun rôle applicatif (rejeu : mêmes droits)
REVOKE ALL ON compte_distant, compte_officine, session_distante, code_second_facteur, journal_acces_distant
    FROM PUBLIC, pharma_cloud_api, pharma_cloud_etat, pharma_cloud_ecrans;

-- Format canonique d'une ligne du journal des accès (chaînage) : champs joints par « | », date en UTC à la microseconde
CREATE OR REPLACE FUNCTION cloud_ecran_journal_canonique(p_id bigint, p_le timestamptz, p_compte uuid, p_session bigint, p_officine uuid,
                                                         p_evenement text, p_resultat text, p_objet text, p_precedente text)
RETURNS text
LANGUAGE sql IMMUTABLE SET search_path = public, pg_temp AS $$
    SELECT encode(digest(convert_to(concat_ws('|', 'acces-v1', p_id::text,
        to_char(p_le AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'), coalesce(p_compte::text, ''), coalesce(p_session::text, ''),
        coalesce(p_officine::text, ''), p_evenement, p_resultat, coalesce(p_objet, ''), coalesce(p_precedente, '')), 'UTF8'), 'sha256'), 'hex')
$$;

-- Ajoute une ligne au journal des accès (verrou : une écriture à la fois, n° et chaînage dans le même ordre)
CREATE OR REPLACE FUNCTION cloud_ecran_journaliser(p_compte uuid, p_session bigint, p_officine uuid, p_evenement text, p_resultat text, p_objet text)
RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v_id bigint; v_le timestamptz := clock_timestamp(); v_prec text;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended('journal_acces_distant', 0));
    SELECT j.empreinte INTO v_prec FROM journal_acces_distant j ORDER BY j.id DESC LIMIT 1;
    v_id := nextval(pg_get_serial_sequence('journal_acces_distant', 'id'));
    INSERT INTO journal_acces_distant (id, le, compte_id, session_id, officine_id, evenement, resultat, objet, empreinte_precedente, empreinte)
    OVERRIDING SYSTEM VALUE
    VALUES (v_id, v_le, p_compte, p_session, p_officine, p_evenement, p_resultat, p_objet, v_prec,
            cloud_ecran_journal_canonique(v_id, v_le, p_compte, p_session, p_officine, p_evenement, p_resultat, p_objet, v_prec));
END $$;

-- Vérification du chaînage (propriétaire, tests) : nombre de lignes et première ligne rompue (NULL si la chaîne est intacte)
CREATE OR REPLACE FUNCTION cloud_ecran_verifier_journal_acces(OUT lignes bigint, OUT premiere_rupture bigint)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE r journal_acces_distant; v_prec text := NULL;
BEGIN
    lignes := 0;
    FOR r IN SELECT * FROM journal_acces_distant ORDER BY id LOOP
        lignes := lignes + 1;
        IF premiere_rupture IS NULL AND (r.empreinte_precedente IS DISTINCT FROM v_prec OR r.empreinte <> cloud_ecran_journal_canonique(
                r.id, r.le, r.compte_id, r.session_id, r.officine_id, r.evenement, r.resultat, r.objet, r.empreinte_precedente)) THEN
            premiere_rupture := r.id;
        END IF;
        v_prec := r.empreinte;
    END LOOP;
END $$;

-- Durée d'une session ouverte selon le rôle (éditeur : 2 h ; titulaire : 8 h au plus, 30 min d'inactivité contrôlées à part)
CREATE OR REPLACE FUNCTION cloud_ecran_duree_session(p_role text) RETURNS interval
LANGUAGE sql IMMUTABLE SET search_path = public, pg_temp AS $$
    SELECT CASE p_role WHEN 'editeur' THEN interval '2 hours' ELSE interval '8 hours' END
$$;

-- Session valable (fin non posée, échéance et inactivité respectées, compte actif) ; une session échue est fermée et journalisée.
-- Rend la ligne de session et le rôle du compte, ou rien. L'activité est notée (inactivité du titulaire).
-- Garde de rejeu (2026-11-21, script 040) : définition remplacée par le script 040 ; rejouer ce script après 040 ne la
-- ramène pas en arrière (marqueur : fonction cloud_inactivite_session, posée par 040).
DO $rejeu$ BEGIN
    IF to_regprocedure('public.cloud_inactivite_session(text)') IS NOT NULL THEN
        RAISE NOTICE 'cloud_ecran_session_valable : définition plus récente (script 040) gardée';
    ELSE
        EXECUTE $def$
CREATE OR REPLACE FUNCTION cloud_ecran_session_valable(p_empreinte_jeton text, OUT session_id bigint, OUT compte_id uuid, OUT role text, OUT etape text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE s session_distante; c compte_distant;
BEGIN
    IF p_empreinte_jeton IS NULL OR p_empreinte_jeton !~ '^[0-9a-f]{64}$' THEN RETURN; END IF;
    SELECT * INTO s FROM session_distante x WHERE x.empreinte_jeton = p_empreinte_jeton AND x.fin_le IS NULL FOR UPDATE;
    IF NOT FOUND THEN RETURN; END IF;
    SELECT * INTO c FROM compte_distant x WHERE x.id = s.compte_id;
    IF NOT c.actif THEN
        UPDATE session_distante SET fin_le = now(), motif_fin = 'compte_desactive' WHERE id = s.id;
        RETURN;
    END IF;
    IF s.expire_le <= now() OR (c.role = 'titulaire' AND s.etape = 'ouverte' AND s.derniere_activite_le <= now() - interval '30 minutes') THEN
        UPDATE session_distante SET fin_le = now(), motif_fin = 'expiree' WHERE id = s.id;
        PERFORM cloud_ecran_journaliser(c.id, s.id, NULL, 'session_expiree', 'ok', NULL);
        RETURN;
    END IF;
    UPDATE session_distante SET derniere_activite_le = now() WHERE id = s.id;
    session_id := s.id; compte_id := c.id; role := c.role; etape := s.etape;
END $$;
$def$;
    END IF;
END $rejeu$;

-- Session ouverte exigée (étape « ouverte ») ; sinon erreur 28000 (aucune ligne lue, rien d'écrit par l'appelant)
CREATE OR REPLACE FUNCTION cloud_ecran_session_ouverte(p_empreinte_jeton text, OUT session_id bigint, OUT compte_id uuid, OUT role text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_session_valable(p_empreinte_jeton);
    IF v.session_id IS NULL OR v.etape <> 'ouverte' THEN
        RAISE EXCEPTION 'session distante absente, échue ou incomplète' USING ERRCODE = '28000';
    END IF;
    session_id := v.session_id; compte_id := v.compte_id; role := v.role;
END $$;

-- ---------------------------------------------------------------------
-- Fonctions des écrans (rôle pharma_cloud_ecrans)
-- ---------------------------------------------------------------------

-- Connexion, 1re partie : empreinte BCrypt du compte actif (vérifiée par l'application) ; NULL si le compte est verrouillé.
-- Aucune ligne : identifiant inconnu ou compte désactivé (l'application répond le même message que pour un mot de passe faux).
CREATE OR REPLACE FUNCTION cloud_ecran_debut_connexion(p_identifiant text)
RETURNS TABLE (compte_id uuid, empreinte_mot_de_passe text, verrouille_jusqu_au timestamptz)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
    SELECT c.id, CASE WHEN c.verrouille_jusqu_au > now() THEN NULL ELSE c.empreinte_mot_de_passe END,
           CASE WHEN c.verrouille_jusqu_au > now() THEN c.verrouille_jusqu_au END
    FROM compte_distant c WHERE c.identifiant = lower(btrim(p_identifiant)) AND c.actif
$$;

-- Connexion, 2e partie : résultat de la vérification du mot de passe. Échec : compté ; au 5e, verrouillage (15 min, doublé à
-- chaque verrouillage, 24 h au plus). Réussite : session à l'étape « code » (10 min) pour le second facteur.
-- Statuts : code_attendu, refuse, verrouille. p_compte NULL : identifiant inconnu (journalisé sans l'identifiant).
-- Garde de rejeu (2026-11-21, script 040) : définition remplacée par le script 040 ; rejouer ce script après 040 ne la
-- ramène pas en arrière (marqueur : fonction cloud_compte_provisoire_echeance, posée par 040).
DO $rejeu$ BEGIN
    IF to_regprocedure('public.cloud_compte_provisoire_echeance(timestamptz,timestamptz)') IS NOT NULL THEN
        RAISE NOTICE 'cloud_ecran_fin_connexion : définition plus récente (script 040) gardée';
    ELSE
        EXECUTE $def$
CREATE OR REPLACE FUNCTION cloud_ecran_fin_connexion(p_compte uuid, p_reussi boolean, p_empreinte_jeton text)
RETURNS TABLE (statut text, verrouille_jusqu_au timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE c compte_distant; v_session bigint;
BEGIN
    IF p_compte IS NULL THEN
        PERFORM cloud_ecran_journaliser(NULL, NULL, NULL, 'connexion', 'refus', 'identifiant_inconnu');
        RETURN QUERY SELECT 'refuse'::text, NULL::timestamptz; RETURN;
    END IF;
    SELECT * INTO c FROM compte_distant x WHERE x.id = p_compte FOR UPDATE;
    IF NOT FOUND OR NOT c.actif THEN
        PERFORM cloud_ecran_journaliser(p_compte, NULL, NULL, 'connexion', 'refus', 'compte_inactif');
        RETURN QUERY SELECT 'refuse'::text, NULL::timestamptz; RETURN;
    END IF;
    IF c.verrouille_jusqu_au > now() THEN
        PERFORM cloud_ecran_journaliser(c.id, NULL, NULL, 'connexion', 'refus', 'verrouille');
        RETURN QUERY SELECT 'verrouille'::text, c.verrouille_jusqu_au; RETURN;
    END IF;
    IF NOT coalesce(p_reussi, false) THEN
        IF c.echecs + 1 >= 5 THEN
            UPDATE compte_distant x SET echecs = 0, verrouillages = x.verrouillages + 1,
                   verrouille_jusqu_au = now() + least(interval '15 minutes' * power(2, least(x.verrouillages, 10))::int, interval '24 hours')
            WHERE x.id = c.id RETURNING * INTO c;
            PERFORM cloud_ecran_journaliser(c.id, NULL, NULL, 'verrouillage', 'refus', 'echecs_5');
            RETURN QUERY SELECT 'verrouille'::text, c.verrouille_jusqu_au; RETURN;
        END IF;
        UPDATE compte_distant x SET echecs = x.echecs + 1 WHERE x.id = c.id;
        PERFORM cloud_ecran_journaliser(c.id, NULL, NULL, 'connexion', 'refus', 'mot_de_passe');
        RETURN QUERY SELECT 'refuse'::text, NULL::timestamptz; RETURN;
    END IF;
    IF p_empreinte_jeton IS NULL OR p_empreinte_jeton !~ '^[0-9a-f]{64}$' THEN
        RAISE EXCEPTION 'empreinte de jeton invalide' USING ERRCODE = '22023';
    END IF;
    UPDATE compte_distant x SET echecs = 0 WHERE x.id = c.id;
    INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le)
    VALUES (p_empreinte_jeton, c.id, 'code', now() + interval '10 minutes') RETURNING id INTO v_session;
    PERFORM cloud_ecran_journaliser(c.id, v_session, NULL, 'mot_de_passe_verifie', 'ok', NULL);
    RETURN QUERY SELECT 'code_attendu'::text, NULL::timestamptz;
END $$;
$def$;
    END IF;
END $rejeu$;

-- Session courante (chaque requête des écrans) : compte, rôle, étape et échéance ; aucune ligne si elle n'est plus valable.
CREATE OR REPLACE FUNCTION cloud_ecran_session(p_empreinte_jeton text)
RETURNS TABLE (session_id bigint, compte_id uuid, identifiant text, nom text, role text, etape text, expire_le timestamptz,
               mot_de_passe_provisoire boolean)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_session_valable(p_empreinte_jeton);
    IF v.session_id IS NULL THEN RETURN; END IF;
    RETURN QUERY SELECT s.id, c.id, c.identifiant, c.nom, c.role, s.etape, s.expire_le, c.mot_de_passe_provisoire
                 FROM session_distante s JOIN compte_distant c ON c.id = s.compte_id WHERE s.id = v.session_id;
END $$;

-- Nouveau code de second facteur (objet « connexion » à l'étape code, « action » en session ouverte) : les codes précédents
-- non utilisés du même objet sont annulés ; 5 codes au plus par compte et par quart d'heure. Rend le canal et le contact
-- (pour l'envoi par l'application). Statuts : envoye, session, trop_de_codes, sans_contact.
CREATE OR REPLACE FUNCTION cloud_ecran_creer_code(p_empreinte_jeton text, p_objet text, p_empreinte_code text, p_canal text)
RETURNS TABLE (statut text, identifiant text, email text, telephone text, expire_le timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; c compte_distant; v_expire timestamptz := now() + interval '5 minutes'; v_etape text;
BEGIN
    IF p_objet NOT IN ('connexion', 'action') OR p_canal NOT IN ('fichier', 'email', 'sms') OR p_empreinte_code !~ '^[0-9a-f]{64}$' THEN
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
    IF (p_canal = 'email' AND c.email IS NULL) OR (p_canal = 'sms' AND c.telephone IS NULL) THEN
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

-- Contrôle d'un code (interne) : le dernier code de la session et de l'objet ; un essai faux est compté (5 au plus) ;
-- un bon code est consommé (usage unique). Statuts : ok, faux, expire, epuise, aucun.
CREATE OR REPLACE FUNCTION cloud_ecran_controler_code(p_session bigint, p_objet text, p_empreinte_code text) RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE k code_second_facteur;
BEGIN
    SELECT * INTO k FROM code_second_facteur x
    WHERE x.session_id = p_session AND x.objet = p_objet AND x.utilise_le IS NULL AND x.annule_le IS NULL
    ORDER BY x.id DESC LIMIT 1 FOR UPDATE;
    IF NOT FOUND THEN RETURN 'aucun'; END IF;
    IF k.expire_le <= now() THEN RETURN 'expire'; END IF;
    IF k.essais >= 5 THEN RETURN 'epuise'; END IF;
    IF p_empreinte_code IS NULL OR k.empreinte_code <> p_empreinte_code THEN
        UPDATE code_second_facteur SET essais = essais + 1 WHERE id = k.id;
        PERFORM cloud_ecran_journaliser(k.compte_id, p_session, NULL, 'code_faux', 'refus', p_objet);
        RETURN CASE WHEN k.essais + 1 >= 5 THEN 'epuise' ELSE 'faux' END;
    END IF;
    UPDATE code_second_facteur SET utilise_le = now() WHERE id = k.id;
    RETURN 'ok';
END $$;

-- Second facteur de la connexion : bon code → la session de l'étape « code » est remplacée par une NOUVELLE session (nouveau
-- jeton : pas de fixation), à l'étape « mot_de_passe » si le mot de passe est provisoire, sinon « ouverte ».
-- Statuts : ok, faux, expire, epuise (session fermée), aucun, session.
CREATE OR REPLACE FUNCTION cloud_ecran_valider_code_connexion(p_empreinte_jeton text, p_empreinte_code text, p_nouvelle_empreinte_jeton text)
RETURNS TABLE (statut text, etape text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; c compte_distant; r text; v_etape text; v_session bigint;
BEGIN
    SELECT * INTO v FROM cloud_ecran_session_valable(p_empreinte_jeton);
    IF v.session_id IS NULL OR v.etape <> 'code' THEN
        RETURN QUERY SELECT 'session'::text, NULL::text; RETURN;
    END IF;
    r := cloud_ecran_controler_code(v.session_id, 'connexion', p_empreinte_code);
    IF r = 'epuise' THEN
        UPDATE session_distante SET fin_le = now(), motif_fin = 'codes_epuises' WHERE id = v.session_id;
    END IF;
    IF r <> 'ok' THEN
        RETURN QUERY SELECT r, NULL::text; RETURN;
    END IF;
    IF p_nouvelle_empreinte_jeton IS NULL OR p_nouvelle_empreinte_jeton !~ '^[0-9a-f]{64}$' OR p_nouvelle_empreinte_jeton = p_empreinte_jeton THEN
        RAISE EXCEPTION 'nouvelle empreinte de jeton invalide' USING ERRCODE = '22023';
    END IF;
    SELECT * INTO c FROM compte_distant x WHERE x.id = v.compte_id FOR UPDATE;
    v_etape := CASE WHEN c.mot_de_passe_provisoire THEN 'mot_de_passe' ELSE 'ouverte' END;
    UPDATE session_distante SET fin_le = now(), motif_fin = 'remplacee' WHERE id = v.session_id;
    INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le)
    VALUES (p_nouvelle_empreinte_jeton, c.id, v_etape,
            now() + CASE v_etape WHEN 'mot_de_passe' THEN interval '10 minutes' ELSE cloud_ecran_duree_session(c.role) END)
    RETURNING id INTO v_session;
    UPDATE compte_distant SET derniere_connexion_le = now(), verrouillages = 0, echecs = 0 WHERE id = c.id;
    PERFORM cloud_ecran_journaliser(c.id, v_session, NULL, 'connexion', 'ok', c.role);
    RETURN QUERY SELECT 'ok'::text, v_etape;
END $$;

-- Changement du mot de passe (provisoire ou volontaire) : nouvelle empreinte BCrypt (coût 12 au moins, calculée par l'application) ;
-- les autres sessions du compte sont fermées ; la session courante passe à l'étape « ouverte » pour la durée de son rôle.
-- Statuts : ok, session.
CREATE OR REPLACE FUNCTION cloud_ecran_changer_mot_de_passe(p_empreinte_jeton text, p_nouvelle_empreinte_mdp text)
RETURNS TABLE (statut text, etape text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; c compte_distant;
BEGIN
    SELECT * INTO v FROM cloud_ecran_session_valable(p_empreinte_jeton);
    IF v.session_id IS NULL OR v.etape NOT IN ('mot_de_passe', 'ouverte') THEN
        RETURN QUERY SELECT 'session'::text, NULL::text; RETURN;
    END IF;
    IF p_nouvelle_empreinte_mdp IS NULL OR p_nouvelle_empreinte_mdp !~ '^\$2[aby]\$1[2-9]\$[./A-Za-z0-9]{53}$' THEN
        RAISE EXCEPTION 'empreinte BCrypt invalide (coût 12 au moins)' USING ERRCODE = '22023';
    END IF;
    UPDATE compte_distant x SET empreinte_mot_de_passe = p_nouvelle_empreinte_mdp, mot_de_passe_provisoire = false, mot_de_passe_change_le = now()
    WHERE x.id = v.compte_id RETURNING * INTO c;
    UPDATE session_distante s SET fin_le = now(), motif_fin = 'revoquee'
    WHERE s.compte_id = c.id AND s.fin_le IS NULL AND s.id <> v.session_id;
    UPDATE session_distante s SET etape = 'ouverte',
           expire_le = CASE WHEN v.etape = 'mot_de_passe' THEN now() + cloud_ecran_duree_session(c.role) ELSE s.expire_le END
    WHERE s.id = v.session_id;
    PERFORM cloud_ecran_journaliser(c.id, v.session_id, NULL, 'mot_de_passe_change', 'ok', NULL);
    RETURN QUERY SELECT 'ok'::text, 'ouverte'::text;
END $$;

-- Déconnexion : la session est fermée (le jeton ne vaut plus rien) ; rien si elle l'était déjà
CREATE OR REPLACE FUNCTION cloud_ecran_deconnexion(p_empreinte_jeton text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE s session_distante;
BEGIN
    UPDATE session_distante x SET fin_le = now(), motif_fin = 'deconnexion'
    WHERE x.empreinte_jeton = p_empreinte_jeton AND x.fin_le IS NULL RETURNING * INTO s;
    IF FOUND THEN PERFORM cloud_ecran_journaliser(s.compte_id, s.id, NULL, 'deconnexion', 'ok', NULL); END IF;
END $$;

-- Ouverture d'une officine dans la transaction : session ouverte et droit sur l'officine vérifiés PAR LA BASE (éditeur : toute
-- officine existante ; titulaire : officines de compte_officine non retirées), puis officine courante posée (set_config local,
-- comme l'API). Les fonctions d'une officine revérifient le jeton : poser app.officine_id soi-même ne donne aucun accès.
CREATE OR REPLACE FUNCTION cloud_ecran_ouvrir(p_empreinte_jeton text, p_officine uuid) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_session_ouverte(p_empreinte_jeton);
    IF p_officine IS NULL OR NOT EXISTS (SELECT 1 FROM officine o WHERE o.id = p_officine)
       OR (v.role = 'titulaire' AND NOT EXISTS (SELECT 1 FROM compte_officine x WHERE x.compte_id = v.compte_id
                                                AND x.officine_id = p_officine AND x.retire_le IS NULL)) THEN
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'ouverture_officine', 'refus', NULL);
        RETURN false;
    END IF;
    PERFORM set_config('app.officine_id', p_officine::text, true);
    PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, p_officine, 'ouverture_officine', 'ok', NULL);
    RETURN true;
END $$;

-- ---------------------------------------------------------------------
-- Administration des comptes (console du central, connexion du propriétaire ; jamais accordées aux écrans)
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION cloud_compte_distant_creer(p_identifiant text, p_nom text, p_role text, p_empreinte_mdp text,
                                                      p_telephone text, p_email text, p_par text) RETURNS uuid
LANGUAGE plpgsql SET search_path = public, pg_temp AS $$
DECLARE v_id uuid;
BEGIN
    INSERT INTO compte_distant (identifiant, nom, role, empreinte_mot_de_passe, telephone, email, cree_par)
    VALUES (lower(btrim(p_identifiant)), btrim(p_nom), p_role, p_empreinte_mdp, nullif(btrim(p_telephone), ''), nullif(btrim(p_email), ''), btrim(p_par))
    RETURNING id INTO v_id;
    PERFORM cloud_ecran_journaliser(v_id, NULL, NULL, 'compte_cree', 'ok', 'console:' || p_role);
    RETURN v_id;
END $$;

CREATE OR REPLACE FUNCTION cloud_compte_distant_desactiver(p_identifiant text) RETURNS int
LANGUAGE plpgsql SET search_path = public, pg_temp AS $$
DECLARE c compte_distant; n int;
BEGIN
    UPDATE compte_distant x SET actif = false, desactive_le = now() WHERE x.identifiant = lower(btrim(p_identifiant)) AND x.actif RETURNING * INTO c;
    IF NOT FOUND THEN RETURN -1; END IF;
    UPDATE session_distante SET fin_le = now(), motif_fin = 'compte_desactive' WHERE compte_id = c.id AND fin_le IS NULL;
    GET DIAGNOSTICS n = ROW_COUNT;
    PERFORM cloud_ecran_journaliser(c.id, NULL, NULL, 'compte_desactive', 'ok', 'console');
    RETURN n;
END $$;

CREATE OR REPLACE FUNCTION cloud_compte_distant_deverrouiller(p_identifiant text) RETURNS boolean
LANGUAGE plpgsql SET search_path = public, pg_temp AS $$
DECLARE c compte_distant;
BEGIN
    UPDATE compte_distant x SET echecs = 0, verrouillages = 0, verrouille_jusqu_au = NULL
    WHERE x.identifiant = lower(btrim(p_identifiant)) RETURNING * INTO c;
    IF NOT FOUND THEN RETURN false; END IF;
    PERFORM cloud_ecran_journaliser(c.id, NULL, NULL, 'compte_deverrouille', 'ok', 'console');
    RETURN true;
END $$;

-- ---- Droits : rien pour PUBLIC ; les écrans n'exécutent que leurs fonctions ----
DO $droits$
DECLARE f regprocedure;
BEGIN
    FOR f IN SELECT p.oid::regprocedure FROM pg_proc p
             WHERE p.pronamespace = 'public'::regnamespace AND (p.proname LIKE 'cloud\_ecran\_%' OR p.proname LIKE 'cloud\_compte\_distant\_%')
    LOOP
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
    END LOOP;
END $droits$;
GRANT EXECUTE ON FUNCTION cloud_ecran_debut_connexion(text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_fin_connexion(uuid, boolean, text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_session(text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_creer_code(text, text, text, text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_valider_code_connexion(text, text, text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_changer_mot_de_passe(text, text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_deconnexion(text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_ouvrir(text, uuid) TO pharma_cloud_ecrans;
