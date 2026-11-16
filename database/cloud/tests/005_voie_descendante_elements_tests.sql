-- =====================================================================
-- Tests de la voie descendante (script 005), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/005_voie_descendante_elements_tests.sql
-- =====================================================================
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000007', 'Essai G'), ('bbbbbbbb-0000-0000-0000-000000000008', 'Essai H');

SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000007', true);
DO $$
DECLARE r record; n int;
BEGIN
    -- Premier échange : le réglage par défaut descend en version 1
    SELECT * INTO r FROM cloud_descendants(0);
    ASSERT r.nom_table = 'reglage_central' AND r.cle = 'familles_envoyees' AND r.version = 1 AND r.version_max = 1, 'version 1';
    ASSERT r.donnees = '{"familles":["courriel_commandes","fournisseurs","pharmacie"]}', r.donnees::text;
    -- Acquitté : rien de nouveau, pas de nouvelle version si rien ne change
    SELECT count(*) INTO n FROM cloud_descendants(1);
    ASSERT n = 0, 'rien après acquittement';
    ASSERT (SELECT version_acquittee FROM officine_descendant) = 1, 'acquittement gardé';
    -- Version inconnue (plus grande) : pas d'acquittement
    PERFORM cloud_descendants(2000000000000); -- au-delà du plafond (script 009) : ignorée
    ASSERT (SELECT version_acquittee FROM officine_descendant) = 1, 'acquittement borné';
    -- Le rôle applicatif n'écrit pas directement
    BEGIN
        INSERT INTO descendant (officine_id, nom_table, cle, version, donnees) VALUES ('aaaaaaaa-0000-0000-0000-000000000007', 'x', 'y', 5, '{}');
        RAISE EXCEPTION 'écriture directe';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;
END $$;
RESET ROLE;

-- Réglage modifié par l'administration : nouvelle version au prochain échange
DO $$ BEGIN PERFORM cloud_regler_famille('fournisseurs', false, 'aaaaaaaa-0000-0000-0000-000000000007'); END $$;
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000007', true);
DO $$
DECLARE r record;
BEGIN
    SELECT * INTO r FROM cloud_descendants(1);
    ASSERT r.version = 2 AND r.donnees = '{"familles":["courriel_commandes","pharmacie"]}', 'version 2 : ' || r.donnees::text;
    SELECT * INTO r FROM cloud_descendants(0);
    ASSERT r.version = 2, 'une seule ligne par clé, dernière version';
END $$;
-- Cloisonnement : H ne voit pas les éléments de G, et a sa propre numérotation
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000008', true);
DO $$
DECLARE r record;
BEGIN
    ASSERT (SELECT count(*) FROM descendant WHERE officine_id = 'aaaaaaaa-0000-0000-0000-000000000007') = 0, 'fuite entre officines';
    SELECT * INTO r FROM cloud_descendants(0);
    ASSERT r.version = 1 AND r.donnees = '{"familles":["courriel_commandes","fournisseurs","pharmacie"]}', 'H : version 1';
END $$;
SELECT set_config('app.officine_id', '', true);
DO $$ BEGIN
    BEGIN
        PERFORM cloud_descendants(0);
        RAISE EXCEPTION 'sans officine posée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE 'Officine courante non posée%', SQLERRM;
    END;
END $$;
RESET ROLE;

\echo 'Tests du script 005 : tous réussis (transaction annulée).'
ROLLBACK;
