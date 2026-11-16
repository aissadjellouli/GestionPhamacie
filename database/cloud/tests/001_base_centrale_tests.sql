-- =====================================================================
-- Tests de la base centrale (scripts 001 et 002), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/001_base_centrale_tests.sql
-- Toute assertion fausse arrête le script ; rien n'est gardé.
-- =====================================================================
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 'Essai A'), ('bbbbbbbb-0000-0000-0000-000000000002', 'Essai B');

SET LOCAL ROLE pharma_cloud_api;

-- Officine A dépose un lot : une vente créée puis modifiée (état final = plus grand n°), un patient sous code
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000001', true);
DO $$
DECLARE r record;
BEGIN
    SELECT * INTO r FROM cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1', convert_to(
        '{"lignes":[{"n":1,"table":"vente","id":"10","op":"creation","donnees":{"id":10,"numero":"T-000010","montant_total":120.50,"patient_id":"P-0123456789abcdef01234567"}},'
        '{"n":2,"table":"vente","id":"10","op":"modification","donnees":{"id":10,"numero":"T-000010","montant_total":99.00,"patient_id":"P-0123456789abcdef01234567"}},'
        '{"n":3,"table":"patient","id":"P-0123456789abcdef01234567","op":"creation","donnees":{"id":"P-0123456789abcdef01234567","solde_credit":0}}]}', 'UTF8'));
    ASSERT r.statut = 'accepte' AND r.dernier = 1, 'lot 1 accepté';
    ASSERT (SELECT montant_total FROM vente WHERE id = 10) = 99.00, 'état final = ligne de plus grand n°';
    ASSERT (SELECT officine_id FROM vente WHERE id = 10) = 'aaaaaaaa-0000-0000-0000-000000000001', 'officine posée par le serveur, pas par le lot';
    -- Idempotence : même n°, même empreinte
    SELECT * INTO r FROM cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1', convert_to('{"lignes":[]}', 'UTF8'));
    ASSERT r.statut = 'deja_recu', 'lot déjà reçu accusé sans réapplication';
    -- Même n°, autre empreinte : anomalie
    SELECT * INTO r FROM cloud_appliquer_lot(1, 1, repeat('b', 64), 'sync-v1', convert_to('{"lignes":[]}', 'UTF8'));
    ASSERT r.statut = 'empreinte_differente', 'même n° autre contenu refusé';
    -- Trou : n° 3 alors que le dernier est 1
    SELECT * INTO r FROM cloud_appliquer_lot(1, 3, repeat('c', 64), 'sync-v1', convert_to('{"lignes":[]}', 'UTF8'));
    ASSERT r.statut = 'trou' AND r.dernier = 1, 'trou refusé';
    -- Suppression
    SELECT * INTO r FROM cloud_appliquer_lot(1, 2, repeat('d', 64), 'sync-v1', convert_to(
        '{"lignes":[{"n":4,"table":"patient","id":"P-0123456789abcdef01234567","op":"suppression","donnees":null}]}', 'UTF8'));
    ASSERT r.statut = 'accepte' AND (SELECT count(*) FROM patient) = 0, 'suppression appliquée';
    ASSERT (SELECT count(*) FROM lot_recu) = 2, 'lots bruts gardés';
    -- Table inconnue refusée
    BEGIN
        PERFORM cloud_appliquer_lot(1, 3, repeat('e', 64), 'sync-v1', convert_to('{"lignes":[{"n":5,"table":"sync_pseudonyme","id":"x","op":"creation","donnees":{}}]}', 'UTF8'));
        RAISE EXCEPTION 'table non reçue acceptée';
    EXCEPTION WHEN raise_exception THEN
        ASSERT SQLERRM LIKE 'Table non reçue%', 'refus attendu : ' || SQLERRM;
    END;
END $$;

-- Officine B : ne voit ni ne modifie les lignes de A, même sans filtre
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000002', true);
DO $$
DECLARE n int;
BEGIN
    ASSERT (SELECT count(*) FROM vente) = 0, 'B ne lit pas les ventes de A';
    ASSERT (SELECT count(*) FROM lot_recu) = 0, 'B ne lit pas les lots de A';
    ASSERT (SELECT count(*) FROM officine_epoque) = 0, 'B ne lit pas les époques de A';
    UPDATE vente SET montant_total = 0; GET DIAGNOSTICS n = ROW_COUNT;
    ASSERT n = 0, 'B ne modifie pas les ventes de A';
    DELETE FROM vente; GET DIAGNOSTICS n = ROW_COUNT;
    ASSERT n = 0, 'B ne supprime pas les ventes de A';
    BEGIN
        INSERT INTO vente (officine_id, id, sync_epoque, sync_numero) VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 11, 1, 1);
        RAISE EXCEPTION 'B a écrit une ligne au nom de A';
    EXCEPTION WHEN insufficient_privilege THEN NULL; -- refus RLS (WITH CHECK)
    END;
    -- Le même n° de lot pour B commence à 1 (époques séparées)
    ASSERT (SELECT statut FROM cloud_appliquer_lot(1, 1, repeat('f', 64), 'sync-v1', convert_to('{"lignes":[]}', 'UTF8'))) = 'accepte', 'B a sa propre suite';
END $$;

-- Sans officine posée : rien de visible
SELECT set_config('app.officine_id', '', true);
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM vente) = 0 AND (SELECT count(*) FROM lot_recu) = 0, 'sans officine : aucune ligne';
END $$;

-- Le rôle applicatif n'a aucun accès direct à l'administration
DO $$ BEGIN
    BEGIN
        PERFORM 1 FROM certificat;
        RAISE EXCEPTION 'certificat lisible par le rôle applicatif';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;
    BEGIN
        PERFORM 1 FROM code_activation;
        RAISE EXCEPTION 'code_activation lisible par le rôle applicatif';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;
END $$;

RESET ROLE;

-- Propriétaire : RLS forcée aussi (sans officine posée, aucune ligne reçue visible)
SELECT set_config('app.officine_id', '', true);
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM vente) = 0, 'RLS forcée pour le propriétaire';
END $$;

-- Activation : code à usage unique, expiration, adoption du code de l'officine installée
INSERT INTO code_activation (empreinte_code, officine_id, serveur, expire_le) VALUES
    (repeat('1', 64), 'aaaaaaaa-0000-0000-0000-000000000001', 'principal', now() + interval '1 day'),
    (repeat('2', 64), 'aaaaaaaa-0000-0000-0000-000000000001', 'second', now() - interval '1 minute');
DO $$ BEGIN
    ASSERT cloud_activer(repeat('1', 64), 'cccccccc-0000-0000-0000-000000000003', repeat('9', 64), '01', now() + interval '3 years'), 'activation';
    ASSERT EXISTS (SELECT 1 FROM officine WHERE id = 'cccccccc-0000-0000-0000-000000000003'), 'officine reprend le code de l''officine installée';
    ASSERT (SELECT officine_id FROM certificat WHERE empreinte = repeat('9', 64)) = 'cccccccc-0000-0000-0000-000000000003', 'certificat lié';
    ASSERT NOT cloud_activer(repeat('1', 64), 'cccccccc-0000-0000-0000-000000000003', repeat('8', 64), '02', now() + interval '3 years'), 'code à usage unique';
    ASSERT NOT cloud_activer(repeat('2', 64), 'cccccccc-0000-0000-0000-000000000003', repeat('7', 64), '03', now() + interval '3 years'), 'code expiré';
    ASSERT (SELECT revoque FROM cloud_certificat(repeat('9', 64))) = false, 'certificat valable';
    UPDATE certificat SET revoque_le = now() WHERE empreinte = repeat('9', 64);
    ASSERT (SELECT revoque FROM cloud_certificat(repeat('9', 64))), 'révocation visible';
    BEGIN
        UPDATE journal_securite SET detail = 'x';
        RAISE EXCEPTION 'journal de sécurité modifiable';
    EXCEPTION WHEN raise_exception THEN
        ASSERT SQLERRM LIKE '%ajout seul%', SQLERRM;
    END;
END $$;

-- Renouvellement : seulement près de l'échéance, une seule fois, successeurs révoqués en cascade ; révocation par serveur
INSERT INTO certificat (empreinte, officine_id, serveur, numero_serie, echeance)
VALUES (repeat('5', 64), 'cccccccc-0000-0000-0000-000000000003', 'second', '05', now() + interval '3 years');
DO $$ BEGIN
    ASSERT NOT cloud_renouveler(repeat('5', 64), repeat('6', 64), '06', now() + interval '3 years'), 'renouvellement refusé loin de l''échéance';
    UPDATE certificat SET echeance = now() + interval '30 days' WHERE empreinte = repeat('5', 64);
    ASSERT cloud_renouveler(repeat('5', 64), repeat('6', 64), '06', now() + interval '3 years'), 'renouvellement près de l''échéance';
    ASSERT (SELECT remplace_empreinte FROM certificat WHERE empreinte = repeat('6', 64)) = repeat('5', 64), 'successeur chaîné';
    ASSERT NOT cloud_renouveler(repeat('5', 64), repeat('4', 64), '04', now() + interval '3 years'), 'second renouvellement depuis l''ancien refusé';
    UPDATE certificat SET revoque_le = now(), motif_revocation = 'essai' WHERE empreinte = repeat('5', 64);
    ASSERT (SELECT revoque_le IS NOT NULL FROM certificat WHERE empreinte = repeat('6', 64)), 'révocation en cascade du successeur';
    INSERT INTO certificat (empreinte, officine_id, serveur, numero_serie, echeance)
    VALUES (repeat('3', 64), 'cccccccc-0000-0000-0000-000000000003', 'second', '03', now() + interval '3 years');
    ASSERT cloud_revoquer_serveur('cccccccc-0000-0000-0000-000000000003', 'second', 'essai') = 1, 'révocation par serveur';
    -- Journal : refus répétés dédoublonnés (une ligne par heure)
    PERFORM cloud_journaliser('cccccccc-0000-0000-0000-000000000003', repeat('3', 64), 'certificat_refuse', 'essai');
    PERFORM cloud_journaliser('cccccccc-0000-0000-0000-000000000003', repeat('3', 64), 'certificat_refuse', 'essai');
    ASSERT (SELECT count(*) FROM journal_securite WHERE evenement = 'certificat_refuse' AND certificat_empreinte = repeat('3', 64)) = 1, 'journal dédoublonné';
END $$;

-- Clé composée : la suppression ne retire que la ligne dont l'id reconstitué est identique
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000001', true);
DO $$ BEGIN
    ASSERT (SELECT statut FROM cloud_appliquer_lot(1, 3, repeat('1', 64), 'sync-v1', convert_to(
        '{"lignes":[{"n":10,"table":"catalogue_grossiste","id":"5|7","op":"creation","donnees":{"fournisseur_id":5,"produit_id":7,"prix_ht":1}},'
        '{"n":11,"table":"catalogue_grossiste","id":"5|8","op":"creation","donnees":{"fournisseur_id":5,"produit_id":8,"prix_ht":2}}]}', 'UTF8'))) = 'accepte', 'lot 3';
    ASSERT (SELECT statut FROM cloud_appliquer_lot(1, 4, repeat('2', 64), 'sync-v1', convert_to(
        '{"lignes":[{"n":12,"table":"catalogue_grossiste","id":"5|7","op":"suppression","donnees":null}]}', 'UTF8'))) = 'accepte', 'lot 4';
    ASSERT (SELECT array_agg(produit_id) FROM catalogue_grossiste) = ARRAY[8::bigint], 'seule la ligne 5|7 est retirée';
END $$;
RESET ROLE;

-- Quota d'octets par officine et par jour
UPDATE cloud_parametre SET quota_octets_jour = 10;
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000001', true);
DO $$ BEGIN
    ASSERT (SELECT statut FROM cloud_appliquer_lot(1, 5, repeat('e', 64), 'sync-v1', convert_to('{"lignes":[]}', 'UTF8'))) = 'quota_depasse', 'quota';
    ASSERT (SELECT dernier_lot FROM officine_epoque WHERE epoque = 1) = 4, 'rien appliqué au-delà du quota';
END $$;
RESET ROLE;

-- Aucune colonne de nom de patient dans la base centrale
DO $$ BEGIN
    ASSERT NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'patient'
                       AND column_name IN ('nom', 'prenom', 'telephone', 'num_assure', 'date_naissance', 'email')), 'aucun nom de patient';
    ASSERT NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public'
                       AND column_name IN ('porteur_nom', 'porteur_piece', 'porteur_adresse')), 'aucun nom de porteur';
END $$;

\echo 'Tests de la base centrale : tous réussis (transaction annulée).'
ROLLBACK;
