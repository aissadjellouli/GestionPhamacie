-- =====================================================================
-- Brique d'intégrité des journaux (2026-11-16, lot 2, « go lot 2 »,
-- décision du titulaire : solution mixte)
--   * La BASE chaîne : brique générique réutilisable par plusieurs
--     journaux (journal des ventes au lot 3, registre des psychotropes
--     au lot 8). Une table chaînée est inscrite dans chaine_integrite
--     par integrite_activer(table, libellé, colonnes). Elle reçoit :
--       cle_chaine     text   (clé de chaîne : « officine » ; version
--                             cloud : identifiant de l'officine)
--       rang           bigint (n° de ligne dans la chaîne : 1, 2, 3…)
--       hash           bytea  (empreinte de la ligne)
--       hash_precedent bytea  (empreinte de la ligne précédente ;
--                             origine : octets nuls)
--     Le déclencheur générique zz_integrite_chainer (BEFORE INSERT,
--     dernier par ordre alphabétique) prend un verrou consultatif par
--     journal et clé de chaîne (plusieurs caisses), lit la dernière
--     ligne, puis calcule
--       hash = sha256( UTF-8(représentation canonique) || hash_precedent )
--     avec la fonction intégrée sha256() (aucune extension).
--   * Représentation canonique (format v1, documenté dans
--     docs/base-de-donnees.md) :
--       v1|<journal>|<champ cle_chaine>|<champ rang>|<champ col1>|…
--     chaque champ = nom=longueur:valeur (longueur en caractères) ou
--     nom=~ si NULL ; colonnes dans l'ordre déclaré (pas l'ordre
--     physique) ; horodatage avec fuseau en UTC
--     AAAA-MM-JJTHH:MI:SS.microsecondesZ ; date AAAA-MM-JJ ; booléen
--     true/false ; bytea en hexadécimal ; autres types : texte
--     PostgreSQL (numeric garde son échelle) ; réels flottants refusés.
--   * Une table chaînée refuse UPDATE, DELETE et TRUNCATE.
--   * integrite_verifier(journal) recalcule chaque chaîne et renvoie la
--     première rupture (rang, id, motif, attendu, trouvé).
--   * L'APPLICATION scelle : sceau_integrite (HMAC-SHA256 calculé avec
--     une clé gardée hors de la base) ; verification_integrite garde
--     chaque vérification (tâche nocturne ou « Vérifier maintenant »).
--   * parametre_integrite [À VÉRIFIER] : heure de vérification (3 h),
--     algorithme par défaut (sha256), format d'export du sceau (hex).
--   * integrite_essai : table d'essai interne de la brique (jamais
--     utilisée par l'application ; tests en transaction annulée).
--   Aucune table métier n'est chaînée ici. Aucune donnée existante
--   modifiée. Idempotent. À exécuter après 110.
-- =====================================================================

-- ---- 1. Paramètres [À VÉRIFIER] -------------------------------------------
CREATE TABLE IF NOT EXISTS parametre_integrite (
    id                   smallint    PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    algorithme_defaut    text        NOT NULL DEFAULT 'sha256',
    heure_verification   smallint    NOT NULL DEFAULT 3,
    format_export_sceau  text        NOT NULL DEFAULT 'hex',
    CONSTRAINT ck_parametre_integrite_algorithme CHECK (algorithme_defaut IN ('sha256', 'sha512')),
    CONSTRAINT ck_parametre_integrite_heure CHECK (heure_verification BETWEEN 0 AND 23),
    CONSTRAINT ck_parametre_integrite_format CHECK (format_export_sceau IN ('hex', 'base64'))
);
INSERT INTO parametre_integrite (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

-- ---- 2. Registre des journaux chaînés --------------------------------------
CREATE TABLE IF NOT EXISTS chaine_integrite (
    journal         text        PRIMARY KEY,
    libelle         text        NOT NULL,
    colonnes        text[]      NOT NULL,
    algorithme      text        NOT NULL DEFAULT 'sha256',
    version_format  smallint    NOT NULL DEFAULT 1,
    active_le       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_chaine_integrite_algorithme CHECK (algorithme IN ('sha256', 'sha512')),
    CONSTRAINT ck_chaine_integrite_colonnes CHECK (cardinality(colonnes) > 0)
);

-- Le registre ne se modifie pas (sauf le libellé) et ne se supprime pas : changer les colonnes casserait la vérification
CREATE OR REPLACE FUNCTION integrite_registre_immuable() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND NEW.journal = OLD.journal AND NEW.colonnes = OLD.colonnes AND NEW.algorithme = OLD.algorithme
       AND NEW.version_format = OLD.version_format AND NEW.active_le = OLD.active_le THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'Le registre des journaux chaînés ne peut être ni modifié (sauf le libellé) ni supprimé';
END;
$$;

CREATE OR REPLACE TRIGGER trg_chaine_integrite_immuable
    BEFORE UPDATE OR DELETE ON chaine_integrite
    FOR EACH ROW EXECUTE FUNCTION integrite_registre_immuable();

-- ---- 3. Fonctions de la brique ---------------------------------------------
-- Un champ de la représentation canonique : nom=longueur:valeur, ou nom=~ si NULL
CREATE OR REPLACE FUNCTION integrite_champ(p_nom text, p_valeur text) RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT p_nom || '=' || CASE WHEN p_valeur IS NULL THEN '~' ELSE char_length(p_valeur)::text || ':' || p_valeur END
$$;

-- Empreinte de l'origine d'une chaîne (octets nuls de la longueur de l'algorithme)
CREATE OR REPLACE FUNCTION integrite_origine(p_algorithme text) RETURNS bytea
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT decode(repeat('00', CASE p_algorithme WHEN 'sha512' THEN 64 ELSE 32 END), 'hex')
$$;

-- Empreinte d'une ligne : algorithme( UTF-8(canonique) || empreinte précédente )
CREATE OR REPLACE FUNCTION integrite_hacher(p_algorithme text, p_canonique text, p_precedent bytea) RETURNS bytea
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT CASE p_algorithme
               WHEN 'sha512' THEN sha512(convert_to(p_canonique, 'UTF8') || p_precedent)
               ELSE sha256(convert_to(p_canonique, 'UTF8') || p_precedent)
           END
$$;

-- Expression SQL de la représentation canonique d'une ligne (alias r), construite depuis le registre.
-- Les mêmes expressions servent au déclencheur et à la vérification : le calcul est identique.
CREATE OR REPLACE FUNCTION integrite_expression(p_journal text) RETURNS text
LANGUAGE plpgsql STABLE AS $$
DECLARE
    v_ci     chaine_integrite;
    v_col    record;
    v_val    text;
    v_parts  text[];
    v_trouve int := 0;
BEGIN
    SELECT * INTO v_ci FROM chaine_integrite WHERE journal = p_journal;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Journal % non inscrit dans chaine_integrite', p_journal;
    END IF;
    v_parts := ARRAY[
        quote_literal('v' || v_ci.version_format || '|' || v_ci.journal),
        'integrite_champ(''cle_chaine'', r.cle_chaine)',
        'integrite_champ(''rang'', r.rang::text)'];
    FOR v_col IN
        SELECT u.nom, t.typname
        FROM unnest(v_ci.colonnes) WITH ORDINALITY AS u(nom, ord)
        JOIN pg_attribute a ON a.attrelid = to_regclass(quote_ident(v_ci.journal)) AND a.attname = u.nom AND a.attnum > 0 AND NOT a.attisdropped
        JOIN pg_type t ON t.oid = a.atttypid
        ORDER BY u.ord
    LOOP
        v_trouve := v_trouve + 1;
        v_val := CASE v_col.typname
            WHEN 'timestamptz' THEN format('to_char(r.%I AT TIME ZONE ''UTC'', ''YYYY-MM-DD"T"HH24:MI:SS.US"Z"'')', v_col.nom)
            WHEN 'timestamp'   THEN format('to_char(r.%I, ''YYYY-MM-DD"T"HH24:MI:SS.US'')', v_col.nom)
            WHEN 'date'        THEN format('to_char(r.%I, ''YYYY-MM-DD'')', v_col.nom)
            WHEN 'bool'        THEN format('CASE WHEN r.%I IS NULL THEN NULL WHEN r.%I THEN ''true'' ELSE ''false'' END', v_col.nom, v_col.nom)
            WHEN 'bytea'       THEN format('encode(r.%I, ''hex'')', v_col.nom)
            ELSE format('r.%I::text', v_col.nom)
        END;
        v_parts := v_parts || format('integrite_champ(%L, %s)', v_col.nom, v_val);
    END LOOP;
    IF v_trouve <> cardinality(v_ci.colonnes) THEN
        RAISE EXCEPTION 'Journal % : colonne déclarée absente de la table', p_journal;
    END IF;
    RETURN 'concat_ws(''|'', ' || array_to_string(v_parts, ', ') || ')';
END;
$$;

-- Déclencheur générique BEFORE INSERT : rang, empreinte précédente et empreinte de la ligne, sous verrou consultatif
CREATE OR REPLACE FUNCTION integrite_chainer() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_ci        chaine_integrite;
    v_rang      bigint;
    v_prec      bytea;
    v_canonique text;
BEGIN
    SELECT * INTO v_ci FROM chaine_integrite WHERE journal = TG_TABLE_NAME;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Table % chaînée mais absente de chaine_integrite', TG_TABLE_NAME;
    END IF;
    -- En lecture répétable, la ligne écrite par une autre caisse juste avant ne serait pas vue : la chaîne se casserait
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'Journal % : écriture seulement en isolation « read committed »', TG_TABLE_NAME;
    END IF;
    NEW.cle_chaine := COALESCE(NEW.cle_chaine, 'officine');
    -- Écriture sérialisée par journal et clé de chaîne (plusieurs caisses), verrou rendu à la fin de la transaction
    PERFORM pg_advisory_xact_lock(hashtextextended('integrite|' || TG_TABLE_NAME || '|' || NEW.cle_chaine, 0));
    EXECUTE format('SELECT rang, hash FROM %I WHERE cle_chaine = $1 ORDER BY rang DESC LIMIT 1', TG_TABLE_NAME)
        INTO v_rang, v_prec USING NEW.cle_chaine;
    NEW.rang := COALESCE(v_rang, 0) + 1;
    NEW.hash_precedent := COALESCE(v_prec, integrite_origine(v_ci.algorithme));
    EXECUTE format('SELECT %s FROM (SELECT ($1).*) r', integrite_expression(TG_TABLE_NAME)) INTO v_canonique USING NEW;
    NEW.hash := integrite_hacher(v_ci.algorithme, v_canonique, NEW.hash_precedent);
    RETURN NEW;
END;
$$;

-- Refus de toute modification ou suppression d'une ligne chaînée, et du vidage de la table
CREATE OR REPLACE FUNCTION integrite_interdire_modification() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Journal % chaîné : il ne peut être ni modifié, ni supprimé, ni vidé', TG_TABLE_NAME;
END;
$$;

-- Inscrit une table comme journal chaîné (idempotent) : colonnes de chaînage, index unique et déclencheurs.
-- Les colonnes déclarées ne changent plus ensuite ; une table qui a déjà des lignes est refusée.
CREATE OR REPLACE FUNCTION integrite_activer(p_journal text, p_libelle text, p_colonnes text[], p_algorithme text DEFAULT NULL)
RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    v_table   regclass := to_regclass(quote_ident(p_journal));
    v_existe  chaine_integrite;
    v_algo    text := COALESCE(p_algorithme, (SELECT algorithme_defaut FROM parametre_integrite WHERE id = 1), 'sha256');
    v_lignes  boolean;
    v_flottant text;
BEGIN
    IF v_table IS NULL THEN
        RAISE EXCEPTION 'Table % introuvable', p_journal;
    END IF;
    SELECT * INTO v_existe FROM chaine_integrite WHERE journal = p_journal;
    IF FOUND THEN
        IF v_existe.colonnes <> p_colonnes OR v_existe.algorithme <> v_algo THEN
            RAISE EXCEPTION 'Journal % déjà chaîné avec d''autres colonnes ou un autre algorithme', p_journal;
        END IF;
    ELSE
        EXECUTE format('SELECT EXISTS (SELECT 1 FROM %I)', p_journal) INTO v_lignes;
        IF v_lignes THEN
            RAISE EXCEPTION 'Table % : elle a déjà des lignes, elle ne peut pas être chaînée après coup', p_journal;
        END IF;
        SELECT string_agg(a.attname, ', ') INTO v_flottant
        FROM pg_attribute a JOIN pg_type t ON t.oid = a.atttypid
        WHERE a.attrelid = v_table AND a.attname = ANY (p_colonnes) AND t.typname IN ('float4', 'float8');
        IF v_flottant IS NOT NULL THEN
            RAISE EXCEPTION 'Table % : colonnes en réel flottant refusées (représentation instable) : %', p_journal, v_flottant;
        END IF;
    END IF;

    EXECUTE format('ALTER TABLE %I ADD COLUMN IF NOT EXISTS cle_chaine text NOT NULL DEFAULT %L', p_journal, 'officine');
    EXECUTE format('ALTER TABLE %I ADD COLUMN IF NOT EXISTS rang bigint', p_journal);
    EXECUTE format('ALTER TABLE %I ADD COLUMN IF NOT EXISTS hash bytea', p_journal);
    EXECUTE format('ALTER TABLE %I ADD COLUMN IF NOT EXISTS hash_precedent bytea', p_journal);
    EXECUTE format('CREATE UNIQUE INDEX IF NOT EXISTS %I ON %I (cle_chaine, rang)', 'ux_' || p_journal || '_chaine', p_journal);

    IF v_existe.journal IS NULL THEN
        INSERT INTO chaine_integrite (journal, libelle, colonnes, algorithme) VALUES (p_journal, p_libelle, p_colonnes, v_algo);
        -- Vérifie tout de suite que les colonnes déclarées existent
        PERFORM integrite_expression(p_journal);
    END IF;

    EXECUTE format('CREATE OR REPLACE TRIGGER zz_integrite_chainer BEFORE INSERT ON %I FOR EACH ROW EXECUTE FUNCTION integrite_chainer()', p_journal);
    EXECUTE format('CREATE OR REPLACE TRIGGER trg_integrite_immuable BEFORE UPDATE OR DELETE ON %I FOR EACH ROW EXECUTE FUNCTION integrite_interdire_modification()', p_journal);
    EXECUTE format('CREATE OR REPLACE TRIGGER trg_integrite_non_vidable BEFORE TRUNCATE ON %I FOR EACH STATEMENT EXECUTE FUNCTION integrite_interdire_modification()', p_journal);
END;
$$;

-- Recalcule les chaînes d'un journal (toutes les clés, ou une seule) : une ligne par clé de chaîne,
-- avec la première rupture (rang, id, motif, attendu, trouvé en hexadécimal) ou des colonnes de rupture vides.
CREATE OR REPLACE FUNCTION integrite_verifier(p_journal text, p_cle text DEFAULT NULL)
RETURNS TABLE (cle_chaine text, lignes bigint, dernier_rang bigint, dernier_hash bytea,
               rang_rupture bigint, id_rupture bigint, motif text, attendu text, trouve text)
LANGUAGE plpgsql STABLE AS $$
#variable_conflict use_variable
DECLARE
    v_ci    chaine_integrite;
    v_cle   text;
    v_ligne record;
    v_prec  bytea;
    v_rang  bigint;
    v_calc  bytea;
BEGIN
    SELECT * INTO v_ci FROM chaine_integrite c WHERE c.journal = p_journal;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Journal % non inscrit dans chaine_integrite', p_journal;
    END IF;
    FOR v_cle IN EXECUTE format('SELECT DISTINCT t.cle_chaine FROM %I t WHERE $1 IS NULL OR t.cle_chaine = $1 ORDER BY 1', p_journal) USING p_cle
    LOOP
        cle_chaine := v_cle; lignes := 0; dernier_rang := 0; dernier_hash := NULL;
        rang_rupture := NULL; id_rupture := NULL; motif := NULL; attendu := NULL; trouve := NULL;
        v_prec := integrite_origine(v_ci.algorithme);
        v_rang := 0;
        FOR v_ligne IN EXECUTE format('SELECT r.id, r.rang, r.hash, r.hash_precedent, %s AS canonique FROM %I r WHERE r.cle_chaine = $1 ORDER BY r.rang',
                                      integrite_expression(p_journal), p_journal) USING v_cle
        LOOP
            lignes := lignes + 1;
            IF v_ligne.rang IS DISTINCT FROM v_rang + 1 THEN
                rang_rupture := v_rang + 1; id_rupture := v_ligne.id; motif := 'ligne manquante ou hors chaîne';
                attendu := 'rang ' || (v_rang + 1); trouve := 'rang ' || COALESCE(v_ligne.rang::text, 'vide');
                EXIT;
            END IF;
            IF v_ligne.hash_precedent IS DISTINCT FROM v_prec THEN
                rang_rupture := v_ligne.rang; id_rupture := v_ligne.id; motif := 'chaînage rompu (empreinte précédente)';
                attendu := encode(v_prec, 'hex'); trouve := COALESCE(encode(v_ligne.hash_precedent, 'hex'), 'vide');
                EXIT;
            END IF;
            v_calc := integrite_hacher(v_ci.algorithme, v_ligne.canonique, v_prec);
            IF v_ligne.hash IS DISTINCT FROM v_calc THEN
                rang_rupture := v_ligne.rang; id_rupture := v_ligne.id; motif := 'ligne modifiée (empreinte recalculée différente)';
                attendu := encode(v_calc, 'hex'); trouve := COALESCE(encode(v_ligne.hash, 'hex'), 'vide');
                EXIT;
            END IF;
            v_prec := v_ligne.hash;
            v_rang := v_ligne.rang;
            dernier_rang := v_ligne.rang;
            dernier_hash := v_ligne.hash;
        END LOOP;
        RETURN NEXT;
    END LOOP;
END;
$$;

-- ---- 4. Sceaux (calculés par l'application, clé hors de la base) ------------
CREATE TABLE IF NOT EXISTS sceau_integrite (
    id              bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    journal         text        NOT NULL REFERENCES chaine_integrite(journal),
    cle_chaine      text        NOT NULL DEFAULT 'officine',
    motif           text        NOT NULL,
    periode_du      date        NOT NULL,
    periode_au      date        NOT NULL,
    scelle_le       timestamptz NOT NULL,
    dernier_rang    bigint      NOT NULL,
    dernier_id      bigint,
    dernier_hash    bytea       NOT NULL,
    algorithme      text        NOT NULL DEFAULT 'HMAC-SHA256',
    empreinte_cle   text        NOT NULL,
    sceau           bytea       NOT NULL,
    auteur_id       bigint      REFERENCES utilisateur(id),
    CONSTRAINT ck_sceau_integrite_motif CHECK (motif IN ('cloture_journaliere', 'cloture_mensuelle', 'manuel')),
    CONSTRAINT ck_sceau_integrite_periode CHECK (periode_au >= periode_du),
    CONSTRAINT ck_sceau_integrite_rang CHECK (dernier_rang >= 0)
);
CREATE INDEX IF NOT EXISTS idx_sceau_integrite_journal ON sceau_integrite (journal, cle_chaine, scelle_le);

-- ---- 5. Vérifications (tâche nocturne ou « Vérifier maintenant ») -----------
CREATE TABLE IF NOT EXISTS verification_integrite (
    id              bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    verifie_le      timestamptz NOT NULL DEFAULT now(),
    journal         text        NOT NULL REFERENCES chaine_integrite(journal),
    cle_chaine      text,
    lignes          bigint      NOT NULL DEFAULT 0,
    sceaux          integer     NOT NULL DEFAULT 0,
    statut          text        NOT NULL,
    rang_rupture    bigint,
    motif           text,
    attendu         text,
    trouve          text,
    declenchee_par  bigint      REFERENCES utilisateur(id),
    CONSTRAINT ck_verification_integrite_statut CHECK (statut IN ('ok', 'rupture', 'erreur'))
);
CREATE INDEX IF NOT EXISTS idx_verification_integrite_journal ON verification_integrite (journal, verifie_le DESC);

-- Sceaux et vérifications : ajout seul (ni modification, ni suppression, ni vidage)
CREATE OR REPLACE FUNCTION integrite_interdire_modification_trace() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Les sceaux et vérifications d''intégrité ne peuvent être ni modifiés, ni supprimés, ni vidés';
END;
$$;

CREATE OR REPLACE TRIGGER trg_sceau_integrite_immuable
    BEFORE UPDATE OR DELETE ON sceau_integrite
    FOR EACH ROW EXECUTE FUNCTION integrite_interdire_modification_trace();
CREATE OR REPLACE TRIGGER trg_sceau_integrite_non_vidable
    BEFORE TRUNCATE ON sceau_integrite
    FOR EACH STATEMENT EXECUTE FUNCTION integrite_interdire_modification_trace();
CREATE OR REPLACE TRIGGER trg_verification_integrite_immuable
    BEFORE UPDATE OR DELETE ON verification_integrite
    FOR EACH ROW EXECUTE FUNCTION integrite_interdire_modification_trace();
CREATE OR REPLACE TRIGGER trg_verification_integrite_non_vidable
    BEFORE TRUNCATE ON verification_integrite
    FOR EACH STATEMENT EXECUTE FUNCTION integrite_interdire_modification_trace();

-- ---- 6. Table d'essai interne de la brique ----------------------------------
-- Jamais utilisée par l'application : sert aux tests (transactions annulées). Aucune table métier chaînée dans ce lot.
CREATE TABLE IF NOT EXISTS integrite_essai (
    id          bigint        GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    libelle     text          NOT NULL,
    montant     numeric(12,2),
    cree_le     timestamptz   NOT NULL DEFAULT now()
);
SELECT integrite_activer('integrite_essai', 'Table d''essai de la brique (interne)', ARRAY['id', 'libelle', 'montant', 'cree_le']);
