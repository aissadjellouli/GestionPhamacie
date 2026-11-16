-- =====================================================================
-- Tests du script 136 (corrections après revue), en transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/136_synchronisation_corrections_revue_tests.sql
-- =====================================================================
BEGIN;

DO $$
DECLARE p sync_parametre; s1 bigint; v_lot bigint; v_cols text[]; st text;
BEGIN
    ASSERT NOT EXISTS (SELECT sync_tables_non_classees()), 'tables non classées';
    -- ---- 1. Colonnes figées ----
    DELETE FROM reglage_central;
    UPDATE sync_parametre SET descendant_version = 0 WHERE id = 1;
    v_cols := sync_colonnes_interdites();
    ASSERT 'utilisateur.nom_complet' = ANY (v_cols) AND 'fournisseur.telephone' = ANY (v_cols), 'rien reçu : tout interdit';
    ASSERT sync_retirer_colonnes('fournisseur', '{"id":1,"telephone":"0","nif":"1"}', '{fournisseur.telephone,utilisateur.nom_complet}') = '{"id":1,"nif":"1"}', 'retrait par colonnes';
    ASSERT sync_retirer_colonnes('fournisseur', '{"id":1,"telephone":"0"}', NULL) = '{"id":1,"telephone":"0"}', 'NULL : aucun retrait';
    SELECT * INTO p FROM sync_parametre WHERE id = 1;
    INSERT INTO sync_sortie (nom_table, id_local, operation, donnees, version_contrat, epoque)
    VALUES ('fournisseur', '7', 'modification', '{"id":7,"telephone":"0"}', p.version_contrat, p.epoque) RETURNING id INTO s1;
    ASSERT sync_constituer_lot(p.dernier_lot + 1, ARRAY[s1], 10, repeat('a', 64), '{fournisseur.telephone}') IS NULL, 'colonnes différentes : refusé';
    v_lot := sync_constituer_lot(p.dernier_lot + 1, ARRAY[s1], 10, repeat('a', 64), v_cols);
    ASSERT (SELECT colonnes_retirees = v_cols FROM sync_lot WHERE epoque = p.epoque AND numero = v_lot), 'colonnes figées';
    -- Une colonne ajoutée ensuite au catalogue ne change pas la liste figée du lot
    INSERT INTO sync_famille_colonne VALUES ('banque', 'fournisseur', 'nif');
    ASSERT (SELECT colonnes_retirees = v_cols FROM sync_lot WHERE epoque = p.epoque AND numero = v_lot), 'liste figée inchangée';
    -- Reforme : seulement en retirant davantage
    PERFORM sync_reformer_lot_colonnes(p.epoque, v_lot, repeat('b', 64), 8, v_cols || '{fournisseur.nif}');
    BEGIN
        PERFORM sync_reformer_lot_colonnes(p.epoque, v_lot, repeat('c', 64), 8, '{fournisseur.nif}');
        RAISE EXCEPTION 'reforme qui retire moins acceptée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%lot figé%', SQLERRM;
    END;
    PERFORM sync_enregistrer_accuse(p.epoque, v_lot);
    BEGIN
        PERFORM sync_reformer_lot_colonnes(p.epoque, v_lot, repeat('d', 64), 8, v_cols || '{fournisseur.nif,x.y}');
        RAISE EXCEPTION 'lot accusé reformé';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%déjà accusé%', SQLERRM;
    END;

    -- ---- 2. Voie descendante ----
    ASSERT sync_recevoir_descendant('reglage_central', 'familles_envoyees', 3, '{"familles":["pharmacie"]}', 3) = 'applique', 'appliqué';
    ASSERT sync_recevoir_descendant('reglage_central', 'familles_envoyees', 2, '{"familles":[]}', 3) = 'ancien', 'ancien ignoré';
    ASSERT sync_recevoir_descendant('prix_central', 'x', 4, '{"a":1}', 4) = 'table_inconnue', 'table inconnue ignorée sans erreur';
    ASSERT sync_recevoir_descendant('reglage_central', 'familles_envoyees', 9, '{"familles":[]}', 5) = 'refuse', 'au-delà de la version max';
    ASSERT sync_recevoir_descendant('reglage_central', 'familles_envoyees', 5000, '{"familles":[]}', 5000) = 'refuse', 'saut anormal';
    ASSERT sync_recevoir_descendant('reglage_central', 'familles_envoyees', 4, '[1]', 4) = 'refuse', 'mal formé';
    ASSERT sync_recevoir_descendant('reglage_central', 'familles_envoyees', 6, jsonb_build_object('x', repeat('a', 70000)), 6) = 'refuse', 'données trop volumineuses';
    ASSERT sync_familles_effectives() = '{pharmacie}', 'seul le valide appliqué';
    ASSERT (SELECT descendant_version FROM sync_parametre) = 0, 'version acquittée non avancée par la réception';
    ASSERT sync_noter_version_descendante(3) = 3 AND sync_noter_version_descendante(1) = 3, 'acquittement monotone';
    ASSERT (SELECT count(*) FROM sync_journal_descendant WHERE statut = 'refuse') = 4, 'refus journalisés';
    ASSERT (SELECT avant IS NULL AND apres = '["pharmacie"]' FROM sync_journal_descendant WHERE statut = 'applique'), 'familles avant / après journalisées';
    ASSERT NOT EXISTS (SELECT 1 FROM sync_journal_descendant WHERE statut = 'table_inconnue' AND (avant IS NOT NULL OR apres IS NOT NULL)), 'aucune donnée journalisée';

    -- ---- 3. Contenance ----
    ASSERT contenance_depuis_conditionnement('b/30 gélules') = 30, 'minuscules accentuées';
    ASSERT contenance_depuis_conditionnement('BOÎTE DE 20 COMPRIMÉS') = 20, 'majuscules accentuées';
    ASSERT contenance_depuis_conditionnement('boîte de 10 récipients unidoses') = 10, 'récipients';
    ASSERT contenance_depuis_conditionnement('FL/100ML') IS NULL, 'flacon exclu';
END $$;

\echo 'Tests du script 136 : tous réussis (transaction annulée).'
ROLLBACK;
