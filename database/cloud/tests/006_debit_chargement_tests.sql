-- =====================================================================
-- Tests du débit pendant un chargement complet (script 006), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/006_debit_chargement_tests.sql
-- =====================================================================
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000009', 'Essai I');
INSERT INTO certificat (empreinte, officine_id, serveur, numero_serie, echeance)
VALUES (repeat('9', 64), 'aaaaaaaa-0000-0000-0000-000000000009', 'principal', '1', now() + interval '1 year');
-- Quotas réduits pour l'essai : 100 octets par jour, 1000 par chargement
UPDATE cloud_parametre SET quota_octets_jour = 100, quota_chargement_octets = 1000 WHERE id = 1;

SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000009', true);
DO $$
DECLARE r record; v_lot bytea := convert_to(rpad('{"lignes":[]}', 80), 'UTF8');
BEGIN
    ASSERT NOT cloud_chargement_en_cours(repeat('9', 64)), 'pas de chargement';
    ASSERT NOT cloud_chargement_en_cours(repeat('8', 64)), 'certificat inconnu';
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000009', true);
    -- Hors chargement : quota du jour (80 + 80 > 100)
    SELECT * INTO r FROM cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1', v_lot);
    ASSERT r.statut = 'accepte', r.statut;
    SELECT * INTO r FROM cloud_appliquer_lot(1, 2, repeat('b', 64), 'sync-v1', v_lot);
    ASSERT r.statut = 'quota_depasse', 'quota du jour : ' || r.statut;
    -- Chargement annoncé : limite relevée signalée, quota du jour ignoré, plafond du chargement appliqué
    PERFORM cloud_debut_chargement(1, 1, 100, 1, 'demande', ARRAY['vente']);
    ASSERT cloud_chargement_en_cours(repeat('9', 64)), 'chargement en cours vu par l''API';
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000009', true);
    FOR i IN 2 .. 13 LOOP
        SELECT * INTO r FROM cloud_appliquer_lot(1, i, repeat('c', 64), 'sync-v1', v_lot);
        ASSERT r.statut = 'accepte', 'lot ' || i || ' pendant le chargement : ' || r.statut;
    END LOOP;
    ASSERT (SELECT octets_recus FROM chargement WHERE numero = 1) = 960, 'octets du chargement';
    ASSERT (SELECT octets FROM officine_volume_jour) = 80, 'quota du jour non entamé par le chargement';
    SELECT * INTO r FROM cloud_appliquer_lot(1, 14, repeat('d', 64), 'sync-v1', v_lot);
    ASSERT r.statut = 'quota_depasse', 'plafond du chargement : ' || r.statut;
    -- Fin du chargement : retour à la limite et au quota normaux
    PERFORM cloud_fin_chargement(1, '{"vente":0}');
    ASSERT NOT cloud_chargement_en_cours(repeat('9', 64)), 'chargement terminé';
END $$;
RESET ROLE;

\echo 'Tests du script 006 : tous réussis (transaction annulée).'
ROLLBACK;
