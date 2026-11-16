-- =====================================================================
-- Tests du script 128 (code officine, accusés), en transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/128_synchronisation_envoi_tests.sql
-- =====================================================================
BEGIN;

DO $$
DECLARE v uuid := officine_courante(); n int; l bigint; s bigint;
BEGIN
    ASSERT v IS NOT NULL, 'code officine présent';
    ASSERT (SELECT officine_id FROM sync_parametre WHERE id = 1) = v, 'code dans sync_parametre';
    -- Toutes les tables envoyées ou reçues portent la colonne, NOT NULL, défaut officine_courante(), hors clé primaire
    SELECT count(*) INTO n FROM sync_table s JOIN pg_tables t ON t.schemaname = 'public' AND t.tablename = s.nom_table
    WHERE (s.sens <> 'jamais' OR s.nom_table = 'medecin')
      AND NOT EXISTS (SELECT 1 FROM information_schema.columns c WHERE c.table_schema = 'public' AND c.table_name = s.nom_table
                      AND c.column_name = 'officine_id' AND c.is_nullable = 'NO' AND c.column_default = 'officine_courante()');
    ASSERT n = 0, n || ' table(s) sans officine_id conforme';
    ASSERT NOT EXISTS (SELECT 1 FROM pg_constraint p JOIN pg_attribute a ON a.attrelid = p.conrelid AND a.attnum = ANY (p.conkey)
                       WHERE p.contype = 'p' AND a.attname = 'officine_id' AND p.conrelid::regclass::text <> 'sync_parametre'), 'pas dans les clés primaires';
    -- Les lignes existantes ont le code (sans UPDATE) ; une nouvelle ligne le reçoit
    ASSERT NOT EXISTS (SELECT 1 FROM vente WHERE officine_id <> v), 'ventes existantes au code de l''officine';
    INSERT INTO wilaya (code, nom) VALUES ('98', 'Essai 128') ON CONFLICT DO NOTHING;
    ASSERT (SELECT officine_id FROM wilaya WHERE code = '98') = v, 'nouvelle ligne au code de l''officine';
    -- Le code ne se modifie pas
    BEGIN
        UPDATE sync_parametre SET officine_id = gen_random_uuid() WHERE id = 1;
        RAISE EXCEPTION 'code officine modifié';
    EXCEPTION WHEN raise_exception THEN
        ASSERT SQLERRM LIKE '%ne se modifie pas%', SQLERRM;
    END;
    -- Inscription : seul le code de l'officine installée est accepté
    BEGIN
        PERFORM sync_enregistrer_inscription(gen_random_uuid());
        RAISE EXCEPTION 'inscription d''une autre officine acceptée';
    EXCEPTION WHEN raise_exception THEN
        ASSERT SQLERRM LIKE '%autre officine%', SQLERRM;
    END;
    PERFORM sync_enregistrer_inscription(v);
    ASSERT (SELECT inscrite_le FROM sync_parametre) IS NOT NULL, 'inscrite';
    -- Accusé : lots jusqu'au n° marqués, dernier_accuse avancé ; accusé au-delà du dernier lot refusé
    INSERT INTO sync_sortie (nom_table, id_local, operation, donnees, version_contrat, epoque)
    VALUES ('vente', '1', 'creation', '{"id":1}', 'sync-v1', (SELECT epoque FROM sync_parametre)) RETURNING id INTO l;
    s := (SELECT dernier_lot FROM sync_parametre) + 1;
    ASSERT sync_enregistrer_lot(s, ARRAY[l], 10, repeat('a', 64)) = s, 'lot enregistré';
    BEGIN
        PERFORM sync_enregistrer_accuse((SELECT epoque FROM sync_parametre), s + 1);
        RAISE EXCEPTION 'accusé incohérent accepté';
    EXCEPTION WHEN raise_exception THEN
        ASSERT SQLERRM LIKE '%incohérent%', SQLERRM;
    END;
    ASSERT sync_enregistrer_accuse((SELECT epoque FROM sync_parametre), s) = s, 'accusé enregistré';
    ASSERT (SELECT accuse_le FROM sync_lot WHERE numero = s) IS NOT NULL AND (SELECT dernier_accuse FROM sync_parametre) = s, 'lot accusé';
    PERFORM sync_noter_envoi('essai');
    ASSERT (SELECT derniere_erreur FROM sync_parametre) = 'essai', 'erreur notée';
    -- Adresse : https seulement
    PERFORM sync_regler_adresse('https://central.exemple.dz:5090');
    BEGIN
        PERFORM sync_regler_adresse('http://central.exemple.dz');
        RAISE EXCEPTION 'adresse http acceptée';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
END $$;

\echo 'Tests du script 128 : tous réussis (transaction annulée).'
ROLLBACK;
