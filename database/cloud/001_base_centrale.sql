-- =====================================================================
-- Base centrale (serveur central de synchronisation), lot S2 (2026-11-16,
-- docs/conception-synchronisation.md § 12). Script de la base CENTRALE,
-- jamais passé sur la base d'une officine.
--   * Une seule base commune à toutes les officines ; tables miroir des
--     tables envoyées (script 002, généré), chaque ligne porte officine_id
--     (dans la clé primaire). Cloisonnement par Row Level Security :
--     ENABLE + FORCE, rôle applicatif pharma_cloud_api NON propriétaire,
--     officine courante posée par SET LOCAL app.officine_id dans chaque
--     transaction (déduite du certificat, jamais du contenu du lot).
--   * officine, certificat (empreinte, échéance, révocation), code
--     d'activation (usage unique, expiration), époques (dernier lot par
--     officine et époque), lot_recu (lots bruts gardés pour preuve,
--     partitionné par mois), journal de sécurité (ajout seul).
--   * Aucun nom de patient : rien n'en reçoit (codes P-… seulement).
-- Idempotent. Le rôle pharma_cloud_api est créé SANS connexion : le mot de
-- passe et LOGIN se posent à l'installation (docs/mise-en-production.md),
-- jamais dans un script ni dans le dépôt.
-- =====================================================================

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pharma_cloud_api') THEN
        CREATE ROLE pharma_cloud_api NOLOGIN NOINHERIT;
    END IF;
END $$;
-- Le propriétaire des objets peut prendre le rôle applicatif (tests en transaction annulée)
DO $$ BEGIN
    EXECUTE format('GRANT pharma_cloud_api TO %I WITH SET TRUE', current_user);
EXCEPTION WHEN others THEN
    RAISE NOTICE 'Droit SET sur pharma_cloud_api non accordé à % : %', current_user, SQLERRM;
END $$;

CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- Officine courante de la transaction (NULL si non posée : aucune ligne visible)
CREATE OR REPLACE FUNCTION cloud_officine_courante() RETURNS uuid
LANGUAGE sql STABLE AS $$
    SELECT nullif(current_setting('app.officine_id', true), '')::uuid
$$;

-- ---------------------------------------------------------------------
-- Tables d'administration (autorité de certification, outil d'administration).
-- Pas de RLS : le rôle applicatif n'y a AUCUN accès direct, seulement
-- par les fonctions SECURITY DEFINER ci-dessous.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS officine (
    id           uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    libelle      text        NOT NULL CHECK (length(btrim(libelle)) BETWEEN 1 AND 200),
    cree_le      timestamptz NOT NULL DEFAULT now(),
    resiliee_le  timestamptz
);

CREATE TABLE IF NOT EXISTS certificat (
    empreinte         text        PRIMARY KEY CHECK (empreinte ~ '^[0-9a-f]{64}$'),
    officine_id       uuid        NOT NULL REFERENCES officine (id) ON UPDATE CASCADE,
    serveur           text        NOT NULL CHECK (serveur ~ '^[a-z0-9-]{1,40}$'),
    numero_serie      text        NOT NULL,
    emis_le           timestamptz NOT NULL DEFAULT now(),
    echeance          timestamptz NOT NULL,
    revoque_le        timestamptz,
    motif_revocation  text,
    remplace_empreinte text REFERENCES certificat (empreinte)
);
ALTER TABLE certificat ADD COLUMN IF NOT EXISTS remplace_empreinte text REFERENCES certificat (empreinte);
CREATE INDEX IF NOT EXISTS ix_certificat_remplace ON certificat (remplace_empreinte) WHERE remplace_empreinte IS NOT NULL;

-- Réglages du serveur central (ligne unique) : quota d'octets reçus par officine et par jour
CREATE TABLE IF NOT EXISTS cloud_parametre (
    id                  int    PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    quota_octets_jour   bigint NOT NULL DEFAULT 536870912 CHECK (quota_octets_jour > 0),
    renouveler_avant_j  int    NOT NULL DEFAULT 60 CHECK (renouveler_avant_j BETWEEN 1 AND 365)
);
INSERT INTO cloud_parametre DEFAULT VALUES ON CONFLICT (id) DO NOTHING;
CREATE INDEX IF NOT EXISTS ix_certificat_officine ON certificat (officine_id, serveur);

CREATE TABLE IF NOT EXISTS code_activation (
    empreinte_code        text        PRIMARY KEY CHECK (empreinte_code ~ '^[0-9a-f]{64}$'),
    officine_id           uuid        NOT NULL REFERENCES officine (id) ON UPDATE CASCADE,
    serveur               text        NOT NULL CHECK (serveur ~ '^[a-z0-9-]{1,40}$'),
    cree_le               timestamptz NOT NULL DEFAULT now(),
    expire_le             timestamptz NOT NULL,
    utilise_le            timestamptz,
    certificat_empreinte  text REFERENCES certificat (empreinte)
);

CREATE TABLE IF NOT EXISTS journal_securite (
    id                    bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    le                    timestamptz NOT NULL DEFAULT now(),
    officine_id           uuid,
    certificat_empreinte  text,
    evenement             text        NOT NULL CHECK (evenement ~ '^[a-z_]{1,60}$'),
    detail                text        CHECK (length(detail) <= 500)
);
CREATE INDEX IF NOT EXISTS ix_journal_securite_officine ON journal_securite (officine_id, le);

CREATE OR REPLACE FUNCTION cloud_ajout_seul() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% : ajout seul', TG_TABLE_NAME;
END $$;
DROP TRIGGER IF EXISTS trg_journal_securite_ajout_seul ON journal_securite;
CREATE TRIGGER trg_journal_securite_ajout_seul BEFORE UPDATE OR DELETE ON journal_securite FOR EACH ROW EXECUTE FUNCTION cloud_ajout_seul();
DROP TRIGGER IF EXISTS trg_journal_securite_non_vidable ON journal_securite;
CREATE TRIGGER trg_journal_securite_non_vidable BEFORE TRUNCATE ON journal_securite FOR EACH STATEMENT EXECUTE FUNCTION cloud_ajout_seul();

-- Catalogue des tables miroir (rempli par le script 002 généré) : clé locale et colonnes reçues
CREATE TABLE IF NOT EXISTS cloud_table_miroir (
    nom_table  text   PRIMARY KEY,
    cle        text[] NOT NULL,
    colonnes   text[] NOT NULL
);

-- ---------------------------------------------------------------------
-- Données reçues : cloisonnées (RLS forcée)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS officine_epoque (
    officine_id      uuid        NOT NULL,
    epoque           int         NOT NULL CHECK (epoque >= 1),
    dernier_lot      bigint      NOT NULL DEFAULT 0 CHECK (dernier_lot >= 0),
    dernier_recu_le  timestamptz,
    PRIMARY KEY (officine_id, epoque)
);

-- Lots bruts reçus (preuve) : partitionnés par mois de réception. L'unicité (officine, époque, n°) est
-- garantie par officine_epoque (n° = dernier + 1 sous verrou), la clé porte aussi recu_le (partition).
CREATE TABLE IF NOT EXISTS lot_recu (
    officine_id      uuid        NOT NULL,
    epoque           int         NOT NULL,
    numero           bigint      NOT NULL CHECK (numero >= 1),
    empreinte        text        NOT NULL CHECK (empreinte ~ '^[0-9a-f]{64}$'),
    version_contrat  text        NOT NULL,
    nb_lignes        int         NOT NULL CHECK (nb_lignes >= 0),
    octets           int         NOT NULL CHECK (octets >= 0),
    contenu          bytea       NOT NULL,
    recu_le          timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (officine_id, epoque, numero, recu_le)
) PARTITION BY RANGE (recu_le);
CREATE TABLE IF NOT EXISTS lot_recu_defaut PARTITION OF lot_recu DEFAULT;

-- Crée les partitions mensuelles de lot_recu (mois courant - avant .. mois courant + apres) ; renvoie le nombre créé
CREATE OR REPLACE FUNCTION cloud_creer_partitions(p_avant int DEFAULT 1, p_apres int DEFAULT 2) RETURNS int
LANGUAGE plpgsql AS $$
DECLARE d date; n int := 0; nom text;
BEGIN
    FOR i IN -p_avant .. p_apres LOOP
        d := (date_trunc('month', now() AT TIME ZONE 'UTC') + make_interval(months => i))::date;
        nom := 'lot_recu_' || to_char(d, 'YYYY_MM');
        IF to_regclass(nom) IS NULL THEN
            EXECUTE format('CREATE TABLE %I PARTITION OF lot_recu FOR VALUES FROM (%L) TO (%L)',
                           nom, (d::timestamp AT TIME ZONE 'UTC'), ((d + interval '1 month')::timestamp AT TIME ZONE 'UTC'));
            EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', nom);
            n := n + 1;
        END IF;
    END LOOP;
    RETURN n;
END $$;
DO $$ BEGIN
    -- Une partition mensuelle ne peut pas être créée si la partition par défaut contient déjà des lignes de ce mois
    IF NOT EXISTS (SELECT 1 FROM lot_recu_defaut) THEN PERFORM cloud_creer_partitions(1, 2); END IF;
END $$;

ALTER TABLE officine_epoque ENABLE ROW LEVEL SECURITY;
ALTER TABLE officine_epoque FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON officine_epoque;
CREATE POLICY cloisonnement ON officine_epoque USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
ALTER TABLE lot_recu ENABLE ROW LEVEL SECURITY;
ALTER TABLE lot_recu FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON lot_recu;
CREATE POLICY cloisonnement ON lot_recu USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());

-- Lots reçus : ajout seul (preuve)
DROP TRIGGER IF EXISTS trg_lot_recu_ajout_seul ON lot_recu;
CREATE TRIGGER trg_lot_recu_ajout_seul BEFORE UPDATE OR DELETE ON lot_recu FOR EACH ROW EXECUTE FUNCTION cloud_ajout_seul();

-- ---------------------------------------------------------------------
-- Fonctions pour l'API centrale (rôle pharma_cloud_api)
-- ---------------------------------------------------------------------

-- Code d'activation encore valable : officine et serveur (lecture seule, avant signature du certificat)
CREATE OR REPLACE FUNCTION cloud_code_valable(p_empreinte_code text)
RETURNS TABLE (officine_id uuid, serveur text, deja_activee boolean)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
    SELECT c.officine_id, c.serveur, EXISTS (SELECT 1 FROM certificat x WHERE x.officine_id = c.officine_id) FROM code_activation c JOIN officine o ON o.id = c.officine_id
    WHERE c.empreinte_code = p_empreinte_code AND c.utilise_le IS NULL AND c.expire_le > now() AND o.resiliee_le IS NULL
$$;

-- Consomme le code (usage unique) et enregistre le certificat signé ; false si le code n'est plus valable.
-- Code officine : celui de l'officine installée (sync_parametre.officine_id local). À la PREMIÈRE activation, l'officine
-- créée par l'outil d'administration prend ce code (clés étrangères en cascade) ; ensuite il doit être identique (2e serveur).
CREATE OR REPLACE FUNCTION cloud_activer(p_empreinte_code text, p_officine uuid, p_certificat text, p_numero_serie text, p_echeance timestamptz)
RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE c code_activation;
BEGIN
    SELECT * INTO c FROM code_activation WHERE empreinte_code = p_empreinte_code FOR UPDATE;
    IF NOT FOUND OR c.utilise_le IS NOT NULL OR c.expire_le <= now()
       OR EXISTS (SELECT 1 FROM officine o WHERE o.id = c.officine_id AND o.resiliee_le IS NOT NULL) THEN
        RETURN false;
    END IF;
    IF c.officine_id <> p_officine THEN
        IF EXISTS (SELECT 1 FROM certificat x WHERE x.officine_id = c.officine_id) OR EXISTS (SELECT 1 FROM officine o WHERE o.id = p_officine) THEN
            RETURN false;
        END IF;
        UPDATE officine SET id = p_officine WHERE id = c.officine_id;
    END IF;
    INSERT INTO certificat (empreinte, officine_id, serveur, numero_serie, echeance)
    VALUES (p_certificat, p_officine, c.serveur, p_numero_serie, p_echeance);
    UPDATE code_activation SET utilise_le = now(), certificat_empreinte = p_certificat WHERE empreinte_code = p_empreinte_code;
    INSERT INTO journal_securite (officine_id, certificat_empreinte, evenement, detail)
    VALUES (p_officine, p_certificat, 'activation', 'serveur ' || c.serveur);
    RETURN true;
END $$;

-- Renouvellement : nouveau certificat pour le même serveur ; l'ancien reste valable jusqu'à son échéance
CREATE OR REPLACE FUNCTION cloud_renouveler(p_ancien text, p_nouveau text, p_numero_serie text, p_echeance timestamptz)
RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE a certificat; v_avant int;
BEGIN
    SELECT renouveler_avant_j INTO v_avant FROM cloud_parametre WHERE id = 1;
    -- Verrou par (officine, serveur) : deux renouvellements simultanés ne passent pas tous les deux
    PERFORM pg_advisory_xact_lock(hashtextextended('renouvellement|' || p_ancien, 0));
    SELECT * INTO a FROM certificat WHERE empreinte = p_ancien FOR UPDATE;
    IF NOT FOUND OR a.revoque_le IS NOT NULL OR a.echeance <= now() THEN RETURN false; END IF;
    -- Seulement dans les derniers jours avant l'échéance, et seulement depuis le certificat le plus récent du serveur
    IF a.echeance > now() + make_interval(days => coalesce(v_avant, 60)) THEN
        PERFORM cloud_journaliser(a.officine_id, p_ancien, 'renouvellement_refuse', 'échéance trop lointaine');
        RETURN false;
    END IF;
    IF EXISTS (SELECT 1 FROM certificat c WHERE c.officine_id = a.officine_id AND c.serveur = a.serveur AND c.revoque_le IS NULL
               AND c.empreinte <> a.empreinte AND (c.emis_le > a.emis_le OR c.remplace_empreinte = a.empreinte)) THEN
        PERFORM cloud_journaliser(a.officine_id, p_ancien, 'renouvellement_refuse', 'un certificat plus récent existe');
        RETURN false;
    END IF;
    INSERT INTO certificat (empreinte, officine_id, serveur, numero_serie, echeance, remplace_empreinte)
    VALUES (p_nouveau, a.officine_id, a.serveur, p_numero_serie, p_echeance, p_ancien);
    INSERT INTO journal_securite (officine_id, certificat_empreinte, evenement, detail)
    VALUES (a.officine_id, p_nouveau, 'renouvellement', 'remplace ' || left(p_ancien, 16));
    RETURN true;
END $$;

-- Certificat présenté : officine, serveur, état (consulté à chaque appel)
CREATE OR REPLACE FUNCTION cloud_certificat(p_empreinte text)
RETURNS TABLE (officine_id uuid, serveur text, echeance timestamptz, revoque boolean, resiliee boolean)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
    SELECT c.officine_id, c.serveur, c.echeance, c.revoque_le IS NOT NULL, o.resiliee_le IS NOT NULL
    FROM certificat c JOIN officine o ON o.id = c.officine_id WHERE c.empreinte = p_empreinte
$$;

-- Refus répétés (même officine, même certificat, même événement) : une ligne par heure au plus, pas une par appel
-- Garde de rejeu (2026-11-18) : définition remplacée par le script 004 ; rejouer ce script après 004 ne la
-- ramène pas en arrière (marqueur : fonction cloud_lot_sans_colonnes, posé par 004).
DO $rejeu$ BEGIN
    IF to_regprocedure('public.cloud_lot_sans_colonnes(jsonb,text[])') IS NOT NULL THEN
        RAISE NOTICE 'cloud_journaliser : définition plus récente (script 004) gardée';
    ELSE
        EXECUTE $def$
CREATE OR REPLACE FUNCTION cloud_journaliser(p_officine uuid, p_certificat text, p_evenement text, p_detail text)
RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
BEGIN
    IF p_evenement IN ('certificat_refuse', 'lot_refuse', 'quota_depasse', 'activation_refusee', 'renouvellement_refuse')
       AND EXISTS (SELECT 1 FROM journal_securite j
                   WHERE j.evenement = p_evenement AND j.le > now() - interval '1 hour'
                     AND j.officine_id IS NOT DISTINCT FROM p_officine AND j.certificat_empreinte IS NOT DISTINCT FROM p_certificat) THEN
        RETURN;
    END IF;
    INSERT INTO journal_securite (officine_id, certificat_empreinte, evenement, detail)
    VALUES (p_officine, p_certificat, p_evenement, left(p_detail, 500));
END $$;
$def$;
    END IF;
END $rejeu$;
CREATE INDEX IF NOT EXISTS ix_journal_securite_evenement ON journal_securite (evenement, le);

-- Révocation en cascade : un certificat révoqué entraîne ses successeurs (renouvellements obtenus avec lui)
CREATE OR REPLACE FUNCTION cloud_revocation_cascade() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.revoque_le IS NULL AND NEW.revoque_le IS NOT NULL THEN
        UPDATE certificat SET revoque_le = NEW.revoque_le, motif_revocation = 'cascade : ' || coalesce(NEW.motif_revocation, '')
        WHERE remplace_empreinte = NEW.empreinte AND revoque_le IS NULL;
    END IF;
    RETURN NULL;
END $$;
DROP TRIGGER IF EXISTS trg_certificat_revocation_cascade ON certificat;
CREATE TRIGGER trg_certificat_revocation_cascade AFTER UPDATE OF revoque_le ON certificat FOR EACH ROW EXECUTE FUNCTION cloud_revocation_cascade();

-- Révoque tous les certificats d'un serveur d'officine (outil d'administration) ; renvoie le nombre révoqué
CREATE OR REPLACE FUNCTION cloud_revoquer_serveur(p_officine uuid, p_serveur text, p_motif text) RETURNS int
LANGUAGE plpgsql AS $$
DECLARE n int;
BEGIN
    UPDATE certificat SET revoque_le = now(), motif_revocation = p_motif
    WHERE officine_id = p_officine AND serveur = p_serveur AND revoque_le IS NULL;
    GET DIAGNOSTICS n = ROW_COUNT;
    INSERT INTO journal_securite (officine_id, evenement, detail) VALUES (p_officine, 'revocation', 'serveur ' || p_serveur || ' : ' || left(p_motif, 400));
    RETURN n;
END $$;
REVOKE EXECUTE ON FUNCTION cloud_revoquer_serveur(uuid, text, text) FROM PUBLIC;

-- Volume reçu par officine et par jour (quota réglable : cloud_parametre.quota_octets_jour)
CREATE TABLE IF NOT EXISTS officine_volume_jour (
    officine_id  uuid   NOT NULL,
    jour         date   NOT NULL,
    octets       bigint NOT NULL DEFAULT 0,
    PRIMARY KEY (officine_id, jour)
);
ALTER TABLE officine_volume_jour ENABLE ROW LEVEL SECURITY;
ALTER TABLE officine_volume_jour FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON officine_volume_jour;
CREATE POLICY cloisonnement ON officine_volume_jour USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());

-- Dépôt d'un lot sync-v1 pour l'officine courante (RLS) : n° = dernier + 1 par officine et époque ; idempotent.
-- Statuts : accepte, deja_recu (même n° et même empreinte), empreinte_differente (même n°, autre contenu), trou (n° trop grand),
-- quota_depasse (octets reçus du jour au-delà du quota : rien n'est appliqué, l'officine renverra le lot plus tard).
-- Les lignes sont appliquées aux tables miroir : état final = ligne de plus grand (époque, n°) ; suppression = ligne retirée.
-- Suppression : la clé est relue depuis l'id du contrat (valeurs jointes par « | », format sync-v1) et convertie au type de la
-- colonne (comparaison indexée) ; pour une clé composée, l'id reconstitué doit aussi être identique (une valeur contenant « | »
-- ne peut donc jamais retirer une autre ligne : au pire la suppression n'est pas appliquée).
-- Garde de rejeu (2026-11-18) : définition remplacée par le script 003 ; rejouer ce script après 003 ne la
-- ramène pas en arrière (marqueur : table chargement, posé par 003).
DO $rejeu$ BEGIN
    IF to_regclass('public.chargement') IS NOT NULL THEN
        RAISE NOTICE 'cloud_appliquer_lot : définition plus récente (script 003) gardée';
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
            INTO v_set FROM unnest(m.colonnes || ARRAY['sync_epoque', 'sync_numero', 'sync_recu_le']) c WHERE NOT c = ANY (m.cle);
        -- État final de chaque donnée dans ce lot (plus grand n° de ligne)
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

-- Dernier lot reçu (reprise après coupure)
CREATE OR REPLACE FUNCTION cloud_dernier_lot(p_epoque int) RETURNS bigint
LANGUAGE sql STABLE AS $$
    SELECT coalesce((SELECT dernier_lot FROM officine_epoque WHERE officine_id = cloud_officine_courante() AND epoque = p_epoque), 0)
$$;

-- ---------------------------------------------------------------------
-- Droits du rôle applicatif (jamais propriétaire, jamais d'accès direct à l'administration)
-- ---------------------------------------------------------------------
REVOKE ALL ON officine, certificat, code_activation, journal_securite FROM pharma_cloud_api;
GRANT SELECT ON cloud_table_miroir, cloud_parametre TO pharma_cloud_api;
GRANT SELECT, INSERT, UPDATE ON officine_volume_jour TO pharma_cloud_api;
GRANT SELECT, INSERT, UPDATE ON officine_epoque TO pharma_cloud_api;
GRANT SELECT, INSERT ON lot_recu TO pharma_cloud_api;
GRANT EXECUTE ON FUNCTION cloud_officine_courante(), cloud_code_valable(text), cloud_activer(text, uuid, text, text, timestamptz),
    cloud_renouveler(text, text, text, timestamptz), cloud_certificat(text), cloud_journaliser(uuid, text, text, text),
    cloud_appliquer_lot(int, bigint, text, text, bytea), cloud_dernier_lot(int) TO pharma_cloud_api;
REVOKE EXECUTE ON FUNCTION cloud_creer_partitions(int, int) FROM PUBLIC;

COMMENT ON TABLE officine IS 'Serveur central : officines inscrites (aucun nom de personne).';
COMMENT ON TABLE certificat IS 'Serveur central : certificats clients délivrés (empreinte SHA-256, échéance, révocation), un par serveur d''officine.';
COMMENT ON TABLE code_activation IS 'Serveur central : codes d''activation à usage unique (seule leur empreinte est gardée), expiration 24 h.';
COMMENT ON TABLE lot_recu IS 'Serveur central : lots bruts reçus (preuve), partitionnés par mois, ajout seul, cloisonnés par officine.';
COMMENT ON TABLE journal_securite IS 'Serveur central : journal de sécurité (activations, refus, révocations), ajout seul, sans donnée personnelle.';
