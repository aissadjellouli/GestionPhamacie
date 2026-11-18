-- =====================================================================
-- Tests du script 024 (plus grand n° de ticket reçu), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/024_dernier_ticket_officine_tests.sql
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000024', 'Essai 024 A'), ('bbbbbbbb-0000-0000-0000-000000000024', 'Essai 024 B');
SET LOCAL ROLE pharma_cloud_api;

-- Officine A : tickets T-000003 et T-000017 (ventes), un n° de démonstration ignoré ; officine B : T-000999
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000024', true) \g /dev/null
DO $$ BEGIN ASSERT cloud_dernier_ticket() IS NULL, 'aucune vente : rien'; END $$;
SELECT cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"vente","id":"1","op":"creation","donnees":{"id":1,"numero":"T-000003"}},'
    '{"n":2,"table":"vente","id":"2","op":"creation","donnees":{"id":2,"numero":"T-000017"}},'
    '{"n":3,"table":"vente","id":"3","op":"creation","donnees":{"id":3,"numero":"T-DEMO-99999"}}]}', 'UTF8')) \g /dev/null
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000024', true) \g /dev/null
SELECT cloud_appliquer_lot(1, 1, repeat('b', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"vente","id":"1","op":"creation","donnees":{"id":1,"numero":"T-000999"}}]}', 'UTF8')) \g /dev/null

SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000024', true) \g /dev/null
DO $$ BEGIN
    ASSERT cloud_dernier_ticket() = 17, 'A : 17 (jamais le n° de B, jamais le n° de démonstration) : ' || coalesce(cloud_dernier_ticket()::text, 'null');
END $$;
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000024', true) \g /dev/null
DO $$ BEGIN ASSERT cloud_dernier_ticket() = 999, 'B : 999'; END $$;

ROLLBACK;
\echo '024 : tous réussis'
