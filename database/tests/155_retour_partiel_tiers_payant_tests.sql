-- =====================================================================
-- Tests du script 155 (retour partiel d'une vente en tiers payant : part organisme des boîtes rendues, créance de l'organisme,
-- majoration, refus après dépôt, journal fiscal au TTC complet), en transaction ANNULÉE, depuis la racine du dépôt :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/155_retour_partiel_tiers_payant_tests.sql
-- Le script 155 est rejoué deux fois dans la transaction ; rien n'est gardé (ROLLBACK). Il faut un lot vendable (TVA 0 %) sur la
-- base de test. Les lignes du journal sont écrites par des déclencheurs différés : « SET CONSTRAINTS ALL IMMEDIATE » les déclenche.
-- Non testé ici : deux postes en même temps (ajout au bordereau / retour), qui demande deux connexions (voir docs/regles-metier.md).
-- =====================================================================
BEGIN;

\ir ../migration/155_retour_partiel_tiers_payant.sql
\ir ../migration/155_retour_partiel_tiers_payant.sql

CREATE TEMP TABLE t155 (cle text PRIMARY KEY, id bigint NOT NULL) ON COMMIT DROP;

-- Données : un organisme propre au test ; trois ventes Chifa identiques (A : retours successifs et majoration ; B : annulation
-- après un retour ; C : bordereau). Lignes : 2 boîtes à 500 remboursées à 80 % (organisme 800, patient 200), 1 boîte à 300
-- remboursée à 80 % (organisme 240, patient 60), 1 boîte à 100 non remboursée (patient 100). Total 1 400, organisme 1 040, patient 360.
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
    INSERT INTO organisme (code, nom) VALUES ('ZZ155', 'Organisme test 155') RETURNING id INTO v_org;
    INSERT INTO t155 VALUES ('organisme', v_org), ('utilisateur', v_user);
    FOREACH v_cle IN ARRAY ARRAY['A', 'B', 'C'] LOOP
        INSERT INTO vente (numero, utilisateur_id, type, organisme_id, montant_total, part_organisme, part_patient, statut, canal_organisme,
                           chifa_majoration_code, chifa_majoration_montant)
        VALUES ('ZZ155-' || v_cle, v_user, 'libre', v_org, 1400, 1040, 360, 'en_cours', 'chifa',
                CASE WHEN v_cle = 'A' THEN 'garde' END, CASE WHEN v_cle = 'A' THEN 50 ELSE 0 END)
        RETURNING id INTO v_id;
        INSERT INTO t155 VALUES ('vente_' || v_cle, v_id);
        INSERT INTO ligne_vente (vente_id, produit_id, lot_id, quantite, prix_unitaire, base_remboursement, taux_remboursement, montant_organisme, montant_patient)
        VALUES (v_id, v_prod, v_lot, 2, 500, 500, 80, 800, 200) RETURNING id INTO v_id;
        INSERT INTO t155 VALUES ('l1_' || v_cle, v_id);
        INSERT INTO ligne_vente (vente_id, produit_id, lot_id, quantite, prix_unitaire, base_remboursement, taux_remboursement, montant_organisme, montant_patient)
        SELECT id, v_prod, v_lot, 1, 300, 300, 80, 240, 60 FROM t155 WHERE cle = 'vente_' || v_cle RETURNING id INTO v_id;
        INSERT INTO t155 VALUES ('l2_' || v_cle, v_id);
        INSERT INTO ligne_vente (vente_id, produit_id, lot_id, quantite, prix_unitaire, montant_organisme, montant_patient)
        SELECT id, v_prod, v_lot, 1, 100, 0, 100 FROM t155 WHERE cle = 'vente_' || v_cle RETURNING id INTO v_id;
        INSERT INTO t155 VALUES ('l3_' || v_cle, v_id);
        UPDATE vente SET statut = 'validee' WHERE id = (SELECT id FROM t155 WHERE cle = 'vente_' || v_cle);
    END LOOP;
END $$;
SET CONSTRAINTS ALL IMMEDIATE;
SET CONSTRAINTS ALL DEFERRED;  -- comme l'application : les déclencheurs suivants attendent la fin des écritures

-- Retour enregistré comme l'application : une écriture retour_client, ses lignes (part patient, part organisme)
CREATE FUNCTION pg_temp.retour155(p_vente text, p_lignes text[], p_quantites int[], p_patient numeric[], p_organisme numeric[]) RETURNS bigint
LANGUAGE plpgsql AS $$
DECLARE
    v_user bigint := (SELECT id FROM t155 WHERE cle = 'utilisateur');
    v_ret  bigint;
BEGIN
    INSERT INTO retour_client (vente_id, motif, utilisateur_id, valide_par, mode, montant_rembourse)
    VALUES ((SELECT id FROM t155 WHERE cle = 'vente_' || p_vente), 'Test 155', v_user, v_user, 'especes',
            (SELECT sum(x) FROM unnest(p_patient) x))
    RETURNING id INTO v_ret;
    FOR i IN 1 .. array_length(p_lignes, 1) LOOP
        INSERT INTO retour_client_ligne (retour_id, ligne_vente_id, quantite, destination, montant, montant_organisme)
        VALUES (v_ret, (SELECT id FROM t155 WHERE cle = p_lignes[i] || '_' || p_vente), p_quantites[i], 'stock', p_patient[i], p_organisme[i]);
    END LOOP;
    RETURN v_ret;
END;
$$;

-- 1. Structure : colonne, contrainte, défaut ; créance de départ (part caisse + majoration)
DO $$
DECLARE v_a bigint := (SELECT id FROM t155 WHERE cle = 'vente_A');
BEGIN
    ASSERT (SELECT column_default FROM information_schema.columns WHERE table_name = 'retour_client_ligne' AND column_name = 'montant_organisme') = '0',
        'montant_organisme : défaut 0';
    ASSERT (SELECT count(*) FROM pg_constraint WHERE conname IN ('ck_retour_client_ligne_montant_organisme', 'ck_retour_client_chifa_corrige',
            'fk_retour_client_chifa_corrige_par')) = 3, 'contraintes posées une fois';
    ASSERT (SELECT part_organisme_retenue FROM v_vente_part_organisme WHERE vente_id = v_a) = 1090, 'départ : 1 040 + majoration 50';
    ASSERT (SELECT majoration_chifa FROM v_vente_part_organisme WHERE vente_id = v_a) = 50, 'départ : majoration 50';
END $$;

-- 2. Vente A, retour 1 boîte remboursée (80 %) : patient 100 rendu, organisme 400 retiré de la créance, majoration gardée
SELECT pg_temp.retour155('A', ARRAY['l1'], ARRAY[1], ARRAY[100::numeric], ARRAY[400::numeric]) AS retour_a1 \gset
SET CONSTRAINTS ALL IMMEDIATE;
SET CONSTRAINTS ALL DEFERRED;  -- comme l'application : les déclencheurs suivants attendent la fin des écritures
DO $$
DECLARE v_a bigint := (SELECT id FROM t155 WHERE cle = 'vente_A'); j journal_vente;
BEGIN
    ASSERT (SELECT part_organisme_retenue FROM v_vente_part_organisme WHERE vente_id = v_a) = 690, '1 040 − 400 + 50';
    ASSERT (SELECT majoration_chifa FROM v_vente_part_organisme WHERE vente_id = v_a) = 50, 'boîtes remboursées encore chez le patient : majoration gardée';
    SELECT * INTO j FROM journal_vente WHERE vente_id = v_a AND evenement = 'retour' ORDER BY rang DESC LIMIT 1;
    ASSERT j.montant_ttc = -500, 'journal : TTC complet (patient 100 + organisme 400), négatif : ' || j.montant_ttc;
    ASSERT (j.part_patient, j.part_organisme) = (-100::numeric, -400::numeric), 'journal : parts négatives';
    ASSERT j.montant_ht + j.montant_tva = j.montant_ttc, 'journal : HT + TVA = TTC';
END $$;

-- 3. Vente A, retour de la ligne non remboursée : toujours permis, créance inchangée, majoration gardée ; journal sans part organisme
SELECT pg_temp.retour155('A', ARRAY['l3'], ARRAY[1], ARRAY[100::numeric], ARRAY[0::numeric]) AS retour_a2 \gset
SET CONSTRAINTS ALL IMMEDIATE;
SET CONSTRAINTS ALL DEFERRED;  -- comme l'application : les déclencheurs suivants attendent la fin des écritures
DO $$
DECLARE v_a bigint := (SELECT id FROM t155 WHERE cle = 'vente_A'); j journal_vente;
BEGIN
    ASSERT (SELECT part_organisme_retenue FROM v_vente_part_organisme WHERE vente_id = v_a) = 690, 'ligne non remboursée : créance inchangée';
    SELECT * INTO j FROM journal_vente WHERE vente_id = v_a AND evenement = 'retour' ORDER BY rang DESC LIMIT 1;
    ASSERT (j.montant_ttc, j.part_patient, j.part_organisme) = (-100::numeric, -100::numeric, 0::numeric), 'journal : patient seul';
END $$;

-- 4. Vente A, dernières boîtes remboursées rendues : créance à 0, majoration retirée
SELECT pg_temp.retour155('A', ARRAY['l1', 'l2'], ARRAY[1, 1], ARRAY[100::numeric, 60], ARRAY[400::numeric, 240]) AS retour_a3 \gset
SET CONSTRAINTS ALL IMMEDIATE;
SET CONSTRAINTS ALL DEFERRED;  -- comme l'application : les déclencheurs suivants attendent la fin des écritures
DO $$
DECLARE v_a bigint := (SELECT id FROM t155 WHERE cle = 'vente_A');
BEGIN
    ASSERT (SELECT part_organisme_retenue FROM v_vente_part_organisme WHERE vente_id = v_a) = 0, 'tout rendu : créance nulle';
    ASSERT (SELECT majoration_chifa FROM v_vente_part_organisme WHERE vente_id = v_a) = 0, 'plus aucune boîte remboursée : majoration retirée';
    ASSERT NOT EXISTS (SELECT 1 FROM v_vente_chifa_a_deposer WHERE vente_id = v_a), 'créance nulle : plus à déposer';
    ASSERT (SELECT sum(montant_ttc) FROM journal_vente WHERE vente_id = v_a) = 0, 'journal : vente 1 400 moins les retours 1 400';
END $$;

-- 5. Vente B, retour 1 boîte remboursée puis annulation : l'annulation déduit le TTC complet déjà rendu
SELECT pg_temp.retour155('B', ARRAY['l1'], ARRAY[1], ARRAY[100::numeric], ARRAY[400::numeric]) AS retour_b1 \gset
SET CONSTRAINTS ALL IMMEDIATE;
SET CONSTRAINTS ALL DEFERRED;  -- comme l'application : les déclencheurs suivants attendent la fin des écritures
INSERT INTO annulation_vente (vente_id, motif, utilisateur_id, ancienne, mode, montant_rembourse)
SELECT (SELECT id FROM t155 WHERE cle = 'vente_B'), 'Test 155 annulation', (SELECT id FROM t155 WHERE cle = 'utilisateur'), false, 'especes', 260;
UPDATE vente SET statut = 'annulee' WHERE id = (SELECT id FROM t155 WHERE cle = 'vente_B');
SET CONSTRAINTS ALL IMMEDIATE;
SET CONSTRAINTS ALL DEFERRED;  -- comme l'application : les déclencheurs suivants attendent la fin des écritures
DO $$
DECLARE v_b bigint := (SELECT id FROM t155 WHERE cle = 'vente_B'); j journal_vente;
BEGIN
    SELECT * INTO j FROM journal_vente WHERE vente_id = v_b AND evenement = 'annulation';
    ASSERT j.montant_ttc = -900, 'annulation : −(1 400 − 500 déjà rendus TTC) : ' || j.montant_ttc;
    ASSERT (j.part_patient, j.part_organisme) = (-260::numeric, -640::numeric), 'annulation : −(360 − 100), −(1 040 − 400)';
    ASSERT j.montant_ht + j.montant_tva = j.montant_ttc, 'annulation : HT + TVA = TTC';
    ASSERT (SELECT sum(montant_ttc) FROM journal_vente WHERE vente_id = v_b) = 0, 'vente + retour + annulation = 0';
END $$;

-- 6. Vente C : retour avant le bordereau, attendu du bordereau diminué ; après dépôt, part organisme refusée, ligne sans part permise
SELECT pg_temp.retour155('C', ARRAY['l2'], ARRAY[1], ARRAY[60::numeric], ARRAY[240::numeric]) AS retour_c1 \gset
SET CONSTRAINTS ALL IMMEDIATE;
SET CONSTRAINTS ALL DEFERRED;  -- comme l'application : les déclencheurs suivants attendent la fin des écritures
DO $$
DECLARE
    v_c    bigint := (SELECT id FROM t155 WHERE cle = 'vente_C');
    v_user bigint := (SELECT id FROM t155 WHERE cle = 'utilisateur');
    v_b    bigint;
    v_ret  bigint;
BEGIN
    INSERT INTO bordereau (organisme_id, numero, periode_debut, periode_fin, statut, cree_par)
    VALUES ((SELECT id FROM t155 WHERE cle = 'organisme'), 'ZZ155-B1', current_date - 7, current_date, 'preparation', v_user) RETURNING id INTO v_b;
    UPDATE vente SET bordereau_id = v_b WHERE id = v_c;
    INSERT INTO bordereau_vente (bordereau_id, vente_id, part_organisme, ajoute_par)
    SELECT v_b, v_c, part_organisme_retenue, v_user FROM v_vente_part_organisme WHERE vente_id = v_c;
    ASSERT (SELECT attendu FROM v_bordereau WHERE id = v_b) = 800, 'attendu du bordereau : 1 040 − 240 rendus';
    ASSERT (SELECT part_organisme FROM bordereau_vente WHERE bordereau_id = v_b) = 800, 'part retenue au rattachement : 800';

    UPDATE bordereau SET statut = 'depose', date_envoi = current_date WHERE id = v_b;
    INSERT INTO retour_client (vente_id, motif, utilisateur_id, valide_par, mode, montant_rembourse)
    VALUES (v_c, 'Test 155 après dépôt', v_user, v_user, 'especes', 100) RETURNING id INTO v_ret;
    BEGIN
        INSERT INTO retour_client_ligne (retour_id, ligne_vente_id, quantite, destination, montant, montant_organisme)
        VALUES (v_ret, (SELECT id FROM t155 WHERE cle = 'l1_C'), 1, 'stock', 100, 400);
        RAISE EXCEPTION 'part organisme rendue acceptée après dépôt';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE 'Vente déposée à la CNAS : retour impossible dans le logiciel%' THEN RAISE; END IF;
    END;
    -- Ligne sans part organisme (non remboursée) : permise même après dépôt ; la créance ne bouge pas
    INSERT INTO retour_client_ligne (retour_id, ligne_vente_id, quantite, destination, montant, montant_organisme)
    VALUES (v_ret, (SELECT id FROM t155 WHERE cle = 'l3_C'), 1, 'stock', 100, 0);
    ASSERT (SELECT attendu FROM v_bordereau WHERE id = v_b) = 800, 'ligne non remboursée : attendu inchangé';
    -- Passer après coup une part organisme sur une ligne existante : refusé aussi
    BEGIN
        UPDATE retour_client_ligne SET montant_organisme = 1 WHERE retour_id = v_ret;
        RAISE EXCEPTION 'part organisme modifiée après dépôt';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE 'Vente déposée à la CNAS%' THEN RAISE; END IF;
    END;
    -- Montant négatif : refusé par la contrainte
    BEGIN
        INSERT INTO retour_client_ligne (retour_id, ligne_vente_id, quantite, destination, montant, montant_organisme)
        VALUES (v_ret, (SELECT id FROM t155 WHERE cle = 'l3_C'), 1, 'stock', 0, -1);
        RAISE EXCEPTION 'montant organisme négatif accepté';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
END $$;

-- 7. Marque « corrigé dans CHIFA-Officine » : date et employé ensemble
DO $$
BEGIN
    BEGIN
        UPDATE retour_client SET chifa_corrige_le = now() WHERE motif = 'Test 155';
        RAISE EXCEPTION 'date sans employé acceptée';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    UPDATE retour_client SET chifa_corrige_le = now(), chifa_corrige_par = (SELECT id FROM t155 WHERE cle = 'utilisateur') WHERE motif = 'Test 155';
    ASSERT (SELECT count(*) FROM retour_client WHERE motif = 'Test 155' AND chifa_corrige_le IS NOT NULL) >= 1, 'marque posée';
END $$;

ROLLBACK;
\echo '155 : tous réussis'
