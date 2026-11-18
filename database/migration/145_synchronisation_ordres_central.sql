-- =====================================================================
-- 145 : synchronisation, ordres envoyés par le serveur central
-- (lots S5.1 et S5.5, 2026-11-18 ; docs/conception-synchronisation.md § 24).
--   * sync_ordre_recu (jamais envoyée) : ordres reçus par la voie
--     descendante (élément « ordre_central », format ordre-v1) : type,
--     cible, valeur attendue, données, auteur au central, expiration,
--     état local (a_appliquer, a_confirmer, applique, refuse, expire,
--     annule) ; appliqués par l'API locale (C#, mêmes règles que les
--     écrans) ; les ordres sur les employés et les droits attendent la
--     confirmation du titulaire, sauf le blocage d'un employé.
--   * sync_ordre_resultat (montante, clé ordre_id) : état, motif, date ;
--     remonte au serveur central (aucune donnée nominative ; motif filtré
--     comme les autres textes libres).
--   * sync_recevoir_descendant : la version du script 141 est renommée
--     sync_recevoir_descendant_reglages et un aiguillage la remplace :
--     « ordre_central » est traité ici, tout le reste lui est passé tel quel.
--   * sync_ordre_noter : change l'état d'un ordre reçu, écrit le résultat
--     et une ligne au journal des actions (utilisateur NULL si automatique).
-- Idempotent. Aucune donnée existante modifiée.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

-- ---- 1. Ordres reçus (local) ----
CREATE TABLE IF NOT EXISTS sync_ordre_recu (
    id           uuid        PRIMARY KEY,
    type         text        NOT NULL CHECK (type ~ '^[a-z_]{1,40}$'),
    cible        text        CHECK (length(cible) <= 100),
    attendu      jsonb       NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(attendu) = 'object'),
    donnees      jsonb       NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(donnees) = 'object'),
    auteur       text        NOT NULL CHECK (length(auteur) BETWEEN 1 AND 100),
    cree_le      timestamptz NOT NULL,
    expire_le    timestamptz NOT NULL,
    version      bigint      NOT NULL CHECK (version >= 1),
    statut       text        NOT NULL DEFAULT 'a_appliquer'
                 CHECK (statut IN ('a_appliquer', 'a_confirmer', 'applique', 'refuse', 'expire', 'annule')),
    motif        text        CHECK (length(motif) <= 500),
    recu_le      timestamptz NOT NULL DEFAULT now(),
    traite_le    timestamptz,
    traite_par   bigint      REFERENCES utilisateur (id)
);
CREATE INDEX IF NOT EXISTS ix_sync_ordre_recu_statut ON sync_ordre_recu (statut, recu_le);
COMMENT ON TABLE sync_ordre_recu IS 'Ordres reçus du serveur central (format ordre-v1), appliqués par l''API locale ; jamais envoyée (script 145).';

-- ---- 2. Résultat des ordres (montante) ----
CREATE TABLE IF NOT EXISTS sync_ordre_resultat (
    ordre_id     uuid        PRIMARY KEY REFERENCES sync_ordre_recu (id),
    statut       text        NOT NULL CHECK (statut IN ('a_confirmer', 'applique', 'refuse', 'expire', 'annule')),
    motif        text        CHECK (length(motif) <= 500),
    traite_le    timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE sync_ordre_resultat IS 'Résultat de chaque ordre du serveur central, envoyé au central (script 145).';
DO $officine$
DECLARE v_code uuid := officine_courante();
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns c
                   WHERE c.table_schema = 'public' AND c.table_name = 'sync_ordre_resultat' AND c.column_name = 'officine_id') THEN
        EXECUTE format('ALTER TABLE public.sync_ordre_resultat ADD COLUMN officine_id uuid NOT NULL DEFAULT %L::uuid', v_code);
    END IF;
    ALTER TABLE public.sync_ordre_resultat ALTER COLUMN officine_id SET DEFAULT officine_courante();
END $officine$;

INSERT INTO sync_table (nom_table, sens, colonnes_exclues, colonnes_patient, porteur, motif) VALUES
    ('sync_ordre_recu', 'jamais', '{}', '{}', false, 'Ordres reçus du serveur central (données d''employés possibles), script 145'),
    ('sync_ordre_resultat', 'montante', '{}', '{}', false, 'Résultat des ordres du serveur central, script 145')
ON CONFLICT (nom_table) DO NOTHING;
UPDATE sync_table SET cle_colonnes = '{ordre_id}', colonnes_texte = '{motif}'
WHERE nom_table = 'sync_ordre_resultat'
  AND (cle_colonnes, colonnes_texte) IS DISTINCT FROM ('{ordre_id}'::text[], '{motif}'::text[]);

-- ---- 3. Changement d'état d'un ordre reçu : ordre, résultat, journal des actions ----
CREATE OR REPLACE FUNCTION sync_ordre_noter(p_id uuid, p_statut text, p_motif text, p_par bigint) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE o sync_ordre_recu;
BEGIN
    IF p_statut NOT IN ('a_confirmer', 'applique', 'refuse', 'expire', 'annule') THEN
        RAISE EXCEPTION 'Ordre du serveur central : état inconnu (%)', p_statut;
    END IF;
    SELECT * INTO o FROM sync_ordre_recu WHERE id = p_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Ordre du serveur central inconnu : %', p_id; END IF;
    IF o.statut IN ('applique', 'refuse', 'expire', 'annule') THEN
        RAISE EXCEPTION 'Ordre du serveur central déjà traité (%)', o.statut;
    END IF;
    UPDATE sync_ordre_recu SET statut = p_statut, motif = left(p_motif, 500),
           traite_le = CASE WHEN p_statut = 'a_confirmer' THEN traite_le ELSE now() END,
           traite_par = CASE WHEN p_statut = 'a_confirmer' THEN traite_par ELSE p_par END
    WHERE id = p_id;
    INSERT INTO sync_ordre_resultat AS r (ordre_id, statut, motif, traite_le) VALUES (p_id, p_statut, left(p_motif, 500), now())
    ON CONFLICT (ordre_id) DO UPDATE SET statut = EXCLUDED.statut, motif = EXCLUDED.motif, traite_le = EXCLUDED.traite_le;
    INSERT INTO journal_audit (utilisateur_id, action, table_cible, details)
    VALUES (p_par, 'ordre_central_' || p_statut, 'sync_ordre_recu',
            jsonb_build_object('ordre', p_id, 'type', o.type, 'cible', o.cible, 'auteur_central', o.auteur, 'motif', left(p_motif, 500),
                               'attendu', o.attendu, 'donnees', o.donnees - 'mot_de_passe'));
END $$;

-- ---- 4. Voie descendante : aiguillage (ordres ici, le reste à la version du script 141) ----
DO $renommer$
BEGIN
    IF to_regprocedure('sync_recevoir_descendant_reglages(text, text, bigint, jsonb, bigint)') IS NULL THEN
        ALTER FUNCTION sync_recevoir_descendant(text, text, bigint, jsonb, bigint) RENAME TO sync_recevoir_descendant_reglages;
    END IF;
END $renommer$;

CREATE OR REPLACE FUNCTION sync_recevoir_descendant(p_table text, p_cle text, p_version bigint, p_donnees jsonb, p_version_max bigint)
RETURNS text LANGUAGE plpgsql AS $$
DECLARE
    p        sync_parametre;
    v_motif  text;
    v_id     uuid;
    v_statut text;
    o        sync_ordre_recu;
BEGIN
    IF p_table IS DISTINCT FROM 'ordre_central' THEN
        RETURN sync_recevoir_descendant_reglages(p_table, p_cle, p_version, p_donnees, p_version_max);
    END IF;
    SELECT * INTO p FROM sync_parametre WHERE id = 1;
    v_motif := CASE
        WHEN p_version IS NULL OR p_version < 1 OR p_donnees IS NULL OR jsonb_typeof(p_donnees) <> 'object' THEN 'élément mal formé'
        WHEN p_version > coalesce(p_version_max, 0) THEN 'version au-delà de la version maximale annoncée'
        WHEN p_version > p.descendant_version + p.descendant_saut_max THEN 'saut de version anormal'
        WHEN length(p_donnees::text) > 65536 THEN 'données trop volumineuses (plus de 64 Kio)'
        WHEN p_donnees ->> 'format' IS DISTINCT FROM 'ordre-v1' THEN 'format d''ordre non pris en charge'
        WHEN p_cle IS NULL OR p_cle !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
             OR p_donnees ->> 'id' IS DISTINCT FROM p_cle THEN 'identifiant d''ordre invalide'
        WHEN coalesce(p_donnees ->> 'type', '') !~ '^[a-z_]{1,40}$' THEN 'type d''ordre invalide'
        WHEN coalesce(p_donnees ->> 'auteur', '') = '' OR length(p_donnees ->> 'auteur') > 100 THEN 'auteur de l''ordre absent'
        WHEN jsonb_typeof(p_donnees -> 'attendu') IS DISTINCT FROM 'object' OR jsonb_typeof(p_donnees -> 'donnees') IS DISTINCT FROM 'object'
             THEN 'contenu de l''ordre invalide'
        WHEN coalesce(p_donnees ->> 'statut', '') NOT IN ('en_attente', 'a_confirmer', 'applique', 'refuse', 'expire', 'annule')
             THEN 'état de l''ordre invalide'
        ELSE NULL END;
    IF v_motif IS NULL THEN
        BEGIN
            PERFORM (p_donnees ->> 'cree_le')::timestamptz, (p_donnees ->> 'expire_le')::timestamptz;
        EXCEPTION WHEN others THEN v_motif := 'dates de l''ordre invalides';
        END;
    END IF;
    IF v_motif IS NOT NULL THEN
        INSERT INTO sync_journal_descendant (nom_table, cle, version, statut, motif)
        SELECT 'ordre_central', left(coalesce(p_cle, '?'), 200), p_version, 'refuse', v_motif
        WHERE NOT EXISTS (SELECT 1 FROM sync_journal_descendant j WHERE j.statut = 'refuse' AND j.nom_table = 'ordre_central'
                          AND j.cle = left(coalesce(p_cle, '?'), 200) AND j.version IS NOT DISTINCT FROM p_version);
        RETURN 'refuse';
    END IF;

    v_id := p_cle::uuid;
    v_statut := p_donnees ->> 'statut';
    SELECT * INTO o FROM sync_ordre_recu WHERE id = v_id FOR UPDATE;
    IF NOT FOUND THEN
        INSERT INTO sync_ordre_recu (id, type, cible, attendu, donnees, auteur, cree_le, expire_le, version, statut)
        VALUES (v_id, p_donnees ->> 'type', left(p_donnees ->> 'cible', 100), p_donnees -> 'attendu', p_donnees -> 'donnees',
                p_donnees ->> 'auteur', (p_donnees ->> 'cree_le')::timestamptz, (p_donnees ->> 'expire_le')::timestamptz, p_version,
                'a_appliquer');
        -- Annulé ou expiré au central avant d'arriver : jamais appliqué
        IF v_statut IN ('annule', 'expire') THEN
            PERFORM sync_ordre_noter(v_id, v_statut, CASE v_statut WHEN 'annule' THEN 'annulé au serveur central' ELSE 'expiré au serveur central' END, NULL);
        END IF;
    ELSIF o.version >= p_version THEN
        RETURN 'ancien';
    ELSE
        UPDATE sync_ordre_recu SET version = p_version WHERE id = v_id;
        -- Seule une annulation ou une expiration décidée au central change un ordre pas encore traité ; le reste est ignoré
        IF v_statut IN ('annule', 'expire') AND o.statut IN ('a_appliquer', 'a_confirmer') THEN
            PERFORM sync_ordre_noter(v_id, v_statut, CASE v_statut WHEN 'annule' THEN 'annulé au serveur central' ELSE 'expiré au serveur central' END, NULL);
        END IF;
    END IF;
    INSERT INTO sync_journal_descendant (nom_table, cle, version, statut, avant, apres)
    VALUES ('ordre_central', p_cle, p_version, 'applique', NULL, jsonb_build_object('type', p_donnees ->> 'type', 'statut', v_statut));
    RETURN 'applique';
END $$;

-- ---- 5. Droits de pharma_app (propriétaire en développement ; après la séparation des rôles, droits par défaut de 111b) ----
DO $droits$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pharma_app') THEN
        IF NOT has_table_privilege('pharma_app', 'public.sync_ordre_recu', 'SELECT, INSERT, UPDATE') THEN
            GRANT SELECT, INSERT, UPDATE ON sync_ordre_recu TO pharma_app;
        END IF;
        IF NOT has_table_privilege('pharma_app', 'public.sync_ordre_resultat', 'SELECT, INSERT, UPDATE') THEN
            GRANT SELECT, INSERT, UPDATE ON sync_ordre_resultat TO pharma_app;
        END IF;
    END IF;
END $droits$;

-- ---- 6. Déclencheurs de la file : posés sur sync_ordre_resultat si la synchronisation est déjà active ----
DO $declencheurs$
BEGIN
    IF EXISTS (SELECT 1 FROM sync_parametre WHERE id = 1 AND active) THEN
        PERFORM sync_installer_declencheurs();
    END IF;
END $declencheurs$;
