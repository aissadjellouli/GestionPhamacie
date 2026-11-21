-- =====================================================================
-- Tests du script 158 (retour d'une vente régularisée « à la charge du patient » : part de la régularisation des boîtes rendues,
-- défense, crédit accordé par vente, journal fiscal du retour et de l'annulation après retour), en transaction ANNULÉE, depuis la
-- racine du dépôt, après le script 157 :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/158_retour_regularisation_dettes_patient_tests.sql
-- Le script 158 est rejoué deux fois dans la transaction ; rien n'est gardé (ROLLBACK). Il faut un lot vendable (TVA 0 %).
-- Vente : 5 boîtes à 100 DA, 80 % (part caisse 400, patient 100), payée en espèces ; prix Chifa 90 (72 par boîte) : écart 40 mis à la
-- charge du patient (8 par boîte), ajouté à sa dette.
-- =====================================================================
BEGIN;

\ir ../migration/158_retour_regularisation_dettes_patient.sql
\ir ../migration/158_retour_regularisation_dettes_patient.sql

CREATE TEMP TABLE t158 (cle text PRIMARY KEY, id bigint NOT NULL) ON COMMIT DROP;

-- Patient, vente payée P (régularisée à sa charge), vente à crédit K (sans régularisation), vente S (sans régularisation)
DO $$
DECLARE
    v_user bigint := (SELECT min(id) FROM utilisateur);
    v_lot  bigint;
    v_prod bigint;
    v_org  bigint;
    v_pat  bigint;
    v_id   bigint;
    v_cle  text;
BEGIN
    SELECT l.id, l.produit_id INTO v_lot, v_prod FROM lot l JOIN produit p ON p.id = l.produit_id
    WHERE COALESCE(p.taux_tva, 0) = 0 AND l.date_peremption > current_date + 60 AND NOT l.bloque AND NOT p.bloque_vente AND p.actif
    ORDER BY l.id LIMIT 1;
    IF v_lot IS NULL THEN RAISE EXCEPTION 'Base de test sans lot vendable à TVA 0 %%'; END IF;
    INSERT INTO organisme (code, nom) VALUES ('ZZ158', 'Organisme test 158') RETURNING id INTO v_org;
    INSERT INTO patient (nom, prenom) VALUES ('Zzessai158', 'Patient') RETURNING id INTO v_pat;
    INSERT INTO t158 VALUES ('utilisateur', v_user), ('patient', v_pat);
    FOREACH v_cle IN ARRAY ARRAY['P', 'K', 'S'] LOOP
        INSERT INTO vente (numero, utilisateur_id, type, organisme_id, patient_id, montant_total, part_organisme, part_patient, statut, canal_organisme)
        VALUES ('ZZ158-' || v_cle, v_user, 'libre', v_org, v_pat, 500, 400, 100, 'en_cours', 'chifa') RETURNING id INTO v_id;
        INSERT INTO t158 VALUES ('vente_' || v_cle, v_id);
        INSERT INTO paiement (vente_id, mode, montant, utilisateur_id) VALUES (v_id, CASE WHEN v_cle = 'K' THEN 'credit' ELSE 'especes' END, 100, v_user);
        INSERT INTO ligne_vente (vente_id, produit_id, lot_id, quantite, prix_unitaire, base_remboursement, taux_remboursement, montant_organisme, montant_patient)
        VALUES (v_id, v_prod, v_lot, 5, 100, 100, 80, 400, 100) RETURNING id INTO v_id;
        INSERT INTO t158 VALUES ('l_' || v_cle, v_id);
        UPDATE vente SET statut = 'validee' WHERE id = (SELECT id FROM t158 WHERE cle = 'vente_' || v_cle);
    END LOOP;
    -- Vente K à crédit : dette de 100
    PERFORM credit_contexte('vente_credit', (SELECT id FROM t158 WHERE cle = 'vente_K'), v_user, NULL, 'Test 158');
    UPDATE patient SET solde_credit = solde_credit + 100 WHERE id = v_pat;
    -- Vente P : écart 40 à la charge du patient (mouvement de dette rattaché à la vente, comme le rattachement du volet)
    PERFORM credit_contexte('correction', (SELECT id FROM t158 WHERE cle = 'vente_P'), v_user, NULL, 'Régularisation part organisme Chifa test 158');
    UPDATE patient SET solde_credit = solde_credit + 40 WHERE id = v_pat;
    INSERT INTO regularisation_part_organisme (vente_id, organisme_id, part_organisme_vente, part_organisme_chifa, ecart, sort, montant_patient,
        credit_mouvement_id, utilisateur_id, part_organisme_rendue_avant)
    SELECT v.id, v.organisme_id, v.part_organisme, 360, 40, 'charge_patient', 40, currval(pg_get_serial_sequence('credit_mouvement', 'id')), v_user, 0
    FROM vente v WHERE v.id = (SELECT id FROM t158 WHERE cle = 'vente_P');
END $$;
SET CONSTRAINTS ALL IMMEDIATE;
SET CONSTRAINTS ALL DEFERRED;

-- Retour : une écriture par mode (part du patient, régularisation), les lignes rattachées à la première
CREATE FUNCTION pg_temp.retour158(p_vente text, p_quantite int, p_patient numeric, p_organisme numeric, p_regul numeric, p_regul_mode text)
RETURNS bigint LANGUAGE plpgsql AS $$
DECLARE
    v_user bigint := (SELECT id FROM t158 WHERE cle = 'utilisateur');
    v_vid  bigint := (SELECT id FROM t158 WHERE cle = 'vente_' || p_vente);
    v_ret  bigint;
BEGIN
    INSERT INTO retour_client (vente_id, motif, utilisateur_id, valide_par, mode, montant_rembourse)
    VALUES (v_vid, 'Test 158', v_user, v_user, CASE WHEN p_vente = 'K' THEN 'credit' ELSE 'especes' END, p_patient)
    RETURNING id INTO v_ret;
    IF p_regul > 0 THEN
        INSERT INTO retour_client (vente_id, motif, utilisateur_id, valide_par, mode, montant_rembourse)
        VALUES (v_vid, 'Test 158', v_user, v_user, p_regul_mode, p_regul);
    END IF;
    INSERT INTO retour_client_ligne (retour_id, ligne_vente_id, quantite, destination, montant, montant_organisme, montant_regularisation)
    VALUES (v_ret, (SELECT id FROM t158 WHERE cle = 'l_' || p_vente), p_quantite, 'stock', p_patient, p_organisme, p_regul);
    RETURN v_ret;
END;
$$;

CREATE FUNCTION pg_temp.accorde158(p_vente text) RETURNS numeric
LANGUAGE sql AS $$
    SELECT COALESCE((SELECT montant FROM v_credit_accorde WHERE vente_id = (SELECT id FROM t158 WHERE cle = 'vente_' || p_vente)), 0);
$$;

-- 1. Structure : colonne, défaut 0, contrainte, déclencheur
DO $$
BEGIN
    ASSERT (SELECT column_default FROM information_schema.columns
            WHERE table_name = 'retour_client_ligne' AND column_name = 'montant_regularisation') = '0', 'défaut 0';
    ASSERT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_retour_client_ligne_montant_regularisation'), 'contrainte >= 0';
    ASSERT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_retour_client_ligne_regularisation'), 'déclencheur de défense';
    ASSERT pg_temp.accorde158('P') = 40, 'régularisation : 40 dus sur la vente payée';
    ASSERT pg_temp.accorde158('K') = 100, 'vente à crédit : 100';
END $$;

-- 2. Vente P : retour de 2 boîtes, part du patient 40 rendue en espèces, part de la régularisation 16 (2 × 8) retirée de sa dette
DO $$
DECLARE v_p bigint := (SELECT id FROM t158 WHERE cle = 'vente_P'); j journal_vente;
BEGIN
    PERFORM pg_temp.retour158('P', 2, 40, 144, 16, 'credit');
    -- Avant 158, l'argent rendu de la part du patient (40) était aussi retranché : 40 − 56 < 0, la vente sortait de la liste des dettes
    ASSERT pg_temp.accorde158('P') = 24, format('vente payée : seule la part de régularisation rendue est retranchée (24), lu %s', pg_temp.accorde158('P'));
    SET CONSTRAINTS ALL IMMEDIATE;
    SET CONSTRAINTS ALL DEFERRED;
    SELECT * INTO j FROM journal_vente WHERE vente_id = v_p AND evenement = 'retour';
    ASSERT j.montant_ttc = -200 AND j.part_patient = -56 AND j.part_organisme = -144,
        format('journal du retour : TTC −200 = 40 + 16 + 144, lu %s / %s / %s', j.montant_ttc, j.part_patient, j.part_organisme);
END $$;

-- 3. Défense : jamais plus que ce qui reste de la régularisation (24), ni sans régularisation à la charge du patient
DO $$
BEGIN
    BEGIN
        PERFORM pg_temp.retour158('P', 3, 60, 216, 25, 'especes');
        RAISE EXCEPTION 'part de régularisation au-delà du reste acceptée';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE 'La régularisation de part organisme de cette vente vient de changer%' THEN RAISE; END IF;
    END;
    BEGIN
        PERFORM pg_temp.retour158('S', 1, 20, 80, 8, 'especes');
        RAISE EXCEPTION 'part de régularisation sans régularisation acceptée';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE 'Cette vente n''a pas de régularisation à la charge du patient%' THEN RAISE; END IF;
    END;
END $$;

-- 4. Vente à crédit : inchangé, tout ce que le retour rend est retranché ; vente payée sans régularisation : hors liste
DO $$
BEGIN
    PERFORM pg_temp.retour158('K', 1, 20, 80, 0, NULL);
    ASSERT pg_temp.accorde158('K') = 80, 'vente à crédit : 100 − 20';
    PERFORM pg_temp.retour158('S', 1, 20, 80, 0, NULL);
    ASSERT pg_temp.accorde158('S') = 0, 'vente payée sans régularisation : aucune dette';
END $$;

-- 5. Annulation de la vente P après le retour : journal soldé (TTC, part patient, part organisme), régularisation annulée en entier
DO $$
DECLARE
    v_p    bigint := (SELECT id FROM t158 WHERE cle = 'vente_P');
    v_user bigint := (SELECT id FROM t158 WHERE cle = 'utilisateur');
BEGIN
    UPDATE regularisation_part_organisme SET statut = 'annulee', annulee_le = now(), annulee_par = v_user, motif_annulation = 'Test 158'
    WHERE vente_id = v_p;
    INSERT INTO annulation_vente (vente_id, motif, utilisateur_id, ancienne, mode, montant_rembourse) VALUES (v_p, 'Test 158', v_user, false, 'especes', 60);
    UPDATE vente SET statut = 'annulee' WHERE id = v_p;
    SET CONSTRAINTS ALL IMMEDIATE;
    SET CONSTRAINTS ALL DEFERRED;
    ASSERT (SELECT sum(montant_ttc) FROM journal_vente WHERE vente_id = v_p AND montant_ttc IS NOT NULL) = 0, 'TTC soldé';
    ASSERT (SELECT sum(part_patient) FROM journal_vente WHERE vente_id = v_p) = 0,
        format('part du patient soldée (100 + 40 − 56 − 40 − 44), lu %s', (SELECT sum(part_patient) FROM journal_vente WHERE vente_id = v_p));
    ASSERT (SELECT sum(part_organisme) FROM journal_vente WHERE vente_id = v_p) = 0, 'part organisme soldée (400 − 40 − 144 + 40 − 256)';
END $$;

ROLLBACK;
\echo '158 : tous réussis'
