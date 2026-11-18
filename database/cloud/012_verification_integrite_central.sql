-- =====================================================================
-- Base centrale : vérification d'intégrité des données reçues (lot S4,
-- 2026-11-17, option A ; docs/conception-synchronisation.md § 22).
-- Script de la base CENTRALE, à passer après 011 par le propriétaire.
--   * cloud_chaine_reference : copie, au central, du registre des journaux
--     chaînés de l'officine (scripts officine 111, 112, 117) : colonnes
--     protégées dans l'ordre, version de format, mode de vérification
--     (« complet » : empreintes recalculées ; « liaison » : rangs et
--     chaînage seulement, registre des psychotropes dont les colonnes du
--     porteur ne partent pas), colonnes de texte filtrées, colonne de date.
--   * Ajout seul au central des journaux chaînés, sceaux, clôtures et
--     vérifications : une ligne reçue avec un autre contenu n'est PAS
--     appliquée (l'original reste, preuve) et une anomalie est notée ; une
--     suppression reçue n'est pas appliquée non plus. Renvoi identique
--     (chargement complet) accepté ; texte filtré différent seul : gardé
--     sans anomalie (le filtre dépend des listes de l'officine).
--   * Vérification incrémentale (cloud_verifier_integrite) : rangs sans
--     trou, origine, chaînage, empreintes (mode complet), ancrages des
--     sceaux, continuité des tickets « T- » (compteur_ticket, script
--     officine 144), vérification croisée avec la dernière vérification de
--     l'officine (verification_integrite.dernier_rang / dernier_hash).
--     Un manque n'est une anomalie qu'après le délai d'attente (24 h).
--   * Résultat publié vers l'officine par la voie descendante :
--     reglage_central / integrite_centrale (format integrite-v1).
--   * Lancement : Pharma.Cloud.Api.exe --verifier-integrite [--complete]
--     (tâche planifiée, toutes les 15 min ; complète chaque semaine).
-- Idempotent. Aucune donnée existante modifiée.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql (sous Windows, une sortie
-- redirigée fait lire le fichier en WIN1252 et abîme les accents des textes et des motifs).
SET client_encoding = 'UTF8';

-- ---- 1. Réglage : délai avant qu'un manque devienne une anomalie ----
ALTER TABLE cloud_parametre ADD COLUMN IF NOT EXISTS integrite_delai_heures integer NOT NULL DEFAULT 24;
DO $garde$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_cloud_parametre_integrite_delai') THEN
        ALTER TABLE cloud_parametre ADD CONSTRAINT ck_cloud_parametre_integrite_delai CHECK (integrite_delai_heures BETWEEN 1 AND 720);
    END IF;
END $garde$;
COMMENT ON COLUMN cloud_parametre.integrite_delai_heures IS 'Heures d''attente avant qu''une ligne, un ticket ou une ligne scellée absents deviennent une anomalie (script 012).';

-- ---- 2. Référentiel des journaux chaînés (copie des scripts officine) ----
CREATE TABLE IF NOT EXISTS cloud_chaine_reference (
    journal            text    PRIMARY KEY CHECK (journal ~ '^[a-z_]{1,63}$'),
    libelle            text    NOT NULL,
    version_format     integer NOT NULL CHECK (version_format >= 1),
    mode               text    NOT NULL CHECK (mode IN ('complet', 'liaison')),
    colonnes           text[]  NOT NULL CHECK (cardinality(colonnes) >= 1),
    colonnes_filtrees  text[]  NOT NULL DEFAULT '{}',
    colonne_date       text    NOT NULL
);
INSERT INTO cloud_chaine_reference AS r (journal, libelle, version_format, mode, colonnes, colonnes_filtrees, colonne_date) VALUES
    ('journal_vente', 'Journal des ventes (fiscal)', 1, 'complet',
     ARRAY['id', 'evenement', 'numero_ticket', 'vente_id', 'annulation_id', 'retour_client_id', 'avoir_mouvement_id', 'regularisation_id',
           'survenu_le', 'enregistre_le', 'session_caisse_id', 'poste', 'utilisateur_id', 'type_vente', 'montant_ttc', 'montant_ht',
           'montant_tva', 'detail_tva', 'part_patient', 'part_organisme', 'paiements', 'montant_mouvement', 'motif'],
     ARRAY['motif'], 'enregistre_le'),
    ('journal_registre_psychotrope', 'Registre des psychotropes', 1, 'liaison',
     ARRAY['id', 'nature', 'registre_id', 'numero_ordre', 'lot_id', 'ligne_vente_id', 'mouvement_id', 'type_mouvement', 'quantite',
           'solde_apres', 'patient_id', 'medecin_id', 'ordonnance_id', 'utilisateur_id', 'reference', 'porteur_nom', 'porteur_adresse',
           'porteur_piece', 'inscrit_le', 'lignes_anterieures', 'empreinte_anterieure', 'dernier_registre_id', 'annulation_id', 'motif', 'cree_le'],
     ARRAY['motif'], 'cree_le')
ON CONFLICT (journal) DO UPDATE SET libelle = EXCLUDED.libelle, version_format = EXCLUDED.version_format, mode = EXCLUDED.mode,
    colonnes = EXCLUDED.colonnes, colonnes_filtrees = EXCLUDED.colonnes_filtrees, colonne_date = EXCLUDED.colonne_date
WHERE (r.libelle, r.version_format, r.mode, r.colonnes, r.colonnes_filtrees, r.colonne_date)
      IS DISTINCT FROM (EXCLUDED.libelle, EXCLUDED.version_format, EXCLUDED.mode, EXCLUDED.colonnes, EXCLUDED.colonnes_filtrees, EXCLUDED.colonne_date);

-- Marqueurs laissés par le filtre des textes libres de l'officine (scripts officine 127, 138) : texte non recalculable
CREATE OR REPLACE FUNCTION cloud_integrite_marqueurs() RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT '\[(COURRIEL|TÉL|N°|PERSONNEL|NOM)\]|P-[0-9a-f]{24}'
$$;

-- ---- 3. Anomalies (ajout seul) et état de la vérification ----
CREATE TABLE IF NOT EXISTS cloud_anomalie_integrite (
    id           bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    officine_id  uuid        NOT NULL,
    journal      text        NOT NULL,
    cle_chaine   text,
    rang         bigint,
    cle_ligne    text,
    nature       text        NOT NULL,
    detail       text        CHECK (length(detail) <= 500),
    attendu      text,
    trouve       text,
    detectee_le  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_cloud_anomalie_integrite_nature CHECK (nature IN (
        'modification_refusee', 'suppression_refusee', 'ligne_manquante', 'algorithme_inconnu', 'chainage_rompu', 'ligne_modifiee',
        'sceau_sans_ligne', 'sceau_incoherent', 'ticket_manquant', 'ticket_hors_compteur', 'vente_sans_ligne', 'annulation_sans_ligne',
        'reecriture_apres_envoi', 'chaine_officine_plus_courte'))
);
-- Une anomalie n'est notée qu'une fois (les passages suivants la retrouvent sans la répéter)
CREATE UNIQUE INDEX IF NOT EXISTS ux_cloud_anomalie_integrite ON cloud_anomalie_integrite
    (officine_id, journal, nature, coalesce(cle_chaine, ''), coalesce(rang, -1), coalesce(cle_ligne, ''));
CREATE INDEX IF NOT EXISTS ix_cloud_anomalie_integrite_officine ON cloud_anomalie_integrite (officine_id, id DESC);
ALTER TABLE cloud_anomalie_integrite ENABLE ROW LEVEL SECURITY;
ALTER TABLE cloud_anomalie_integrite FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON cloud_anomalie_integrite;
CREATE POLICY cloisonnement ON cloud_anomalie_integrite USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
DROP TRIGGER IF EXISTS trg_cloud_anomalie_integrite_ajout_seul ON cloud_anomalie_integrite;
CREATE TRIGGER trg_cloud_anomalie_integrite_ajout_seul BEFORE UPDATE OR DELETE ON cloud_anomalie_integrite FOR EACH ROW EXECUTE FUNCTION cloud_ajout_seul();
DROP TRIGGER IF EXISTS trg_cloud_anomalie_integrite_non_vidable ON cloud_anomalie_integrite;
CREATE TRIGGER trg_cloud_anomalie_integrite_non_vidable BEFORE TRUNCATE ON cloud_anomalie_integrite FOR EACH STATEMENT EXECUTE FUNCTION cloud_ajout_seul();

-- Point de reprise par chaîne (officine, journal, clé de chaîne)
CREATE TABLE IF NOT EXISTS cloud_integrite_etat (
    officine_id               uuid        NOT NULL,
    journal                   text        NOT NULL,
    cle_chaine                text        NOT NULL,
    algorithme                text        CHECK (algorithme IN ('sha256', 'sha512')),
    dernier_rang              bigint      NOT NULL DEFAULT 0 CHECK (dernier_rang >= 0),
    dernier_hash              bytea,
    statut                    text        NOT NULL CHECK (statut IN ('ok', 'attente', 'rupture')),
    rang_rupture              bigint,
    non_verifiables           bigint      NOT NULL DEFAULT 0 CHECK (non_verifiables >= 0),
    verifie_le                timestamptz NOT NULL DEFAULT now(),
    verification_complete_le  timestamptz,
    PRIMARY KEY (officine_id, journal, cle_chaine)
);
-- Ancrage de chaque sceau reçu
CREATE TABLE IF NOT EXISTS cloud_integrite_sceau (
    officine_id  uuid        NOT NULL,
    sceau_id     bigint      NOT NULL,
    statut       text        NOT NULL CHECK (statut IN ('ok', 'attente', 'incoherent', 'non_verifiable')),
    verifie_le   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (officine_id, sceau_id)
);
-- Points de reprise par officine (tickets, ventes, vérifications de l'officine) et dernier passage
CREATE TABLE IF NOT EXISTS cloud_integrite_officine (
    officine_id                uuid        PRIMARY KEY,
    tickets_jusqu_a            bigint      NOT NULL DEFAULT 0,
    ventes_controlees_jusqu_au timestamptz,
    derniere_verification_id   bigint      NOT NULL DEFAULT 0,
    verifie_le                 timestamptz,
    verification_complete_le   timestamptz
);
DO $rls$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['cloud_integrite_etat', 'cloud_integrite_sceau', 'cloud_integrite_officine'] LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS cloisonnement ON %I', t);
        EXECUTE format('CREATE POLICY cloisonnement ON %I USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante())', t);
    END LOOP;
END $rls$;

-- Note une anomalie (une seule fois) et l'inscrit au journal de sécurité ; renvoie vrai si elle est nouvelle
CREATE OR REPLACE FUNCTION cloud_integrite_noter(p_journal text, p_cle_chaine text, p_rang bigint, p_cle_ligne text, p_nature text,
                                                 p_detail text, p_attendu text DEFAULT NULL, p_trouve text DEFAULT NULL) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v_off uuid := cloud_officine_courante(); n int;
BEGIN
    IF v_off IS NULL THEN RAISE EXCEPTION 'Officine courante non posée (app.officine_id)'; END IF;
    INSERT INTO cloud_anomalie_integrite (officine_id, journal, cle_chaine, rang, cle_ligne, nature, detail, attendu, trouve)
    VALUES (v_off, p_journal, p_cle_chaine, p_rang, left(p_cle_ligne, 200), p_nature, left(p_detail, 500), left(p_attendu, 200), left(p_trouve, 200))
    ON CONFLICT DO NOTHING;
    GET DIAGNOSTICS n = ROW_COUNT;
    IF n > 0 THEN
        PERFORM cloud_journaliser(v_off, NULL, 'integrite_anomalie',
            left(format('%s : %s%s', p_journal, p_nature, CASE WHEN p_rang IS NULL THEN '' ELSE ' (rang ' || p_rang || ')' END), 500));
    END IF;
    RETURN n > 0;
END $$;
REVOKE EXECUTE ON FUNCTION cloud_integrite_noter(text, text, bigint, text, text, text, text, text) FROM PUBLIC;

-- ---- 4. Ajout seul des journaux chaînés et de leurs pièces au central ----
-- Garde de rejeu (2026-11-18) : définition remplacée par le script 021 ; rejouer ce script après 021 ne la
-- ramène pas en arrière (marqueur : table journal_depart_officine, posé par 021).
DO $rejeu$ BEGIN
    IF to_regclass('public.journal_depart_officine') IS NOT NULL THEN
        RAISE NOTICE 'cloud_integrite_ajout_seul : définition plus récente (script 021) gardée';
    ELSE
        EXECUTE $def$
CREATE OR REPLACE FUNCTION cloud_integrite_ajout_seul() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE
    v_sync    text[] := ARRAY['sync_epoque', 'sync_numero', 'sync_recu_le', 'sync_chargement', 'sync_absente_le'];
    v_avant   jsonb;
    v_apres   jsonb;
    v_diff    text[];
    v_filtres text[];
BEGIN
    IF TG_OP = 'UPDATE' THEN
        v_avant := to_jsonb(OLD) - v_sync;
        v_apres := to_jsonb(NEW) - v_sync;
        IF v_avant = v_apres THEN RETURN NEW; END IF;   -- renvoi identique (chargement complet), marques du chargement
        SELECT array_agg(k ORDER BY k) INTO v_diff FROM jsonb_object_keys(v_avant || v_apres) k
        WHERE v_avant -> k IS DISTINCT FROM v_apres -> k;
        SELECT colonnes_filtrees INTO v_filtres FROM cloud_chaine_reference WHERE journal = TG_TABLE_NAME;
        IF v_filtres IS NOT NULL AND v_diff <@ v_filtres THEN
            RETURN NULL;   -- seul un texte filtré diffère (listes de l'officine changées) : l'original est gardé, sans anomalie
        END IF;
        PERFORM cloud_integrite_noter(TG_TABLE_NAME, v_avant ->> 'cle_chaine', (v_avant ->> 'rang')::bigint,
            (v_avant ->> 'id') || '@' || NEW.sync_epoque || '/' || NEW.sync_numero, 'modification_refusee',
            format('ligne %s reçue avec un autre contenu (colonnes : %s) : version d''origine gardée', v_avant ->> 'id', array_to_string(v_diff, ', ')));
        RETURN NULL;
    END IF;
    -- DELETE
    v_avant := to_jsonb(OLD);
    PERFORM cloud_integrite_noter(TG_TABLE_NAME, v_avant ->> 'cle_chaine', (v_avant ->> 'rang')::bigint, v_avant ->> 'id', 'suppression_refusee',
        format('suppression de la ligne %s reçue : ligne gardée', v_avant ->> 'id'));
    RETURN NULL;
END $$;
$def$;
    END IF;
END $rejeu$;
DO $declencheurs$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['journal_vente', 'journal_registre_psychotrope', 'sceau_integrite', 'cloture_fiscale', 'verification_integrite'] LOOP
        IF to_regclass('public.' || t) IS NOT NULL THEN
            EXECUTE format('DROP TRIGGER IF EXISTS trg_cloud_integrite_ajout_seul ON public.%I', t);
            EXECUTE format('CREATE TRIGGER trg_cloud_integrite_ajout_seul BEFORE UPDATE OR DELETE ON public.%I '
                           'FOR EACH ROW EXECUTE FUNCTION cloud_integrite_ajout_seul()', t);
        END IF;
    END LOOP;
END $declencheurs$;

-- ---- 5. Représentation canonique (même calcul que l'officine, script 111) ----
CREATE OR REPLACE FUNCTION cloud_integrite_champ(p_nom text, p_valeur text) RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT p_nom || '=' || CASE WHEN p_valeur IS NULL THEN '~' ELSE char_length(p_valeur)::text || ':' || p_valeur END
$$;

-- Expression SQL de la forme canonique v<n> d'une ligne (alias r) de la table miroir, d'après le référentiel et les types de la table
CREATE OR REPLACE FUNCTION cloud_integrite_expression(p_journal text) RETURNS text
LANGUAGE plpgsql STABLE AS $$
DECLARE
    ref      cloud_chaine_reference;
    v_col    record;
    v_val    text;
    v_parts  text[];
    v_trouve int := 0;
BEGIN
    SELECT * INTO ref FROM cloud_chaine_reference WHERE journal = p_journal;
    IF NOT FOUND THEN RAISE EXCEPTION 'Journal % absent du référentiel cloud_chaine_reference', p_journal; END IF;
    v_parts := ARRAY[
        quote_literal('v' || ref.version_format || '|' || ref.journal),
        'cloud_integrite_champ(''cle_chaine'', r.cle_chaine)',
        'cloud_integrite_champ(''rang'', r.rang::text)'];
    FOR v_col IN
        SELECT u.nom, t.typname
        FROM unnest(ref.colonnes) WITH ORDINALITY AS u(nom, ord)
        JOIN pg_attribute a ON a.attrelid = to_regclass('public.' || quote_ident(ref.journal)) AND a.attname = u.nom AND a.attnum > 0 AND NOT a.attisdropped
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
        v_parts := v_parts || format('cloud_integrite_champ(%L, %s)', v_col.nom, v_val);
    END LOOP;
    IF v_trouve <> cardinality(ref.colonnes) THEN
        RAISE EXCEPTION 'Journal % : colonne du référentiel absente de la table miroir', p_journal;
    END IF;
    RETURN 'concat_ws(''|'', ' || array_to_string(v_parts, ', ') || ')';
END $$;

-- ---- 6. Vérification d'une chaîne (officine courante), à partir du point de reprise ----
-- Garde de rejeu (2026-11-18, lot S6.5) : définition remplacée par le script 023 ; rejouer ce script après 023 ne la
-- ramène pas en arrière (marqueur : fonction cloud_integrite_verifier_branches, posée par 023).
DO $rejeu$ BEGIN
    IF to_regprocedure('cloud_integrite_verifier_branches(text,text,interval)') IS NOT NULL THEN
        RAISE NOTICE 'cloud_integrite_verifier_chaine : définition plus récente (script 023) gardée';
    ELSE
        EXECUTE $def$
CREATE OR REPLACE FUNCTION cloud_integrite_verifier_chaine(p_journal text, p_cle text, p_complete boolean, p_delai interval) RETURNS text
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
$def$;
    END IF;
END $rejeu$;

-- ---- 7. Ancrages des sceaux : la ligne scellée existe au central avec la même empreinte ----
-- Garde de rejeu (2026-11-18, lot S6.5) : définition remplacée par le script 023 ; rejouer ce script après 023 ne la
-- ramène pas en arrière (marqueur : fonction cloud_integrite_verifier_branches, posée par 023).
DO $rejeu$ BEGIN
    IF to_regprocedure('cloud_integrite_verifier_branches(text,text,interval)') IS NOT NULL THEN
        RAISE NOTICE 'cloud_integrite_verifier_sceaux : définition plus récente (script 023) gardée';
    ELSE
        EXECUTE $def$
CREATE OR REPLACE FUNCTION cloud_integrite_verifier_sceaux(p_complete boolean, p_delai interval) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    v_off    uuid := cloud_officine_courante();
    s        record;
    v_statut text;
    v_id     text;
    v_hash   bytea;
BEGIN
    FOR s IN SELECT x.id, x.journal, x.cle_chaine, x.dernier_rang, x.dernier_id, x.dernier_hash, x.sync_recu_le FROM sceau_integrite x
             WHERE x.officine_id = v_off
               AND (p_complete OR NOT EXISTS (SELECT 1 FROM cloud_integrite_sceau c WHERE c.officine_id = v_off AND c.sceau_id = x.id AND c.statut <> 'attente'))
             ORDER BY x.id LOOP
        IF NOT EXISTS (SELECT 1 FROM cloud_chaine_reference WHERE journal = s.journal) THEN
            v_statut := 'non_verifiable';
        ELSIF s.dernier_rang = 0 THEN
            v_statut := 'ok';   -- journal vide au moment du sceau
        ELSE
            EXECUTE format('SELECT r.id::text, r.hash FROM public.%I r WHERE r.officine_id = $1 AND r.cle_chaine = $2 AND r.rang = $3', s.journal)
                INTO v_id, v_hash USING v_off, s.cle_chaine, s.dernier_rang;
            IF v_hash IS NULL THEN
                IF s.sync_recu_le < now() - p_delai THEN
                    PERFORM cloud_integrite_noter(s.journal, s.cle_chaine, s.dernier_rang, 'sceau ' || s.id, 'sceau_sans_ligne',
                        format('sceau n° %s : ligne scellée (rang %s) jamais reçue', s.id, s.dernier_rang));
                    v_statut := 'incoherent';
                ELSE
                    v_statut := 'attente';
                END IF;
            ELSIF v_hash IS DISTINCT FROM s.dernier_hash OR (s.dernier_id IS NOT NULL AND v_id IS DISTINCT FROM s.dernier_id::text) THEN
                PERFORM cloud_integrite_noter(s.journal, s.cle_chaine, s.dernier_rang, 'sceau ' || s.id, 'sceau_incoherent',
                    format('sceau n° %s : la ligne scellée reçue ne correspond pas au sceau', s.id),
                    encode(s.dernier_hash, 'hex'), encode(v_hash, 'hex'));
                v_statut := 'incoherent';
            ELSE
                v_statut := 'ok';
            END IF;
        END IF;
        INSERT INTO cloud_integrite_sceau (officine_id, sceau_id, statut) VALUES (v_off, s.id, v_statut)
        ON CONFLICT (officine_id, sceau_id) DO UPDATE SET statut = EXCLUDED.statut, verifie_le = now();
        v_hash := NULL; v_id := NULL;
    END LOOP;
END $$;
$def$;
    END IF;
END $rejeu$;

-- ---- 8. Continuité des tickets « T- » (même contrôle que journal_vente_continuite de l'officine) ----
CREATE OR REPLACE FUNCTION cloud_integrite_verifier_tickets(p_complete boolean, p_delai interval) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    v_off    uuid := cloud_officine_courante();
    o        cloud_integrite_officine;
    c        record;
    v_debut  bigint;
    v_point  bigint;
    m        record;
    v_depuis timestamptz;
    v_limite timestamptz := now() - p_delai;
BEGIN
    IF to_regclass('public.compteur_ticket') IS NULL THEN RETURN; END IF;
    EXECUTE 'SELECT prefixe, dernier_numero, premier_numero_continu, mis_en_service_le FROM public.compteur_ticket WHERE officine_id = $1 AND id = 1'
        INTO c USING v_off;
    IF c IS NULL OR c.prefixe IS NULL THEN RETURN; END IF;   -- compteur pas encore reçu
    SELECT * INTO o FROM cloud_integrite_officine WHERE officine_id = v_off FOR UPDATE;
    v_debut := CASE WHEN p_complete THEN c.premier_numero_continu ELSE greatest(c.premier_numero_continu, coalesce(o.tickets_jusqu_a, 0) + 1) END;
    v_point := c.dernier_numero;
    -- Numéros manquants : anomalie si un numéro plus grand est arrivé il y a plus que le délai ; sinon on attend (le point de reprise s'arrête là)
    FOR m IN
        WITH v AS (SELECT substring(x.numero FROM '^' || c.prefixe || '([0-9]+)$')::bigint AS n, x.sync_recu_le FROM vente x WHERE x.officine_id = v_off)
        SELECT s.n, (SELECT min(v.sync_recu_le) FROM v WHERE v.n > s.n) AS suivant_recu_le
        FROM generate_series(v_debut, c.dernier_numero) AS s(n)
        WHERE NOT EXISTS (SELECT 1 FROM v WHERE v.n = s.n) ORDER BY s.n
    LOOP
        IF m.suivant_recu_le IS NOT NULL AND m.suivant_recu_le < v_limite THEN
            PERFORM cloud_integrite_noter('journal_vente', 'numerotation', NULL, c.prefixe || lpad(m.n::text, greatest(6, char_length(m.n::text)), '0'),
                'ticket_manquant', format('ticket %s%s jamais reçu', c.prefixe, lpad(m.n::text, greatest(6, char_length(m.n::text)), '0')));
        ELSE
            v_point := m.n - 1;
            EXIT;
        END IF;
    END LOOP;
    -- Ventes reçues depuis le dernier contrôle (toutes si complet), plus anciennes que le délai
    v_depuis := CASE WHEN p_complete THEN '-infinity'::timestamptz ELSE coalesce(o.ventes_controlees_jusqu_au, '-infinity'::timestamptz) END;
    FOR m IN
        SELECT x.id, x.numero, x.statut, substring(x.numero FROM '^' || c.prefixe || '([0-9]+)$')::bigint AS n FROM vente x
        WHERE x.officine_id = v_off AND x.sync_recu_le > v_depuis AND x.sync_recu_le <= v_limite
    LOOP
        IF m.n IS NULL THEN CONTINUE; END IF;
        IF m.n > c.dernier_numero THEN
            PERFORM cloud_integrite_noter('journal_vente', 'numerotation', NULL, m.numero, 'ticket_hors_compteur',
                format('ticket %s au-delà du compteur (%s)', m.numero, c.dernier_numero));
        END IF;
        IF m.n >= c.premier_numero_continu AND m.statut IN ('validee', 'annulee')
           AND NOT EXISTS (SELECT 1 FROM journal_vente j WHERE j.officine_id = v_off AND j.vente_id = m.id AND j.evenement = 'vente') THEN
            PERFORM cloud_integrite_noter('journal_vente', 'numerotation', NULL, m.numero, 'vente_sans_ligne',
                format('vente %s sans ligne « vente » au journal fiscal', m.numero));
        END IF;
        IF m.statut = 'annulee'
           AND EXISTS (SELECT 1 FROM annulation_vente a WHERE a.officine_id = v_off AND a.vente_id = m.id AND a.cree_le >= c.mis_en_service_le)
           AND NOT EXISTS (SELECT 1 FROM journal_vente j WHERE j.officine_id = v_off AND j.vente_id = m.id AND j.evenement = 'annulation') THEN
            PERFORM cloud_integrite_noter('journal_vente', 'numerotation', NULL, m.numero, 'annulation_sans_ligne',
                format('vente %s annulée sans ligne « annulation » au journal fiscal', m.numero));
        END IF;
    END LOOP;
    INSERT INTO cloud_integrite_officine AS x (officine_id, tickets_jusqu_a, ventes_controlees_jusqu_au) VALUES (v_off, greatest(v_point, 0), v_limite)
    ON CONFLICT (officine_id) DO UPDATE SET tickets_jusqu_a = EXCLUDED.tickets_jusqu_a, ventes_controlees_jusqu_au = EXCLUDED.ventes_controlees_jusqu_au;
END $$;

-- ---- 9. Vérification croisée : la fin de chaîne vue par l'officine (verification_integrite) contre ce que le central a reçu ----
-- Garde de rejeu (2026-11-18, lot S6.5) : définition remplacée par le script 023 ; rejouer ce script après 023 ne la
-- ramène pas en arrière (marqueur : fonction cloud_integrite_verifier_branches, posée par 023).
DO $rejeu$ BEGIN
    IF to_regprocedure('cloud_integrite_verifier_branches(text,text,interval)') IS NOT NULL THEN
        RAISE NOTICE 'cloud_integrite_verifier_croisee : définition plus récente (script 023) gardée';
    ELSE
        EXECUTE $def$
CREATE OR REPLACE FUNCTION cloud_integrite_verifier_croisee(p_complete boolean, p_delai interval) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    v_off   uuid := cloud_officine_courante();
    o       cloud_integrite_officine;
    v       record;
    ref     cloud_chaine_reference;
    v_hash  bytea;
    v_rang  bigint;
    v_point bigint;
    v_date  timestamptz;
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
        -- Sans fin de chaîne exploitable (rupture locale, contrôle de numérotation, journal hors référentiel) : passée
        IF NOT FOUND OR v.cle_chaine IS NULL OR v.statut IS DISTINCT FROM 'ok' OR v.dernier_rang IS NULL THEN v_point := v.id; CONTINUE; END IF;
        v_hash := NULL;
        IF v.dernier_rang > 0 THEN
            EXECUTE format('SELECT r.hash FROM public.%I r WHERE r.officine_id = $1 AND r.cle_chaine = $2 AND r.rang = $3', v.journal)
                INTO v_hash USING v_off, v.cle_chaine, v.dernier_rang;
            IF v_hash IS NULL THEN
                -- Fin de chaîne de l'officine pas encore reçue : attente, puis anomalie
                IF v.sync_recu_le < now() - p_delai THEN
                    PERFORM cloud_integrite_noter(v.journal, v.cle_chaine, v.dernier_rang, NULL, 'ligne_manquante',
                        format('rang %s vérifié par l''officine le %s, jamais reçu', v.dernier_rang, to_char(v.verifie_le AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI "UTC"')));
                ELSE
                    EXIT;
                END IF;
            ELSIF v_hash IS DISTINCT FROM v.dernier_hash THEN
                PERFORM cloud_integrite_noter(v.journal, v.cle_chaine, v.dernier_rang, NULL, 'reecriture_apres_envoi',
                    format('empreinte du rang %s dans l''officine (vérification du %s) différente de celle reçue', v.dernier_rang,
                           to_char(v.verifie_le AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI "UTC"')),
                    encode(v_hash, 'hex'), encode(v.dernier_hash, 'hex'));
            END IF;
        END IF;
        -- Ligne reçue au-delà de la fin de chaîne de l'officine, mais enregistrée AVANT sa vérification : l'officine a perdu des lignes
        EXECUTE format('SELECT r.rang, r.%I FROM public.%I r WHERE r.officine_id = $1 AND r.cle_chaine = $2 AND r.rang = $3', ref.colonne_date, v.journal)
            INTO v_rang, v_date USING v_off, v.cle_chaine, v.dernier_rang + 1;
        IF v_rang IS NOT NULL AND v_date < v.verifie_le - interval '1 minute' THEN
            PERFORM cloud_integrite_noter(v.journal, v.cle_chaine, v.dernier_rang + 1, NULL, 'chaine_officine_plus_courte',
                format('l''officine s''arrête au rang %s le %s alors que le rang %s, enregistré avant, a été reçu', v.dernier_rang,
                       to_char(v.verifie_le AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI "UTC"'), v.dernier_rang + 1));
        END IF;
        v_rang := NULL; v_date := NULL;
        v_point := v.id;
    END LOOP;
    INSERT INTO cloud_integrite_officine AS x (officine_id, derniere_verification_id) VALUES (v_off, v_point)
    ON CONFLICT (officine_id) DO UPDATE SET derniere_verification_id = EXCLUDED.derniere_verification_id;
END $$;
$def$;
    END IF;
END $rejeu$;

-- ---- 10. Passage complet pour l'officine courante : chaînes, sceaux, tickets, croisée, puis publication vers l'officine ----
-- Garde de rejeu (2026-11-18, lot S6.5) : définition remplacée par le script 023 ; rejouer ce script après 023 ne la
-- ramène pas en arrière (marqueur : fonction cloud_integrite_verifier_branches, posée par 023).
DO $rejeu$ BEGIN
    IF to_regprocedure('cloud_integrite_verifier_branches(text,text,interval)') IS NOT NULL THEN
        RAISE NOTICE 'cloud_verifier_integrite : définition plus récente (script 023) gardée';
    ELSE
        EXECUTE $def$
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
    v_total    bigint;
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
                                                 'dernier_rang', e.dernier_rang, 'non_verifiables', e.non_verifiables) ORDER BY e.journal, e.cle_chaine), '[]'::jsonb)
      INTO v_chaines FROM cloud_integrite_etat e JOIN cloud_chaine_reference ref ON ref.journal = e.journal WHERE e.officine_id = v_off;
    SELECT jsonb_build_object('ok', count(*) FILTER (WHERE statut = 'ok'), 'attente', count(*) FILTER (WHERE statut = 'attente'),
                              'incoherents', count(*) FILTER (WHERE statut = 'incoherent'), 'non_verifiables', count(*) FILTER (WHERE statut = 'non_verifiable'))
      INTO v_sceaux FROM cloud_integrite_sceau WHERE officine_id = v_off;
    SELECT count(*), coalesce(max(id), 0) INTO v_total, v_dernier FROM cloud_anomalie_integrite WHERE officine_id = v_off;
    SELECT coalesce(jsonb_agg(jsonb_build_object('id', a.id, 'nature', a.nature, 'journal', a.journal, 'rang', a.rang, 'ligne', a.cle_ligne,
                                                 'detail', a.detail, 'detectee_le', a.detectee_le) ORDER BY a.id DESC), '[]'::jsonb)
      INTO v_anom FROM (SELECT * FROM cloud_anomalie_integrite WHERE officine_id = v_off ORDER BY id DESC LIMIT 20) a;
    v_nouv := (SELECT count(*) FROM cloud_anomalie_integrite WHERE officine_id = v_off AND id > v_avant);
    v_statut := CASE WHEN v_total > 0 OR EXISTS (SELECT 1 FROM cloud_integrite_etat WHERE officine_id = v_off AND statut = 'rupture') THEN 'anomalie'
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
        'derniere_anomalie_id', v_dernier,
        'anomalies', v_anom);
    PERFORM cloud_publier_descendant('reglage_central', 'integrite_centrale', v_resume);
    RETURN v_resume || jsonb_build_object('nouvelles', v_nouv);
END $$;
$def$;
    END IF;
END $rejeu$;

-- Droits : vérification et lecture réservées au propriétaire (outil d'administration) ; l'API n'écrit des anomalies
-- que par le déclencheur d'ajout seul (SECURITY DEFINER)
REVOKE EXECUTE ON FUNCTION cloud_integrite_verifier_chaine(text, text, boolean, interval), cloud_integrite_verifier_sceaux(boolean, interval),
    cloud_integrite_verifier_tickets(boolean, interval), cloud_integrite_verifier_croisee(boolean, interval), cloud_verifier_integrite(boolean),
    cloud_integrite_expression(text) FROM PUBLIC;

COMMENT ON TABLE cloud_chaine_reference IS 'Journaux chaînés de l''officine vérifiés au central (copie des scripts officine 111, 112, 117 ; script central 012).';
COMMENT ON TABLE cloud_anomalie_integrite IS 'Anomalies d''intégrité constatées au central, une fois chacune, ajout seul (script 012).';
COMMENT ON TABLE cloud_integrite_etat IS 'Point de reprise de la vérification de chaque chaîne (script 012).';
