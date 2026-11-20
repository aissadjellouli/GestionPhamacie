-- =====================================================================
-- Tests du script 026 (tables sans archive : cloud_table_miroir.archive_remplacee), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/026_archive_sans_tables_recalculees_tests.sql
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000026', 'Essai 026 A'), ('bbbbbbbb-0000-0000-0000-000000000026', 'Essai 026 B');

-- Réglage posé par le script : faux pour les deux tables d'alertes seulement
DO $$ BEGIN
    ASSERT (SELECT array_agg(nom_table ORDER BY nom_table) FROM cloud_table_miroir WHERE NOT archive_remplacee) = ARRAY['alerte', 'alerte_surveillance'],
        'archive désactivée pour alerte et alerte_surveillance seulement : '
        || (SELECT string_agg(nom_table, ',' ORDER BY nom_table) FROM cloud_table_miroir WHERE NOT archive_remplacee);
    ASSERT (SELECT archive_remplacee FROM cloud_table_miroir WHERE nom_table = 'produit'), 'produit archivé (défaut vrai)';
END $$;

SET LOCAL ROLE pharma_cloud_api;
-- Officine A, époque 1 : deux alertes, une alerte de surveillance, deux produits
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000026', true) \g /dev/null
SELECT cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"alerte","id":"1","op":"creation","donnees":{"id":1,"message":"v1"}},'
    '{"n":2,"table":"alerte","id":"2","op":"creation","donnees":{"id":2,"message":"a supprimer"}},'
    '{"n":3,"table":"alerte_surveillance","id":"1","op":"creation","donnees":{"id":1,"message":"s1"}},'
    '{"n":4,"table":"produit","id":"1","op":"creation","donnees":{"id":1,"nom_commercial":"p1"}},'
    '{"n":5,"table":"produit","id":"2","op":"creation","donnees":{"id":2,"nom_commercial":"p2"}}]}', 'UTF8')) \g /dev/null

-- Officine A repartie sur l'époque 2 avec un chargement complet : alerte 1 remplacée, alerte 2 et surveillance 1 supprimées,
-- produit 1 remplacé, produit 2 supprimé
SELECT * FROM cloud_debut_chargement(1, 2, 1, 0, 'demande', ARRAY['alerte', 'alerte_surveillance', 'produit']) \g /dev/null
SELECT cloud_appliquer_lot(2, 1, repeat('b', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"alerte","id":"1","op":"instantane","donnees":{"id":1,"message":"v2"}},'
    '{"n":2,"table":"alerte","id":"2","op":"suppression","donnees":null},'
    '{"n":3,"table":"alerte_surveillance","id":"1","op":"suppression","donnees":null},'
    '{"n":4,"table":"produit","id":"1","op":"instantane","donnees":{"id":1,"nom_commercial":"p1 bis"}},'
    '{"n":5,"table":"produit","id":"2","op":"suppression","donnees":null}]}', 'UTF8')) \g /dev/null
DO $$ BEGIN
    -- Miroir à jour comme avant
    ASSERT (SELECT message FROM alerte WHERE id = 1) = 'v2', 'alerte remplacée : miroir à jour';
    ASSERT NOT EXISTS (SELECT 1 FROM alerte WHERE id = 2), 'alerte supprimée : miroir à jour';
    ASSERT NOT EXISTS (SELECT 1 FROM alerte_surveillance WHERE id = 1), 'alerte de surveillance supprimée : miroir à jour';
    ASSERT (SELECT nom_commercial FROM produit WHERE id = 1) = 'p1 bis' AND NOT EXISTS (SELECT 1 FROM produit WHERE id = 2), 'produit : miroir à jour';
    -- Archive : seulement pour produit
    ASSERT cloud_lignes_remplacees(2) = 2, 'deux archives (produit remplacé et supprimé) : ' || cloud_lignes_remplacees(2);
    -- Maximum : l'alerte supprimée n'est plus archivée, le maximum retombe au miroir ; produit garde l'archive
    ASSERT (SELECT maximum FROM cloud_maximums_officine() WHERE nom_table = 'alerte') = 1, 'maximum alerte lu dans le miroir';
    ASSERT (SELECT maximum FROM cloud_maximums_officine() WHERE nom_table = 'produit') = 2, 'maximum produit : archive comprise';
END $$;
-- Fin de chargement : seules les lignes archivées sont comptées
SELECT * FROM cloud_fin_chargement(1, '{"produit":1}') \g /dev/null
DO $$ BEGIN
    ASSERT (SELECT lignes_remplacees FROM chargement WHERE numero = 1) = 2, 'compte rendu : 2 lignes archivées';
END $$;
RESET ROLE;

-- Propriétaire : contenu de l'archive
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000026', true) \g /dev/null
DO $$ BEGIN
    ASSERT NOT EXISTS (SELECT 1 FROM ligne_remplacee WHERE nom_table IN ('alerte', 'alerte_surveillance')), 'aucune alerte archivée';
    ASSERT (SELECT count(*) FROM ligne_remplacee WHERE nom_table = 'produit') = 2, 'deux produits archivés';
    ASSERT (SELECT ancienne ->> 'nom_commercial' FROM ligne_remplacee WHERE nom_table = 'produit' AND cle = '{"id": 1}') = 'p1', 'ancienne version de produit 1';
END $$;

-- Le réglage est modifiable : une table remise à vrai archive de nouveau (époque 3 : alerte 1 remplacée)
UPDATE cloud_table_miroir SET archive_remplacee = true WHERE nom_table = 'alerte';
SET LOCAL ROLE pharma_cloud_api;
SELECT cloud_appliquer_lot(3, 1, repeat('c', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"alerte","id":"1","op":"modification","donnees":{"id":1,"message":"v3"}}]}', 'UTF8')) \g /dev/null
RESET ROLE;
DO $$ BEGIN
    ASSERT (SELECT ancienne ->> 'message' FROM ligne_remplacee WHERE nom_table = 'alerte') = 'v2', 'table remise à archiver : ancienne version gardée';
END $$;
UPDATE cloud_table_miroir SET archive_remplacee = false WHERE nom_table = 'alerte';

-- Rejeu : le script repassé ne réécrit pas un réglage posé ensuite (alerte_surveillance remise à vrai, alerte laissée à faux)
UPDATE cloud_table_miroir SET archive_remplacee = true WHERE nom_table = 'alerte_surveillance';
DO $$ BEGIN
    ASSERT (SELECT archive_remplacee FROM cloud_table_miroir WHERE nom_table = 'alerte_surveillance'), 'réglage ultérieur posé';
END $$;
\ir ../026_archive_sans_tables_recalculees.sql
DO $$ BEGIN
    ASSERT (SELECT archive_remplacee FROM cloud_table_miroir WHERE nom_table = 'alerte_surveillance'), 'rejeu : réglage ultérieur gardé (alerte_surveillance reste à vrai)';
    ASSERT NOT (SELECT archive_remplacee FROM cloud_table_miroir WHERE nom_table = 'alerte'), 'rejeu : alerte reste à faux';
END $$;
-- Rejeu de 020 après 026 : la définition de 026 est gardée (colonne archive_remplacee respectée)
\ir ../020_restauration_officine_archive.sql
DO $$ BEGIN
    ASSERT pg_get_functiondef('cloud_appliquer_lot(int, bigint, text, text, bytea)'::regprocedure) LIKE '%m.archive_remplacee%', 'rejeu de 020 : définition de 026 gardée';
END $$;

-- Cloisonnement : B ne voit pas l'archive de A
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000026', true) \g /dev/null
DO $$ BEGIN ASSERT (SELECT count(*) FROM ligne_remplacee) = 0, 'cloisonnement : B ne lit aucune archive'; END $$;

\echo 'Tests du script central 026 (tables sans archive) : tous réussis (transaction annulée).'
ROLLBACK;
