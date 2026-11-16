-- =====================================================================
-- 128 — Synchronisation, lot S2 (2026-11-16) : code officine sur les
-- tables envoyées ou reçues, envoi au serveur central, accusés.
-- docs/conception-synchronisation.md § 12.
--   * Code de l'officine installée : sync_parametre.officine_id, généré
--     une fois (uuid aléatoire) s'il manque, puis JAMAIS modifié
--     (déclencheur). officine_courante() le renvoie.
--   * Colonne officine_id (uuid NOT NULL, défaut officine_courante()) sur
--     chaque table classée montante, bidirectionnelle ou descendante (et
--     medecin, envoyée en option) : mêmes tables et colonnes que la base
--     centrale. Hors clé primaire locale (une seule officine localement) :
--     aucune clé étrangère ni requête existante n'est touchée.
--     Ajout SANS UPDATE : ADD COLUMN ... DEFAULT <constante> remplit les
--     lignes existantes sans réécriture ni déclencheur, donc les gardes
--     « ajout seul » ne sont pas contournées et les empreintes chaînées
--     (colonnes listées dans chaine_integrite) ne changent pas. Le défaut
--     devient ensuite officine_courante() pour les nouvelles lignes.
--     Tables « jamais » exclues : techniques ou locales, jamais envoyées.
--   * sync_parametre : adresse du serveur central (réglage en base, pas
--     dans appsettings), inscription, dernier envoi, dernier accusé,
--     dernière erreur.
--   * sync_enregistrer_accuse, sync_noter_envoi, sync_enregistrer_inscription,
--     sync_regler_adresse.
-- Idempotent ; aucune donnée existante perdue ni modifiée (hors
-- sync_parametre.officine_id rempli s'il était vide) ; synchronisation
-- toujours désactivée par défaut.
-- =====================================================================

ALTER TABLE sync_parametre ALTER COLUMN officine_id SET DEFAULT gen_random_uuid();
UPDATE sync_parametre SET officine_id = gen_random_uuid() WHERE id = 1 AND officine_id IS NULL;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS adresse_serveur text;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS inscrite_le timestamptz;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS dernier_envoi_le timestamptz;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS dernier_accuse_le timestamptz;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS derniere_erreur text;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS derniere_erreur_le timestamptz;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'sync_parametre_adresse_serveur_check') THEN
        ALTER TABLE sync_parametre ADD CONSTRAINT sync_parametre_adresse_serveur_check
            CHECK (adresse_serveur IS NULL OR adresse_serveur ~ '^https://[A-Za-z0-9.-]+(:[0-9]{1,5})?/?$');
    END IF;
END $$;

-- Code de l'officine installée ; créé s'il manque (installation neuve sans ligne de réglage)
CREATE OR REPLACE FUNCTION officine_courante() RETURNS uuid
LANGUAGE plpgsql AS $$
DECLARE v uuid;
BEGIN
    SELECT officine_id INTO v FROM sync_parametre WHERE id = 1;
    IF v IS NOT NULL THEN RETURN v; END IF;
    INSERT INTO sync_parametre (id, officine_id) VALUES (1, gen_random_uuid())
    ON CONFLICT (id) DO UPDATE SET officine_id = coalesce(sync_parametre.officine_id, EXCLUDED.officine_id);
    SELECT officine_id INTO v FROM sync_parametre WHERE id = 1;
    RETURN v;
END $$;

-- Le code de l'officine ne change jamais une fois posé (il est sur chaque ligne et au serveur central)
CREATE OR REPLACE FUNCTION sync_parametre_garde_officine() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.officine_id IS NOT NULL AND NEW.officine_id IS DISTINCT FROM OLD.officine_id THEN
        RAISE EXCEPTION 'Synchronisation : le code de l''officine ne se modifie pas';
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_sync_parametre_garde_officine ON sync_parametre;
CREATE TRIGGER trg_sync_parametre_garde_officine BEFORE UPDATE ON sync_parametre FOR EACH ROW EXECUTE FUNCTION sync_parametre_garde_officine();

-- Colonne officine_id sur les tables envoyées ou reçues (sans UPDATE des lignes existantes)
DO $$
DECLARE r record; v_code uuid := officine_courante();
BEGIN
    FOR r IN SELECT s.nom_table FROM sync_table s
             JOIN pg_tables t ON t.schemaname = 'public' AND t.tablename = s.nom_table
             WHERE s.sens <> 'jamais' OR s.nom_table = 'medecin' ORDER BY 1 LOOP
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns c
                       WHERE c.table_schema = 'public' AND c.table_name = r.nom_table AND c.column_name = 'officine_id') THEN
            EXECUTE format('ALTER TABLE public.%I ADD COLUMN officine_id uuid NOT NULL DEFAULT %L::uuid', r.nom_table, v_code);
        END IF;
        EXECUTE format('ALTER TABLE public.%I ALTER COLUMN officine_id SET DEFAULT officine_courante()', r.nom_table);
    END LOOP;
END $$;

-- Accusé du serveur central : lots jusqu'au n° reçu marqués accusés (seul changement permis sur sync_lot)
CREATE OR REPLACE FUNCTION sync_enregistrer_accuse(p_epoque int, p_numero bigint) RETURNS bigint
LANGUAGE plpgsql AS $$
DECLARE p sync_parametre;
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1 FOR UPDATE;
    IF p_epoque <> p.epoque OR p_numero > p.dernier_lot THEN
        RAISE EXCEPTION 'Synchronisation : accusé % de l''époque % incohérent (dernier lot constitué %, époque %)', p_numero, p_epoque, p.dernier_lot, p.epoque;
    END IF;
    UPDATE sync_lot SET accuse_le = now() WHERE epoque = p_epoque AND numero <= p_numero AND accuse_le IS NULL;
    UPDATE sync_parametre SET dernier_accuse = greatest(dernier_accuse, p_numero), dernier_accuse_le = now(),
                              dernier_envoi_le = now(), derniere_erreur = NULL, derniere_erreur_le = NULL
    WHERE id = 1;
    RETURN greatest(p.dernier_accuse, p_numero);
END $$;

-- Erreur d'envoi (message technique, sans donnée personnelle) ; NULL efface
CREATE OR REPLACE FUNCTION sync_noter_envoi(p_erreur text) RETURNS void
LANGUAGE sql AS $$
    UPDATE sync_parametre SET dernier_envoi_le = now(), derniere_erreur = left(p_erreur, 500),
                              derniere_erreur_le = CASE WHEN p_erreur IS NULL THEN NULL ELSE now() END
    WHERE id = 1
$$;

-- Inscription au serveur central : le code renvoyé doit être celui de l'officine installée
CREATE OR REPLACE FUNCTION sync_enregistrer_inscription(p_officine uuid) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    IF p_officine IS DISTINCT FROM officine_courante() THEN
        RAISE EXCEPTION 'Synchronisation : le serveur central a répondu pour une autre officine';
    END IF;
    UPDATE sync_parametre SET inscrite_le = coalesce(inscrite_le, now()), derniere_erreur = NULL, derniere_erreur_le = NULL WHERE id = 1;
END $$;

CREATE OR REPLACE FUNCTION sync_regler_adresse(p_adresse text) RETURNS void
LANGUAGE sql AS $$
    UPDATE sync_parametre SET adresse_serveur = nullif(btrim(p_adresse), ''), modifie_le = now() WHERE id = 1
$$;

COMMENT ON FUNCTION officine_courante() IS 'Code de l''officine installée (sync_parametre.officine_id), défaut de la colonne officine_id des tables synchronisées (script 128).';
