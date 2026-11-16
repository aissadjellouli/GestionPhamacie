-- =====================================================================
-- Tests du script 131 (voie descendante), en transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/131_synchronisation_descendant_tests.sql
-- =====================================================================
BEGIN;

DO $$
BEGIN
    ASSERT (SELECT sens FROM sync_table WHERE nom_table = 'reglage_central') = 'descendante', 'classée descendante';
    ASSERT NOT EXISTS (SELECT sync_tables_non_classees()), 'tables non classées';
    ASSERT NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'sync_parametre' AND column_name = 'familles_envoyees'), 'ancien cache retiré';
    DELETE FROM reglage_central;
    UPDATE sync_parametre SET descendant_version = 0 WHERE id = 1;
    ASSERT sync_familles_effectives() = '{}', 'rien reçu : aucune famille';

    -- Version 2 appliquée, version 1 (plus ancienne) puis 2 (égale) ignorées, version 3 appliquée
    ASSERT sync_appliquer_descendant('reglage_central', 'familles_envoyees', 2, '{"familles":["pharmacie","fournisseurs"]}'), 'v2';
    ASSERT sync_familles_effectives() = '{fournisseurs,pharmacie}', 'v2 lue localement';
    ASSERT NOT sync_appliquer_descendant('reglage_central', 'familles_envoyees', 1, '{"familles":["employes"]}'), 'v1 ignorée';
    ASSERT NOT sync_appliquer_descendant('reglage_central', 'familles_envoyees', 2, '{"familles":["employes"]}'), 'v2 rejouée ignorée';
    ASSERT sync_familles_effectives() = '{fournisseurs,pharmacie}', 'jamais écrasé par plus ancien';
    ASSERT sync_appliquer_descendant('reglage_central', 'familles_envoyees', 3, '{"familles":[]}'), 'v3';
    ASSERT sync_familles_effectives() = '{}', 'v3 : plus rien';
    ASSERT (SELECT descendant_version FROM sync_parametre) = 3, 'acquittement local';
    ASSERT (SELECT officine_id FROM reglage_central WHERE cle = 'familles_envoyees') = officine_courante(), 'code officine';

    -- Table non prévue, élément mal formé : refusés
    BEGIN
        PERFORM sync_appliquer_descendant('vente', '1', 9, '{}');
        RAISE EXCEPTION 'table non prévue acceptée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%non prévue%', SQLERRM;
    END;
    BEGIN
        PERFORM sync_appliquer_descendant('reglage_central', 'familles_envoyees', 0, '{}');
        RAISE EXCEPTION 'version 0 acceptée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%mal formé%', SQLERRM;
    END;
END $$;

\echo 'Tests du script 131 : tous réussis (transaction annulée).'
ROLLBACK;
