-- =====================================================================
-- Tests du script 129 (chargement initial et rechargement complet), en
-- transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/129_synchronisation_chargement_tests.sql
-- La prise de l'instantané en deux transactions (verrou court + image
-- exportée) est faite par l'application (ChargementSyncSql) : essayée de
-- bout en bout (docs/conception-synchronisation.md § 13).
-- =====================================================================
BEGIN;

DO $$
DECLARE
    n int; n2 int; m bigint; v_rep bigint; v_tables int; t sync_table; v_p record; v_id bigint; v_premier text;
BEGIN
    PERFORM sync_definir_active(true);
    ASSERT NOT EXISTS (SELECT * FROM sync_tables_non_classees()), 'sync_chargement classée';
    ASSERT (SELECT sens FROM sync_table WHERE nom_table = 'sync_chargement') = 'jamais', 'sync_chargement jamais envoyée';

    -- Demande : un seul chargement en cours ; une 2e demande réutilise celui qui n'a pas encore d'instantané
    n := sync_demander_chargement('activation', NULL);
    ASSERT n IS NOT NULL, 'chargement demandé';
    ASSERT sync_demander_chargement('demande', 1) = n, 'demande réutilisée tant que pas d''instantané';
    BEGIN
        PERFORM sync_demander_chargement('inconnu', NULL);
        RAISE EXCEPTION 'motif inconnu accepté';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%motif%', SQLERRM;
    END;

    -- Préparation : état constitution, aucun lot formé pendant ce temps, codes créés d'avance
    v_tables := sync_chargement_preparer(n);
    ASSERT v_tables = (SELECT count(*) FROM sync_table s JOIN pg_tables p ON p.schemaname = 'public' AND p.tablename = s.nom_table
                       WHERE s.sens IN ('montante', 'bidirectionnelle')), 'toutes les tables envoyées (médecins hors option)';
    ASSERT NOT EXISTS (SELECT 1 FROM patient pa WHERE NOT EXISTS (SELECT 1 FROM sync_pseudonyme s WHERE s.nature = 'patient' AND s.cle_locale = pa.id::text)),
        'code de chaque patient créé d''avance';
    INSERT INTO sync_sortie (nom_table, id_local, operation, donnees, version_contrat, epoque)
    VALUES ('vente', '999999001', 'modification', '{"id":999999001}', 'sync-v1', (SELECT epoque FROM sync_parametre)) RETURNING id INTO v_id;
    ASSERT sync_enregistrer_lot((SELECT dernier_lot FROM sync_parametre) + 1, ARRAY[v_id], 10, repeat('a', 64)) IS NULL,
        'aucun lot pendant la constitution';

    -- Instantané de patient et de vente : minimisé exactement comme la file
    v_rep := (SELECT max(id) FROM sync_sortie);
    ASSERT sync_instantane_table(n, 'patient', v_rep) = (SELECT count(*) FROM patient), 'une ligne par patient';
    m := sync_instantane_table(n, 'vente', v_rep);
    ASSERT m = (SELECT count(*) FROM vente), 'une ligne par vente';
    ASSERT NOT EXISTS (SELECT 1 FROM sync_sortie WHERE chargement = n AND nom_table = 'patient'
                       AND (donnees ? 'nom' OR donnees ? 'prenom' OR donnees ? 'telephone' OR donnees ? 'num_assure' OR id_local !~ '^P-')),
        'patients sous code, sans nom ni téléphone';
    SELECT * INTO t FROM sync_table WHERE nom_table = 'vente';
    ASSERT NOT EXISTS (SELECT 1 FROM sync_sortie s JOIN vente v ON s.id_local = v.id::text
                       WHERE s.chargement = n AND s.nom_table = 'vente' AND s.donnees IS DISTINCT FROM sync_minimiser(t, to_jsonb(v))),
        'même minimisation que le déclencheur';
    ASSERT NOT EXISTS (SELECT 1 FROM sync_sortie WHERE chargement = n AND (operation <> 'instantane' OR ordre <> v_rep)), 'ordre = repère';
    BEGIN
        PERFORM sync_instantane_table(n, 'sync_pseudonyme', v_rep);
        RAISE EXCEPTION 'table jamais envoyée acceptée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%non envoyée%', SQLERRM;
    END;
    BEGIN
        INSERT INTO sync_sortie (nom_table, id_local, operation, donnees, version_contrat, epoque)
        VALUES ('vente', '1', 'instantane', '{"id":1}', 'sync-v1', 1);
        RAISE EXCEPTION 'instantané sans chargement accepté';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    PERFORM sync_chargement_pret(n, v_rep, jsonb_build_object('patient', (SELECT count(*) FROM patient), 'vente', m));
    ASSERT (SELECT etat FROM sync_chargement WHERE numero = n) = 'pret', 'prêt';
    ASSERT (SELECT lignes_total FROM sync_chargement WHERE numero = n) = (SELECT count(*) FROM patient) + m, 'total';

    -- Ordre des lignes en attente : (ordre, n°) -> modification antérieure, instantané, modification postérieure
    INSERT INTO sync_sortie (nom_table, id_local, operation, donnees, version_contrat, epoque)
    VALUES ('vente', '999999002', 'modification', '{"id":999999002}', 'sync-v1', (SELECT epoque FROM sync_parametre));
    SELECT string_agg(operation, ',' ORDER BY coalesce(ordre, id), id) INTO v_premier
    FROM (SELECT DISTINCT ON (operation, chargement IS NULL, id > v_rep) operation, ordre, id, chargement FROM sync_sortie
          WHERE lot_numero IS NULL AND id >= v_id ORDER BY operation, chargement IS NULL, id > v_rep, id) x;
    ASSERT v_premier = 'modification,instantane,modification', 'ordre d''envoi : ' || v_premier;

    -- Lots de nouveau permis ; progression
    ASSERT sync_enregistrer_lot((SELECT dernier_lot FROM sync_parametre) + 1, ARRAY[v_id], 10, repeat('a', 64)) IS NOT NULL, 'lots permis après';
    SELECT * INTO v_p FROM sync_chargement_progression(n);
    ASSERT v_p.inscrites = (SELECT count(*) FROM patient) + m AND v_p.accusees = 0, 'progression';
    ASSERT sync_chargement_accuse(n) = (v_p.inscrites = 0), 'pas encore accusé';

    -- Une simple demande n'interrompt pas un chargement dont l'instantané est pris ; des lots manquants le remplacent
    ASSERT sync_demander_chargement('demande', 1) IS NULL, 'demande refusée pendant l''envoi';
    n2 := sync_demander_chargement('lots_manquants', NULL);
    ASSERT n2 > n AND (SELECT etat FROM sync_chargement WHERE numero = n) = 'remplace', 'remplacé';
    BEGIN
        PERFORM sync_chargement_annonce(n2, 0);
        RAISE EXCEPTION 'annonce sans instantané acceptée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%non prêt%', SQLERRM;
    END;
    PERFORM sync_chargement_preparer(n2);
    PERFORM sync_chargement_pret(n2, v_rep, '{}'::jsonb);
    PERFORM sync_chargement_annonce(n2, 5);
    ASSERT sync_chargement_accuse(n2), 'chargement vide accusé';
    PERFORM sync_chargement_terminer(n2, 3, '{"vente":3}');
    ASSERT (SELECT etat || absentes_central FROM sync_chargement WHERE numero = n2) = 'termine3', 'terminé';
    ASSERT sync_demander_chargement('demande', 1) > n2, 'nouvelle demande après la fin';
END $$;

\echo 'Tests du script 129 : tous réussis (transaction annulée).'
ROLLBACK;
