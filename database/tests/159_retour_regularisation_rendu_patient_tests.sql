-- =====================================================================
-- Tests du script 159 (retour d'une vente régularisée « rendu au patient » : retenue de la part de l'écart des boîtes rendues, dette
-- ajoutée pour la part non couverte, défense, crédit accordé par vente, journal fiscal du retour et de l'annulation après retour), en
-- transaction ANNULÉE, depuis la racine du dépôt, après le script 158 :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/159_retour_regularisation_rendu_patient_tests.sql
-- Le script 159 est rejoué deux fois dans la transaction ; rien n'est gardé (ROLLBACK). Il faut un lot vendable (TVA 0 %).
-- Vente R : 5 boîtes à 100 DA, 80 % (part caisse 400, patient 100), payée en espèces ; prix Chifa 110 (part Chifa 440) : l'organisme
-- couvre 40 de plus, rendus au patient (8 par boîte).
-- Vente M : ligne A à 100 % (2 boîtes à 100, part caisse 200) et ligne B à 50 % (2 boîtes à 100, part caisse 100, patient 100), payée
-- en espèces ; part Chifa 340 : 40 rendus au patient. Rendre les 2 boîtes A : rien de payé pour elles, 26,67 déjà rendus : dette.
-- Vente S : sans régularisation.
-- =====================================================================
BEGIN;

\ir ../migration/159_retour_regularisation_rendu_patient.sql
\ir ../migration/159_retour_regularisation_rendu_patient.sql

CREATE TEMP TABLE t159 (cle text PRIMARY KEY, id bigint NOT NULL) ON COMMIT DROP;

DO $$
DECLARE
    v_user bigint := (SELECT min(id) FROM utilisateur);
    v_lot  bigint;
    v_prod bigint;
    v_org  bigint;
    v_pat  bigint;
    v_id   bigint;
    v_l    bigint;
BEGIN
    SELECT l.id, l.produit_id INTO v_lot, v_prod FROM lot l JOIN produit p ON p.id = l.produit_id
    WHERE COALESCE(p.taux_tva, 0) = 0 AND l.date_peremption > current_date + 60 AND NOT l.bloque AND NOT p.bloque_vente AND p.actif
    ORDER BY l.id LIMIT 1;
    IF v_lot IS NULL THEN RAISE EXCEPTION 'Base de test sans lot vendable à TVA 0 %%'; END IF;
    INSERT INTO organisme (code, nom) VALUES ('ZZ159', 'Organisme test 159') RETURNING id INTO v_org;
    INSERT INTO patient (nom, prenom) VALUES ('Zzessai159', 'Patient') RETURNING id INTO v_pat;
    INSERT INTO t159 VALUES ('utilisateur', v_user), ('patient', v_pat);

    -- Vente R
    INSERT INTO vente (numero, utilisateur_id, type, organisme_id, patient_id, montant_total, part_organisme, part_patient, statut, canal_organisme)
    VALUES ('ZZ159-R', v_user, 'libre', v_org, v_pat, 500, 400, 100, 'en_cours', 'chifa') RETURNING id INTO v_id;
    INSERT INTO paiement (vente_id, mode, montant, utilisateur_id) VALUES (v_id, 'especes', 100, v_user);
    INSERT INTO ligne_vente (vente_id, produit_id, lot_id, quantite, prix_unitaire, base_remboursement, taux_remboursement, montant_organisme, montant_patient)
    VALUES (v_id, v_prod, v_lot, 5, 100, 100, 80, 400, 100) RETURNING id INTO v_l;
    INSERT INTO t159 VALUES ('vente_R', v_id), ('l_R', v_l);
    UPDATE vente SET statut = 'validee' WHERE id = v_id;
    INSERT INTO regularisation_part_organisme (vente_id, organisme_id, part_organisme_vente, part_organisme_chifa, ecart, sort, montant_patient,
        montant_dette_diminuee, montant_a_rendre, montant_pharmacie, utilisateur_id, part_organisme_rendue_avant)
    SELECT v.id, v.organisme_id, v.part_organisme, 440, -40, 'avoir_patient', 40, 0, 40, 0, v_user, 0 FROM vente v WHERE v.id = v_id;

    -- Vente M
    INSERT INTO vente (numero, utilisateur_id, type, organisme_id, patient_id, montant_total, part_organisme, part_patient, statut, canal_organisme)
    VALUES ('ZZ159-M', v_user, 'libre', v_org, v_pat, 400, 300, 100, 'en_cours', 'chifa') RETURNING id INTO v_id;
    INSERT INTO paiement (vente_id, mode, montant, utilisateur_id) VALUES (v_id, 'especes', 100, v_user);
    INSERT INTO ligne_vente (vente_id, produit_id, lot_id, quantite, prix_unitaire, base_remboursement, taux_remboursement, montant_organisme, montant_patient)
    VALUES (v_id, v_prod, v_lot, 2, 100, 100, 100, 200, 0) RETURNING id INTO v_l;
    INSERT INTO t159 VALUES ('vente_M', v_id), ('l_MA', v_l);
    INSERT INTO ligne_vente (vente_id, produit_id, lot_id, quantite, prix_unitaire, base_remboursement, taux_remboursement, montant_organisme, montant_patient)
    VALUES (v_id, v_prod, v_lot, 2, 100, 100, 50, 100, 100) RETURNING id INTO v_l;
    INSERT INTO t159 VALUES ('l_MB', v_l);
    UPDATE vente SET statut = 'validee' WHERE id = v_id;
    INSERT INTO regularisation_part_organisme (vente_id, organisme_id, part_organisme_vente, part_organisme_chifa, ecart, sort, montant_patient,
        montant_dette_diminuee, montant_a_rendre, montant_pharmacie, utilisateur_id, part_organisme_rendue_avant)
    SELECT v.id, v.organisme_id, v.part_organisme, 340, -40, 'avoir_patient', 40, 0, 40, 0, v_user, 0 FROM vente v WHERE v.id = v_id;

    -- Vente S (sans régularisation, sans patient)
    INSERT INTO vente (numero, utilisateur_id, type, organisme_id, montant_total, part_organisme, part_patient, statut, canal_organisme)
    VALUES ('ZZ159-S', v_user, 'libre', v_org, 500, 400, 100, 'en_cours', 'chifa') RETURNING id INTO v_id;
    INSERT INTO paiement (vente_id, mode, montant, utilisateur_id) VALUES (v_id, 'especes', 100, v_user);
    INSERT INTO ligne_vente (vente_id, produit_id, lot_id, quantite, prix_unitaire, base_remboursement, taux_remboursement, montant_organisme, montant_patient)
    VALUES (v_id, v_prod, v_lot, 5, 100, 100, 80, 400, 100) RETURNING id INTO v_l;
    INSERT INTO t159 VALUES ('vente_S', v_id), ('l_S', v_l);
    UPDATE vente SET statut = 'validee' WHERE id = v_id;
END $$;
SET CONSTRAINTS ALL IMMEDIATE;
SET CONSTRAINTS ALL DEFERRED;

-- Retour écrit comme le logiciel : une écriture (montant rendu, dette ajoutée), la ligne rattachée
CREATE FUNCTION pg_temp.retour159(p_ligne text, p_vente text, p_quantite int, p_patient numeric, p_organisme numeric, p_retenu numeric,
                                  p_dette numeric)
RETURNS bigint LANGUAGE plpgsql AS $$
DECLARE
    v_user bigint := (SELECT id FROM t159 WHERE cle = 'utilisateur');
    v_vid  bigint := (SELECT id FROM t159 WHERE cle = 'vente_' || p_vente);
    v_ret  bigint;
BEGIN
    INSERT INTO retour_client (vente_id, motif, utilisateur_id, valide_par, mode, montant_rembourse, dette_avoir_retenu)
    VALUES (v_vid, 'Test 159', v_user, v_user, 'especes', p_patient - (p_retenu - p_dette), p_dette)
    RETURNING id INTO v_ret;
    INSERT INTO retour_client_ligne (retour_id, ligne_vente_id, quantite, destination, montant, montant_organisme, montant_avoir_retenu)
    VALUES (v_ret, (SELECT id FROM t159 WHERE cle = 'l_' || p_ligne), p_quantite, 'stock', p_patient, p_organisme, p_retenu);
    RETURN v_ret;
END;
$$;

CREATE FUNCTION pg_temp.accorde159(p_vente text) RETURNS numeric
LANGUAGE sql AS $$
    SELECT COALESCE((SELECT montant FROM v_credit_accorde WHERE vente_id = (SELECT id FROM t159 WHERE cle = 'vente_' || p_vente)), 0);
$$;

-- 1. Structure : colonnes, défauts 0, contraintes, déclencheurs ; aucune dette avant tout retour
DO $$
BEGIN
    ASSERT (SELECT column_default FROM information_schema.columns
            WHERE table_name = 'retour_client_ligne' AND column_name = 'montant_avoir_retenu') = '0', 'défaut 0 (ligne)';
    ASSERT (SELECT column_default FROM information_schema.columns
            WHERE table_name = 'retour_client' AND column_name = 'dette_avoir_retenu') = '0', 'défaut 0 (retour)';
    ASSERT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_retour_client_ligne_montant_avoir_retenu'), 'contrainte >= 0 (ligne)';
    ASSERT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_retour_client_dette_avoir_retenu'), 'contrainte >= 0 (retour)';
    ASSERT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_retour_client_ligne_avoir_retenu'), 'déclencheur de défense (ligne)';
    ASSERT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_retour_client_dette_avoir'), 'déclencheur de défense (retour)';
    ASSERT pg_temp.accorde159('R') = 0 AND pg_temp.accorde159('M') = 0, 'ventes payées sans dette';
END $$;

-- 2. Vente R : retour de 2 boîtes, part du patient 40, part organisme 176 (prix Chifa), retenue 16 : 24 rendus en espèces
DO $$
DECLARE v_r bigint := (SELECT id FROM t159 WHERE cle = 'vente_R'); j journal_vente;
BEGIN
    PERFORM pg_temp.retour159('R', 'R', 2, 40, 176, 16, 0);
    SET CONSTRAINTS ALL IMMEDIATE;
    SET CONSTRAINTS ALL DEFERRED;
    SELECT * INTO j FROM journal_vente WHERE vente_id = v_r AND evenement = 'retour';
    ASSERT j.montant_ttc = -200 AND j.part_patient = -24 AND j.part_organisme = -176,
        format('journal du retour : TTC −200 = 40 − 16 + 176, lu %s / %s / %s', j.montant_ttc, j.part_patient, j.part_organisme);
    ASSERT j.paiements = 'especes:-24.00', format('paiements du retour : %s', j.paiements);
    ASSERT (SELECT part_organisme_retenue FROM v_vente_part_organisme WHERE vente_id = v_r) = 264, 'créance : 440 − 176';
    ASSERT pg_temp.accorde159('R') = 0, 'retenue couverte : aucune dette';
END $$;

-- 3. Défense : jamais plus que ce qui reste (24), ni sans régularisation « rendu au patient » ; dette : vente avec patient exigée
DO $$
BEGIN
    BEGIN
        PERFORM pg_temp.retour159('R', 'R', 3, 60, 264, 25, 0);
        RAISE EXCEPTION 'retenue au-delà du reste acceptée';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE 'La régularisation de part organisme de cette vente vient de changer%' THEN RAISE; END IF;
    END;
    BEGIN
        PERFORM pg_temp.retour159('S', 'S', 1, 20, 80, 8, 0);
        RAISE EXCEPTION 'retenue sans régularisation acceptée';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE 'Cette vente n''a pas de régularisation « rendu au patient »%' THEN RAISE; END IF;
    END;
    BEGIN
        PERFORM pg_temp.retour159('S', 'S', 1, 20, 80, 0, 5);
        RAISE EXCEPTION 'dette ajoutée sur une vente sans patient acceptée';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE 'Vente sans patient ou sans régularisation%' THEN RAISE; END IF;
    END;
END $$;

-- 4. Vente M : rendre les 2 boîtes à 100 % : rien de payé, 26,67 retenus, dette ajoutée (mouvement « correction » au relevé)
DO $$
DECLARE
    v_m    bigint := (SELECT id FROM t159 WHERE cle = 'vente_M');
    v_pat  bigint := (SELECT id FROM t159 WHERE cle = 'patient');
    v_user bigint := (SELECT id FROM t159 WHERE cle = 'utilisateur');
    j      journal_vente;
BEGIN
    PERFORM pg_temp.retour159('MA', 'M', 2, 0, 226.67, 26.67, 26.67);
    PERFORM credit_contexte('correction', v_m, v_user, NULL, 'Retour client ZZ159-M : part de la régularisation Chifa « rendu au patient » des boîtes rendues, non couverte par le remboursement');
    UPDATE patient SET solde_credit = solde_credit + 26.67 WHERE id = v_pat;
    ASSERT pg_temp.accorde159('M') = 26.67, format('dette ajoutée comptée sur la vente : lu %s', pg_temp.accorde159('M'));
    ASSERT (SELECT reste FROM v_vente_credit WHERE vente_id = v_m) = 26.67, 'reste dû de la vente : 26,67';
    ASSERT EXISTS (SELECT 1 FROM credit_mouvement WHERE patient_id = v_pat AND vente_id = v_m AND type = 'correction' AND montant = 26.67),
        'mouvement « correction » de 26,67 au relevé, rattaché à la vente';
    SET CONSTRAINTS ALL IMMEDIATE;
    SET CONSTRAINTS ALL DEFERRED;
    SELECT * INTO j FROM journal_vente WHERE vente_id = v_m AND evenement = 'retour';
    ASSERT j.montant_ttc = -200 AND j.part_patient = 26.67 AND j.part_organisme = -226.67,
        format('journal du retour : TTC −200 = 0 − 26,67 + 226,67, lu %s / %s / %s', j.montant_ttc, j.part_patient, j.part_organisme);
    -- « Déjà rendu » de la part du patient (lu par le logiciel) : montant rendu − dette ajoutée + retenue = part brute (0)
    ASSERT (SELECT (SELECT sum(rc.montant_rembourse) - sum(rc.dette_avoir_retenu) FROM retour_client rc WHERE rc.vente_id = v_m)
                 + (SELECT sum(rl.montant_avoir_retenu) FROM retour_client_ligne rl JOIN ligne_vente l ON l.id = rl.ligne_vente_id WHERE l.vente_id = v_m)) = 0,
        'déjà rendu de la part du patient : 0';
END $$;

-- 5. Annulations après retour : journal soldé (TTC, part patient, part organisme) ; vente M : la dette ajoutée reste due
DO $$
DECLARE
    v_r    bigint := (SELECT id FROM t159 WHERE cle = 'vente_R');
    v_m    bigint := (SELECT id FROM t159 WHERE cle = 'vente_M');
    v_user bigint := (SELECT id FROM t159 WHERE cle = 'utilisateur');
    v_id   bigint;
BEGIN
    FOREACH v_id IN ARRAY ARRAY[v_r, v_m] LOOP
        -- Régularisation « rendu au patient » : neutralisée à l'annulation (script 098), sans mouvement d'argent ni de dette
        UPDATE regularisation_part_organisme SET statut = 'annulee', annulee_le = now(), annulee_par = v_user, motif_annulation = 'Test 159'
        WHERE vente_id = v_id;
        INSERT INTO annulation_vente (vente_id, motif, utilisateur_id, ancienne, mode, montant_rembourse)
        VALUES (v_id, 'Test 159', v_user, false, 'especes', CASE WHEN v_id = v_r THEN 60 ELSE 100 END);
        UPDATE vente SET statut = 'annulee' WHERE id = v_id;
    END LOOP;
    SET CONSTRAINTS ALL IMMEDIATE;
    SET CONSTRAINTS ALL DEFERRED;
    FOREACH v_id IN ARRAY ARRAY[v_r, v_m] LOOP
        ASSERT (SELECT sum(montant_ttc) FROM journal_vente WHERE vente_id = v_id AND montant_ttc IS NOT NULL) = 0,
            format('TTC soldé (vente %s), lu %s', v_id, (SELECT sum(montant_ttc) FROM journal_vente WHERE vente_id = v_id AND montant_ttc IS NOT NULL));
        ASSERT (SELECT sum(part_patient) FROM journal_vente WHERE vente_id = v_id) = 0,
            format('part du patient soldée (vente %s), lu %s', v_id, (SELECT sum(part_patient) FROM journal_vente WHERE vente_id = v_id));
        ASSERT (SELECT sum(part_organisme) FROM journal_vente WHERE vente_id = v_id) = 0,
            format('part organisme soldée (vente %s), lu %s', v_id, (SELECT sum(part_organisme) FROM journal_vente WHERE vente_id = v_id));
    END LOOP;
    ASSERT pg_temp.accorde159('M') = 26.67, 'vente M annulée : la dette ajoutée par la retenue reste due';
    ASSERT pg_temp.accorde159('R') = 0, 'vente R annulée : aucune dette';
END $$;

ROLLBACK;
\echo '159 : tous réussis'
