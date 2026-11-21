-- =====================================================================
-- Tests du script 157 (rapprochement d'un volet après un retour : part déjà rendue avant le rattachement, créance sans double
-- déduction dans les deux ordres, journal fiscal de la régularisation, défense et protection), en transaction ANNULÉE, depuis la
-- racine du dépôt, après le script 155 :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/157_rapprochement_volet_apres_retour_tests.sql
-- Le script 157 est rejoué deux fois dans la transaction ; rien n'est gardé (ROLLBACK). Il faut un lot vendable (TVA 0 %).
-- Vente : 5 boîtes à 100 DA, 80 % (part caisse 400, patient 100) ; prix Chifa 90 (part Chifa 72 par boîte).
-- =====================================================================
BEGIN;

\ir ../migration/157_rapprochement_volet_apres_retour.sql
\ir ../migration/157_rapprochement_volet_apres_retour.sql

CREATE TEMP TABLE t157 (cle text PRIMARY KEY, id bigint NOT NULL) ON COMMIT DROP;

-- Deux ventes Chifa identiques : A (retour puis volet puis retour), B (volet puis retour)
DO $$
DECLARE
    v_user bigint := (SELECT min(id) FROM utilisateur);
    v_lot  bigint;
    v_prod bigint;
    v_org  bigint;
    v_id   bigint;
    v_cle  text;
BEGIN
    SELECT l.id, l.produit_id INTO v_lot, v_prod FROM lot l JOIN produit p ON p.id = l.produit_id
    WHERE COALESCE(p.taux_tva, 0) = 0 AND l.date_peremption > current_date + 60 AND NOT l.bloque AND NOT p.bloque_vente AND p.actif
    ORDER BY l.id LIMIT 1;
    IF v_lot IS NULL THEN RAISE EXCEPTION 'Base de test sans lot vendable à TVA 0 %%'; END IF;
    INSERT INTO organisme (code, nom) VALUES ('ZZ157', 'Organisme test 157') RETURNING id INTO v_org;
    INSERT INTO t157 VALUES ('utilisateur', v_user);
    FOREACH v_cle IN ARRAY ARRAY['A', 'B'] LOOP
        INSERT INTO vente (numero, utilisateur_id, type, organisme_id, montant_total, part_organisme, part_patient, statut, canal_organisme)
        VALUES ('ZZ157-' || v_cle, v_user, 'libre', v_org, 500, 400, 100, 'en_cours', 'chifa') RETURNING id INTO v_id;
        INSERT INTO t157 VALUES ('vente_' || v_cle, v_id);
        INSERT INTO ligne_vente (vente_id, produit_id, lot_id, quantite, prix_unitaire, base_remboursement, taux_remboursement, montant_organisme, montant_patient)
        VALUES (v_id, v_prod, v_lot, 5, 100, 100, 80, 400, 100) RETURNING id INTO v_id;
        INSERT INTO t157 VALUES ('l_' || v_cle, v_id);
        UPDATE vente SET statut = 'validee' WHERE id = (SELECT id FROM t157 WHERE cle = 'vente_' || v_cle);
    END LOOP;
END $$;
SET CONSTRAINTS ALL IMMEDIATE;
SET CONSTRAINTS ALL DEFERRED;

CREATE FUNCTION pg_temp.retour157(p_vente text, p_quantite int, p_patient numeric, p_organisme numeric) RETURNS bigint
LANGUAGE plpgsql AS $$
DECLARE
    v_user bigint := (SELECT id FROM t157 WHERE cle = 'utilisateur');
    v_ret  bigint;
BEGIN
    INSERT INTO retour_client (vente_id, motif, utilisateur_id, valide_par, mode, montant_rembourse)
    VALUES ((SELECT id FROM t157 WHERE cle = 'vente_' || p_vente), 'Test 157', v_user, v_user, 'especes', p_patient)
    RETURNING id INTO v_ret;
    INSERT INTO retour_client_ligne (retour_id, ligne_vente_id, quantite, destination, montant, montant_organisme)
    VALUES (v_ret, (SELECT id FROM t157 WHERE cle = 'l_' || p_vente), p_quantite, 'stock', p_patient, p_organisme);
    RETURN v_ret;
END;
$$;

CREATE FUNCTION pg_temp.regul157(p_vente text, p_chifa numeric, p_ecart numeric, p_rendue numeric) RETURNS void
LANGUAGE sql AS $$
    INSERT INTO regularisation_part_organisme (vente_id, organisme_id, part_organisme_vente, part_organisme_chifa, ecart, sort,
        montant_pharmacie, utilisateur_id, part_organisme_rendue_avant)
    SELECT v.id, v.organisme_id, v.part_organisme, p_chifa, p_ecart, 'perte_pharmacie', -p_ecart, (SELECT id FROM t157 WHERE cle = 'utilisateur'), p_rendue
    FROM vente v WHERE v.id = (SELECT id FROM t157 WHERE cle = 'vente_' || p_vente);
$$;

CREATE FUNCTION pg_temp.creance157(p_vente text) RETURNS numeric
LANGUAGE sql AS $$
    SELECT part_organisme_retenue FROM v_vente_part_organisme WHERE vente_id = (SELECT id FROM t157 WHERE cle = 'vente_' || p_vente);
$$;

-- 1. Structure : colonne, défaut 0, contrainte
DO $$
BEGIN
    ASSERT (SELECT column_default FROM information_schema.columns
            WHERE table_name = 'regularisation_part_organisme' AND column_name = 'part_organisme_rendue_avant') = '0', 'défaut 0';
    ASSERT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_regularisation_rendue_avant'), 'contrainte >= 0';
    ASSERT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_regularisation_rendue_avant'), 'déclencheur de défense';
END $$;

-- 2. Vente A : retour de 2 boîtes (160), puis régularisation sur les 3 boîtes gardées (Chifa 216, écart 240 − 216 = 24)
DO $$
DECLARE v_a bigint := (SELECT id FROM t157 WHERE cle = 'vente_A'); j journal_vente;
BEGIN
    PERFORM pg_temp.retour157('A', 2, 40, 160);
    ASSERT pg_temp.creance157('A') = 240, 'après retour : 400 − 160';
    -- Défense : part rendue avant différente des retours de la vente → refus
    BEGIN
        PERFORM pg_temp.regul157('A', 216, 24, 0);
        RAISE EXCEPTION 'part rendue avant fausse acceptée';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE 'Un retour vient d''être enregistré sur cette vente%' THEN RAISE; END IF;
    END;
    PERFORM pg_temp.regul157('A', 216, 24, 160);
    ASSERT pg_temp.creance157('A') = 216, 'retour puis volet : 72 × 3, le retour déduit une seule fois';
    SET CONSTRAINTS ALL IMMEDIATE;
    SET CONSTRAINTS ALL DEFERRED;
    SELECT * INTO j FROM journal_vente WHERE vente_id = v_a AND evenement = 'regularisation';
    ASSERT j.part_organisme = -24 AND j.part_patient = 24, format('journal : écart réel −24 (et non −184), lu %s', j.part_organisme);
    ASSERT (SELECT sum(part_organisme) FROM journal_vente WHERE vente_id = v_a) = 216, 'journal : 400 − 160 − 24 = créance';
    -- Retour d'1 boîte après la régularisation (72), puis des 2 dernières (144) : créance 144 puis 0
    PERFORM pg_temp.retour157('A', 1, 20, 72);
    ASSERT pg_temp.creance157('A') = 144, 'après le deuxième retour : 72 × 2';
    PERFORM pg_temp.retour157('A', 2, 40, 144);
    ASSERT pg_temp.creance157('A') = 0, 'tout rendu : créance nulle';
END $$;

-- 3. Vente B : régularisation d'abord (Chifa 360, écart 40), puis retour de 2 boîtes (144) : même créance que A (216)
DO $$
BEGIN
    PERFORM pg_temp.regul157('B', 360, 40, 0);
    ASSERT pg_temp.creance157('B') = 360, 'volet : part Chifa';
    PERFORM pg_temp.retour157('B', 2, 40, 144);
    ASSERT pg_temp.creance157('B') = 216, 'volet puis retour : 72 × 3';
END $$;

-- 4. Protection : la part rendue avant ne se modifie pas
DO $$
BEGIN
    BEGIN
        UPDATE regularisation_part_organisme SET part_organisme_rendue_avant = 0
        WHERE vente_id = (SELECT id FROM t157 WHERE cle = 'vente_A');
        RAISE EXCEPTION 'part rendue avant modifiée';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE 'Une régularisation de part organisme ne se modifie pas%' THEN RAISE; END IF;
    END;
END $$;

ROLLBACK;
\echo '157 : tous réussis'
