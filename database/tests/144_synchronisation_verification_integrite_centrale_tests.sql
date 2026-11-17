-- =====================================================================
-- Tests du script 144 (vérification d'intégrité au serveur central, côté officine), en transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/144_synchronisation_verification_integrite_centrale_tests.sql
-- =====================================================================
BEGIN;

DO $$
DECLARE j jsonb; v_code uuid := officine_courante();
BEGIN
    -- 1. compteur_ticket : montante, code officine présent, la garde « +1 seulement » toujours active
    ASSERT (SELECT sens FROM sync_table WHERE nom_table = 'compteur_ticket') = 'montante', 'compteur_ticket montante';
    ASSERT (SELECT officine_id FROM compteur_ticket WHERE id = 1) = v_code, 'code officine sur le compteur';
    ASSERT (SELECT column_default FROM information_schema.columns WHERE table_name = 'compteur_ticket' AND column_name = 'officine_id')
           = 'officine_courante()', 'défaut officine_courante()';
    BEGIN
        UPDATE compteur_ticket SET dernier_numero = dernier_numero + 2 WHERE id = 1;
        RAISE EXCEPTION 'compteur sauté de 2';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE 'Le compteur des tickets avance%', SQLERRM;
    END;
    ASSERT NOT EXISTS (SELECT * FROM sync_tables_non_classees()), 'toutes les tables restent classées';

    -- 2. verification_integrite : fin de chaîne (rang positif ou nul), toujours en ajout seul
    ASSERT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'verification_integrite' AND column_name = 'dernier_rang' AND data_type = 'bigint'), 'dernier_rang';
    ASSERT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'verification_integrite' AND column_name = 'dernier_hash' AND data_type = 'bytea'), 'dernier_hash';
    INSERT INTO verification_integrite (journal, cle_chaine, lignes, statut, dernier_rang, dernier_hash)
    VALUES ('journal_vente', 'officine', 3, 'ok', 3, '\xabcd');
    BEGIN
        INSERT INTO verification_integrite (journal, cle_chaine, lignes, statut, dernier_rang) VALUES ('journal_vente', 'officine', 3, 'ok', -1);
        RAISE EXCEPTION 'rang négatif accepté';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        UPDATE verification_integrite SET dernier_rang = 4 WHERE dernier_hash = '\xabcd';
        RAISE EXCEPTION 'vérification modifiée';
    EXCEPTION WHEN raise_exception THEN NULL;
    END;

    -- 3. Textes libres : journal chaîné toujours filtré, jamais vidé ; autres tables : option « retirés » respectée
    UPDATE sync_parametre SET textes_libres = 'retires' WHERE id = 1;
    SELECT sync_minimiser(t, '{"id": 1, "motif": "rappeler au 0661234567"}'::jsonb) INTO j FROM sync_table t WHERE nom_table = 'journal_vente';
    ASSERT j ->> 'motif' = 'rappeler au [TÉL]', 'journal_vente : motif filtré, pas vidé : ' || j::text;
    SELECT sync_minimiser(t, '{"id": 1, "motif": "rappeler au 0661234567", "porteur_nom": "X", "porteur_adresse": "Y", "porteur_piece": "Z"}'::jsonb)
      INTO j FROM sync_table t WHERE nom_table = 'journal_registre_psychotrope';
    ASSERT j ->> 'motif' = 'rappeler au [TÉL]' AND NOT j ? 'porteur_nom' AND j ? 'porteur_code', 'registre : motif filtré, porteur sous code : ' || j::text;
    SELECT sync_minimiser(t, '{"id": 1, "motif": "rappeler au 0661234567"}'::jsonb) INTO j FROM sync_table t
    WHERE cardinality(t.colonnes_texte) > 0 AND t.colonnes_texte @> '{motif}' AND NOT EXISTS (SELECT 1 FROM chaine_integrite c WHERE c.journal = t.nom_table)
    LIMIT 1;
    ASSERT j IS NULL OR j -> 'motif' = 'null'::jsonb, 'table non chaînée : motif retiré : ' || coalesce(j::text, '');
    UPDATE sync_parametre SET textes_libres = 'filtres' WHERE id = 1;
    SELECT sync_minimiser(t, '{"id": 1, "motif": "rappeler au 0661234567"}'::jsonb) INTO j FROM sync_table t WHERE nom_table = 'journal_vente';
    ASSERT j ->> 'motif' = 'rappeler au [TÉL]', 'mode filtré inchangé';
END $$;

\echo 'Tests 144 réussis'
ROLLBACK;
