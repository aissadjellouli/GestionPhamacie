-- =====================================================================
-- Tests de la file de synchronisation (script 126), en transaction
-- annulée : rien n'est conservé (ni activation, ni ligne, ni code). Lancer :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/126_synchronisation_sortie_tests.sql
-- Chaque test lève une exception en cas d'échec ; « Tests 126 : OK » à la fin.
-- =====================================================================
BEGIN;

DO $$
DECLARE
    v_avant     bigint;
    v_n         bigint;
    v_vente     record;
    v_patient   record;
    v_ligne     record;
    v_code      text;
    v_json      jsonb;
    v_ids       bigint[];
    v_lot       bigint;
    v_echec     boolean;
BEGIN
    -- 1. Inventaire complet, synchronisation désactivée par défaut, aucun déclencheur posé
    IF EXISTS (SELECT 1 FROM sync_tables_non_classees()) THEN
        RAISE EXCEPTION 'Test 1 : tables non classées : %', (SELECT string_agg(x, ', ') FROM sync_tables_non_classees() x);
    END IF;
    IF (SELECT active FROM sync_parametre WHERE id = 1) THEN RAISE EXCEPTION 'Test 1 : la synchronisation doit être désactivée'; END IF;
    IF EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'zz_sync_sortie') THEN RAISE EXCEPTION 'Test 1 : déclencheur posé alors que désactivée'; END IF;

    -- Une vente avec patient nommé (données de la base)
    SELECT v.id, v.patient_id INTO v_vente FROM vente v JOIN patient p ON p.id = v.patient_id
    WHERE coalesce(p.nom, '') <> '' ORDER BY v.id DESC LIMIT 1;
    IF v_vente.id IS NULL THEN RAISE EXCEPTION 'Test : aucune vente avec patient dans la base'; END IF;
    SELECT id, nom, prenom, num_assure, telephone INTO v_patient FROM patient WHERE id = v_vente.patient_id;

    -- 2. Désactivée : une modification de vente n'écrit rien dans la file
    SELECT count(*) INTO v_avant FROM sync_sortie;
    UPDATE vente SET montant_don = montant_don WHERE id = v_vente.id;
    UPDATE patient SET plafond_credit = plafond_credit WHERE id = v_patient.id;
    IF (SELECT count(*) FROM sync_sortie) <> v_avant THEN RAISE EXCEPTION 'Test 2 : la file a reçu une ligne alors que désactivée'; END IF;

    -- 3. Activée : déclencheurs posés sur les seules tables montantes et bidirectionnelles
    v_n := sync_definir_active(true);
    IF v_n <> (SELECT count(*) FROM sync_table WHERE sens IN ('montante', 'bidirectionnelle')) THEN
        RAISE EXCEPTION 'Test 3 : % déclencheurs posés', v_n;
    END IF;
    IF EXISTS (SELECT 1 FROM pg_trigger tg JOIN pg_class c ON c.oid = tg.tgrelid
               WHERE tg.tgname = 'zz_sync_sortie' AND c.relname IN ('ordonnance', 'medecin', 'sync_pseudonyme', 'message_sortant')) THEN
        RAISE EXCEPTION 'Test 3 : déclencheur sur une table jamais envoyée';
    END IF;

    -- 4. Vente modifiée : une ligne, même transaction, patient sous code, aucun nom
    UPDATE vente SET montant_don = montant_don WHERE id = v_vente.id;
    SELECT * INTO v_ligne FROM sync_sortie WHERE nom_table = 'vente' AND id > 0 ORDER BY id DESC LIMIT 1;
    IF v_ligne.id IS NULL OR v_ligne.id_local <> v_vente.id::text OR v_ligne.operation <> 'modification' OR v_ligne.lot_numero IS NOT NULL THEN
        RAISE EXCEPTION 'Test 4 : ligne de file attendue pour la vente %', v_vente.id;
    END IF;
    v_code := sync_code_patient(v_patient.id);
    IF v_ligne.donnees ->> 'patient_id' IS DISTINCT FROM v_code OR v_code !~ '^P-[0-9a-f]{24}$' THEN
        RAISE EXCEPTION 'Test 4 : patient_id doit être remplacé par le code';
    END IF;
    IF v_ligne.donnees ? 'remis_a_nom' OR v_ligne.donnees ? 'pour_nom' OR v_ligne.donnees ? 'chifa_qr' THEN
        RAISE EXCEPTION 'Test 4 : colonne exclue présente';
    END IF;

    -- 5. Patient modifié : identifiant = code, ni nom, ni prénom, ni n° d'assuré, ni téléphone
    UPDATE patient SET plafond_credit = plafond_credit WHERE id = v_patient.id;
    SELECT * INTO v_ligne FROM sync_sortie WHERE nom_table = 'patient' ORDER BY id DESC LIMIT 1;
    IF v_ligne.id_local <> v_code OR v_ligne.donnees ->> 'id' <> v_code THEN RAISE EXCEPTION 'Test 5 : patient non pseudonymisé'; END IF;
    IF v_ligne.donnees ?| ARRAY['nom', 'prenom', 'num_assure', 'telephone', 'email', 'date_naissance'] THEN
        RAISE EXCEPTION 'Test 5 : colonne personnelle présente';
    END IF;
    IF EXISTS (SELECT 1 FROM sync_sortie s, unnest(ARRAY[v_patient.nom, v_patient.prenom, v_patient.num_assure, v_patient.telephone]) AS x(val)
               WHERE length(coalesce(x.val, '')) >= 3 AND s.donnees::text ILIKE '%' || x.val || '%') THEN
        RAISE EXCEPTION 'Test 5 : nom, n° d''assuré ou téléphone du patient trouvé dans la file';
    END IF;

    -- 6. Code stable
    IF sync_code_patient(v_patient.id) <> v_code THEN RAISE EXCEPTION 'Test 6 : code patient instable'; END IF;
    IF sync_code_porteur('Porteur Essai', 'AB12') <> sync_code_porteur('  porteur essai ', 'ab12')
       OR sync_code_porteur('Porteur Essai', 'AB12') !~ '^R-[0-9a-f]{24}$' THEN
        RAISE EXCEPTION 'Test 6 : code porteur instable';
    END IF;
    IF EXISTS (SELECT 1 FROM sync_pseudonyme WHERE cle_locale ILIKE '%porteur%') THEN
        RAISE EXCEPTION 'Test 6 : nom du porteur recopié dans la correspondance';
    END IF;

    -- 7. Registre : porteur sous code, nom, adresse et pièce retirés
    v_json := sync_minimiser((SELECT s FROM sync_table s WHERE nom_table = 'registre_psychotrope'),
        jsonb_build_object('id', 1, 'patient_id', v_patient.id, 'porteur_nom', 'Porteur Essai', 'porteur_adresse', 'Rue X', 'porteur_piece', 'AB12'));
    IF v_json ?| ARRAY['porteur_nom', 'porteur_adresse', 'porteur_piece'] OR v_json ->> 'porteur_code' IS DISTINCT FROM sync_code_porteur('Porteur Essai', 'AB12')
       OR v_json ->> 'patient_id' IS DISTINCT FROM v_code THEN
        RAISE EXCEPTION 'Test 7 : registre mal minimisé : %', v_json;
    END IF;

    -- 8. Utilisateur : jamais de mot de passe ni de code comptoir
    v_json := sync_minimiser((SELECT s FROM sync_table s WHERE nom_table = 'utilisateur'),
        jsonb_build_object('id', 1, 'mot_de_passe_hash', 'x', 'pin_hash', 'y', 'nom_complet', 'Employé'));
    IF v_json ?| ARRAY['mot_de_passe_hash', 'pin_hash'] THEN RAISE EXCEPTION 'Test 8 : secret présent'; END IF;

    -- 9. Ajout seul : modification et suppression refusées
    v_echec := false;
    BEGIN UPDATE sync_sortie SET donnees = '{}' WHERE id = v_ligne.id; EXCEPTION WHEN raise_exception THEN v_echec := true; END;
    IF NOT v_echec THEN RAISE EXCEPTION 'Test 9 : modification acceptée'; END IF;
    v_echec := false;
    BEGIN DELETE FROM sync_sortie WHERE id = v_ligne.id; EXCEPTION WHEN raise_exception THEN v_echec := true; END;
    IF NOT v_echec THEN RAISE EXCEPTION 'Test 9 : suppression avant accusé acceptée'; END IF;

    -- 10. Lots numérotés sans trou à la constitution
    SELECT array_agg(id ORDER BY id) INTO v_ids FROM sync_sortie WHERE lot_numero IS NULL;
    SELECT dernier_lot + 1 INTO v_lot FROM sync_parametre WHERE id = 1;
    IF sync_enregistrer_lot(v_lot + 1, v_ids, 10, repeat('a', 64)) IS NOT NULL THEN RAISE EXCEPTION 'Test 10 : n° de lot avec trou accepté'; END IF;
    IF sync_enregistrer_lot(v_lot, v_ids, 10, repeat('a', 64)) <> v_lot THEN RAISE EXCEPTION 'Test 10 : lot refusé'; END IF;
    IF (SELECT dernier_lot FROM sync_parametre WHERE id = 1) <> v_lot
       OR EXISTS (SELECT 1 FROM sync_sortie WHERE id = ANY (v_ids) AND lot_numero IS DISTINCT FROM v_lot) THEN
        RAISE EXCEPTION 'Test 10 : lignes non rattachées au lot';
    END IF;
    v_echec := false;
    BEGIN PERFORM sync_enregistrer_lot(v_lot + 1, v_ids, 10, repeat('a', 64)); EXCEPTION WHEN raise_exception THEN v_echec := true; END;
    IF NOT v_echec THEN RAISE EXCEPTION 'Test 10 : ligne rattachée à deux lots'; END IF;

    -- 11. Désactivation : déclencheurs retirés, plus rien n'est inscrit
    PERFORM sync_definir_active(false);
    SELECT count(*) INTO v_avant FROM sync_sortie;
    UPDATE vente SET montant_don = montant_don WHERE id = v_vente.id;
    IF EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'zz_sync_sortie') OR (SELECT count(*) FROM sync_sortie) <> v_avant THEN
        RAISE EXCEPTION 'Test 11 : désactivation incomplète';
    END IF;

    RAISE NOTICE 'Tests 126 : OK';
END $$;

ROLLBACK;
