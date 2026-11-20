-- =====================================================================
-- Tests du script 028 (effacement d'une famille nominative aussi dans les lignes archivées), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/028_effacer_famille_lignes_archivees_tests.sql
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000028', 'Essai 028 A'), ('bbbbbbbb-0000-0000-0000-000000000028', 'Essai 028 B');

-- A et B : un fournisseur reçu avec ses coordonnées (famille « fournisseurs » envoyée par défaut), puis repris à l'époque 2
-- avec un autre nom : la version de l'époque 1, coordonnées comprises, est archivée.
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000028', true) \g /dev/null
SELECT cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"fournisseur","id":"1","op":"creation","donnees":{"id":1,"raison_sociale":"Grossiste A","telephone":"0550000001","email":"a@exemple.dz","adresse":"1 rue A"}}]}', 'UTF8')) \g /dev/null
SELECT cloud_appliquer_lot(2, 1, repeat('b', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"fournisseur","id":"1","op":"creation","donnees":{"id":1,"raison_sociale":"Grossiste A bis","telephone":"0550000002","email":"a2@exemple.dz","adresse":"2 rue A"}}]}', 'UTF8')) \g /dev/null
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000028', true) \g /dev/null
SELECT cloud_appliquer_lot(1, 1, repeat('c', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"fournisseur","id":"1","op":"creation","donnees":{"id":1,"raison_sociale":"Grossiste B","telephone":"0660000001","email":"b@exemple.dz","adresse":"1 rue B"}}]}', 'UTF8')) \g /dev/null
SELECT cloud_appliquer_lot(2, 1, repeat('d', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"fournisseur","id":"1","op":"creation","donnees":{"id":1,"raison_sociale":"Grossiste B bis","telephone":"0660000002","email":"b2@exemple.dz","adresse":"2 rue B"}}]}', 'UTF8')) \g /dev/null
RESET ROLE;

SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000028', true) \g /dev/null
DO $$
DECLARE l ligne_remplacee;
BEGIN
    SELECT * INTO l FROM ligne_remplacee WHERE nom_table = 'fournisseur';
    ASSERT l.ancienne ->> 'telephone' = '0550000001' AND l.ancienne ->> 'email' = 'a@exemple.dz' AND l.ancienne ->> 'adresse' = '1 rue A',
        'archive de A avec les coordonnées : ' || l.ancienne::text;
    ASSERT l.expurgee_le IS NULL AND l.colonnes_effacees IS NULL, 'archive non expurgée';
END $$;

-- 1. Famille encore envoyée : refus
DO $$ BEGIN
    BEGIN PERFORM * FROM cloud_effacer_famille('fournisseurs', 'aaaaaaaa-0000-0000-0000-000000000028', true); RAISE EXCEPTION 'famille encore envoyée acceptée';
    EXCEPTION WHEN raise_exception THEN IF SQLERRM NOT LIKE '%encore envoyée%' THEN RAISE; END IF; END;
END $$;

-- 2. Famille décochée pour A seulement : simulation (compte, rien ne change)
SELECT cloud_regler_famille('fournisseurs', false, 'aaaaaaaa-0000-0000-0000-000000000028') \g /dev/null
DO $$
DECLARE r record; n_mirroir bigint := 0; n_arch bigint := 0; n_cols int := 0;
BEGIN
    FOR r IN SELECT * FROM cloud_effacer_famille('fournisseurs', 'aaaaaaaa-0000-0000-0000-000000000028') LOOP
        IF r.nom_table = 'ligne_remplacee' THEN
            n_arch := n_arch + r.lignes; n_cols := n_cols + 1;
            ASSERT r.colonne IN ('fournisseur.adresse', 'fournisseur.email', 'fournisseur.telephone') AND r.lignes = 1, 'ligne archivée : ' || r.colonne || ' ' || r.lignes;
        ELSIF r.nom_table = 'fournisseur' THEN
            n_mirroir := n_mirroir + r.lignes;
        END IF;
    END LOOP;
    ASSERT n_arch = 3 AND n_cols = 3, 'simulation : 3 valeurs archivées (téléphone, courriel, adresse) : ' || n_arch;
    ASSERT n_mirroir = 3, 'simulation : 3 valeurs du miroir : ' || n_mirroir;
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000028', true);
    ASSERT (SELECT ancienne ->> 'telephone' FROM ligne_remplacee WHERE nom_table = 'fournisseur') = '0550000001', 'simulation sans effet sur l''archive';
    ASSERT (SELECT telephone FROM fournisseur WHERE id = 1) = '0550000002', 'simulation sans effet sur le miroir';
END $$;

-- 3. Confirmation : coordonnées retirées de la copie archivée, le reste gardé, ligne archivée conservée
DO $$
DECLARE r record; n_arch bigint := 0; l ligne_remplacee;
BEGIN
    FOR r IN SELECT * FROM cloud_effacer_famille('fournisseurs', 'aaaaaaaa-0000-0000-0000-000000000028', true) WHERE nom_table = 'ligne_remplacee' LOOP
        n_arch := n_arch + r.lignes;
    END LOOP;
    ASSERT n_arch = 3, 'confirmation : 3 valeurs archivées effacées : ' || n_arch;
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000028', true);
    SELECT * INTO l FROM ligne_remplacee WHERE nom_table = 'fournisseur';
    ASSERT l.id IS NOT NULL, 'ligne archivée conservée';
    ASSERT NOT (l.ancienne ? 'telephone') AND NOT (l.ancienne ? 'email') AND NOT (l.ancienne ? 'adresse'), 'coordonnées retirées : ' || l.ancienne::text;
    ASSERT l.ancienne ->> 'raison_sociale' = 'Grossiste A' AND l.operation = 'remplacement' AND l.ancienne_epoque = 1 AND l.nouvelle_epoque = 2, 'reste de la copie gardé';
    ASSERT l.colonnes_effacees = ARRAY['adresse', 'email', 'telephone'] AND l.expurgee_le IS NOT NULL, 'colonnes et date gardées : ' || coalesce(l.colonnes_effacees::text, 'NULL');
    ASSERT (SELECT telephone IS NULL AND email IS NULL AND adresse IS NULL AND raison_sociale = 'Grossiste A bis' FROM fournisseur WHERE id = 1), 'miroir effacé comme avant';
    ASSERT (SELECT length(detail) <= 500 AND detail LIKE '%lignes archivées%' FROM journal_securite
            WHERE evenement = 'famille_effacee' AND officine_id = 'aaaaaaaa-0000-0000-0000-000000000028'), 'journal de sécurité complété';
    ASSERT coalesce(current_setting('cloud.ligne_remplacee_operation', true), '') = '', 'opération retirée';
    -- Rejoué : plus rien dans l'archive à effacer
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_effacer_famille('fournisseurs', 'aaaaaaaa-0000-0000-0000-000000000028') WHERE nom_table = 'ligne_remplacee'), 'rejoué : rien à effacer';
END $$;

-- 4. Officine B intacte (famille encore envoyée, non ciblée)
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000028', true) \g /dev/null
DO $$ BEGIN
    ASSERT (SELECT ancienne ->> 'telephone' FROM ligne_remplacee WHERE nom_table = 'fournisseur') = '0660000001', 'B : archive intacte';
    ASSERT (SELECT colonnes_effacees IS NULL AND expurgee_le IS NULL FROM ligne_remplacee WHERE nom_table = 'fournisseur'), 'B : non expurgée';
    ASSERT (SELECT telephone FROM fournisseur WHERE id = 1) = '0660000002', 'B : miroir intact';
END $$;

-- 5. Toute autre modification de l'archive reste refusée, même pour le propriétaire et même avec l'opération posée à la main
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000028', true) \g /dev/null
DO $$ BEGIN
    BEGIN UPDATE ligne_remplacee SET operation = 'suppression'; RAISE EXCEPTION 'modification permise'; EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE '%ajout seul%' THEN RAISE; END IF; END;
    BEGIN UPDATE ligne_remplacee SET ancienne = '{}'; RAISE EXCEPTION 'contenu remplacé'; EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE '%ajout seul%' THEN RAISE; END IF; END;
    BEGIN DELETE FROM ligne_remplacee; RAISE EXCEPTION 'suppression permise'; EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE '%ajout seul%' THEN RAISE; END IF; END;
    PERFORM set_config('cloud.ligne_remplacee_operation', 'expurger', true);
    -- colonne qui n'appartient à aucune famille
    BEGIN UPDATE ligne_remplacee SET ancienne = ancienne - 'raison_sociale', colonnes_effacees = ARRAY['raison_sociale'], expurgee_le = now();
          RAISE EXCEPTION 'colonne hors famille retirée'; EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE '%ajout seul%' THEN RAISE; END IF; END;
    -- autre colonne modifiée en même temps
    BEGIN UPDATE ligne_remplacee SET ancienne = ancienne - 'telephone', colonnes_effacees = ARRAY['telephone'], expurgee_le = now(), operation = 'suppression';
          RAISE EXCEPTION 'autre colonne modifiée'; EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE '%ajout seul%' THEN RAISE; END IF; END;
    -- copie archivée changée au-delà des colonnes déclarées
    BEGIN UPDATE ligne_remplacee SET ancienne = ancienne - 'telephone' - 'email', colonnes_effacees = ARRAY['telephone'], expurgee_le = now();
          RAISE EXCEPTION 'colonne retirée non déclarée'; EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE '%ajout seul%' THEN RAISE; END IF; END;
    -- contenu réécrit
    BEGIN UPDATE ligne_remplacee SET ancienne = jsonb_set(ancienne - 'telephone', '{raison_sociale}', '"x"'), colonnes_effacees = ARRAY['telephone'], expurgee_le = now();
          RAISE EXCEPTION 'contenu réécrit'; EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE '%ajout seul%' THEN RAISE; END IF; END;
    PERFORM set_config('cloud.ligne_remplacee_operation', '', true);
    ASSERT (SELECT ancienne ->> 'telephone' FROM ligne_remplacee WHERE nom_table = 'fournisseur') = '0660000001', 'B : archive toujours intacte';
END $$;
-- L'API ne peut toujours pas modifier ni lire l'archive
SET LOCAL ROLE pharma_cloud_api;
DO $$ BEGIN
    BEGIN UPDATE ligne_remplacee SET colonnes_effacees = ARRAY['telephone']; RAISE EXCEPTION 'modification permise à l''API';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM * FROM cloud_effacer_famille('fournisseurs'); RAISE EXCEPTION 'effacement de famille permis à l''API';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
END $$;
RESET ROLE;

-- 6. Rejeu : 004 et 021 repassés ne ramènent pas les anciennes définitions
\ir ../004_reglage_familles_envoyees.sql
\ir ../021_depart_officine.sql
\ir ../028_effacer_famille_lignes_archivees.sql
DO $$ BEGIN
    ASSERT pg_get_functiondef('cloud_effacer_famille(text, uuid, boolean)'::regprocedure) LIKE '%ligne_remplacee%', 'rejeu de 004 : définition de 028 gardée';
    ASSERT pg_get_functiondef('cloud_ligne_remplacee_garde()'::regprocedure) LIKE '%expurger%', 'rejeu de 021 : garde de 028 gardée';
END $$;

\echo 'Tests du script central 028 (effacement de famille dans les lignes archivées) : tous réussis (transaction annulée).'
ROLLBACK;
