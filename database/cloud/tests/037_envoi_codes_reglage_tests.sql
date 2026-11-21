-- =====================================================================
-- Tests du script 037 (envoi des codes réglé dans l'administration du site), en transaction ANNULÉE :
--   psql -U <propriétaire> -d <base centrale de test> -v ON_ERROR_STOP=1 -f database/cloud/tests/037_envoi_codes_reglage_tests.sql
-- Droits (écrans seulement, rien pour PUBLIC, aucune table) ; réglage unique ; lecture par l'éditeur SANS le mot de passe ;
-- titulaire refusé ; enregistrement : contrôles avant le code, SMTP complet exigé pour courriel et les_deux, mot de passe
-- remplacé seulement s'il est saisi, jamais au journal ; canal « les_deux » d'un code ; contact d'un compte (e-mail ET téléphone,
-- au moins un, vide = inchangé, « - » = retiré) ; comptes existants intacts ; journal des accès chaîné.
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

-- Droits et structure
DO $$ BEGIN
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_facteur_reglage_lire(text)', 'EXECUTE'), 'lecture : écrans';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_facteur_reglage_enregistrer(text, text, text, text, int, text, text, text, text)', 'EXECUTE'), 'écriture : écrans';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_facteur_demander_code(text, text, text, text)', 'EXECUTE'), 'code : écrans';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_facteur_compte_contact(text, text, uuid, text, text)', 'EXECUTE'), 'contact : écrans';
    ASSERT NOT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_facteur_reglage_complet(parametre_envoi_codes)', 'EXECUTE'), 'interne : jamais aux écrans';
    ASSERT NOT EXISTS (SELECT 1 FROM pg_proc p WHERE p.pronamespace = 'public'::regnamespace AND p.proname LIKE 'cloud\_ecran\_facteur\_%'
                       AND has_function_privilege('public', p.oid, 'EXECUTE')), 'rien pour PUBLIC';
    ASSERT NOT has_function_privilege('pharma_cloud_api', 'cloud_ecran_facteur_reglage_envoi(text)', 'EXECUTE'), 'API : rien';
    ASSERT NOT has_function_privilege('pharma_cloud_etat', 'cloud_ecran_facteur_reglage_lire(text)', 'EXECUTE'), 'page d''état : rien';
    ASSERT NOT has_table_privilege('pharma_cloud_ecrans', 'parametre_envoi_codes', 'SELECT, INSERT, UPDATE, DELETE'), 'aucun droit direct';
    ASSERT NOT has_any_column_privilege('pharma_cloud_ecrans', 'parametre_envoi_codes', 'SELECT'), 'aucun droit par colonne';
    ASSERT (SELECT count(*) FROM parametre_envoi_codes) = 1, 'une seule ligne de réglage';
    ASSERT (SELECT count(*) FROM pg_proc p WHERE p.pronamespace = 'public'::regnamespace AND p.proname LIKE 'cloud\_ecran\_facteur\_%'
            AND has_function_privilege('pharma_cloud_ecrans', p.oid, 'EXECUTE')) = 7, 'fonctions des écrans du 037 : 7';
    -- La lecture de l'éditeur ne rend aucune colonne de mot de passe
    ASSERT NOT EXISTS (SELECT 1 FROM pg_proc p, unnest(p.proargnames) WITH ORDINALITY AS a(nom, n)
                       WHERE p.oid = 'cloud_ecran_facteur_reglage_lire(text)'::regprocedure AND p.proargmodes[a.n] IN ('o', 't')
                         AND a.nom LIKE '%mot\_de\_passe%' AND a.nom <> 'mot_de_passe_renseigne'), 'lecture : aucune colonne de mot de passe';
    ASSERT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_compte_distant_contact'), 'au moins un contact';
    ASSERT NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'code_second_facteur_canal_check'), 'ancienne contrainte du canal retirée';
    BEGIN
        INSERT INTO parametre_envoi_codes (id) VALUES (2);
        RAISE EXCEPTION 'deuxième ligne de réglage acceptée';
    EXCEPTION WHEN check_violation THEN NULL; END;
    BEGIN
        UPDATE parametre_envoi_codes SET smtp_mot_de_passe_chiffre = 'mot de passe en clair' WHERE id = 1;
        RAISE EXCEPTION 'mot de passe en clair accepté';
    EXCEPTION WHEN check_violation THEN NULL; END;
END $$;

-- Données d'essai : un éditeur (e-mail seul), un titulaire (téléphone seul), un compte avec les deux
SELECT cloud_compte_distant_creer('editeur.370', 'Éditeur 0370', 'editeur', '$2a$12$' || repeat('a', 53), NULL, 'e370@exemple.invalid', 'tests 037') \g /dev/null
SELECT cloud_compte_distant_creer('titulaire.370', 'Titulaire 0370', 'titulaire', '$2a$12$' || repeat('a', 53), '+213555000370', NULL, 'tests 037') \g /dev/null
SELECT cloud_compte_distant_creer('deux.370', 'Deux 0370', 'titulaire', '$2a$12$' || repeat('a', 53), '0555000371', 'd370@exemple.invalid', 'tests 037') \g /dev/null
UPDATE compte_distant SET mot_de_passe_provisoire = false WHERE identifiant IN ('editeur.370', 'titulaire.370', 'deux.370');
INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le)
SELECT repeat(x.j, 64), c.id, 'ouverte', now() + interval '2 hours'
FROM (VALUES ('editeur.370', 'a'), ('titulaire.370', 'b'), ('deux.370', 'c')) x(ident, j) JOIN compte_distant c ON c.identifiant = x.ident;
INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le)
SELECT repeat('d', 64), c.id, 'code', now() + interval '10 minutes' FROM compte_distant c WHERE c.identifiant = 'deux.370';

-- Lecture : éditeur seulement ; titulaire et jeton inconnu refusés
DO $$ DECLARE r record; BEGIN
    SELECT * INTO r FROM cloud_ecran_facteur_reglage_lire(repeat('a', 64));
    ASSERT r.canal IS NOT NULL AND NOT r.mot_de_passe_renseigne IS NULL, 'réglage lu';
    BEGIN
        PERFORM cloud_ecran_facteur_reglage_lire(repeat('b', 64));
        RAISE EXCEPTION 'titulaire : lecture acceptée';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN
        PERFORM cloud_ecran_facteur_reglage_lire(repeat('0', 64));
        RAISE EXCEPTION 'sans session : lecture acceptée';
    EXCEPTION WHEN invalid_authorization_specification THEN NULL; END;
    BEGIN
        PERFORM cloud_ecran_facteur_comptes(repeat('b', 64), 1, 20);
        RAISE EXCEPTION 'titulaire : comptes lus';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
END $$;

-- Enregistrement : contrôles AVANT le code (aucun code consommé), SMTP complet exigé, puis code
UPDATE parametre_envoi_codes SET canal = 'sms', smtp_serveur = NULL, smtp_port = NULL, smtp_identifiant = NULL, smtp_expediteur = NULL,
       smtp_mot_de_passe_chiffre = NULL WHERE id = 1;
INSERT INTO code_second_facteur (compte_id, session_id, objet, empreinte_code, canal, expire_le)
SELECT s.compte_id, s.id, 'action', repeat('1', 64), 'email', now() + interval '5 minutes' FROM session_distante s WHERE s.empreinte_jeton = repeat('a', 64);
DO $$ DECLARE j text := repeat('a', 64); k text := repeat('1', 64); m text := 'CfDJ8' || repeat('Ab-_', 20); BEGIN
    ASSERT (SELECT statut FROM cloud_ecran_facteur_reglage_enregistrer(j, k, 'pigeon', NULL, NULL, 'starttls', NULL, NULL, NULL)) = 'canal', 'canal hors liste';
    ASSERT (SELECT statut FROM cloud_ecran_facteur_reglage_enregistrer(j, k, 'courriel', 'smtp gmail', 587, 'starttls', NULL, NULL, NULL)) = 'serveur', 'serveur invalide';
    ASSERT (SELECT statut FROM cloud_ecran_facteur_reglage_enregistrer(j, k, 'courriel', 'smtp.gmail.com', 70000, 'starttls', NULL, NULL, NULL)) = 'port', 'port invalide';
    ASSERT (SELECT statut FROM cloud_ecran_facteur_reglage_enregistrer(j, k, 'courriel', 'smtp.gmail.com', 587, 'aucun', NULL, NULL, NULL)) = 'tls', 'jamais sans TLS';
    ASSERT (SELECT statut FROM cloud_ecran_facteur_reglage_enregistrer(j, k, 'courriel', 'smtp.gmail.com', 587, 'starttls', 'a b', NULL, NULL)) = 'identifiant', 'identifiant invalide';
    ASSERT (SELECT statut FROM cloud_ecran_facteur_reglage_enregistrer(j, k, 'courriel', 'smtp.gmail.com', 587, 'starttls', 'x@exemple.invalid', 'pas-une-adresse', NULL)) = 'expediteur', 'expéditeur invalide';
    -- Courriel sans mot de passe (ni saisi ni déjà renseigné) : incomplet, rien n'est enregistré
    ASSERT (SELECT statut FROM cloud_ecran_facteur_reglage_enregistrer(j, k, 'courriel', 'smtp.gmail.com', 587, 'starttls', 'x@exemple.invalid', 'x@exemple.invalid', NULL)) = 'incomplet', 'incomplet';
    ASSERT (SELECT canal FROM parametre_envoi_codes) = 'sms', 'rien enregistré';
    ASSERT (SELECT essais FROM code_second_facteur WHERE empreinte_code = k) = 0, 'aucun code consommé par les contrôles';
    BEGIN
        PERFORM cloud_ecran_facteur_reglage_enregistrer(j, k, 'courriel', 'smtp.gmail.com', 587, 'starttls', 'x@exemple.invalid', 'x@exemple.invalid', 'mot de passe en clair');
        RAISE EXCEPTION 'mot de passe en clair accepté';
    EXCEPTION WHEN invalid_parameter_value THEN NULL; END;
    BEGIN
        PERFORM cloud_ecran_facteur_reglage_enregistrer(repeat('b', 64), k, 'courriel', 'smtp.gmail.com', 587, 'starttls', 'x@exemple.invalid', 'x@exemple.invalid', m);
        RAISE EXCEPTION 'titulaire : écriture acceptée';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    -- Code faux : compté, rien enregistré
    ASSERT (SELECT statut FROM cloud_ecran_facteur_reglage_enregistrer(j, repeat('2', 64), 'courriel', 'smtp.gmail.com', 587, 'starttls', 'x@exemple.invalid', 'x@exemple.invalid', m)) = 'faux', 'code faux';
    ASSERT (SELECT smtp_mot_de_passe_chiffre IS NULL FROM parametre_envoi_codes), 'rien enregistré après un code faux';
    -- Bon code : enregistré
    ASSERT (SELECT statut FROM cloud_ecran_facteur_reglage_enregistrer(j, k, 'courriel', 'smtp.gmail.com', 587, 'starttls', 'x@exemple.invalid', 'x@exemple.invalid', m)) = 'ok', 'enregistré';
    ASSERT (SELECT smtp_mot_de_passe_chiffre = m AND modifie_le IS NOT NULL AND modifie_par IS NOT NULL FROM parametre_envoi_codes), 'chiffré gardé tel quel';
    ASSERT (SELECT mot_de_passe_renseigne AND complet FROM cloud_ecran_facteur_reglage_lire(j)), 'lecture : renseigné, complet';
    -- Code usé : un deuxième enregistrement exige un nouveau code
    ASSERT (SELECT statut FROM cloud_ecran_facteur_reglage_enregistrer(j, k, 'les_deux', 'smtp.gmail.com', 587, 'starttls', 'x@exemple.invalid', 'x@exemple.invalid', NULL)) = 'aucun', 'usage unique';
END $$;

-- Champ vide = mot de passe inchangé ; jamais au journal
INSERT INTO code_second_facteur (compte_id, session_id, objet, empreinte_code, canal, expire_le)
SELECT s.compte_id, s.id, 'action', repeat('3', 64), 'email', now() + interval '5 minutes' FROM session_distante s WHERE s.empreinte_jeton = repeat('a', 64);
DO $$ DECLARE m text := 'CfDJ8' || repeat('Ab-_', 20); BEGIN
    ASSERT (SELECT statut FROM cloud_ecran_facteur_reglage_enregistrer(repeat('a', 64), repeat('3', 64), 'les_deux', 'smtp.gmail.com', 465, 'ssl',
            'x@exemple.invalid', 'x@exemple.invalid', '')) = 'ok', 'enregistré sans mot de passe saisi';
    ASSERT (SELECT smtp_mot_de_passe_chiffre = m AND canal = 'les_deux' AND smtp_port = 465 AND smtp_tls = 'ssl' FROM parametre_envoi_codes), 'mot de passe inchangé';
    ASSERT (SELECT smtp_mot_de_passe_chiffre FROM cloud_ecran_facteur_reglage_envoi(repeat('b', 64))) = m, 'envoi : chiffré rendu à une session valable';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_ecran_facteur_reglage_envoi(repeat('0', 64))), 'envoi : rien sans session';
    ASSERT NOT EXISTS (SELECT 1 FROM journal_securite WHERE detail LIKE '%' || m || '%' OR detail LIKE '%x@exemple.invalid%'), 'journal de sécurité sans secret ni adresse';
    ASSERT NOT EXISTS (SELECT 1 FROM journal_acces_distant WHERE objet LIKE '%Ab-_%' OR objet LIKE '%@%'), 'journal des accès sans secret';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'envoi_codes_modifie' AND detail LIKE '%mot de passe inchangé%'), 'inchangé noté';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'envoi_codes_modifie' AND detail LIKE '%mot de passe remplacé%'), 'remplacé noté';
END $$;

-- Code « les_deux » : contact exigé (au moins un) ; e-mail seul exigé pour « email »
DO $$ DECLARE r record; BEGIN
    SELECT * INTO r FROM cloud_ecran_facteur_demander_code(repeat('d', 64), 'connexion', repeat('4', 64), 'les_deux');
    ASSERT r.statut = 'envoye' AND r.email = 'd370@exemple.invalid' AND r.telephone = '0555000371', 'les deux : e-mail et téléphone rendus pour l''envoi';
    ASSERT (SELECT canal FROM code_second_facteur WHERE empreinte_code = repeat('4', 64)) = 'les_deux', 'canal noté';
    ASSERT (SELECT statut FROM cloud_ecran_facteur_demander_code(repeat('b', 64), 'action', repeat('5', 64), 'email')) = 'sans_contact', 'titulaire sans e-mail : refusé';
    ASSERT (SELECT statut FROM cloud_ecran_facteur_demander_code(repeat('b', 64), 'action', repeat('5', 64), 'les_deux')) = 'envoye', 'titulaire, téléphone seul : les deux permis';
    ASSERT (SELECT statut FROM cloud_ecran_facteur_demander_code(repeat('b', 64), 'connexion', repeat('6', 64), 'sms')) = 'session', 'objet hors étape';
    BEGIN
        PERFORM cloud_ecran_facteur_demander_code(repeat('b', 64), 'action', repeat('6', 64), 'pigeon');
        RAISE EXCEPTION 'canal hors liste accepté';
    EXCEPTION WHEN invalid_parameter_value THEN NULL; END;
    -- Résultat d'envoi : liste fermée ; e-mail d'essai réservé à l'éditeur
    PERFORM cloud_ecran_facteur_noter_envoi(repeat('b', 64), 'code_envoi', 'sms:indisponible');
    PERFORM cloud_ecran_facteur_noter_envoi(repeat('a', 64), 'envoi_codes_essai', 'email:ok');
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'code_envoi' AND resultat = 'refus' AND objet = 'sms:indisponible'), 'SMS indisponible noté';
    BEGIN
        PERFORM cloud_ecran_facteur_noter_envoi(repeat('b', 64), 'envoi_codes_essai', 'email:ok');
        RAISE EXCEPTION 'titulaire : essai noté';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN
        PERFORM cloud_ecran_facteur_noter_envoi(repeat('b', 64), 'code_envoi', 'email:e370@exemple.invalid');
        RAISE EXCEPTION 'texte libre accepté au journal';
    EXCEPTION WHEN invalid_parameter_value THEN NULL; END;
END $$;

-- Contact d'un compte : e-mail ET téléphone, au moins un ; vide = inchangé, « - » = retiré ; code exigé
INSERT INTO code_second_facteur (compte_id, session_id, objet, empreinte_code, canal, expire_le)
SELECT s.compte_id, s.id, 'action', repeat('7', 64), 'email', now() + interval '5 minutes' FROM session_distante s WHERE s.empreinte_jeton = repeat('a', 64);
DO $$ DECLARE j text := repeat('a', 64); k text := repeat('7', 64); t uuid := (SELECT id FROM compte_distant WHERE identifiant = 'titulaire.370'); r record; BEGIN
    ASSERT (SELECT statut FROM cloud_ecran_facteur_compte_contact(j, k, gen_random_uuid(), 'a@exemple.invalid', '')) = 'inconnu', 'compte inconnu';
    ASSERT (SELECT statut FROM cloud_ecran_facteur_compte_contact(j, k, t, 'pas une adresse', '')) = 'email', 'e-mail invalide';
    ASSERT (SELECT statut FROM cloud_ecran_facteur_compte_contact(j, k, t, '', '12')) = 'telephone', 'téléphone invalide';
    ASSERT (SELECT statut FROM cloud_ecran_facteur_compte_contact(j, k, t, '', '-')) = 'sans_contact', 'compte sans e-mail ni téléphone refusé';
    ASSERT (SELECT statut FROM cloud_ecran_facteur_compte_contact(j, k, t, '', '')) = 'inchange', 'rien à changer';
    ASSERT (SELECT essais FROM code_second_facteur WHERE empreinte_code = k) = 0, 'aucun code consommé par les contrôles';
    BEGIN
        PERFORM cloud_ecran_facteur_compte_contact(repeat('b', 64), k, t, 't@exemple.invalid', '');
        RAISE EXCEPTION 'titulaire : contact modifié';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    ASSERT (SELECT statut FROM cloud_ecran_facteur_compte_contact(j, k, t, 't370@exemple.invalid', '')) = 'ok', 'e-mail ajouté';
    ASSERT (SELECT email = 't370@exemple.invalid' AND telephone = '+213555000370' FROM compte_distant WHERE id = t), 'e-mail ET téléphone';
    SELECT * INTO r FROM cloud_ecran_facteur_comptes(j, 1, 20) x WHERE x.id = t;
    ASSERT r.email_masque = 't***@exemple.invalid' AND r.telephone_masque = '***70', 'contact masqué';
    ASSERT NOT EXISTS (SELECT 1 FROM journal_securite WHERE detail LIKE '%t370@%' OR detail LIKE '%555000370%'), 'journal sans adresse ni numéro';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'compte_contact_modifie' AND detail LIKE '%e-mail modifié, téléphone inchangé%'), 'modification notée';
    ASSERT (SELECT premiere_rupture FROM cloud_ecran_verifier_journal_acces()) IS NULL, 'journal des accès : chaîne intacte';
END $$;

-- Comptes existants : contraintes du 030 toujours vraies, aucun compte sans contact
DO $$ BEGIN
    ASSERT NOT EXISTS (SELECT 1 FROM compte_distant WHERE email IS NULL AND telephone IS NULL), 'aucun compte sans contact';
    BEGIN
        UPDATE compte_distant SET email = NULL, telephone = NULL WHERE identifiant = 'editeur.370';
        RAISE EXCEPTION 'compte vidé de ses contacts accepté';
    EXCEPTION WHEN check_violation THEN NULL; END;
END $$;

ROLLBACK;
\echo '037 : tous réussis'
