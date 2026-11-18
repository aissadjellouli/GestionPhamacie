-- =====================================================================
-- Tests du script 145 (ordres envoyés par le serveur central, côté officine), en transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/145_synchronisation_ordres_central_tests.sql
-- =====================================================================
BEGIN;

DO $$
DECLARE v bigint := (SELECT descendant_version FROM sync_parametre WHERE id = 1); s text;
BEGIN
    -- 1. Classement : ordres reçus jamais envoyés, résultats montants (clé ordre_id, motif filtré)
    ASSERT (SELECT sens FROM sync_table WHERE nom_table = 'sync_ordre_recu') = 'jamais', 'ordres reçus jamais envoyés';
    ASSERT (SELECT sens || cle_colonnes::text || colonnes_texte::text FROM sync_table WHERE nom_table = 'sync_ordre_resultat') = 'montante{ordre_id}{motif}', 'résultats montants';
    ASSERT NOT EXISTS (SELECT * FROM sync_tables_non_classees()), 'toutes les tables classées';

    -- 2. Les réglages passent toujours par la version du script 141
    s := sync_recevoir_descendant('reglage_central', 'essai_ordres', v + 1, '{"x":1}', v + 1);
    ASSERT s = 'applique' AND EXISTS (SELECT 1 FROM reglage_central WHERE cle = 'essai_ordres'), 'réglage reçu : ' || s;

    -- 3. Ordre reçu, puis renvoyé à l'identique (ancien), puis annulé au central
    s := sync_recevoir_descendant('ordre_central', '11111111-2222-3333-4444-555555555555', v + 2,
        '{"format":"ordre-v1","id":"11111111-2222-3333-4444-555555555555","type":"employe_bloquer","cible":"7","attendu":{"actif":true},"donnees":{},"auteur":"admin.essai","cree_le":"2026-11-18T10:00:00Z","expire_le":"2026-11-25T10:00:00Z","statut":"en_attente"}', v + 2);
    ASSERT s = 'applique' AND (SELECT statut FROM sync_ordre_recu) = 'a_appliquer', 'ordre rangé à appliquer';
    ASSERT sync_recevoir_descendant('ordre_central', '11111111-2222-3333-4444-555555555555', v + 2,
        '{"format":"ordre-v1","id":"11111111-2222-3333-4444-555555555555","type":"employe_bloquer","cible":"7","attendu":{},"donnees":{},"auteur":"admin.essai","cree_le":"2026-11-18T10:00:00Z","expire_le":"2026-11-25T10:00:00Z","statut":"en_attente"}', v + 2) = 'ancien', 'même version : ancien';
    s := sync_recevoir_descendant('ordre_central', '11111111-2222-3333-4444-555555555555', v + 3,
        '{"format":"ordre-v1","id":"11111111-2222-3333-4444-555555555555","type":"employe_bloquer","cible":"7","attendu":{},"donnees":{},"auteur":"admin.essai","cree_le":"2026-11-18T10:00:00Z","expire_le":"2026-11-25T10:00:00Z","statut":"annule"}', v + 3);
    ASSERT (SELECT statut FROM sync_ordre_recu) = 'annule' AND (SELECT statut FROM sync_ordre_resultat) = 'annule', 'annulé au central';
    ASSERT EXISTS (SELECT 1 FROM journal_audit WHERE action = 'ordre_central_annule' AND utilisateur_id IS NULL), 'journal des actions';

    -- 4. Ordre traité : plus de changement d'état
    BEGIN
        PERFORM sync_ordre_noter('11111111-2222-3333-4444-555555555555', 'applique', NULL, NULL);
        RAISE EXCEPTION 'ordre retraité';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%déjà traité%', SQLERRM;
    END;

    -- 5. Éléments mal formés : refusés et journalisés, sans ordre créé
    ASSERT sync_recevoir_descendant('ordre_central', 'pas-un-uuid', v + 4, '{"format":"ordre-v1"}', v + 4) = 'refuse', 'clé invalide';
    ASSERT sync_recevoir_descendant('ordre_central', '22222222-2222-3333-4444-555555555555', v + 5,
        '{"format":"autre","id":"22222222-2222-3333-4444-555555555555"}', v + 5) = 'refuse', 'format inconnu';
    ASSERT (SELECT count(*) FROM sync_ordre_recu) = 1, 'aucun ordre mal formé rangé';
    ASSERT (SELECT count(*) FROM sync_journal_descendant WHERE nom_table = 'ordre_central' AND statut = 'refuse') >= 2, 'refus journalisés';

    -- 6. Ordre expiré au central avant d'arriver : jamais appliqué
    PERFORM sync_recevoir_descendant('ordre_central', '33333333-2222-3333-4444-555555555555', v + 6,
        '{"format":"ordre-v1","id":"33333333-2222-3333-4444-555555555555","type":"essai","attendu":{},"donnees":{},"auteur":"admin.essai","cree_le":"2026-11-10T10:00:00Z","expire_le":"2026-11-17T10:00:00Z","statut":"expire"}', v + 6);
    ASSERT (SELECT statut FROM sync_ordre_recu WHERE id = '33333333-2222-3333-4444-555555555555') = 'expire', 'arrivé expiré';
END $$;

\echo 'Tests 145 réussis'
ROLLBACK;
