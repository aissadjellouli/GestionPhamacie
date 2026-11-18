-- =====================================================================
-- Tests de l'ordre de commande en brouillon (script 016), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/016_commande_brouillon_vers_officines_tests.sql
-- =====================================================================
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000016', 'Essai S5.4');
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000016', true) \g /dev/null
SELECT cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"fournisseur","id":"4","op":"creation","donnees":{"id":4,"raison_sociale":"Grossiste Essai","actif":true}},'
    '{"n":2,"table":"fournisseur","id":"5","op":"creation","donnees":{"id":5,"raison_sociale":"Ancien grossiste","actif":false}},'
    '{"n":3,"table":"produit","id":"2","op":"creation","donnees":{"id":2,"nom_commercial":"Amoxicilline 500","prix_public":350.00}},'
    '{"n":4,"table":"produit","id":"3","op":"creation","donnees":{"id":3,"nom_commercial":"Crème","prix_public":1000.00}}]}', 'UTF8')) \g /dev/null
RESET ROLE;

DO $$
DECLARE a uuid := 'aaaaaaaa-0000-0000-0000-000000000016'; v uuid; o ordre_central;
BEGIN
    -- 1. Commande valide : noms ajoutés, ordre des lignes gardé, prix facultatif
    v := cloud_creer_ordre(a, 'commande', '4', '{"lignes":[{"produit":3,"quantite":2,"prix_achat_ht":700.5},{"produit":2,"quantite":10,"prix_achat_ht":null}]}', 'admin.essai');
    PERFORM set_config('app.officine_id', a::text, true);
    SELECT * INTO o FROM ordre_central WHERE id = v;
    ASSERT o.cible = '4' AND o.attendu = '{}'::jsonb, 'cible et attendu';
    ASSERT o.donnees = '{"grossiste":"Grossiste Essai","lignes":[{"produit":3,"nom":"Crème","quantite":2,"prix_achat_ht":700.5},{"produit":2,"nom":"Amoxicilline 500","quantite":10}]}'::jsonb,
        'données : ' || o.donnees::text;
    ASSERT (SELECT donnees ->> 'type' FROM descendant WHERE cle = v::text) = 'commande', 'publié';

    -- 2. Refus
    BEGIN PERFORM cloud_creer_ordre(a, 'commande', NULL, '{"lignes":[{"produit":2,"quantite":1}]}', 'x'); RAISE EXCEPTION 'sans grossiste';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%N° du grossiste%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'commande', '99', '{"lignes":[{"produit":2,"quantite":1}]}', 'x'); RAISE EXCEPTION 'grossiste inconnu';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%Grossiste n° 99 inconnu%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'commande', '5', '{"lignes":[{"produit":2,"quantite":1}]}', 'x'); RAISE EXCEPTION 'grossiste désactivé';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%désactivé%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'commande', '4', '{"lignes":[]}', 'x'); RAISE EXCEPTION 'aucune ligne';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%1 à 200%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'commande', '4', jsonb_build_object('lignes', (SELECT jsonb_agg(jsonb_build_object('produit', g, 'quantite', 1)) FROM generate_series(1, 201) g)), 'x');
          RAISE EXCEPTION '201 lignes';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%1 à 200%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'commande', '4', '{"lignes":[{"produit":2,"quantite":0}]}', 'x'); RAISE EXCEPTION 'quantité nulle';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%Ligne invalide%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'commande', '4', '{"lignes":[{"produit":2,"quantite":1.5}]}', 'x'); RAISE EXCEPTION 'quantité décimale';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%Ligne invalide%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'commande', '4', '{"lignes":[{"produit":2,"quantite":1,"prix_achat_ht":-1}]}', 'x'); RAISE EXCEPTION 'prix négatif';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%Ligne invalide%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'commande', '4', '{"lignes":[{"produit":2,"quantite":1,"prix_achat_ht":1.234}]}', 'x'); RAISE EXCEPTION 'trois décimales';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%Ligne invalide%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'commande', '4', '{"lignes":[{"produit":"2","quantite":1}]}', 'x'); RAISE EXCEPTION 'produit en texte';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%Ligne invalide%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'commande', '4', '{"lignes":[{"produit":2,"quantite":1},{"produit":2,"quantite":3}]}', 'x'); RAISE EXCEPTION 'doublon';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%deux lignes%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'commande', '4', '{"lignes":[{"produit":2,"quantite":1},{"produit":77,"quantite":1},{"produit":78,"quantite":1}]}', 'x');
          RAISE EXCEPTION 'produit inconnu';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%inconnu(s)%77, 78%', SQLERRM; END;

    -- 3. Les autres types restent acceptés
    PERFORM cloud_creer_ordre(a, 'essai', NULL, '{}', 'admin.essai');
END $$;

\echo 'Tests 016 : commandes en brouillon envoyées aux officines réussis'
ROLLBACK;
