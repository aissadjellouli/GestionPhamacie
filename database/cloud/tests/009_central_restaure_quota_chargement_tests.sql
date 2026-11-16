-- =====================================================================
-- Tests du script central 009 (corrections après revue), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/009_central_restaure_quota_chargement_tests.sql
-- =====================================================================
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000011', 'Essai K'), ('bbbbbbbb-0000-0000-0000-000000000012', 'Essai L');
INSERT INTO certificat (empreinte, officine_id, serveur, numero_serie, echeance)
VALUES (repeat('7', 64), 'aaaaaaaa-0000-0000-0000-000000000011', 'principal', '1', now() + interval '1 year');

-- ---- F4 : le rôle applicatif ne peut ni modifier, ni effacer, ni vider les lots reçus (table et partitions) ----
DO $$
DECLARE t regclass;
BEGIN
    FOR t IN SELECT 'lot_recu'::regclass UNION ALL SELECT inhrelid::regclass FROM pg_inherits WHERE inhparent = 'lot_recu'::regclass LOOP
        ASSERT NOT has_table_privilege('pharma_cloud_api', t, 'UPDATE'), 'UPDATE sur ' || t;
        ASSERT NOT has_table_privilege('pharma_cloud_api', t, 'DELETE'), 'DELETE sur ' || t;
        ASSERT NOT has_table_privilege('pharma_cloud_api', t, 'TRUNCATE'), 'TRUNCATE sur ' || t;
    END LOOP;
END $$;

-- ---- 1. Central restauré : la version annoncée par l'officine dépasse la version maximale ----
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000011', true);
DO $$
DECLARE r record; v_max bigint;
BEGIN
    SELECT * INTO r FROM cloud_descendants(0);
    ASSERT r.version = 1 AND r.version_max = 1, 'version 1';
    -- L'officine a déjà appliqué la version 40 (central restauré : il ne la connaît plus)
    SELECT * INTO r FROM cloud_descendants(40);
    ASSERT r.version = 41 AND r.version_max = 41, 'élément republié au-dessus de 40 : ' || r.version;
    ASSERT (SELECT version_max FROM officine_descendant) = 41, 'version maximale remontée';
    ASSERT (SELECT version_acquittee FROM officine_descendant) = 40, 'acquittement de 40';
    -- Plus rien ensuite, et aucune republication à chaque échange
    ASSERT (SELECT count(*) FROM cloud_descendants(41)) = 0, 'rien de plus';
    ASSERT (SELECT version_max FROM officine_descendant) = 41, 'stable';
    -- Version absurde : ignorée
    PERFORM cloud_descendants(5000000000000);
    ASSERT (SELECT version_max FROM officine_descendant) = 41, 'au-delà du plafond ignoré';
END $$;
RESET ROLE;
-- Le réglage change ensuite : la version suivante dépasse bien celle de l'officine
DO $$ BEGIN PERFORM cloud_regler_famille('fournisseurs', false, 'aaaaaaaa-0000-0000-0000-000000000011'); END $$;
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000011', true);
DO $$
DECLARE r record;
BEGIN
    SELECT * INTO r FROM cloud_descendants(41);
    ASSERT r.version = 42, 'version suivante au-dessus : ' || r.version;
END $$;
RESET ROLE;

-- ---- 3. Le positionnement de l'officine est nécessaire à cloud_chargement_en_cours (sécurité par ligne forcée) ----
-- ---- 2. Plafonds de chargement sur 24 h ----
UPDATE cloud_parametre SET chargements_max_24h = 2, quota_chargements_octets_24h = 150, quota_chargement_octets = 1000 WHERE id = 1;
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000011', true);
DO $$
DECLARE r record; v_lot bytea := convert_to(rpad('{"lignes":[]}', 80), 'UTF8');
BEGIN
    PERFORM cloud_debut_chargement(1, 1, 100, 0, 'demande', ARRAY['vente']);
    ASSERT cloud_chargement_en_cours(repeat('7', 64)), 'chargement en cours vu';
    -- Preuve que le positionnement est nécessaire : sans lui, la sécurité par ligne cache le chargement
    PERFORM set_config('app.officine_id', '', true);
    ASSERT (SELECT count(*) FROM chargement) = 0, 'sécurité par ligne : rien sans officine posée';
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000011', true);
    -- Octets cumulés : 80 accepté, puis 160 > 150 sur 24 h refusé (plafond du chargement 1000 non atteint)
    SELECT * INTO r FROM cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1', v_lot);
    ASSERT r.statut = 'accepte', r.statut;
    SELECT * INTO r FROM cloud_appliquer_lot(1, 2, repeat('b', 64), 'sync-v1', v_lot);
    ASSERT r.statut = 'quota_depasse', 'plafond 24 h : ' || r.statut;
    -- Un chargement remplacé compte toujours dans les 24 h
    PERFORM cloud_debut_chargement(2, 1, 200, 1, 'demande', ARRAY['vente']);
    ASSERT (SELECT etat FROM chargement WHERE numero = 1) = 'remplace', 'remplacé';
    SELECT * INTO r FROM cloud_appliquer_lot(1, 2, repeat('b', 64), 'sync-v1', v_lot);
    ASSERT r.statut = 'quota_depasse', 'remplacé compris : ' || r.statut;
    -- Répétition du même n° : sans effet ; 3e début en 24 h : refusé
    PERFORM cloud_debut_chargement(2, 1, 200, 1, 'demande', ARRAY['vente']);
    BEGIN
        PERFORM cloud_debut_chargement(3, 1, 300, 1, 'demande', ARRAY['vente']);
        RAISE EXCEPTION 'troisième début accepté';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE 'quota_depasse%', SQLERRM;
    END;
    ASSERT (SELECT count(*) FROM chargement) = 2, 'aucun chargement de plus';
END $$;
-- Une autre officine n'est pas touchée par ces plafonds
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000012', true);
DO $$ BEGIN PERFORM cloud_debut_chargement(1, 1, 100, 0, 'demande', ARRAY['vente']); END $$;
RESET ROLE;

\echo 'Tests du script central 009 : tous réussis (transaction annulée).'
ROLLBACK;
