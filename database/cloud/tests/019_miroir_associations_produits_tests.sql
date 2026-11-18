-- =====================================================================
-- Tests des tables miroir des associations de produits (script 019), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/019_miroir_associations_produits_tests.sql
-- =====================================================================
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000019', 'Essai 019 A'), ('bbbbbbbb-0000-0000-0000-000000000019', 'Essai 019 B');
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000019', true) \g /dev/null
SELECT cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"chifa_code_produit","id":"6111234567890","op":"creation","donnees":{"code":"6111234567890","produit_id":7,"associe_par":2,"associe_le":"2026-11-18T10:00:00+01:00"}},'
    '{"n":2,"table":"produit_indication","id":"7|3","op":"creation","donnees":{"produit_id":7,"indication_id":3,"cree_le":"2026-11-18T10:00:00+01:00"}},'
    '{"n":3,"table":"produit_indication","id":"7|4","op":"creation","donnees":{"produit_id":7,"indication_id":4}}]}', 'UTF8')) \g /dev/null
-- Suppression d'une association : n° plus grand que celui de sa création (le central garde la ligne de plus grand n°)
SELECT cloud_appliquer_lot(1, 2, repeat('b', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":4,"table":"produit_indication","id":"7|4","op":"suppression","donnees":null}]}', 'UTF8')) \g /dev/null
DO $$
BEGIN
    ASSERT (SELECT produit_id || '|' || associe_par FROM chifa_code_produit WHERE code = '6111234567890') = '7|2', 'code CHIFA reçu';
    ASSERT (SELECT string_agg(indication_id::text, ',') FROM produit_indication WHERE produit_id = 7) = '3', 'une association reçue, une supprimée : '
        || (SELECT string_agg(indication_id::text, ',') FROM produit_indication);
END $$;
-- Cloisonnement : l'officine B ne voit rien
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000019', true) \g /dev/null
DO $$ BEGIN ASSERT (SELECT count(*) FROM chifa_code_produit) + (SELECT count(*) FROM produit_indication) = 0, 'officine B : rien'; END $$;
RESET ROLE;

\echo 'Tests 019 : associations de produits reçues au central réussis'
ROLLBACK;
