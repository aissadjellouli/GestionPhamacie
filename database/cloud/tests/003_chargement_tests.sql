-- =====================================================================
-- Tests du chargement complet (script 003), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/003_chargement_tests.sql
-- =====================================================================
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000003', 'Essai C'), ('bbbbbbbb-0000-0000-0000-000000000004', 'Essai D');
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000003', true);

DO $$
DECLARE r record; d record; f record;
BEGIN
    -- Avant : lots 1 et 2 reçus (ventes 1, 2, 3 ; vente 3 supprimée plus tard dans l'officine sans que le central le sache)
    PERFORM cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1', convert_to(
        '{"lignes":[{"n":10,"table":"vente","id":"1","op":"creation","donnees":{"id":1,"montant_total":10}},'
        '{"n":11,"table":"vente","id":"2","op":"creation","donnees":{"id":2,"montant_total":20}},'
        '{"n":12,"table":"vente","id":"3","op":"creation","donnees":{"id":3,"montant_total":30}}]}', 'UTF8'));
    PERFORM cloud_appliquer_lot(1, 2, repeat('b', 64), 'sync-v1', convert_to(
        '{"lignes":[{"n":20,"table":"vente","id":"2","op":"modification","donnees":{"id":2,"montant_total":21}}]}', 'UTF8'));

    -- Le central a perdu les lots 3 à 5 (accusés dans l'officine) : début avec reprise après 5
    SELECT * INTO d FROM cloud_debut_chargement(1, 1, 100, 5, 'lots_manquants', ARRAY['vente']);
    ASSERT d.dernier = 5 AND d.abandonnes = 3, 'lots 3 à 5 abandonnés : ' || d.dernier || '/' || d.abandonnes;
    ASSERT (SELECT dernier_lot FROM officine_epoque WHERE epoque = 1) = 5, 'n° attendu : 6';
    SELECT * INTO d FROM cloud_debut_chargement(1, 1, 100, 5, 'lots_manquants', ARRAY['vente']);
    ASSERT d.dernier = 5 AND d.abandonnes = 3, 'début idempotent';
    BEGIN
        PERFORM cloud_debut_chargement(1, 1, 101, 5, 'lots_manquants', ARRAY['vente']);
        RAISE EXCEPTION 'autre repère accepté';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%autre repère%', SQLERRM;
    END;
    BEGIN
        PERFORM cloud_debut_chargement(2, 1, 100, 5, 'demande', ARRAY['sync_pseudonyme']);
        RAISE EXCEPTION 'table inconnue acceptée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE 'Table non reçue%', SQLERRM;
    END;

    -- Lot 6 : modification antérieure (n° 90), instantané (repère 100) de 1 et 2, modification postérieure de 2 (n° 101)
    -- dans le lot ; lot 7 : vente 4 créée après le repère (jamais vue par l'instantané, pas absente)
    SELECT * INTO r FROM cloud_appliquer_lot(1, 6, repeat('c', 64), 'sync-v1', convert_to(
        '{"lignes":[{"n":90,"table":"vente","id":"1","op":"modification","donnees":{"id":1,"montant_total":11}},'
        '{"n":100,"table":"vente","id":"1","op":"instantane","donnees":{"id":1,"montant_total":11}},'
        '{"n":100,"table":"vente","id":"2","op":"instantane","donnees":{"id":2,"montant_total":22}},'
        '{"n":101,"table":"vente","id":"2","op":"modification","donnees":{"id":2,"montant_total":23}}]}', 'UTF8'));
    ASSERT r.statut = 'accepte', 'lot 6 accepté après le saut : ' || r.statut;
    ASSERT (SELECT montant_total FROM vente WHERE id = 2) = 23, 'modification postérieure gagne sur l''instantané';
    ASSERT (SELECT montant_total FROM vente WHERE id = 1) = 11 AND (SELECT sync_numero FROM vente WHERE id = 1) = 100, 'instantané appliqué';
    ASSERT (SELECT count(*) FROM vente WHERE sync_chargement = 1) = 2, 'lignes de l''instantané vues';
    PERFORM cloud_appliquer_lot(1, 7, repeat('d', 64), 'sync-v1', convert_to(
        '{"lignes":[{"n":102,"table":"vente","id":"4","op":"creation","donnees":{"id":4,"montant_total":40}}]}', 'UTF8'));
    ASSERT (SELECT sync_chargement FROM vente WHERE id = 4) IS NULL, 'ligne postérieure non marquée';

    -- Fin : la vente 3 (absente de l'officine) est signalée, jamais effacée
    SELECT * INTO f FROM cloud_fin_chargement(1, '{"vente":2}');
    ASSERT f.absentes = 1 AND f.absentes_par_table = '{"vente":1}' AND f.lignes_vues = '{"vente":2}', 'absentes : ' || f.absentes;
    ASSERT (SELECT sync_absente_le FROM vente WHERE id = 3) IS NOT NULL, 'vente 3 marquée absente';
    ASSERT (SELECT count(*) FROM vente) = 4, 'rien n''est effacé';
    ASSERT (SELECT count(*) FROM vente WHERE sync_absente_le IS NOT NULL) = 1, 'seule la vente 3 est absente';
    SELECT * INTO f FROM cloud_fin_chargement(1, '{"vente":2}');
    ASSERT f.absentes = 1, 'fin idempotente';
    ASSERT (SELECT etat FROM chargement WHERE numero = 1) = 'termine', 'terminé';

    -- La vente 3 reçue de nouveau perd sa marque
    PERFORM cloud_appliquer_lot(1, 8, repeat('e', 64), 'sync-v1', convert_to(
        '{"lignes":[{"n":110,"table":"vente","id":"3","op":"modification","donnees":{"id":3,"montant_total":31}}]}', 'UTF8'));
    ASSERT (SELECT sync_absente_le FROM vente WHERE id = 3) IS NULL, 'marque retirée';

    -- Un nouveau chargement remplace celui en cours ; la fin d'un remplacé est refusée ; sans saut si le central est à jour
    SELECT * INTO d FROM cloud_debut_chargement(2, 1, 200, 3, 'demande', ARRAY['vente']);
    ASSERT d.abandonnes = 0 AND d.dernier = 8, 'aucun saut quand le central est à jour';
    SELECT * INTO d FROM cloud_debut_chargement(3, 1, 300, 8, 'demande', ARRAY['vente']);
    ASSERT (SELECT etat FROM chargement WHERE numero = 2) = 'remplace', 'remplacé';
    BEGIN
        PERFORM cloud_fin_chargement(2, '{}');
        RAISE EXCEPTION 'fin d''un chargement remplacé acceptée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%remplacé%', SQLERRM;
    END;END $$;

-- Cloisonnement : D ne voit pas les chargements de C
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000004', true);
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM chargement) = 0, 'D ne lit pas les chargements de C';
    BEGIN
        PERFORM cloud_fin_chargement(1, '{}');
        RAISE EXCEPTION 'D a terminé un chargement de C';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%inconnu%', SQLERRM;
    END;
END $$;

RESET ROLE;
DO $$ BEGIN
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'lots_abandonnes' AND officine_id = 'aaaaaaaa-0000-0000-0000-000000000003'), 'lots abandonnés journalisés';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'chargement_absentes' AND detail LIKE '%1 ligne(s)%'), 'absentes journalisées';
END $$;

\echo 'Tests du script central 003 : tous réussis (transaction annulée).'
ROLLBACK;
