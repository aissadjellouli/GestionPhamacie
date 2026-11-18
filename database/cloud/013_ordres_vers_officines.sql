-- =====================================================================
-- Base centrale : ordres envoyés aux officines (lots S5.1 et S5.5,
-- 2026-11-18 ; docs/conception-synchronisation.md § 24). Script de la
-- base CENTRALE, à passer après 012 (et après 002 régénéré avec
-- sync_ordre_resultat) par le propriétaire.
--   * ordre_central : un ordre par officine (essai, employe_creer,
--     employe_modifier, employe_bloquer, employe_debloquer, droits_role),
--     auteur obligatoire, valeur ATTENDUE calculée ici depuis les tables
--     miroir (l'officine n'applique que si sa valeur actuelle est égale),
--     expiration à 7 jours ; statut en_attente, a_confirmer, applique,
--     refuse, expire, annule. Jamais le titulaire ni le rôle système ;
--     jamais de mot de passe ni de code comptoir.
--   * ordre_central_journal : chaque changement d'état, ajout seul.
--   * Publication par la voie descendante : élément « ordre_central »,
--     clé = identifiant de l'ordre, format ordre-v1 ; republié à chaque
--     changement d'état décidé ici (annulation, expiration).
--   * Retour : le résultat envoyé par l'officine (miroir
--     sync_ordre_resultat) met à jour l'ordre (déclencheur).
--   * Fonctions d'administration (propriétaire) : cloud_creer_ordre,
--     cloud_annuler_ordre, cloud_expirer_ordres.
-- Idempotent. Aucune donnée existante modifiée.
-- =====================================================================

SET client_encoding = 'UTF8';

CREATE TABLE IF NOT EXISTS ordre_central (
    id          uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    officine_id uuid        NOT NULL REFERENCES officine (id) ON UPDATE CASCADE,
    type        text        NOT NULL,
    cible       text        CHECK (length(cible) <= 100),
    attendu     jsonb       NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(attendu) = 'object'),
    donnees     jsonb       NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(donnees) = 'object'),
    auteur      text        NOT NULL CHECK (length(btrim(auteur)) BETWEEN 1 AND 100),
    cree_le     timestamptz NOT NULL DEFAULT now(),
    expire_le   timestamptz NOT NULL,
    statut      text        NOT NULL DEFAULT 'en_attente',
    motif       text        CHECK (length(motif) <= 500),
    statut_le   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_ordre_central_type CHECK (type IN ('essai', 'employe_creer', 'employe_modifier', 'employe_bloquer', 'employe_debloquer', 'droits_role')),
    CONSTRAINT ck_ordre_central_statut CHECK (statut IN ('en_attente', 'a_confirmer', 'applique', 'refuse', 'expire', 'annule')),
    CONSTRAINT ck_ordre_central_expiration CHECK (expire_le > cree_le)
);
CREATE INDEX IF NOT EXISTS ix_ordre_central_officine ON ordre_central (officine_id, cree_le DESC);
CREATE INDEX IF NOT EXISTS ix_ordre_central_ouverts ON ordre_central (expire_le) WHERE statut IN ('en_attente', 'a_confirmer');

CREATE TABLE IF NOT EXISTS ordre_central_journal (
    id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    officine_id uuid        NOT NULL,
    ordre_id    uuid        NOT NULL REFERENCES ordre_central (id),
    statut      text        NOT NULL,
    motif       text        CHECK (length(motif) <= 500),
    par         text        NOT NULL,
    le          timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_ordre_central_journal_ordre ON ordre_central_journal (ordre_id, id);
DROP TRIGGER IF EXISTS trg_ordre_central_journal_ajout_seul ON ordre_central_journal;
CREATE TRIGGER trg_ordre_central_journal_ajout_seul BEFORE UPDATE OR DELETE ON ordre_central_journal FOR EACH ROW EXECUTE FUNCTION cloud_ajout_seul();
DROP TRIGGER IF EXISTS trg_ordre_central_journal_non_vidable ON ordre_central_journal;
CREATE TRIGGER trg_ordre_central_journal_non_vidable BEFORE TRUNCATE ON ordre_central_journal FOR EACH STATEMENT EXECUTE FUNCTION cloud_ajout_seul();

DO $rls$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['ordre_central', 'ordre_central_journal'] LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS cloisonnement ON %I', t);
        EXECUTE format('CREATE POLICY cloisonnement ON %I USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante())', t);
    END LOOP;
END $rls$;

-- Publication d'un ordre vers son officine (officine courante posée par l'appelant)
CREATE OR REPLACE FUNCTION cloud_ordre_publier(p_id uuid) RETURNS bigint
LANGUAGE plpgsql AS $$
DECLARE o ordre_central;
BEGIN
    SELECT * INTO o FROM ordre_central WHERE id = p_id;
    IF NOT FOUND THEN RAISE EXCEPTION 'Ordre inconnu : %', p_id; END IF;
    RETURN cloud_publier_descendant('ordre_central', o.id::text, jsonb_build_object(
        'format', 'ordre-v1', 'id', o.id, 'type', o.type, 'cible', o.cible, 'attendu', o.attendu, 'donnees', o.donnees,
        'auteur', o.auteur, 'cree_le', o.cree_le, 'expire_le', o.expire_le, 'statut', o.statut));
END $$;

-- Rôle d'une officine par son code (miroir) : id et rôle système ou non
CREATE OR REPLACE FUNCTION cloud_ordre_role(p_code text, OUT id bigint, OUT systeme boolean)
LANGUAGE sql STABLE AS $$
    SELECT r.id, coalesce(r.systeme, false) FROM role r WHERE r.officine_id = cloud_officine_courante() AND r.code = p_code
$$;

-- Création d'un ordre : contrôles, valeur attendue lue dans les tables miroir, journal, publication
CREATE OR REPLACE FUNCTION cloud_creer_ordre(p_officine uuid, p_type text, p_cible text, p_donnees jsonb, p_auteur text) RETURNS uuid
LANGUAGE plpgsql AS $$
DECLARE
    v_id      uuid;
    v_attendu jsonb := '{}'::jsonb;
    v_donnees jsonb := coalesce(p_donnees, '{}'::jsonb);
    v_cible   text := nullif(btrim(coalesce(p_cible, '')), '');
    u         record;
    r         record;
    v_perms   jsonb;
BEGIN
    IF p_auteur IS NULL OR length(btrim(p_auteur)) NOT BETWEEN 1 AND 100 THEN RAISE EXCEPTION 'Auteur de l''ordre obligatoire (1 à 100 caractères).'; END IF;
    IF NOT EXISTS (SELECT 1 FROM officine WHERE id = p_officine AND resiliee_le IS NULL) THEN RAISE EXCEPTION 'Officine inconnue ou résiliée : %', p_officine; END IF;
    IF jsonb_typeof(v_donnees) <> 'object' THEN RAISE EXCEPTION 'Données de l''ordre : objet JSON attendu.'; END IF;
    PERFORM set_config('app.officine_id', p_officine::text, true);

    IF p_type = 'essai' THEN
        v_cible := NULL; v_donnees := '{}'::jsonb;
    ELSIF p_type = 'employe_creer' THEN
        IF coalesce(btrim(v_donnees ->> 'nom'), '') = '' OR length(v_donnees ->> 'nom') > 120 THEN RAISE EXCEPTION 'Nom de l''employé obligatoire (120 caractères au plus).'; END IF;
        IF coalesce(v_donnees ->> 'identifiant', '') !~ '^[A-Za-z0-9._-]{2,40}$' THEN RAISE EXCEPTION 'Identifiant de connexion invalide (lettres, chiffres, point, tiret, 2 à 40 caractères).'; END IF;
        SELECT * INTO r FROM cloud_ordre_role(v_donnees ->> 'role');
        IF r.id IS NULL THEN RAISE EXCEPTION 'Rôle inconnu dans cette officine : %', v_donnees ->> 'role'; END IF;
        IF r.systeme THEN RAISE EXCEPTION 'Le rôle de pharmacien titulaire ne se donne jamais à distance.'; END IF;
        v_cible := NULL;
        v_donnees := jsonb_build_object('nom', btrim(v_donnees ->> 'nom'), 'identifiant', v_donnees ->> 'identifiant', 'role', v_donnees ->> 'role');
    ELSIF p_type IN ('employe_modifier', 'employe_bloquer', 'employe_debloquer') THEN
        IF v_cible IS NULL OR v_cible !~ '^[0-9]{1,18}$' THEN RAISE EXCEPTION 'N° de l''employé attendu.'; END IF;
        SELECT x.id, x.nom_complet, x.actif, rr.code AS role, coalesce(rr.systeme, false) AS systeme INTO u
        FROM utilisateur x LEFT JOIN role rr ON rr.officine_id = x.officine_id AND rr.id = x.role_id
        WHERE x.officine_id = p_officine AND x.id = v_cible::bigint;
        IF u.id IS NULL THEN RAISE EXCEPTION 'Employé n° % inconnu au serveur central pour cette officine.', v_cible; END IF;
        IF u.systeme THEN RAISE EXCEPTION 'Le compte d''un pharmacien titulaire ne se modifie jamais à distance.'; END IF;
        v_attendu := jsonb_strip_nulls(jsonb_build_object('role', u.role, 'actif', u.actif, 'nom', u.nom_complet));
        IF p_type = 'employe_modifier' THEN
            IF v_donnees ? 'role' THEN
                SELECT * INTO r FROM cloud_ordre_role(v_donnees ->> 'role');
                IF r.id IS NULL THEN RAISE EXCEPTION 'Rôle inconnu dans cette officine : %', v_donnees ->> 'role'; END IF;
                IF r.systeme THEN RAISE EXCEPTION 'Le rôle de pharmacien titulaire ne se donne jamais à distance.'; END IF;
            END IF;
            IF v_donnees ? 'nom' AND (coalesce(btrim(v_donnees ->> 'nom'), '') = '' OR length(v_donnees ->> 'nom') > 120) THEN
                RAISE EXCEPTION 'Nom de l''employé invalide (1 à 120 caractères).';
            END IF;
            v_donnees := jsonb_strip_nulls(jsonb_build_object('nom', btrim(v_donnees ->> 'nom'), 'role', v_donnees ->> 'role'));
            IF v_donnees = '{}'::jsonb THEN RAISE EXCEPTION 'Rien à modifier : nom ou rôle attendu.'; END IF;
        ELSE
            IF p_type = 'employe_bloquer' AND u.actif IS FALSE THEN RAISE EXCEPTION 'Employé déjà bloqué.'; END IF;
            IF p_type = 'employe_debloquer' AND u.actif IS TRUE THEN RAISE EXCEPTION 'Employé déjà actif.'; END IF;
            v_donnees := '{}'::jsonb;
        END IF;
    ELSIF p_type = 'droits_role' THEN
        SELECT * INTO r FROM cloud_ordre_role(v_cible);
        IF r.id IS NULL THEN RAISE EXCEPTION 'Rôle inconnu dans cette officine : %', v_cible; END IF;
        IF r.systeme THEN RAISE EXCEPTION 'Les droits du pharmacien titulaire ne se modifient jamais.'; END IF;
        IF jsonb_typeof(v_donnees -> 'permissions') IS DISTINCT FROM 'array'
           OR EXISTS (SELECT 1 FROM jsonb_array_elements(v_donnees -> 'permissions') e WHERE jsonb_typeof(e) <> 'string' OR e #>> '{}' !~ '^[a-z0-9_.]{1,60}$') THEN
            RAISE EXCEPTION 'Liste des droits attendue (codes de permission).';
        END IF;
        SELECT coalesce(jsonb_agg(p.permission_code ORDER BY p.permission_code), '[]'::jsonb) INTO v_perms
        FROM role_permission p WHERE p.officine_id = p_officine AND p.role_id = r.id;
        v_attendu := jsonb_build_object('permissions', v_perms);
        v_donnees := jsonb_build_object('permissions', (SELECT coalesce(jsonb_agg(DISTINCT e #>> '{}' ORDER BY e #>> '{}'), '[]'::jsonb)
                                                       FROM jsonb_array_elements(v_donnees -> 'permissions') e));
    ELSE
        RAISE EXCEPTION 'Type d''ordre inconnu : %', p_type;
    END IF;

    INSERT INTO ordre_central (officine_id, type, cible, attendu, donnees, auteur, expire_le)
    VALUES (p_officine, p_type, v_cible, v_attendu, v_donnees, btrim(p_auteur), now() + interval '7 days')
    RETURNING id INTO v_id;
    INSERT INTO ordre_central_journal (officine_id, ordre_id, statut, par) VALUES (p_officine, v_id, 'en_attente', btrim(p_auteur));
    PERFORM cloud_journaliser(p_officine, NULL, 'ordre_cree', left(format('ordre %s (%s%s) par %s', v_id, p_type,
        CASE WHEN v_cible IS NULL THEN '' ELSE ' ' || v_cible END, btrim(p_auteur)), 500));
    PERFORM cloud_ordre_publier(v_id);
    RETURN v_id;
END $$;

-- Annulation d'un ordre pas encore traité par l'officine
CREATE OR REPLACE FUNCTION cloud_annuler_ordre(p_officine uuid, p_id uuid, p_auteur text) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE o ordre_central;
BEGIN
    IF p_auteur IS NULL OR length(btrim(p_auteur)) NOT BETWEEN 1 AND 100 THEN RAISE EXCEPTION 'Auteur obligatoire.'; END IF;
    PERFORM set_config('app.officine_id', p_officine::text, true);
    SELECT * INTO o FROM ordre_central WHERE id = p_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Ordre % inconnu pour cette officine.', p_id; END IF;
    IF o.statut NOT IN ('en_attente', 'a_confirmer') THEN RAISE EXCEPTION 'Ordre déjà traité (%) : il ne s''annule plus.', o.statut; END IF;
    UPDATE ordre_central SET statut = 'annule', motif = 'annulé par ' || btrim(p_auteur), statut_le = now() WHERE id = p_id;
    INSERT INTO ordre_central_journal (officine_id, ordre_id, statut, motif, par) VALUES (p_officine, p_id, 'annule', NULL, btrim(p_auteur));
    PERFORM cloud_journaliser(p_officine, NULL, 'ordre_annule', left(format('ordre %s par %s', p_id, btrim(p_auteur)), 500));
    PERFORM cloud_ordre_publier(p_id);
END $$;

-- Expiration des ordres non traités après leur date limite (toutes les officines) ; renvoie le nombre d'ordres expirés
CREATE OR REPLACE FUNCTION cloud_expirer_ordres() RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE f record; o record; n int := 0;
BEGIN
    FOR f IN SELECT id FROM officine ORDER BY cree_le LOOP
        PERFORM set_config('app.officine_id', f.id::text, true);
        FOR o IN SELECT id FROM ordre_central WHERE statut IN ('en_attente', 'a_confirmer') AND expire_le <= now() FOR UPDATE LOOP
            UPDATE ordre_central SET statut = 'expire', motif = 'non traité dans les 7 jours', statut_le = now() WHERE id = o.id;
            INSERT INTO ordre_central_journal (officine_id, ordre_id, statut, motif, par) VALUES (f.id, o.id, 'expire', 'non traité dans les 7 jours', 'serveur central');
            PERFORM cloud_ordre_publier(o.id);
            n := n + 1;
        END LOOP;
    END LOOP;
    PERFORM set_config('app.officine_id', '', true);
    RETURN n;
END $$;

-- Retour du résultat de l'officine (miroir sync_ordre_resultat) : l'ordre prend l'état constaté par l'officine
CREATE OR REPLACE FUNCTION cloud_ordre_resultat_recu() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE n int;
BEGIN
    IF NEW.statut IS NULL OR NEW.statut NOT IN ('a_confirmer', 'applique', 'refuse', 'expire', 'annule') THEN RETURN NEW; END IF;
    UPDATE ordre_central SET statut = NEW.statut, motif = left(NEW.motif, 500), statut_le = coalesce(NEW.traite_le, now())
    WHERE id = NEW.ordre_id AND officine_id = NEW.officine_id
      AND statut IS DISTINCT FROM NEW.statut AND statut NOT IN ('applique', 'refuse');
    GET DIAGNOSTICS n = ROW_COUNT;
    IF n > 0 THEN
        INSERT INTO ordre_central_journal (officine_id, ordre_id, statut, motif, par)
        VALUES (NEW.officine_id, NEW.ordre_id, NEW.statut, left(NEW.motif, 500), 'officine');
    END IF;
    RETURN NEW;
END $$;
DO $declencheur$
BEGIN
    IF to_regclass('public.sync_ordre_resultat') IS NOT NULL THEN
        DROP TRIGGER IF EXISTS trg_ordre_resultat_recu ON public.sync_ordre_resultat;
        CREATE TRIGGER trg_ordre_resultat_recu AFTER INSERT OR UPDATE ON public.sync_ordre_resultat
            FOR EACH ROW EXECUTE FUNCTION cloud_ordre_resultat_recu();
    ELSE
        RAISE EXCEPTION 'Table miroir sync_ordre_resultat absente : repasser 002 (régénéré) avant 013.';
    END IF;
END $declencheur$;

REVOKE EXECUTE ON FUNCTION cloud_creer_ordre(uuid, text, text, jsonb, text), cloud_annuler_ordre(uuid, uuid, text), cloud_expirer_ordres(),
    cloud_ordre_publier(uuid) FROM PUBLIC;

COMMENT ON TABLE ordre_central IS 'Ordres envoyés aux officines (format ordre-v1, voie descendante), script 013.';
COMMENT ON TABLE ordre_central_journal IS 'Changements d''état des ordres, ajout seul, script 013.';
