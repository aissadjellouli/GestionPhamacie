-- =====================================================================
-- 143 — Copie de la GED vers le 2e serveur : fréquence réglable (2026-11-17, décision du titulaire)
--   « Copier la GED toutes les N minutes », N de 1 à 60, 5 par défaut, réglé dans Administration › 2e serveur.
--   1. surveillance_replication.ged_copie_minutes : la valeur réglée (la table, une seule ligne, est déjà répliquée avec
--      la base : le 2e serveur la lit dans SA copie locale, en lecture seule). Table jamais envoyée au serveur central.
--   2. Garde du CHECK (1 à 60) : posé une seule fois, jamais recréé sur une base à jour.
--   3. Droit de lecture de cette seule colonne au rôle « pharma_ged_lecture » (rôle de lecture du réglage par la tâche de copie
--      du 2e serveur, créé par scripts/second-serveur/autoriser-lecture-copie-ged.ps1 avec le superutilisateur) : accordé ici
--      seulement si le rôle existe déjà (rien n'est créé : un script de migration ne crée pas de rôle de connexion).
-- Idempotent et rejouable sur une base plus récente ; aucune donnée modifiée (les lignes existantes prennent 5).
-- A exécuter après 142_mutualisation_reglages_modification_locale.sql.
-- =====================================================================

-- ---- 1. Colonne ----
ALTER TABLE surveillance_replication ADD COLUMN IF NOT EXISTS ged_copie_minutes int NOT NULL DEFAULT 5;

-- ---- 2. Garde du CHECK ----
DO $garde$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_surveillance_replication_ged_copie_minutes'
                   AND conrelid = 'surveillance_replication'::regclass) THEN
        ALTER TABLE surveillance_replication ADD CONSTRAINT ck_surveillance_replication_ged_copie_minutes
            CHECK (ged_copie_minutes BETWEEN 1 AND 60);
    END IF;
END $garde$;

-- La ligne unique existe déjà (script 122) ; rejouable
INSERT INTO surveillance_replication (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

-- ---- 3. Lecture de cette seule colonne par le rôle de la tâche de copie (si le rôle existe) ----
DO $garde$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pharma_ged_lecture') THEN
        GRANT SELECT (ged_copie_minutes) ON surveillance_replication TO pharma_ged_lecture;
    END IF;
END $garde$;

COMMENT ON COLUMN surveillance_replication.ged_copie_minutes IS 'Copie de la GED vers le 2e serveur : toutes les N minutes (1 à 60, défaut 5), réglé dans Administration › 2e serveur, lu par scripts/second-serveur/copier-ged.ps1 dans la base locale du 2e serveur (script 143).';
