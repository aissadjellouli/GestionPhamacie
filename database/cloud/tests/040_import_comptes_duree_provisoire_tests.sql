-- =====================================================================
-- Tests du script 040 (import de comptes, durée de vie des mots de passe provisoires), en transaction ANNULÉE :
--   psql -U <propriétaire> -d <base centrale de test> -v ON_ERROR_STOP=1 -f database/cloud/tests/040_import_comptes_duree_provisoire_tests.sql
-- Droits (écrans seulement ; contrôle interne et console jamais accordés) ; refus au titulaire ; aperçu sans code (forme, doublon,
-- existe, nom, rôle, contact, formes) ; import tout ou rien avec UN seul code ; console tout ou rien ; durée réglée (bornes, code) ;
-- expiration à la connexion (création, remise à zéro qui repart pour une durée complète) ; liste avec l'état du mot de passe ;
-- note des envois ; inactivité par rôle (réglage, bornes, code, échéance glissante, aucune durée maximale pour une session active) ;
-- journaux sans identifiant, nom ni contact ; chaîne intacte.
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

DO $$ BEGIN
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_comptes_apercu(text, jsonb)', 'EXECUTE'), 'aperçu : écrans';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_comptes_importer(text, text, jsonb)', 'EXECUTE'), 'import : écrans';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_comptes_liste(text, int, int)', 'EXECUTE'), 'liste : écrans';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_comptes_noter_envoi(text, uuid, text, text)', 'EXECUTE'), 'envois : écrans';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_provisoire_duree_lire(text)', 'EXECUTE'), 'durée : écrans';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_provisoire_duree_enregistrer(text, text, int)', 'EXECUTE'), 'durée : écrans';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_fin_connexion(uuid, boolean, text)', 'EXECUTE'), 'connexion : écrans';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_comptes_destinataire_remise(text, uuid)', 'EXECUTE'), 'destinataire : écrans';
    ASSERT NOT has_function_privilege('pharma_cloud_ecrans', 'cloud_comptes_import_controle(jsonb)', 'EXECUTE'), 'contrôle interne : jamais accordé';
    ASSERT NOT has_function_privilege('pharma_cloud_ecrans', 'cloud_compte_distant_creer_lot(jsonb, text)', 'EXECUTE'), 'console : jamais aux écrans';
    ASSERT NOT has_function_privilege('pharma_cloud_ecrans', 'cloud_duree_provisoire()', 'EXECUTE'), 'durée interne';
    ASSERT NOT has_function_privilege('public', 'cloud_ecran_comptes_importer(text, text, jsonb)', 'EXECUTE'), 'pas pour PUBLIC';
    ASSERT NOT has_function_privilege('public', 'cloud_compte_distant_creer_lot(jsonb, text)', 'EXECUTE'), 'pas pour PUBLIC';
    ASSERT NOT has_function_privilege('pharma_cloud_api', 'cloud_ecran_comptes_importer(text, text, jsonb)', 'EXECUTE'), 'API : rien';
    ASSERT (SELECT p.prosecdef FROM pg_proc p WHERE p.oid = 'cloud_ecran_comptes_importer(text, text, jsonb)'::regprocedure), 'SECURITY DEFINER';
    ASSERT (SELECT duree_provisoire_jours FROM parametre_envoi_codes WHERE id = 1) BETWEEN 1 AND 30, 'durée présente';
END $$;

-- Données d'essai : éditeur E (session 6, codes e puis d), titulaire T (session 4, code f), compte existant « existe.400 »
SELECT cloud_compte_distant_creer('editeur.e400', 'Éditeur 0400', 'editeur', '$2a$12$' || repeat('a', 53), NULL, 'e400@exemple.invalid', 'tests 040') \g /dev/null
SELECT cloud_compte_distant_creer('titulaire.t400', 'Titulaire 0400', 'titulaire', '$2a$12$' || repeat('a', 53), NULL, 't400@exemple.invalid', 'tests 040') \g /dev/null
SELECT cloud_compte_distant_creer('existe.400', 'Existe 0400', 'titulaire', '$2a$12$' || repeat('a', 53), NULL, 'x400@exemple.invalid', 'tests 040') \g /dev/null
UPDATE compte_distant SET mot_de_passe_provisoire = false WHERE identifiant IN ('editeur.e400', 'titulaire.t400');
UPDATE parametre_envoi_codes SET duree_provisoire_jours = 7 WHERE id = 1;
INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le)
SELECT repeat(x.j, 64), c.id, 'ouverte', now() + interval '2 hours'
FROM (VALUES ('editeur.e400', '6'), ('titulaire.t400', '4')) x(ident, j) JOIN compte_distant c ON c.identifiant = x.ident;
INSERT INTO code_second_facteur (compte_id, session_id, objet, empreinte_code, canal, expire_le)
SELECT s.compte_id, s.id, 'action', repeat(x.k, 64), 'fichier', now() + interval '5 minutes'
FROM (VALUES ('6', 'e'), ('4', 'f')) x(j, k) JOIN session_distante s ON s.empreinte_jeton = repeat(x.j, 64);

SET LOCAL ROLE pharma_cloud_ecrans;
DO $$
DECLARE m text := '$2a$12$' || repeat('b', 53);
        mauvais jsonb; bon jsonb; n int;
BEGIN
    mauvais := jsonb_build_array(
        '"pas un objet"'::jsonb,
        jsonb_build_object('ligne', 2, 'role', 'titulaire', 'identifiant', ' Nouveau.A400 ', 'nom', 'Nouveau A', 'email', 'a400@exemple.invalid', 'telephone', '', 'empreinte', m),
        jsonb_build_object('ligne', 3, 'role', 'titulaire', 'identifiant', 'nouveau.a400', 'nom', 'Doublon', 'email', 'b400@exemple.invalid', 'empreinte', m),
        jsonb_build_object('ligne', 4, 'role', 'titulaire', 'identifiant', 'EXISTE.400', 'nom', 'Pris', 'email', 'c400@exemple.invalid', 'empreinte', m),
        jsonb_build_object('ligne', 5, 'role', 'titulaire', 'identifiant', 'x', 'nom', 'Court', 'email', 'd400@exemple.invalid', 'empreinte', m),
        jsonb_build_object('ligne', 6, 'role', 'titulaire', 'identifiant', 'sans.nom400', 'nom', '  ', 'email', 'e400b@exemple.invalid', 'empreinte', m),
        jsonb_build_object('ligne', 7, 'role', 'admin', 'identifiant', 'role.400', 'nom', 'Rôle', 'email', 'f400@exemple.invalid', 'empreinte', m),
        jsonb_build_object('ligne', 8, 'role', 'titulaire', 'identifiant', 'contact.400', 'nom', 'Sans contact', 'email', '', 'telephone', ' ', 'empreinte', m),
        jsonb_build_object('ligne', 9, 'role', 'titulaire', 'identifiant', 'email.400', 'nom', 'Adresse', 'email', 'pas-une-adresse', 'empreinte', m),
        jsonb_build_object('ligne', 10, 'role', 'titulaire', 'identifiant', 'tel.400', 'nom', 'Téléphone', 'telephone', '05-55', 'empreinte', m));
    bon := jsonb_build_array(
        jsonb_build_object('ligne', 2, 'role', 'titulaire', 'identifiant', ' Nouveau.A400 ', 'nom', ' Nouveau A ', 'email', 'a400@exemple.invalid', 'telephone', '0555 000 400', 'empreinte', m),
        jsonb_build_object('ligne', 3, 'role', 'EDITEUR', 'identifiant', 'nouveau.b400', 'nom', 'Nouveau B', 'telephone', '+213555000401', 'empreinte', m));

    -- Titulaire : refusé partout
    BEGIN PERFORM cloud_ecran_comptes_apercu(repeat('4', 64), bon); RAISE EXCEPTION 'aperçu par un titulaire';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM cloud_ecran_comptes_importer(repeat('4', 64), repeat('f', 64), bon); RAISE EXCEPTION 'import par un titulaire';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM cloud_ecran_provisoire_duree_enregistrer(repeat('4', 64), repeat('f', 64), 3); RAISE EXCEPTION 'durée par un titulaire';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM cloud_ecran_comptes_liste(repeat('4', 64), 1, 20); RAISE EXCEPTION 'liste par un titulaire';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    -- Formes du tableau : vide, plus de 200 lignes, pas un tableau
    BEGIN PERFORM cloud_ecran_comptes_apercu(repeat('6', 64), '[]'::jsonb); RAISE EXCEPTION 'tableau vide accepté';
    EXCEPTION WHEN invalid_parameter_value THEN NULL; END;
    BEGIN PERFORM cloud_ecran_comptes_apercu(repeat('6', 64), (SELECT jsonb_agg(jsonb_build_object('ligne', g)) FROM generate_series(1, 201) g));
          RAISE EXCEPTION '201 lignes acceptées';
    EXCEPTION WHEN invalid_parameter_value THEN NULL; END;
    BEGIN PERFORM cloud_ecran_comptes_apercu(repeat('6', 64), '{"a": 1}'::jsonb); RAISE EXCEPTION 'objet accepté';
    EXCEPTION WHEN invalid_parameter_value THEN NULL; END;
    -- Empreinte invalide : refusée avant tout
    BEGIN PERFORM cloud_ecran_comptes_importer(repeat('6', 64), repeat('e', 64), jsonb_set(bon, '{1,empreinte}', '"pas-une-empreinte"'));
          RAISE EXCEPTION 'empreinte invalide acceptée';
    EXCEPTION WHEN invalid_parameter_value THEN NULL; END;

    -- Aperçu : chaque ligne et son statut, sans code
    ASSERT (SELECT string_agg(numero || ':' || statut, ',' ORDER BY numero) FROM cloud_ecran_comptes_apercu(repeat('6', 64), mauvais))
           = '1:forme,2:ok,3:doublon,4:existe,5:identifiant,6:nom,7:role,8:contact,9:email,10:telephone', 'aperçu ligne par ligne';
    ASSERT (SELECT string_agg(statut, ',') FROM cloud_ecran_comptes_apercu(repeat('6', 64), bon)) = 'ok,ok', 'aperçu valable';
    -- Import d'un fichier invalide : rien n'est créé, le code n'est pas consommé
    ASSERT (SELECT count(*) FROM cloud_ecran_comptes_importer(repeat('6', 64), repeat('e', 64), mauvais) WHERE statut <> 'ok') = 9, 'statuts rendus';
    -- Code faux : une ligne, rien de créé
    ASSERT (SELECT string_agg(coalesce(numero::text, '-') || ':' || statut, ',') FROM cloud_ecran_comptes_importer(repeat('6', 64), repeat('9', 64), bon)) = '-:faux', 'code faux';
    -- UN code pour tout le fichier
    ASSERT (SELECT string_agg(numero || ':' || statut, ',' ORDER BY numero) FROM cloud_ecran_comptes_importer(repeat('6', 64), repeat('e', 64), bon)
            WHERE compte IS NOT NULL) = '2:ok,3:ok', 'deux comptes créés avec un seul code';
    -- Code consommé : un nouvel import exige un nouveau code
    ASSERT (SELECT statut FROM cloud_ecran_comptes_importer(repeat('6', 64), repeat('e', 64),
            jsonb_build_array(jsonb_build_object('ligne', 2, 'role', 'titulaire', 'identifiant', 'nouveau.c400', 'nom', 'C', 'email', 'c4@exemple.invalid', 'empreinte', m)))) = 'aucun', 'code à usage unique';
    -- Aperçu après création : les identifiants sont pris
    ASSERT (SELECT string_agg(statut, ',') FROM cloud_ecran_comptes_apercu(repeat('6', 64), bon)) = 'existe,existe', 'identifiants pris';

    -- Durée : bornes avant le code, puis enregistrement
    ASSERT (SELECT statut FROM cloud_ecran_provisoire_duree_enregistrer(repeat('6', 64), repeat('d', 64), 0)) = 'duree', 'durée 0';
    ASSERT (SELECT statut FROM cloud_ecran_provisoire_duree_enregistrer(repeat('6', 64), repeat('d', 64), 31)) = 'duree', 'durée 31';
    ASSERT (SELECT statut FROM cloud_ecran_provisoire_duree_enregistrer(repeat('6', 64), repeat('d', 64), NULL)) = 'duree', 'durée absente';
    ASSERT (SELECT statut FROM cloud_ecran_provisoire_duree_enregistrer(repeat('6', 64), repeat('d', 64), 3)) = 'aucun', 'code exigé';
    ASSERT cloud_ecran_provisoire_duree_lire(repeat('6', 64)) = 7, 'durée inchangée';
END $$;
RESET ROLE;

-- Comptes créés par l'import : normalisés, provisoires, auteur « écran : import »
DO $$ DECLARE a compte_distant; b compte_distant; BEGIN
    SELECT * INTO a FROM compte_distant WHERE identifiant = 'nouveau.a400';
    SELECT * INTO b FROM compte_distant WHERE identifiant = 'nouveau.b400';
    ASSERT a.nom = 'Nouveau A' AND a.role = 'titulaire' AND a.telephone = '0555000400' AND a.email = 'a400@exemple.invalid', 'ligne A normalisée';
    ASSERT b.role = 'editeur' AND b.email IS NULL AND b.telephone = '+213555000401', 'ligne B (rôle en majuscules, téléphone seul)';
    ASSERT a.mot_de_passe_provisoire AND b.mot_de_passe_provisoire AND a.mot_de_passe_change_le IS NULL, 'provisoires';
    ASSERT a.cree_par LIKE 'écran : import, éditeur n° %', 'auteur';
    ASSERT NOT EXISTS (SELECT 1 FROM compte_distant WHERE identifiant IN ('nouveau.c400', 'contact.400', 'role.400')), 'rien d''autre';
END $$;

-- Durée enregistrée avec un code (nouveau code d)
INSERT INTO code_second_facteur (compte_id, session_id, objet, empreinte_code, canal, expire_le)
SELECT s.compte_id, s.id, 'action', repeat('d', 64), 'fichier', now() + interval '5 minutes' FROM session_distante s WHERE s.empreinte_jeton = repeat('6', 64);
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ BEGIN
    ASSERT (SELECT statut FROM cloud_ecran_provisoire_duree_enregistrer(repeat('6', 64), repeat('d', 64), 3)) = 'ok', 'durée enregistrée';
    ASSERT cloud_ecran_provisoire_duree_lire(repeat('6', 64)) = 3, 'durée relue';
END $$;
RESET ROLE;

-- Expiration : nouveau.a400 créé il y a 4 jours (durée 3) → refusé ; nouveau.b400 créé il y a 2 jours → accepté ;
-- existe.400 remis à zéro il y a 1 jour (créé il y a 30 jours) → accepté (la remise repart pour une durée complète) ;
-- titulaire.t400 au mot de passe choisi, créé il y a 60 jours → accepté
UPDATE compte_distant SET cree_le = now() - interval '4 days' WHERE identifiant = 'nouveau.a400';
UPDATE compte_distant SET cree_le = now() - interval '2 days' WHERE identifiant = 'nouveau.b400';
UPDATE compte_distant SET cree_le = now() - interval '30 days', mot_de_passe_change_le = now() - interval '1 day' WHERE identifiant = 'existe.400';
UPDATE compte_distant SET cree_le = now() - interval '60 days' WHERE identifiant = 'titulaire.t400';
-- Script 041 passé : le contrôle d'expiration est supposé en place depuis longtemps (sinon son délai de grâce couvre ces comptes)
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'parametre_envoi_codes' AND column_name = 'provisoire_controle_depuis') THEN
        EXECUTE 'UPDATE parametre_envoi_codes SET provisoire_controle_depuis = now() - interval ''1 year'' WHERE id = 1';
    END IF;
END $$;
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ DECLARE ida uuid; idb uuid; idx uuid; idt uuid; BEGIN
    SELECT compte_id INTO ida FROM cloud_ecran_debut_connexion('nouveau.a400');
    SELECT compte_id INTO idb FROM cloud_ecran_debut_connexion('nouveau.b400');
    SELECT compte_id INTO idx FROM cloud_ecran_debut_connexion('existe.400');
    SELECT compte_id INTO idt FROM cloud_ecran_debut_connexion('titulaire.t400');
    ASSERT (SELECT statut FROM cloud_ecran_fin_connexion(ida, false, NULL)) = 'refuse', 'mot de passe faux : message habituel, même expiré';
    ASSERT (SELECT statut FROM cloud_ecran_fin_connexion(ida, true, repeat('1', 64))) = 'provisoire_expire', 'provisoire expiré refusé';
    ASSERT (SELECT statut FROM cloud_ecran_fin_connexion(idb, true, repeat('2', 64))) = 'code_attendu', 'provisoire encore valable';
    ASSERT (SELECT statut FROM cloud_ecran_fin_connexion(idx, true, repeat('3', 64))) = 'code_attendu', 'remise à zéro récente : valable';
    ASSERT (SELECT statut FROM cloud_ecran_fin_connexion(idt, true, repeat('5', 64))) = 'code_attendu', 'mot de passe choisi : jamais expiré';
    -- Liste : état du mot de passe
    ASSERT (SELECT mot_de_passe_etat FROM cloud_ecran_comptes_liste(repeat('6', 64), 1, 100) WHERE identifiant = 'nouveau.a400') = 'provisoire_expire', 'liste : expiré';
    ASSERT (SELECT mot_de_passe_etat || '|' || (provisoire_expire_le > now())::text FROM cloud_ecran_comptes_liste(repeat('6', 64), 1, 100)
            WHERE identifiant = 'nouveau.b400') = 'provisoire|true', 'liste : provisoire et son échéance';
    ASSERT (SELECT mot_de_passe_etat IS NULL AND provisoire_expire_le IS NULL FROM cloud_ecran_comptes_liste(repeat('6', 64), 1, 100)
            WHERE identifiant = 'titulaire.t400'), 'liste : mot de passe choisi';
    ASSERT (SELECT email_masque FROM cloud_ecran_comptes_liste(repeat('6', 64), 1, 100) WHERE identifiant = 'nouveau.a400') = 'a***@exemple.invalid', 'contact masqué';
    -- Note des envois : liste fermée
    PERFORM cloud_ecran_comptes_noter_envoi(repeat('6', 64), ida, 'identifiant_envoi', 'email:ok');
    PERFORM cloud_ecran_comptes_noter_envoi(repeat('6', 64), idb, 'provisoire_envoi', 'sms:indisponible');
    BEGIN PERFORM cloud_ecran_comptes_noter_envoi(repeat('6', 64), ida, 'identifiant_envoi', 'email:a400@exemple.invalid');
          RAISE EXCEPTION 'adresse acceptée au journal';
    EXCEPTION WHEN invalid_parameter_value THEN NULL; END;
    BEGIN PERFORM cloud_ecran_comptes_noter_envoi(repeat('6', 64), ida, 'mot_de_passe_envoi', 'email:ok');
          RAISE EXCEPTION 'mot de passe par e-mail accepté';
    EXCEPTION WHEN invalid_parameter_value THEN NULL; END;
    BEGIN PERFORM cloud_ecran_comptes_noter_envoi(repeat('4', 64), ida, 'identifiant_envoi', 'email:ok'); RAISE EXCEPTION 'titulaire';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
END $$;
RESET ROLE;

-- Remise à zéro (038) d'un compte au provisoire expiré : permise, repart pour une durée complète (mot_de_passe_change_le = now())
INSERT INTO code_second_facteur (compte_id, session_id, objet, empreinte_code, canal, expire_le)
SELECT s.compte_id, s.id, 'action', repeat('c', 64), 'fichier', now() + interval '5 minutes' FROM session_distante s WHERE s.empreinte_jeton = repeat('6', 64);
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ DECLARE ida uuid; BEGIN
    SELECT compte_id INTO ida FROM cloud_ecran_debut_connexion('nouveau.a400');
    ASSERT (SELECT statut FROM cloud_ecran_remise_mot_de_passe(repeat('6', 64), repeat('c', 64), ida, 'Mot de passe provisoire expiré', 'nouveau.a400',
            '$2a$12$' || repeat('c', 53))) = 'ok', 'remise à zéro d''un provisoire expiré';
    ASSERT (SELECT statut FROM cloud_ecran_fin_connexion(ida, true, repeat('7', 64))) = 'code_attendu', 'nouveau provisoire valable';
    ASSERT (SELECT mot_de_passe_etat FROM cloud_ecran_comptes_liste(repeat('6', 64), 1, 100) WHERE identifiant = 'nouveau.a400') = 'provisoire', 'liste : de nouveau provisoire';
    -- Contact du compte tout juste remis à zéro (envoi de l'identifiant) ; aucun pour un autre compte
    ASSERT (SELECT identifiant || '|' || role || '|' || email FROM cloud_ecran_comptes_destinataire_remise(repeat('6', 64), ida))
           = 'nouveau.a400|titulaire|a400@exemple.invalid', 'destinataire de la remise';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_ecran_comptes_destinataire_remise(repeat('6', 64), (SELECT compte_id FROM cloud_ecran_debut_connexion('nouveau.b400')))),
           'aucun contact d''un compte non remis à zéro';
    BEGIN PERFORM cloud_ecran_comptes_destinataire_remise(repeat('4', 64), ida); RAISE EXCEPTION 'destinataire pour un titulaire';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
END $$;
RESET ROLE;
DO $$ BEGIN
    ASSERT (SELECT mot_de_passe_change_le > now() - interval '1 minute' AND mot_de_passe_provisoire FROM compte_distant WHERE identifiant = 'nouveau.a400'),
           'durée complète depuis la remise à zéro';
END $$;

-- Console : tout ou rien, sans code
DO $$ DECLARE m text := '$2a$12$' || repeat('b', 53); BEGIN
    ASSERT (SELECT string_agg(numero || ':' || statut, ',' ORDER BY numero) FROM cloud_compte_distant_creer_lot(jsonb_build_array(
            jsonb_build_object('ligne', 2, 'role', 'titulaire', 'identifiant', 'console.a400', 'nom', 'Console A', 'email', 'ca400@exemple.invalid', 'empreinte', m),
            jsonb_build_object('ligne', 3, 'role', 'titulaire', 'identifiant', 'nouveau.b400', 'nom', 'Pris', 'email', 'cb400@exemple.invalid', 'empreinte', m)), 'tests 040'))
           = '2:ok,3:existe', 'console : statuts';
    ASSERT NOT EXISTS (SELECT 1 FROM compte_distant WHERE identifiant = 'console.a400'), 'console : rien créé (tout ou rien)';
    ASSERT (SELECT count(*) FROM cloud_compte_distant_creer_lot(jsonb_build_array(
            jsonb_build_object('ligne', 2, 'role', 'titulaire', 'identifiant', 'console.a400', 'nom', 'Console A', 'email', 'ca400@exemple.invalid', 'empreinte', m),
            jsonb_build_object('ligne', 3, 'role', 'editeur', 'identifiant', 'console.b400', 'nom', 'Console B', 'telephone', '0555000402', 'empreinte', m)), 'tests 040')
            WHERE statut = 'ok' AND compte IS NOT NULL) = 2, 'console : deux comptes';
    ASSERT (SELECT cree_par FROM compte_distant WHERE identifiant = 'console.a400') = 'tests 040', 'console : auteur';
    BEGIN PERFORM cloud_compte_distant_creer_lot('[]'::jsonb, 'tests 040'); RAISE EXCEPTION 'console : vide accepté';
    EXCEPTION WHEN invalid_parameter_value THEN NULL; END;
    BEGIN PERFORM cloud_compte_distant_creer_lot(jsonb_build_array(jsonb_build_object('ligne', 2, 'role', 'titulaire', 'identifiant', 'console.c400',
              'nom', 'C', 'email', 'cc@exemple.invalid', 'empreinte', m)), ' '); RAISE EXCEPTION 'console : auteur vide accepté';
    EXCEPTION WHEN invalid_parameter_value THEN NULL; END;
END $$;

-- Inactivité (décision du 2026-11-21 : un utilisateur actif n'est jamais déconnecté) : défauts 15 / 30 min, bornes et code, sessions
-- ouvertes sans durée maximale (échéance glissante), fermées après l'inactivité réglée de leur rôle ; étapes intermédiaires inchangées
DO $$ BEGIN
    ASSERT (SELECT inactivite_editeur_minutes = 15 AND inactivite_titulaire_minutes = 30 FROM parametre_envoi_codes WHERE id = 1), 'défauts 15 / 30 min';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_inactivite_enregistrer(text, text, int, int)', 'EXECUTE'), 'inactivité : écrans';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_inactivite_lire(text)', 'EXECUTE'), 'inactivité : écrans';
    ASSERT NOT has_function_privilege('pharma_cloud_ecrans', 'cloud_inactivite_session(text)', 'EXECUTE'), 'inactivité interne';
    ASSERT NOT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_session_valable(text)', 'EXECUTE'), 'validation interne';
    ASSERT NOT has_function_privilege('public', 'cloud_ecran_inactivite_enregistrer(text, text, int, int)', 'EXECUTE'), 'pas pour PUBLIC';
END $$;
INSERT INTO code_second_facteur (compte_id, session_id, objet, empreinte_code, canal, expire_le)
SELECT s.compte_id, s.id, 'action', repeat('b', 64), 'fichier', now() + interval '5 minutes' FROM session_distante s WHERE s.empreinte_jeton = repeat('6', 64);
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ BEGIN
    BEGIN PERFORM cloud_ecran_inactivite_enregistrer(repeat('4', 64), repeat('f', 64), 20, 20); RAISE EXCEPTION 'inactivité par un titulaire';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    ASSERT (SELECT inactivite_editeur_minutes || '/' || inactivite_titulaire_minutes FROM cloud_ecran_inactivite_lire(repeat('6', 64))) = '15/30', 'lecture';
    ASSERT (SELECT statut FROM cloud_ecran_inactivite_enregistrer(repeat('6', 64), repeat('b', 64), 4, 30)) = 'editeur', 'éditeur < 5';
    ASSERT (SELECT statut FROM cloud_ecran_inactivite_enregistrer(repeat('6', 64), repeat('b', 64), 15, 61)) = 'titulaire', 'titulaire > 60';
    ASSERT (SELECT statut FROM cloud_ecran_inactivite_enregistrer(repeat('6', 64), repeat('b', 64), NULL, 30)) = 'editeur', 'éditeur absent';
    ASSERT (SELECT statut FROM cloud_ecran_inactivite_enregistrer(repeat('6', 64), repeat('0', 64), 10, 20)) = 'faux', 'code faux';
    ASSERT (SELECT statut FROM cloud_ecran_inactivite_enregistrer(repeat('6', 64), repeat('b', 64), 10, 20)) = 'ok', 'enregistré';
    ASSERT (SELECT inactivite_editeur_minutes || '/' || inactivite_titulaire_minutes FROM cloud_ecran_inactivite_lire(repeat('6', 64))) = '10/20', 'relu';
END $$;
RESET ROLE;
-- Sessions d'essai : éditeur actif il y a 9 min (gardée), éditeur inactif 11 min (fermée), titulaire inactif 19 min (gardée) et 21 min
-- (fermée), éditeur ouvert depuis 5 h mais actif (gardé : plus de durée maximale), étape code échue (fermée, motif expiree)
INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le, creee_le, derniere_activite_le)
SELECT repeat(x.j, 32), c.id, x.etape, now() + x.reste, now() - x.age, now() - x.inactif
FROM (VALUES ('editeur.e400', 'c1', 'ouverte', interval '1 minute', interval '9 minutes', interval '9 minutes'),
             ('editeur.e400', 'c2', 'ouverte', interval '1 hour', interval '11 minutes', interval '11 minutes'),
             ('titulaire.t400', 'c3', 'ouverte', interval '1 minute', interval '19 minutes', interval '19 minutes'),
             ('titulaire.t400', 'c4', 'ouverte', interval '7 hours', interval '21 minutes', interval '21 minutes'),
             ('editeur.e400', 'c5', 'ouverte', interval '-3 hours', interval '5 hours', interval '1 minute'),
             ('editeur.e400', 'c6', 'code', interval '-1 second', interval '11 minutes', interval '1 minute')) x(ident, j, etape, reste, age, inactif)
JOIN compte_distant c ON c.identifiant = x.ident;
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ BEGIN
    ASSERT EXISTS (SELECT 1 FROM cloud_ecran_session(repeat('c1', 32))), 'éditeur actif il y a 9 min (10 réglées) : gardé';
    ASSERT (SELECT expire_le BETWEEN now() + interval '9 minutes' AND now() + interval '11 minutes' FROM cloud_ecran_session(repeat('c1', 32))),
           'échéance glissante : maintenant + 10 min';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_ecran_session(repeat('c2', 32))), 'éditeur inactif 11 min : fermé (échéance pas encore passée)';
    ASSERT EXISTS (SELECT 1 FROM cloud_ecran_session(repeat('c3', 32))), 'titulaire inactif 19 min (20 réglées) : gardé';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_ecran_session(repeat('c4', 32))), 'titulaire inactif 21 min : fermé';
    ASSERT EXISTS (SELECT 1 FROM cloud_ecran_session(repeat('c5', 32))), 'éditeur actif depuis 5 h : jamais déconnecté';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_ecran_session(repeat('c6', 32))), 'étape code échue : fermée';
END $$;
RESET ROLE;
DO $$ BEGIN
    ASSERT (SELECT string_agg(right(empreinte_jeton, 2) || ':' || coalesce(motif_fin, '-'), ',' ORDER BY empreinte_jeton) FROM session_distante
            WHERE empreinte_jeton IN (repeat('c1', 32), repeat('c2', 32), repeat('c3', 32), repeat('c4', 32), repeat('c5', 32), repeat('c6', 32)))
           = 'c1:-,c2:inactivite,c3:-,c4:inactivite,c5:-,c6:expiree', 'motifs de fin';
    ASSERT (SELECT count(*) FROM journal_acces_distant WHERE evenement = 'session_expiree' AND objet = 'inactivite') = 2, 'inactivité journalisée';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'inactivite_modifiee' AND detail LIKE '%éditeur 10 min, titulaire 20 min (avant : 15 / 30)%'),
           'réglage au journal de sécurité';
    BEGIN UPDATE session_distante SET motif_fin = 'autre' WHERE empreinte_jeton = repeat('c1', 32); RAISE EXCEPTION 'motif hors liste accepté';
    EXCEPTION WHEN check_violation THEN NULL; END;
END $$;

-- Journaux : jamais l'identifiant, le nom ni le contact ; chaîne intacte
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM journal_acces_distant WHERE evenement = 'compte_cree' AND objet LIKE 'import:%') = 2, 'journal : deux comptes importés';
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'comptes_importes' AND resultat = 'ok' AND objet = 'comptes:2'), 'journal de l''éditeur';
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'connexion' AND resultat = 'refus' AND objet = 'provisoire_expire'), 'expiration notée';
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'identifiant_envoi' AND objet = 'email:ok'), 'envoi noté';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'comptes_importes' AND detail LIKE '%2 compte(s)%'), 'journal de sécurité (écran)';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'comptes_importes' AND detail LIKE 'console (tests 040)%'), 'journal de sécurité (console)';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'duree_provisoire_modifiee' AND detail LIKE '%3 jour(s) (avant : 7)%'), 'durée au journal';
    ASSERT NOT EXISTS (SELECT 1 FROM journal_securite WHERE evenement IN ('comptes_importes', 'duree_provisoire_modifiee')
                       AND (detail LIKE '%@%' OR detail LIKE '%nouveau%' OR detail LIKE '%console.a%' OR detail LIKE '%0555%' OR detail LIKE '%$2a$%')),
           'jamais l''identifiant, le nom, le contact ni l''empreinte au journal de sécurité';
    ASSERT NOT EXISTS (SELECT 1 FROM journal_acces_distant WHERE objet LIKE '%@%' OR objet LIKE '%nouveau%' OR objet LIKE '%0555%'), 'ni au journal des accès';
    ASSERT (SELECT premiere_rupture FROM cloud_ecran_verifier_journal_acces()) IS NULL, 'chaîne intacte';
END $$;

ROLLBACK;
\echo '040 : tous réussis'
