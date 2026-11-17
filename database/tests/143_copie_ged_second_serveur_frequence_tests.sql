-- =====================================================================
-- Tests du script 143 (copie de la GED vers le 2e serveur : fréquence réglable), en transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/143_copie_ged_second_serveur_frequence_tests.sql
-- =====================================================================
BEGIN;

-- 1. Colonne présente, entière, obligatoire, défaut 5 ; une seule ligne
DO $$
BEGIN
    ASSERT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'surveillance_replication' AND column_name = 'ged_copie_minutes'
                   AND data_type = 'integer' AND is_nullable = 'NO' AND column_default = '5'), 'colonne ged_copie_minutes (int, non nulle, défaut 5)';
    ASSERT (SELECT count(*) FROM surveillance_replication) = 1, 'une seule ligne de réglage';
    ASSERT (SELECT count(*) FROM pg_constraint WHERE conname = 'ck_surveillance_replication_ged_copie_minutes') = 1, 'CHECK posé une seule fois';
END $$;

-- 2. Valeurs permises : 1 à 60 ; refus de 0, 61, négatif ; NULL refusé
DO $$
BEGIN
    UPDATE surveillance_replication SET ged_copie_minutes = 1 WHERE id = 1;
    UPDATE surveillance_replication SET ged_copie_minutes = 60 WHERE id = 1;
    UPDATE surveillance_replication SET ged_copie_minutes = 15 WHERE id = 1;
    ASSERT (SELECT ged_copie_minutes FROM surveillance_replication WHERE id = 1) = 15, 'valeur enregistrée';
    BEGIN UPDATE surveillance_replication SET ged_copie_minutes = 0 WHERE id = 1; RAISE EXCEPTION '0 accepté';
    EXCEPTION WHEN check_violation THEN NULL; END;
    BEGIN UPDATE surveillance_replication SET ged_copie_minutes = 61 WHERE id = 1; RAISE EXCEPTION '61 accepté';
    EXCEPTION WHEN check_violation THEN NULL; END;
    BEGIN UPDATE surveillance_replication SET ged_copie_minutes = -5 WHERE id = 1; RAISE EXCEPTION '-5 accepté';
    EXCEPTION WHEN check_violation THEN NULL; END;
    BEGIN UPDATE surveillance_replication SET ged_copie_minutes = NULL WHERE id = 1; RAISE EXCEPTION 'NULL accepté';
    EXCEPTION WHEN not_null_violation THEN NULL; END;
    ASSERT (SELECT ged_copie_minutes FROM surveillance_replication WHERE id = 1) = 15, 'valeur inchangée après les refus';
END $$;

-- 3. Réglage d'une base neuve : défaut 5 (nouvelle ligne)
DO $$
BEGIN
    DELETE FROM surveillance_replication;
    INSERT INTO surveillance_replication (id) VALUES (1);
    ASSERT (SELECT ged_copie_minutes FROM surveillance_replication WHERE id = 1) = 5, 'défaut 5 minutes';
END $$;

-- 4. Seuils d'alerte de la réplication inchangés
DO $$
BEGIN
    ASSERT (SELECT seuil_secondes FROM surveillance_replication WHERE id = 1) = 300, 'seuil secondes inchangé';
    ASSERT (SELECT seuil_octets FROM surveillance_replication WHERE id = 1) = 67108864, 'seuil octets inchangé';
END $$;

-- 5. Rôle de lecture : s'il existe, il lit la seule colonne du réglage et rien d'autre
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pharma_ged_lecture') THEN
        ASSERT has_column_privilege('pharma_ged_lecture', 'public.surveillance_replication', 'ged_copie_minutes', 'SELECT'), 'lecture du réglage';
        ASSERT NOT has_column_privilege('pharma_ged_lecture', 'public.surveillance_replication', 'seuil_secondes', 'SELECT'), 'pas les seuils';
        ASSERT NOT has_table_privilege('pharma_ged_lecture', 'public.utilisateur', 'SELECT'), 'pas les autres tables';
        ASSERT NOT has_column_privilege('pharma_ged_lecture', 'public.surveillance_replication', 'ged_copie_minutes', 'UPDATE'), 'jamais d''écriture';
    END IF;
END $$;

ROLLBACK;
