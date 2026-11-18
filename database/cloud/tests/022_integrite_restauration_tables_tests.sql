-- =====================================================================
-- Tests du script 022 (lot S6.5.1 : déclaration d'époque, lignes divergentes, natures, effacement), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/022_integrite_restauration_tables_tests.sql
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000022', 'Essai 022 A'), ('bbbbbbbb-0000-0000-0000-000000000022', 'Essai 022 B');
SET LOCAL ROLE pharma_cloud_api;

-- Officine A, époque 1 : trois lignes du journal des ventes (rangs 1 à 3)
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000022', true) \g /dev/null
SELECT cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1', convert_to(
    '{"lignes":['
    '{"n":1,"table":"journal_vente","id":"1","op":"creation","donnees":{"id":1,"evenement":"ouverture","survenu_le":"2026-11-01T08:00:00+00:00","enregistre_le":"2026-11-01T08:00:00+00:00","montant_ttc":0.00,"cle_chaine":"officine","rang":1}},'
    '{"n":2,"table":"journal_vente","id":"2","op":"creation","donnees":{"id":2,"evenement":"vente","numero_ticket":"T-000001","survenu_le":"2026-11-01T09:00:00+00:00","enregistre_le":"2026-11-01T09:00:00+00:00","montant_ttc":10.00,"cle_chaine":"officine","rang":2}},'
    '{"n":3,"table":"journal_vente","id":"3","op":"creation","donnees":{"id":3,"evenement":"vente","numero_ticket":"T-000002","survenu_le":"2026-11-01T10:00:00+00:00","enregistre_le":"2026-11-01T10:00:00+00:00","montant_ttc":20.00,"cle_chaine":"officine","rang":3}}'
    ']}', 'UTF8')) \g /dev/null

-- 1. Sans déclaration : ligne 3 renvoyée en époque 2 avec un autre contenu → refusée, anomalie modification_refusee (comme avant)
SELECT cloud_appliquer_lot(2, 1, repeat('b', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"journal_vente","id":"3","op":"modification","donnees":{"id":3,"evenement":"vente","numero_ticket":"T-000002","survenu_le":"2026-11-02T10:00:00+00:00","enregistre_le":"2026-11-02T10:00:00+00:00","montant_ttc":99.00,"cle_chaine":"officine","rang":3}}]}', 'UTF8')) \g /dev/null
RESET ROLE;
DO $$ BEGIN
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000022', true);
    ASSERT (SELECT montant_ttc FROM journal_vente WHERE id = 3) = 20.00, 'sans déclaration : original gardé';
    ASSERT (SELECT count(*) FROM cloud_anomalie_integrite WHERE nature = 'modification_refusee') = 1, 'sans déclaration : anomalie notée';
    ASSERT (SELECT count(*) FROM cloud_ligne_divergente) = 0, 'sans déclaration : rien dans cloud_ligne_divergente';
END $$;

-- 2. Déclaration : contrôles, puis réception (après un lot de l'époque 2 : avant_premier_lot faux), renvoi identique, renvoi différent
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000022', true) \g /dev/null
DO $$ BEGIN
    BEGIN PERFORM cloud_declarer_changement_epoque(2, 2, 1, 3, now(), now(), 1, '[]'); RAISE EXCEPTION 'même époque acceptée';
    EXCEPTION WHEN invalid_parameter_value THEN NULL; END;
    BEGIN PERFORM cloud_declarer_changement_epoque(1, 3, 1, 3, now(), now(), 1, '[{"journal":"inconnu","cle_chaine":"officine","rang":2}]'); RAISE EXCEPTION 'journal inconnu accepté';
    EXCEPTION WHEN invalid_parameter_value THEN NULL; END;
    BEGIN PERFORM cloud_declarer_changement_epoque(1, 3, 1, 3, now(), now(), 1, '[{"journal":"journal_vente","cle_chaine":"officine","rang":2,"hash":"zz"}]'); RAISE EXCEPTION 'empreinte invalide acceptée';
    EXCEPTION WHEN invalid_parameter_value THEN NULL; END;
    BEGIN PERFORM cloud_declarer_changement_epoque(1, 3, 1, 3, now(), NULL, 1, '[]'); RAISE EXCEPTION 'sans date de décision';
    EXCEPTION WHEN invalid_parameter_value THEN NULL; END;
END $$;
DO $$
DECLARE r record; v_fins jsonb := '[{"journal":"journal_vente","cle_chaine":"officine","rang":2,"id":"2","hash":"' || repeat('ab', 32) || '"}]';
BEGIN
    -- Époque 2 déjà ouverte par le lot du point 1 : la déclaration arrive après un lot de la nouvelle époque
    SELECT * INTO r FROM cloud_declarer_changement_epoque(1, 2, 2, 3, now() - interval '5 minutes', now(), 7, v_fins);
    ASSERT NOT r.deja_declare AND NOT r.avant_premier_lot, 'époque 2 : déclarée après un lot (avant_premier_lot faux)';
    BEGIN
        PERFORM cloud_declarer_changement_epoque(1, 2, 2, 3, NULL, now(), 7, v_fins);
        RAISE EXCEPTION 'un renvoi différent devait être refusé';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
END $$;
-- Déclaration propre de l'époque 3, AVANT tout lot de l'époque 3
DO $$
DECLARE r record; r2 record; v_d timestamptz := '2026-11-18T10:00:00Z'; v_fins jsonb := '[{"journal":"journal_vente","cle_chaine":"officine","rang":2}]';
BEGIN
    SELECT * INTO r FROM cloud_declarer_changement_epoque(2, 3, 2, 3, v_d, v_d, 7, v_fins);
    ASSERT NOT r.deja_declare AND r.avant_premier_lot, 'époque 3 : déclarée avant le premier lot';
    SELECT * INTO r2 FROM cloud_declarer_changement_epoque(2, 3, 2, 3, v_d, v_d, 7, v_fins);
    ASSERT r2.deja_declare AND r2.changement_id = r.changement_id, 'renvoi identique : même déclaration, rien d''ajouté';
END $$;

-- 3. Époque 3 déclarée : ligne 3 renvoyée avec un autre contenu → original en place, version gardée dans cloud_ligne_divergente, sans anomalie
SELECT cloud_appliquer_lot(3, 1, repeat('c', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"journal_vente","id":"3","op":"instantane","donnees":{"id":3,"evenement":"vente","numero_ticket":"T-000002","survenu_le":"2026-11-03T10:00:00+00:00","enregistre_le":"2026-11-03T10:00:00+00:00","montant_ttc":55.00,"cle_chaine":"officine","rang":3}}]}', 'UTF8')) \g /dev/null
-- Même lot rejoué (n° suivant, même contenu) : gardé une seule fois
SELECT cloud_appliquer_lot(3, 2, repeat('d', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":2,"table":"journal_vente","id":"3","op":"instantane","donnees":{"id":3,"evenement":"vente","numero_ticket":"T-000002","survenu_le":"2026-11-03T10:00:00+00:00","enregistre_le":"2026-11-03T10:00:00+00:00","montant_ttc":55.00,"cle_chaine":"officine","rang":3}}]}', 'UTF8')) \g /dev/null
RESET ROLE;
DO $$ BEGIN
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000022', true);
    ASSERT (SELECT montant_ttc FROM journal_vente WHERE id = 3) = 20.00, 'déclaré : original toujours en place';
    ASSERT (SELECT count(*) FROM cloud_ligne_divergente) = 1, 'déclaré : une version divergente gardée (renvoi identique non doublé) : '
        || (SELECT count(*) FROM cloud_ligne_divergente);
    ASSERT (SELECT (ligne ->> 'montant_ttc')::numeric FROM cloud_ligne_divergente) = 55.00, 'version reçue gardée telle quelle';
    ASSERT (SELECT rang FROM cloud_ligne_divergente) = 3 AND (SELECT cle_chaine FROM cloud_ligne_divergente) = 'officine', 'rang et chaîne notés';
    ASSERT (SELECT epoque_existante FROM cloud_ligne_divergente) = 1 AND (SELECT epoque FROM cloud_ligne_divergente) = 3, 'époques notées';
    ASSERT (SELECT count(*) FROM cloud_anomalie_integrite WHERE nature = 'modification_refusee') = 1, 'déclaré : aucune nouvelle anomalie';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'changement_epoque_declare'), 'déclaration journalisée';
END $$;

-- 4. Natures nouvelles acceptées ; nature inconnue refusée
DO $$ BEGIN
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000022', true);
    ASSERT cloud_integrite_noter('journal_vente', 'officine', 3, NULL, 'remplacee_apres_restauration', 'essai'), 'nature remplacee_apres_restauration';
    ASSERT cloud_integrite_noter('journal_vente', 'officine', 3, NULL, 'sceau_perdu_restauration', 'essai'), 'nature sceau_perdu_restauration';
    ASSERT cloud_integrite_noter('journal_vente', 'numerotation', NULL, 'T-000002', 'ticket_reattribue_restauration', 'essai'), 'nature ticket_reattribue_restauration';
    BEGIN PERFORM cloud_integrite_noter('journal_vente', 'officine', 3, NULL, 'nature_inventee', 'essai'); RAISE EXCEPTION 'nature inconnue acceptée';
    EXCEPTION WHEN check_violation THEN NULL; END;
END $$;

-- 5. Ajout seul et droits : l'API ne lit ni n'écrit directement les quatre tables ; le propriétaire ne modifie ni ne supprime
SET LOCAL ROLE pharma_cloud_api;
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['cloud_epoque_changement', 'cloud_ligne_divergente', 'cloud_integrite_bifurcation', 'cloud_anomalie_classement'] LOOP
        BEGIN EXECUTE format('SELECT count(*) FROM %I', t); RAISE EXCEPTION 'lecture permise sur %', t; EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    END LOOP;
END $$;
RESET ROLE;
DO $$ BEGIN
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000022', true);
    BEGIN UPDATE cloud_ligne_divergente SET epoque = 9; RAISE EXCEPTION 'modification permise'; EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE '%ajout seul%' THEN RAISE; END IF; END;
    BEGIN DELETE FROM cloud_epoque_changement; RAISE EXCEPTION 'suppression permise'; EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE '%ajout seul%' THEN RAISE; END IF; END;
END $$;

-- 6. Cloisonnement : l'officine B ne voit ni la déclaration ni la ligne divergente de A
DO $$ BEGIN
    PERFORM set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000022', true);
    ASSERT (SELECT count(*) FROM cloud_epoque_changement) = 0, 'B ne voit pas les déclarations de A';
    ASSERT (SELECT count(*) FROM cloud_ligne_divergente) = 0, 'B ne voit pas les lignes divergentes de A';
    ASSERT cloud_changement_epoque_pour('bbbbbbbb-0000-0000-0000-000000000022', 3) IS NULL, 'aucune déclaration pour B';
END $$;

-- 7. Effacement d'une officine partie : les nouvelles tables sont comptées puis effacées
DO $$
DECLARE n bigint;
BEGIN
    UPDATE officine SET resiliee_le = now() - interval '6 years 1 day' WHERE id = 'aaaaaaaa-0000-0000-0000-000000000022';
    SELECT lignes INTO n FROM cloud_effacer_officine('aaaaaaaa-0000-0000-0000-000000000022') WHERE nom_table = 'cloud_ligne_divergente';
    ASSERT n = 1, 'simulation : 1 ligne divergente : ' || coalesce(n::text, 'aucune');
    SELECT lignes INTO n FROM cloud_effacer_officine('aaaaaaaa-0000-0000-0000-000000000022') WHERE nom_table = 'cloud_epoque_changement';
    ASSERT n = 2, 'simulation : 2 déclarations : ' || coalesce(n::text, 'aucune');
    PERFORM cloud_effacer_officine('aaaaaaaa-0000-0000-0000-000000000022', true, 'essai 022');
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000022', true);
    ASSERT (SELECT count(*) FROM cloud_ligne_divergente) = 0 AND (SELECT count(*) FROM cloud_epoque_changement) = 0, 'effacement confirmé : tables vidées';
END $$;

ROLLBACK;
\echo '022 : tous réussis'
