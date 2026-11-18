-- =====================================================================
-- Tests du script 146 (commandes en brouillon du serveur central), en transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/146_synchronisation_commande_brouillon_central_tests.sql
-- =====================================================================
BEGIN;

DO $$
DECLARE f bigint; c1 bigint; c2 bigint; o uuid := '11111111-0000-0000-0000-000000000146';
BEGIN
    ASSERT (SELECT sens FROM sync_table WHERE nom_table = 'commande_ordre_central') = 'jamais', 'jamais envoyée';
    SELECT id INTO f FROM fournisseur ORDER BY id LIMIT 1;
    ASSERT f IS NOT NULL, 'un grossiste existe dans la base de test';
    INSERT INTO sync_ordre_recu (id, type, cible, auteur, cree_le, expire_le, version)
    VALUES (o, 'commande', f::text, 'admin.essai', now(), now() + interval '7 days', 1);
    INSERT INTO commande_fournisseur (fournisseur_id, numero) VALUES (f, 'ESSAI-146-A') RETURNING id INTO c1;
    INSERT INTO commande_fournisseur (fournisseur_id, numero) VALUES (f, 'ESSAI-146-B') RETURNING id INTO c2;
    INSERT INTO commande_ordre_central (commande_id, ordre_id, auteur) VALUES (c1, o, 'admin.essai');
    -- Un même ordre ne crée jamais deux commandes
    BEGIN INSERT INTO commande_ordre_central (commande_id, ordre_id, auteur) VALUES (c2, o, 'admin.essai'); RAISE EXCEPTION 'ordre en double accepté';
    EXCEPTION WHEN unique_violation THEN NULL; END;
    BEGIN INSERT INTO commande_ordre_central (commande_id, ordre_id, auteur) VALUES (c2, '22222222-0000-0000-0000-000000000146', 'x'); RAISE EXCEPTION 'ordre inconnu accepté';
    EXCEPTION WHEN foreign_key_violation THEN NULL; END;
END $$;

\echo 'Tests 146 : commandes en brouillon du serveur central réussis'
ROLLBACK;
