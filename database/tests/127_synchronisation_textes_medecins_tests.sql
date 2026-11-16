-- =====================================================================
-- Tests du filtre des textes libres et de l'option médecins (script 127),
-- en transaction annulée : rien n'est conservé. Lancer :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/127_synchronisation_textes_medecins_tests.sql
-- « Tests 127 : OK » à la fin.
-- =====================================================================
BEGIN;

DO $$
DECLARE
    v_pid    bigint;
    v_code   text;
    v_txt    text;
    v_json   jsonb;
    v_n      int;
    v_echec  boolean;
BEGIN
    -- Patient au nom unique créé pour le test
    INSERT INTO patient (nom, prenom) VALUES ('Zerhouniessai', 'Ouardiaessai') RETURNING id INTO v_pid;
    v_code := sync_code_patient(v_pid);

    -- 1. Valeurs par défaut
    IF (SELECT textes_libres FROM sync_parametre WHERE id = 1) <> 'filtres'
       OR (SELECT envoyer_medecins FROM sync_parametre WHERE id = 1) THEN
        RAISE EXCEPTION 'Test 1 : options par défaut inattendues';
    END IF;

    -- 2. Filtre : nom, prénom, téléphone, courriel, n° d'assuré
    v_txt := sync_filtrer_texte('Rendu à Mme ZERHOUNIESSAI Ouardiaessai, tél 0661 23 45 67 ou +213 551234567, a.b@mail.dz, assuré 123456789012, 3 boîtes');
    IF v_txt ~* 'zerhouniessai|ouardiaessai|0661|551234567|mail\.dz|123456789012' THEN
        RAISE EXCEPTION 'Test 2 : donnée sensible restante : %', v_txt;
    END IF;
    IF position(v_code IN v_txt) = 0 OR position('[TÉL]' IN v_txt) = 0 OR position('[COURRIEL]' IN v_txt) = 0
       OR position('[N°]' IN v_txt) = 0 OR position('3 boîtes' IN v_txt) = 0 OR position('Rendu à Mme' IN v_txt) = 0 THEN
        RAISE EXCEPTION 'Test 2 : remplacement inattendu : %', v_txt;
    END IF;

    -- 3. Porteur du registre repéré
    IF EXISTS (SELECT 1 FROM registre_psychotrope WHERE coalesce(porteur_nom, '') ~ '^[[:alpha:]]{3,}') THEN
        SELECT split_part(porteur_nom, ' ', 1) INTO v_txt FROM registre_psychotrope
        WHERE coalesce(porteur_nom, '') ~ '^[[:alpha:]]{3,}' LIMIT 1;
        IF sync_filtrer_texte('remis à ' || v_txt) ~* ('\m' || v_txt || '\M') THEN
            RAISE EXCEPTION 'Test 3 : porteur % non masqué', v_txt;
        END IF;
    END IF;

    -- 4. Minimisation : texte filtré par défaut, retiré en mode « retires »
    v_json := sync_minimiser((SELECT s FROM sync_table s WHERE nom_table = 'retour_client'),
        jsonb_build_object('id', 1, 'motif_texte', 'pour Zerhouniessai'));
    IF v_json ->> 'motif_texte' <> 'pour ' || v_code THEN RAISE EXCEPTION 'Test 4 : %', v_json; END IF;
    PERFORM sync_definir_options('retires', false);
    v_json := sync_minimiser((SELECT s FROM sync_table s WHERE nom_table = 'retour_client'),
        jsonb_build_object('id', 1, 'motif_texte', 'pour Zerhouniessai'));
    IF NOT (v_json ? 'motif_texte') OR v_json -> 'motif_texte' <> 'null'::jsonb THEN RAISE EXCEPTION 'Test 4 : texte non retiré : %', v_json; END IF;
    v_echec := false;
    BEGIN PERFORM sync_definir_options('autre', false); EXCEPTION WHEN raise_exception THEN v_echec := true; END;
    IF NOT v_echec THEN RAISE EXCEPTION 'Test 4 : valeur invalide acceptée'; END IF;

    -- 5. Médecins : jamais par défaut, envoyés sans coordonnées si l'option est activée
    PERFORM sync_definir_options('filtres', false);
    v_n := sync_definir_active(true);
    IF EXISTS (SELECT 1 FROM pg_trigger tg JOIN pg_class c ON c.oid = tg.tgrelid
               WHERE tg.tgname = 'zz_sync_sortie' AND c.relname = 'medecin') THEN
        RAISE EXCEPTION 'Test 5 : médecins suivis sans option';
    END IF;
    IF sync_definir_options('filtres', true) <> v_n + 1 THEN RAISE EXCEPTION 'Test 5 : table medecin non suivie'; END IF;
    INSERT INTO medecin (nom, specialite, telephone, adresse, email, wilaya)
    VALUES ('Dr Essai', 'Généraliste', '0550000000', 'Rue Y', 'dr@essai.dz', '16');
    SELECT donnees INTO v_json FROM sync_sortie WHERE nom_table = 'medecin' ORDER BY id DESC LIMIT 1;
    IF v_json IS NULL OR v_json ->> 'nom' <> 'Dr Essai' OR v_json ?| ARRAY['telephone', 'adresse', 'email', 'numero_ordre', 'code_prestataire'] THEN
        RAISE EXCEPTION 'Test 5 : médecin mal minimisé : %', v_json;
    END IF;
    PERFORM sync_definir_options('filtres', false);
    IF EXISTS (SELECT 1 FROM pg_trigger tg JOIN pg_class c ON c.oid = tg.tgrelid
               WHERE tg.tgname = 'zz_sync_sortie' AND c.relname = 'medecin') THEN
        RAISE EXCEPTION 'Test 5 : déclencheur medecin non retiré';
    END IF;
    PERFORM sync_definir_active(false);

    RAISE NOTICE 'Tests 127 : OK';
END $$;

ROLLBACK;
