-- =====================================================================
-- Tests du script 147 (associations de produits montantes), en transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/147_synchronisation_associations_produits_montantes_tests.sql
-- =====================================================================
BEGIN;

DO $$
DECLARE v_produit bigint; v_indication bigint; v_avant bigint;
BEGIN
    -- 1. Classement : montantes, avec leur clé
    ASSERT (SELECT sens || ' ' || cle_colonnes::text FROM sync_table WHERE nom_table = 'chifa_code_produit') = 'montante {code}', 'chifa_code_produit';
    ASSERT (SELECT sens || ' ' || cle_colonnes::text FROM sync_table WHERE nom_table = 'produit_indication') = 'montante {produit_id,indication_id}', 'produit_indication';

    -- 2. Rejeu sur une base déjà reclassée : rien n'est inscrit dans la file
    SELECT count(*) INTO v_avant FROM sync_sortie;
    UPDATE sync_parametre SET active = true WHERE id = 1;
END $$;
\i database/migration/147_synchronisation_associations_produits_montantes.sql
DO $$
DECLARE v_produit bigint; v_indication bigint;
BEGIN
    ASSERT (SELECT count(*) FROM sync_sortie WHERE nom_table IN ('chifa_code_produit', 'produit_indication')) = 0, 'rejeu : rien d''inscrit';

    -- 3. Reclassement depuis « descendante », synchronisation active : une association existante est inscrite une fois
    SELECT id INTO v_produit FROM produit ORDER BY id LIMIT 1;
    SELECT id INTO v_indication FROM indication ORDER BY id LIMIT 1;
    ASSERT v_produit IS NOT NULL AND v_indication IS NOT NULL, 'un produit et une indication existent dans la base de test';
    ALTER TABLE produit_indication DISABLE TRIGGER USER;          -- l'association « existait avant » le reclassement
    INSERT INTO produit_indication (produit_id, indication_id) VALUES (v_produit, v_indication) ON CONFLICT DO NOTHING;
    ALTER TABLE produit_indication ENABLE TRIGGER USER;
    UPDATE sync_table SET sens = 'descendante' WHERE nom_table = 'produit_indication';
END $$;
\i database/migration/147_synchronisation_associations_produits_montantes.sql
DO $$
BEGIN
    ASSERT (SELECT count(*) FROM sync_sortie WHERE nom_table = 'produit_indication' AND operation = 'creation') >= 1, 'association existante inscrite';
    ASSERT (SELECT count(*) FROM sync_sortie WHERE nom_table = 'chifa_code_produit') = 0, 'chifa_code_produit déjà montante : rien';
    ASSERT (SELECT bool_and(id_local ~ '^[0-9]+\|[0-9]+$') FROM sync_sortie WHERE nom_table = 'produit_indication'), 'clé « produit|indication »';
END $$;

\echo 'Tests 147 : associations de produits montantes réussis'
ROLLBACK;
