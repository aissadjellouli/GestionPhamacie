-- =====================================================================
-- Tests du script 130 (familles nominatives), en transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/130_synchronisation_familles_nominatives_tests.sql
-- =====================================================================
BEGIN;

DO $$
DECLARE
    j jsonb := '{"id":7,"raison_sociale":"Grossiste","telephone":"0550000000","email":"a@b.dz","adresse":"Rue 1","nif":"123"}';
    u jsonb := '{"id":3,"nom_complet":"Nom Prénom","identifiant":"np","role_id":2,"actif":true}';
    p sync_parametre; v_epoque int; v_lot bigint; s1 bigint; s2 bigint;
BEGIN
    -- Inventaire : classée, toutes les familles présentes, colonnes existantes dans des tables envoyées
    ASSERT NOT EXISTS (SELECT sync_tables_non_classees()), 'tables non classées';
    ASSERT (SELECT count(DISTINCT famille) FROM sync_famille_colonne) = 6, 'six familles (scripts 130 et 133)';
    ASSERT NOT EXISTS (SELECT 1 FROM sync_famille_colonne f WHERE NOT EXISTS (
        SELECT 1 FROM information_schema.columns c WHERE c.table_schema = 'public' AND c.table_name = f.nom_table AND c.column_name = f.colonne)),
        'colonne de famille inexistante';
    ASSERT NOT EXISTS (SELECT 1 FROM sync_famille_colonne f JOIN sync_table s ON s.nom_table = f.nom_table
                       WHERE s.sens NOT IN ('montante', 'bidirectionnelle')), 'famille sur une table non envoyée';

    -- Réglage jamais lu : rien de nominatif
    DELETE FROM reglage_central WHERE cle = 'familles_envoyees'; -- réglage reçu par la voie descendante (script 131)
    ASSERT sync_familles_effectives() = '{}', 'inconnu = aucune famille';
    ASSERT NOT (sync_retirer_familles('fournisseur', j, sync_familles_effectives()) ?| ARRAY['telephone', 'email', 'adresse']), 'contacts retirés';
    ASSERT sync_retirer_familles('fournisseur', j, sync_familles_effectives()) ->> 'nif' = '123', 'autres colonnes gardées';
    ASSERT sync_retirer_familles('utilisateur', u, '{}') = '{"id":3,"role_id":2,"actif":true}'::jsonb, 'employé : rôle et code seulement';
    ASSERT sync_retirer_familles('fournisseur', j, NULL) = j, 'lot antérieur : aucun retrait';
    ASSERT sync_retirer_familles('vente', '{"id":1}', '{}') = '{"id":1}', 'table hors familles inchangée';
    ASSERT sync_retirer_familles('fournisseur', NULL, '{}') IS NULL, 'suppression : rien';

    -- Réglage lu : familles inconnues ignorées, triées
    PERFORM sync_appliquer_descendant('reglage_central', 'familles_envoyees', 1, '{"familles":["pharmacie","inconnue","fournisseurs"]}');
    ASSERT sync_familles_effectives() = '{fournisseurs,pharmacie}', 'reçu : inconnues ignorées, triées';
    ASSERT sync_retirer_familles('fournisseur', j, sync_familles_effectives()) = j, 'fournisseurs autorisés : tout part';
    ASSERT NOT (sync_retirer_familles('utilisateur', u, sync_familles_effectives()) ? 'nom_complet'), 'employés toujours retirés';

    -- Lot : familles gardées ; refusé si le réglage a changé depuis la lecture des lignes
    SELECT * INTO p FROM sync_parametre WHERE id = 1;
    v_epoque := p.epoque;
    INSERT INTO sync_sortie (nom_table, id_local, operation, donnees, version_contrat, epoque)
    VALUES ('fournisseur', '7', 'modification', j, p.version_contrat, p.epoque) RETURNING id INTO s1;
    INSERT INTO sync_sortie (nom_table, id_local, operation, donnees, version_contrat, epoque)
    VALUES ('fournisseur', '8', 'creation', j, p.version_contrat, p.epoque) RETURNING id INTO s2;
    ASSERT sync_enregistrer_lot(p.dernier_lot + 1, ARRAY[s1], 10, repeat('a', 64), ARRAY['pharmacie']) IS NULL, 'réglage changé : refusé';
    v_lot := sync_enregistrer_lot(p.dernier_lot + 1, ARRAY[s1], 10, repeat('a', 64), ARRAY['pharmacie', 'fournisseurs']);
    ASSERT v_lot = p.dernier_lot + 1, 'lot enregistré';
    ASSERT (SELECT familles FROM sync_lot WHERE epoque = v_epoque AND numero = v_lot) = '{fournisseurs,pharmacie}', 'familles du lot';
    -- Ancienne signature : lot sans retrait
    ASSERT sync_enregistrer_lot(v_lot + 1, ARRAY[s2], 10, repeat('c', 64)) = v_lot + 1, 'ancienne signature';
    ASSERT (SELECT familles FROM sync_lot WHERE epoque = v_epoque AND numero = v_lot + 1) IS NULL, 'NULL = sans retrait';

    -- Reforme : seulement en réduction stricte, lot non accusé, rien d'autre ne change
    PERFORM sync_reformer_lot(v_epoque, v_lot, repeat('b', 64), 8, ARRAY['pharmacie']);
    ASSERT (SELECT empreinte = repeat('b', 64) AND octets = 8 AND familles = '{pharmacie}' AND reforme_le IS NOT NULL
            FROM sync_lot WHERE epoque = v_epoque AND numero = v_lot), 'lot reformé';
    BEGIN
        PERFORM sync_reformer_lot(v_epoque, v_lot, repeat('d', 64), 8, ARRAY['pharmacie', 'employes']);
        RAISE EXCEPTION 'familles élargies acceptées';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%lot figé%', SQLERRM;
    END;
    BEGIN
        UPDATE sync_lot SET familles = '{}', nb_lignes = 9 WHERE epoque = v_epoque AND numero = v_lot;
        RAISE EXCEPTION 'autre colonne modifiée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%lot figé%', SQLERRM;
    END;
    PERFORM sync_reformer_lot(v_epoque, v_lot + 1, repeat('e', 64), 8, '{}');
    PERFORM sync_enregistrer_accuse(v_epoque, v_lot + 1);
    BEGIN
        PERFORM sync_reformer_lot(v_epoque, v_lot, repeat('f', 64), 8, '{}');
        RAISE EXCEPTION 'lot accusé reformé';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%déjà accusé%', SQLERRM;
    END;
END $$;

\echo 'Tests du script 130 : tous réussis (transaction annulée).'
ROLLBACK;
