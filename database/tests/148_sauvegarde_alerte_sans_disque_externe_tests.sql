-- =====================================================================
-- Tests du script 148 (alerte « aucune sauvegarde sur disque externe depuis N jours »), en transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/148_sauvegarde_alerte_sans_disque_externe_tests.sql
-- =====================================================================
BEGIN;

-- 1. Colonnes présentes ; CHECK posé une seule fois ; une seule ligne de réglage
DO $$
BEGIN
    ASSERT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'parametre_sauvegarde' AND column_name = 'alerte_sans_disque_jours'
                   AND data_type = 'integer' AND is_nullable = 'NO' AND column_default = '7'), 'colonne alerte_sans_disque_jours (int, non nulle, défaut 7)';
    ASSERT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'parametre_sauvegarde' AND column_name = 'suivi_disque_depuis'
                   AND data_type = 'timestamp with time zone' AND is_nullable = 'NO' AND column_default = 'now()'), 'colonne suivi_disque_depuis (timestamptz, non nulle, défaut now())';
    ASSERT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'parametre_sauvegarde' AND column_name = 'derniere_alerte_sans_disque'
                   AND data_type = 'timestamp with time zone' AND is_nullable = 'YES'), 'colonne derniere_alerte_sans_disque (timestamptz, facultative)';
    ASSERT (SELECT count(*) FROM pg_constraint WHERE conname = 'ck_sauvegarde_alerte_sans_disque') = 1, 'CHECK posé une seule fois';
    ASSERT (SELECT count(*) FROM parametre_sauvegarde) = 1, 'une seule ligne de réglage';
END $$;

-- 2. Valeurs permises : 1 à 90 ; refus de 0, 91, NULL
DO $$
BEGIN
    UPDATE parametre_sauvegarde SET alerte_sans_disque_jours = 1 WHERE id = 1;
    UPDATE parametre_sauvegarde SET alerte_sans_disque_jours = 90 WHERE id = 1;
    UPDATE parametre_sauvegarde SET alerte_sans_disque_jours = 14 WHERE id = 1;
    ASSERT (SELECT alerte_sans_disque_jours FROM parametre_sauvegarde WHERE id = 1) = 14, 'valeur enregistrée';
    BEGIN UPDATE parametre_sauvegarde SET alerte_sans_disque_jours = 0 WHERE id = 1; RAISE EXCEPTION '0 accepté';
    EXCEPTION WHEN check_violation THEN NULL; END;
    BEGIN UPDATE parametre_sauvegarde SET alerte_sans_disque_jours = 91 WHERE id = 1; RAISE EXCEPTION '91 accepté';
    EXCEPTION WHEN check_violation THEN NULL; END;
    BEGIN UPDATE parametre_sauvegarde SET alerte_sans_disque_jours = NULL WHERE id = 1; RAISE EXCEPTION 'NULL accepté';
    EXCEPTION WHEN not_null_violation THEN NULL; END;
    ASSERT (SELECT alerte_sans_disque_jours FROM parametre_sauvegarde WHERE id = 1) = 14, 'valeur inchangée après les refus';
END $$;

-- 3. Base neuve : 7 jours, suivi depuis la création, aucune alerte
DO $$
BEGIN
    DELETE FROM parametre_sauvegarde;
    INSERT INTO parametre_sauvegarde (id) VALUES (1);
    ASSERT (SELECT alerte_sans_disque_jours FROM parametre_sauvegarde WHERE id = 1) = 7, 'défaut 7 jours';
    ASSERT (SELECT suivi_disque_depuis FROM parametre_sauvegarde WHERE id = 1) = now(), 'suivi depuis la création';
    ASSERT (SELECT derniere_alerte_sans_disque FROM parametre_sauvegarde WHERE id = 1) IS NULL, 'aucune alerte';
END $$;

ROLLBACK;
