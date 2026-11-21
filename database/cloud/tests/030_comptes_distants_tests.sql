-- =====================================================================
-- Tests du script 030 (comptes distants des écrans), en transaction ANNULÉE :
--   psql -U <propriétaire> -d <base centrale de test> -v ON_ERROR_STOP=1 -f database/cloud/tests/030_comptes_distants_tests.sql
-- Rôle sans connexion ni droit sur les tables ; rien sans session ; verrouillage (5 échecs, durée doublée) ; second facteur
-- (code faux compté, 5 essais, expiré, usage unique, nouveau jeton) ; mot de passe provisoire ; expiration et révocation des
-- sessions ; journal des accès en ajout seul, chaîné, sans texte libre ; aucune fonction SECURITY DEFINER pour PUBLIC.
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

-- Rôle : sans connexion, ni superutilisateur ni BYPASSRLS, propriétaire de rien, AUCUN droit sur aucune table
DO $$ BEGIN
    ASSERT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pharma_cloud_ecrans' AND NOT rolcanlogin AND NOT rolsuper AND NOT rolbypassrls),
        'rôle pharma_cloud_ecrans sans connexion, ni superutilisateur ni BYPASSRLS';
    ASSERT NOT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'public' AND tableowner = 'pharma_cloud_ecrans'), 'propriétaire d''aucune table';
    ASSERT NOT EXISTS (SELECT 1 FROM pg_class c WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r', 'p', 'v', 'm')
        AND (has_table_privilege('pharma_cloud_ecrans', c.oid, 'SELECT, INSERT, UPDATE, DELETE, TRUNCATE')
             OR has_any_column_privilege('pharma_cloud_ecrans', c.oid, 'SELECT, INSERT, UPDATE'))), 'aucun droit direct sur une table';
    -- Seules les fonctions cloud_ecran_* sont exécutables par les écrans (hors fonctions ordinaires du système)
    ASSERT NOT EXISTS (SELECT 1 FROM pg_proc p WHERE p.pronamespace = 'public'::regnamespace AND p.prosecdef AND p.prorettype <> 'trigger'::regtype
        AND has_function_privilege('pharma_cloud_ecrans', p.oid, 'EXECUTE') AND p.proname NOT LIKE 'cloud\_ecran\_%'), 'fonction hors cloud_ecran_*';
    ASSERT NOT EXISTS (SELECT 1 FROM pg_proc p WHERE p.pronamespace = 'public'::regnamespace AND p.prokind = 'f' AND p.prosecdef
        AND p.prorettype <> 'trigger'::regtype AND has_function_privilege('public', p.oid, 'EXECUTE')), 'fonction SECURITY DEFINER exécutable par PUBLIC';
    ASSERT NOT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_journaliser(uuid, bigint, uuid, text, text, text)', 'EXECUTE'), 'journaliser réservé';
    ASSERT NOT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_controler_code(bigint, text, text)', 'EXECUTE'), 'contrôle du code réservé';
    ASSERT NOT has_function_privilege('pharma_cloud_ecrans', 'cloud_compte_distant_creer(text, text, text, text, text, text, text)', 'EXECUTE'), 'création de compte réservée';
    ASSERT NOT has_function_privilege('pharma_cloud_api', 'cloud_ecran_session(text)', 'EXECUTE'), 'API : pas les fonctions des écrans';
    ASSERT NOT has_function_privilege('pharma_cloud_etat', 'cloud_ecran_session(text)', 'EXECUTE'), 'page d''état : pas les fonctions des écrans';
END $$;

-- Compte d'essai (empreinte BCrypt factice de forme valable : la vérification du mot de passe est faite par l'application)
SELECT cloud_compte_distant_creer('essai.031', 'Essai 031', 'editeur', '$2a$12$' || repeat('a', 53), NULL, 'essai031@exemple.invalid', 'tests 031') \g /dev/null
DO $$ BEGIN
    BEGIN
        PERFORM cloud_compte_distant_creer('x', 'Trop court', 'editeur', '$2a$12$' || repeat('a', 53), NULL, 'x@exemple.invalid', 'tests 031');
        RAISE EXCEPTION 'identifiant trop court accepté';
    EXCEPTION WHEN check_violation THEN NULL; END;
    BEGIN
        PERFORM cloud_compte_distant_creer('essai.031b', 'Sans contact', 'editeur', '$2a$12$' || repeat('a', 53), NULL, NULL, 'tests 031');
        RAISE EXCEPTION 'compte sans téléphone ni e-mail accepté';
    EXCEPTION WHEN check_violation THEN NULL; END;
    BEGIN
        PERFORM cloud_compte_distant_creer('essai.031c', 'Coût 10', 'editeur', '$2a$10$' || repeat('a', 53), NULL, 'c@exemple.invalid', 'tests 031');
        RAISE EXCEPTION 'empreinte BCrypt de coût 10 acceptée';
    EXCEPTION WHEN check_violation THEN NULL; END;
END $$;

SET LOCAL ROLE pharma_cloud_ecrans;
-- Rien sans session ; aucune lecture directe
DO $$ BEGIN
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_ecran_session(repeat('0', 64))), 'session inconnue : rien';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_ecran_session('pas un jeton')), 'jeton mal formé : rien';
    ASSERT (SELECT statut FROM cloud_ecran_creer_code(repeat('0', 64), 'connexion', repeat('c', 64), 'fichier')) = 'session', 'code sans session';
    BEGIN PERFORM cloud_ecran_ouvrir(repeat('0', 64), gen_random_uuid()); RAISE EXCEPTION 'ouverture sans session acceptée';
    EXCEPTION WHEN invalid_authorization_specification THEN NULL; END;
    BEGIN PERFORM 1 FROM compte_distant; RAISE EXCEPTION 'lecture directe de compte_distant';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM 1 FROM journal_acces_distant; RAISE EXCEPTION 'lecture directe du journal';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM 1 FROM officine; RAISE EXCEPTION 'lecture directe de officine';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM cloud_ecran_journaliser(NULL, NULL, NULL, 'essai', 'ok', NULL); RAISE EXCEPTION 'journaliser accepté';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
END $$;

-- Identifiant inconnu et mot de passe faux : même statut
DO $$
DECLARE c uuid;
BEGIN
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_ecran_debut_connexion('inconnu.031')), 'identifiant inconnu : aucune ligne';
    ASSERT (SELECT statut FROM cloud_ecran_fin_connexion(NULL, false, NULL)) = 'refuse', 'inconnu : refuse';
    SELECT compte_id INTO c FROM cloud_ecran_debut_connexion(' ESSAI.031 ');
    ASSERT c IS NOT NULL, 'identifiant retrouvé (casse et espaces ignorés)';
    -- 4 échecs : refusés ; le 5e verrouille 15 min ; ensuite plus d'empreinte rendue
    FOR i IN 1..4 LOOP
        ASSERT (SELECT statut FROM cloud_ecran_fin_connexion(c, false, NULL)) = 'refuse', 'échec ' || i;
    END LOOP;
    ASSERT (SELECT statut FROM cloud_ecran_fin_connexion(c, false, NULL)) = 'verrouille', '5e échec : verrouillé';
    ASSERT (SELECT empreinte_mot_de_passe FROM cloud_ecran_debut_connexion('essai.031')) IS NULL, 'verrouillé : pas d''empreinte';
    ASSERT (SELECT statut FROM cloud_ecran_fin_connexion(c, true, repeat('1', 64))) = 'verrouille', 'verrouillé : même un bon mot de passe est refusé';
END $$;
RESET ROLE;
DO $$ BEGIN
    ASSERT (SELECT verrouille_jusqu_au BETWEEN now() + interval '14 minutes' AND now() + interval '16 minutes' FROM compte_distant WHERE identifiant = 'essai.031'),
        '1er verrouillage : 15 min';
    -- 2e verrouillage : 30 min (durée doublée)
    UPDATE compte_distant SET verrouille_jusqu_au = now() - interval '1 second' WHERE identifiant = 'essai.031';
END $$;
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$
DECLARE c uuid := (SELECT compte_id FROM cloud_ecran_debut_connexion('essai.031'));
BEGIN
    FOR i IN 1..4 LOOP PERFORM cloud_ecran_fin_connexion(c, false, NULL); END LOOP;
    ASSERT (SELECT verrouille_jusqu_au BETWEEN now() + interval '29 minutes' AND now() + interval '31 minutes'
            FROM cloud_ecran_fin_connexion(c, false, NULL)), '2e verrouillage : 30 min';
END $$;
RESET ROLE;
SELECT cloud_compte_distant_deverrouiller('essai.031') \g /dev/null

-- Connexion complète : mot de passe vérifié → session « code » → code (faux compté, puis bon) → nouveau jeton → mot de passe provisoire
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$
DECLARE c uuid := (SELECT compte_id FROM cloud_ecran_debut_connexion('essai.031'));
        j1 text := repeat('1', 64); j2 text := repeat('2', 64); bon text := repeat('b', 64);
BEGIN
    ASSERT (SELECT statut FROM cloud_ecran_fin_connexion(c, true, j1)) = 'code_attendu', 'mot de passe vérifié : code attendu';
    ASSERT (SELECT etape FROM cloud_ecran_session(j1)) = 'code', 'étape code';
    BEGIN PERFORM cloud_ecran_ouvrir(j1, gen_random_uuid()); RAISE EXCEPTION 'ouverture à l''étape code acceptée';
    EXCEPTION WHEN invalid_authorization_specification THEN NULL; END;
    ASSERT (SELECT statut FROM cloud_ecran_creer_code(j1, 'action', bon, 'fichier')) = 'session', 'code d''action refusé avant la fin de la connexion';
    ASSERT (SELECT statut FROM cloud_ecran_creer_code(j1, 'connexion', bon, 'sms')) = 'sans_contact', 'SMS sans téléphone';
    ASSERT (SELECT statut FROM cloud_ecran_creer_code(j1, 'connexion', bon, 'fichier')) = 'envoye', 'code créé';
    ASSERT (SELECT identifiant FROM cloud_ecran_creer_code(j1, 'connexion', bon, 'fichier')) = 'essai.031', 'second code : le premier est annulé';
    ASSERT (SELECT statut FROM cloud_ecran_valider_code_connexion(j1, repeat('f', 64), j2)) = 'faux', 'code faux';
    ASSERT (SELECT statut FROM cloud_ecran_valider_code_connexion(j1, bon, j2)) = 'ok', 'bon code';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_ecran_session(j1)), 'ancien jeton : plus rien (pas de fixation)';
    ASSERT (SELECT etape FROM cloud_ecran_session(j2)) = 'mot_de_passe', 'mot de passe provisoire : à changer';
    ASSERT (SELECT statut FROM cloud_ecran_valider_code_connexion(j2, bon, repeat('3', 64))) = 'session', 'code réutilisé : refusé';
    BEGIN PERFORM cloud_ecran_mon_journal(j2, 1, 20); RAISE EXCEPTION 'journal lu avant le changement du mot de passe';
    EXCEPTION WHEN invalid_authorization_specification OR undefined_function THEN NULL; END;
    BEGIN PERFORM cloud_ecran_changer_mot_de_passe(j2, 'clair'); RAISE EXCEPTION 'mot de passe en clair accepté';
    EXCEPTION WHEN invalid_parameter_value THEN NULL; END;
    ASSERT (SELECT etape FROM cloud_ecran_changer_mot_de_passe(j2, '$2b$12$' || repeat('z', 53))) = 'ouverte', 'mot de passe changé : session ouverte';
    ASSERT (SELECT NOT mot_de_passe_provisoire AND role = 'editeur' AND expire_le BETWEEN now() + interval '119 minutes' AND now() + interval '121 minutes'
            FROM cloud_ecran_session(j2)), 'éditeur : 2 h';
END $$;

-- Second facteur : 5 essais au plus, code expiré refusé
DO $$
DECLARE c uuid := (SELECT compte_id FROM cloud_ecran_debut_connexion('essai.031')); j text := repeat('4', 64);
BEGIN
    PERFORM cloud_ecran_fin_connexion(c, true, j);
    PERFORM cloud_ecran_creer_code(j, 'connexion', repeat('b', 64), 'email');
    FOR i IN 1..4 LOOP ASSERT (SELECT statut FROM cloud_ecran_valider_code_connexion(j, repeat('f', 64), repeat('5', 64))) = 'faux', 'faux ' || i; END LOOP;
    ASSERT (SELECT statut FROM cloud_ecran_valider_code_connexion(j, repeat('f', 64), repeat('5', 64))) = 'epuise', '5e essai faux : épuisé';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_ecran_session(j)), 'codes épuisés : session fermée';
END $$;
RESET ROLE;
-- Limite : 5 codes par quart d'heure et par compte (4 déjà créés ci-dessus : 2 + 1, le suivant passe, le sixième est refusé)
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$
DECLARE c uuid := (SELECT compte_id FROM cloud_ecran_debut_connexion('essai.031')); j text := repeat('6', 64);
BEGIN
    PERFORM cloud_ecran_fin_connexion(c, true, j);
    ASSERT (SELECT statut FROM cloud_ecran_creer_code(j, 'connexion', repeat('b', 64), 'fichier')) = 'envoye', '4e code';
    ASSERT (SELECT statut FROM cloud_ecran_creer_code(j, 'connexion', repeat('b', 64), 'fichier')) = 'envoye', '5e code';
    ASSERT (SELECT statut FROM cloud_ecran_creer_code(j, 'connexion', repeat('b', 64), 'fichier')) = 'trop_de_codes', '6e code : refusé';
END $$;
RESET ROLE;
-- Code expiré
UPDATE code_second_facteur SET cree_le = now() - interval '20 minutes', expire_le = now() - interval '1 second'
WHERE session_id = (SELECT id FROM session_distante WHERE empreinte_jeton = repeat('6', 64));
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ BEGIN
    ASSERT (SELECT statut FROM cloud_ecran_valider_code_connexion(repeat('6', 64), repeat('b', 64), repeat('7', 64))) = 'expire', 'code expiré refusé';
END $$;

-- Session expirée, déconnexion, compte désactivé
DO $$ BEGIN
    PERFORM cloud_ecran_deconnexion(repeat('2', 64));
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_ecran_session(repeat('2', 64))), 'déconnexion : jeton sans valeur';
END $$;
RESET ROLE;
INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le)
SELECT repeat('8', 64), id, 'ouverte', now() - interval '1 second' FROM compte_distant WHERE identifiant = 'essai.031';
INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le)
SELECT repeat('9', 64), id, 'ouverte', now() + interval '1 hour' FROM compte_distant WHERE identifiant = 'essai.031';
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ BEGIN
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_ecran_session(repeat('8', 64))), 'session échue : rien';
    ASSERT EXISTS (SELECT 1 FROM cloud_ecran_session(repeat('9', 64))), 'session valable';
END $$;
RESET ROLE;
DO $$ BEGIN
    ASSERT (SELECT motif_fin FROM session_distante WHERE empreinte_jeton = repeat('8', 64)) = 'expiree', 'session échue fermée';
    ASSERT cloud_compte_distant_desactiver('essai.031') >= 1, 'désactivation : sessions fermées';
END $$;
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ BEGIN
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_ecran_session(repeat('9', 64))), 'compte désactivé : session refusée';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_ecran_debut_connexion('essai.031')), 'compte désactivé : connexion impossible';
END $$;
RESET ROLE;

-- Titulaire : 30 min d'inactivité
SELECT cloud_compte_distant_creer('titulaire.031', 'Titulaire 031', 'titulaire', '$2a$12$' || repeat('a', 53), '+213555000031', NULL, 'tests 031') \g /dev/null
INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le, derniere_activite_le)
SELECT repeat('a1', 32), id, 'ouverte', now() + interval '7 hours', now() - interval '31 minutes' FROM compte_distant WHERE identifiant = 'titulaire.031';
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ BEGIN
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_ecran_session(repeat('a1', 32))), 'titulaire inactif 30 min : session fermée';
END $$;
RESET ROLE;

-- Journal des accès : ajout seul, chaîné, sans texte libre ni identifiant
DO $$
DECLARE v record;
BEGIN
    ASSERT (SELECT count(*) FROM journal_acces_distant WHERE compte_id = (SELECT id FROM compte_distant WHERE identifiant = 'essai.031')) >= 10, 'accès journalisés';
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'connexion' AND objet = 'identifiant_inconnu' AND compte_id IS NULL), 'inconnu journalisé sans identifiant';
    ASSERT NOT EXISTS (SELECT 1 FROM journal_acces_distant WHERE objet LIKE '%essai%' OR objet LIKE '%@%'), 'aucun identifiant ni e-mail au journal';
    SELECT * INTO v FROM cloud_ecran_verifier_journal_acces();
    ASSERT v.lignes > 0 AND v.premiere_rupture IS NULL, 'chaîne intacte';
    BEGIN UPDATE journal_acces_distant SET objet = 'modifie' WHERE id = (SELECT max(id) FROM journal_acces_distant); RAISE EXCEPTION 'journal modifiable';
    EXCEPTION WHEN raise_exception THEN IF SQLERRM NOT LIKE '%ajout seul%' THEN RAISE; END IF; END;
    BEGIN DELETE FROM journal_acces_distant; RAISE EXCEPTION 'journal effaçable';
    EXCEPTION WHEN raise_exception THEN IF SQLERRM NOT LIKE '%ajout seul%' THEN RAISE; END IF; END;
    BEGIN TRUNCATE journal_acces_distant; RAISE EXCEPTION 'journal vidable';
    EXCEPTION WHEN raise_exception THEN IF SQLERRM NOT LIKE '%ajout seul%' THEN RAISE; END IF; END;
    BEGIN INSERT INTO journal_acces_distant (evenement, resultat, objet, empreinte) VALUES ('essai', 'ok', 'Texte libre !', repeat('0', 64));
        RAISE EXCEPTION 'texte libre accepté';
    EXCEPTION WHEN check_violation THEN NULL; END;
END $$;
-- Rupture détectée : ligne ajoutée hors fonction (propriétaire), sans le bon chaînage
INSERT INTO journal_acces_distant (evenement, resultat, empreinte_precedente, empreinte) VALUES ('essai_rupture', 'ok', repeat('0', 64), repeat('0', 64));
DO $$ BEGIN
    ASSERT (SELECT premiere_rupture FROM cloud_ecran_verifier_journal_acces()) = (SELECT max(id) FROM journal_acces_distant), 'rupture détectée';
END $$;

ROLLBACK;
\echo '030 : tous réussis'
