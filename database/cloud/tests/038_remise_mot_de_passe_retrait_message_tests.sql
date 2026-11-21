-- =====================================================================
-- Tests du script 038 (remise à zéro d'un mot de passe par l'éditeur, retrait d'un rattachement par le titulaire, message à afficher
-- sur les postes), en transaction ANNULÉE :
--   psql -U <propriétaire> -d <base centrale de test> -v ON_ERROR_STOP=1 -f database/cloud/tests/038_remise_mot_de_passe_retrait_message_tests.sql
-- Droits (écrans seulement, rien pour PUBLIC ni pour l'API) ; contrôle du message (bornes, texte simple, lien, durée) dans la
-- fonction ET dans la table ; non-fuite : B n'envoie pas de message à O1 et ne retire pas O1 ; message hors bornes refusé avant le
-- code ; message envoyé (origine titulaire, 24 h, publié ordre-v1, jamais le texte au journal de sécurité) ; retrait : confirmation,
-- code, accès coupé tout de suite, ordres ouverts annulés et republiés ; remise à zéro : jamais son propre compte, motif,
-- confirmation, code, mot de passe provisoire, sessions fermées, verrou levé, jamais l'empreinte au journal ; chaîne intacte.
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

-- Droits
DO $$ BEGIN
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_remise_mot_de_passe(text, text, uuid, text, text, text)', 'EXECUTE'), 'remise : écrans';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_titulaire_retirer_officine(text, uuid, text, text)', 'EXECUTE'), 'retrait : écrans';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_titulaire_envoyer_message(text, uuid, text, int, text)', 'EXECUTE'), 'message : écrans';
    ASSERT NOT has_function_privilege('public', 'cloud_ecran_remise_mot_de_passe(text, text, uuid, text, text, text)', 'EXECUTE'), 'remise : pas pour PUBLIC';
    ASSERT NOT has_function_privilege('public', 'cloud_ecran_titulaire_retirer_officine(text, uuid, text, text)', 'EXECUTE'), 'retrait : pas pour PUBLIC';
    ASSERT NOT has_function_privilege('public', 'cloud_ecran_titulaire_envoyer_message(text, uuid, text, int, text)', 'EXECUTE'), 'message : pas pour PUBLIC';
    ASSERT NOT has_function_privilege('pharma_cloud_api', 'cloud_ecran_titulaire_envoyer_message(text, uuid, text, int, text)', 'EXECUTE'), 'API : rien';
    ASSERT NOT has_function_privilege('pharma_cloud_etat', 'cloud_ecran_remise_mot_de_passe(text, text, uuid, text, text, text)', 'EXECUTE'), 'page d''état : rien';
    ASSERT NOT cloud_ordre_titulaire_permis('message'), 'le message passe par sa propre fonction (données contrôlées), jamais par creer_ordre';
END $$;

-- Contrôle du message (pur) : mêmes codes que Pharma.Domain.Reglages.MessagePostes
DO $$
DECLARE c timestamptz := '2026-11-21 10:00:00+00';
BEGIN
    ASSERT cloud_message_poste_controle('{"format":"message-v1","texte":"Fermeture à 18 h samedi","jusqu_au":"2026-11-24T10:00:00Z"}', c) IS NULL, 'valable';
    ASSERT cloud_message_poste_controle('{"format":"message-v1","texte":"Inventaire lundi","jusqu_au":"2026-11-28T10:00:00Z"}', c) IS NULL, '7 jours tout juste';
    ASSERT cloud_message_poste_controle('{"format":"message-v1","texte":"Inventaire lundi","jusqu_au":"2026-11-28T10:00:01Z"}', c) = 'duree', 'plus de 7 jours';
    ASSERT cloud_message_poste_controle('{"format":"message-v1","texte":"Inventaire lundi","jusqu_au":"2026-11-21T10:00:00Z"}', c) = 'duree', 'fin à la création';
    ASSERT cloud_message_poste_controle('{"format":"message-v1","texte":"Inventaire lundi","jusqu_au":"2026-11-24 10:00:00"}', c) = 'format', 'heure locale ambiguë';
    ASSERT cloud_message_poste_controle('{"format":"message-v1","texte":"Inventaire lundi","jusqu_au":"2026-13-24T10:00:00Z"}', c) = 'format', 'date impossible';
    ASSERT cloud_message_poste_controle('{"format":"message-v1","texte":"   ","jusqu_au":"2026-11-24T10:00:00Z"}', c) = 'vide', 'vide';
    ASSERT cloud_message_poste_controle(jsonb_build_object('format', 'message-v1', 'texte', repeat('é', 200), 'jusqu_au', '2026-11-24T10:00:00Z'), c) IS NULL, '200 caractères';
    ASSERT cloud_message_poste_controle(jsonb_build_object('format', 'message-v1', 'texte', repeat('é', 201), 'jusqu_au', '2026-11-24T10:00:00Z'), c) = 'trop_long', '201 caractères';
    ASSERT cloud_message_poste_controle('{"format":"message-v1","texte":"<b>Urgent</b>","jusqu_au":"2026-11-24T10:00:00Z"}', c) = 'texte', 'balise';
    ASSERT cloud_message_poste_controle(jsonb_build_object('format', 'message-v1', 'texte', E'Ligne 1\nLigne 2', 'jusqu_au', '2026-11-24T10:00:00Z'), c) = 'texte', 'retour à la ligne';
    ASSERT cloud_message_poste_controle(jsonb_build_object('format', 'message-v1', 'texte', 'Caché' || chr(8238) || 'txt', 'jusqu_au', '2026-11-24T10:00:00Z'), c) = 'texte', 'sens d''écriture';
    ASSERT cloud_message_poste_controle('{"format":"message-v1","texte":" espace en tête","jusqu_au":"2026-11-24T10:00:00Z"}', c) = 'texte', 'non rogné';
    ASSERT cloud_message_poste_controle('{"format":"message-v1","texte":"Voir https://exemple.invalid","jusqu_au":"2026-11-24T10:00:00Z"}', c) = 'lien', 'adresse';
    ASSERT cloud_message_poste_controle('{"format":"message-v1","texte":"Voir WWW.exemple","jusqu_au":"2026-11-24T10:00:00Z"}', c) = 'lien', 'www';
    ASSERT cloud_message_poste_controle('{"format":"message-v1","texte":"Écrire à contact@pharma.dz demain","jusqu_au":"2026-11-24T10:00:00Z"}', c) = 'lien', 'domaine';
    ASSERT cloud_message_poste_controle('{"format":"message-v1","texte":"javascript:alert(1)","jusqu_au":"2026-11-24T10:00:00Z"}', c) = 'lien', 'protocole';
    ASSERT cloud_message_poste_controle('{"format":"message-v1","texte":"Livraison vers 10 h. Merci !","jusqu_au":"2026-11-24T10:00:00Z"}', c) IS NULL, 'ponctuation ordinaire';
    ASSERT cloud_message_poste_controle('{"format":"message-v1","texte":"x","jusqu_au":"2026-11-24T10:00:00Z","lien":"y"}', c) = 'format', 'clé en trop';
    ASSERT cloud_message_poste_controle('{"format":"message-v2","texte":"x","jusqu_au":"2026-11-24T10:00:00Z"}', c) = 'format', 'autre format';
    ASSERT cloud_message_poste_controle('{"format":"message-v1","texte":12,"jusqu_au":"2026-11-24T10:00:00Z"}', c) = 'format', 'texte non textuel';
END $$;

-- Données d'essai : O1 et O2 ; titulaires A (O1 et O2, droit « ordres » sur O1) et B (O2, droit « ordres ») ; éditeurs E1 et E2
INSERT INTO officine (id, libelle) VALUES ('11111111-0000-0000-0000-000000000380', 'Essai 0380 O1'), ('22222222-0000-0000-0000-000000000380', 'Essai 0380 O2');
SELECT cloud_compte_distant_creer('titulaire.a380', 'Titulaire A 0380', 'titulaire', '$2a$12$' || repeat('a', 53), NULL, 'a380@exemple.invalid', 'tests 038') \g /dev/null
SELECT cloud_compte_distant_creer('titulaire.b380', 'Titulaire B 0380', 'titulaire', '$2a$12$' || repeat('a', 53), NULL, 'b380@exemple.invalid', 'tests 038') \g /dev/null
SELECT cloud_compte_distant_creer('editeur.e1380', 'Éditeur 1 0380', 'editeur', '$2a$12$' || repeat('a', 53), NULL, 'e1380@exemple.invalid', 'tests 038') \g /dev/null
SELECT cloud_compte_distant_creer('editeur.e2380', 'Éditeur 2 0380', 'editeur', '$2a$12$' || repeat('a', 53), NULL, 'e2380@exemple.invalid', 'tests 038') \g /dev/null
UPDATE compte_distant SET mot_de_passe_provisoire = false WHERE identifiant LIKE '%380';
INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le)
SELECT repeat(x.j, 64), c.id, 'ouverte', now() + interval '2 hours'
FROM (VALUES ('titulaire.a380', '4'), ('titulaire.b380', '5'), ('editeur.e1380', '6'), ('editeur.e2380', '7'), ('titulaire.a380', '8')) x(ident, j)
JOIN compte_distant c ON c.identifiant = x.ident;
INSERT INTO compte_officine (compte_id, officine_id, accorde_par, droits)
SELECT c.id, o.id, 'tests 038', o.droits FROM compte_distant c,
    (VALUES ('11111111-0000-0000-0000-000000000380'::uuid, '{consulter,journal_acces,ordres}'::text[]),
            ('22222222-0000-0000-0000-000000000380'::uuid, '{consulter,journal_acces}'::text[])) o(id, droits)
WHERE c.identifiant = 'titulaire.a380';
INSERT INTO compte_officine (compte_id, officine_id, accorde_par, droits)
SELECT c.id, '22222222-0000-0000-0000-000000000380', 'tests 038', '{consulter,journal_acces,ordres}' FROM compte_distant c WHERE c.identifiant = 'titulaire.b380';

-- La table revérifie : message de la console refusé, message hors bornes refusé
DO $$ BEGIN
    PERFORM set_config('app.officine_id', '11111111-0000-0000-0000-000000000380', true);
    BEGIN INSERT INTO ordre_central (officine_id, type, auteur, expire_le, donnees)
          VALUES ('11111111-0000-0000-0000-000000000380', 'message', 'console', now() + interval '1 hour',
                  jsonb_build_object('format', 'message-v1', 'texte', 'x', 'jusqu_au', to_char((now() + interval '1 day') AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"')));
          RAISE EXCEPTION 'message de la console accepté';
    EXCEPTION WHEN check_violation THEN NULL; END;
    BEGIN INSERT INTO ordre_central (officine_id, type, auteur, expire_le, origine, compte_id, donnees)
          SELECT '11111111-0000-0000-0000-000000000380', 'message', 'x', now() + interval '1 hour', 'titulaire', c.id,
                 jsonb_build_object('format', 'message-v1', 'texte', repeat('x', 201), 'jusqu_au', to_char((now() + interval '1 day') AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'))
          FROM compte_distant c WHERE c.identifiant = 'titulaire.a380';
          RAISE EXCEPTION 'message de 201 caractères accepté par la table';
    EXCEPTION WHEN check_violation THEN NULL; END;
    BEGIN INSERT INTO ordre_central (officine_id, type, auteur, expire_le, origine, compte_id, donnees)
          SELECT '11111111-0000-0000-0000-000000000380', 'message', 'x', now() + interval '1 hour', 'titulaire', c.id,
                 jsonb_build_object('format', 'message-v1', 'texte', 'x', 'jusqu_au', to_char((now() + interval '8 days') AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'))
          FROM compte_distant c WHERE c.identifiant = 'titulaire.a380';
          RAISE EXCEPTION 'message de 8 jours accepté par la table';
    EXCEPTION WHEN check_violation THEN NULL; END;
    PERFORM set_config('app.officine_id', '', true);
END $$;

-- Codes de confirmation (empreintes HMAC simulées) : A (f), B (c), E1 (e)
INSERT INTO code_second_facteur (compte_id, session_id, objet, empreinte_code, canal, expire_le)
SELECT s.compte_id, s.id, 'action', repeat(x.k, 64), 'fichier', now() + interval '5 minutes'
FROM (VALUES ('4', 'f'), ('5', 'c'), ('6', 'e')) x(j, k) JOIN session_distante s ON s.empreinte_jeton = repeat(x.j, 64);

-- ---- Message : rôles, non-fuite, bornes (avant le code), envoi ----
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ DECLARE v_id uuid; BEGIN
    BEGIN PERFORM cloud_ecran_titulaire_envoyer_message(repeat('6', 64), '11111111-0000-0000-0000-000000000380', 'x', 1, NULL); RAISE EXCEPTION 'message de l''éditeur';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM cloud_ecran_titulaire_envoyer_message(repeat('0', 64), '11111111-0000-0000-0000-000000000380', 'x', 1, NULL); RAISE EXCEPTION 'message sans session';
    EXCEPTION WHEN invalid_authorization_specification THEN NULL; END;
    -- Non-fuite : B (droit « ordres » sur O2 seulement) n'envoie rien vers O1, même avec son code
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_envoyer_message(repeat('5', 64), '11111111-0000-0000-0000-000000000380', 'Message de B', 1, repeat('c', 64))) = 'refuse', 'B → O1 refusé';
    -- A sans le droit « ordres » sur O2
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_envoyer_message(repeat('4', 64), '22222222-0000-0000-0000-000000000380', 'Message', 1, repeat('f', 64))) = 'refuse', 'droit « ordres » exigé';
    -- Hors bornes : refusés AVANT le code (le code reste utilisable)
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_envoyer_message(repeat('4', 64), '11111111-0000-0000-0000-000000000380', repeat('x', 201), 1, repeat('f', 64))) = 'trop_long', '201 caractères';
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_envoyer_message(repeat('4', 64), '11111111-0000-0000-0000-000000000380', 'Message', 8, repeat('f', 64))) = 'duree', '8 jours';
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_envoyer_message(repeat('4', 64), '11111111-0000-0000-0000-000000000380', 'Message', 0, repeat('f', 64))) = 'duree', '0 jour';
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_envoyer_message(repeat('4', 64), '11111111-0000-0000-0000-000000000380', 'Voir www.exemple', 1, repeat('f', 64))) = 'lien', 'lien';
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_envoyer_message(repeat('4', 64), '11111111-0000-0000-0000-000000000380', '<script>', 1, repeat('f', 64))) = 'texte', 'balise';
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_envoyer_message(repeat('4', 64), '11111111-0000-0000-0000-000000000380', '  ', 1, repeat('f', 64))) = 'vide', 'vide';
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_envoyer_message(repeat('4', 64), '11111111-0000-0000-0000-000000000380', 'Message', 3, repeat('9', 64))) = 'faux', 'code faux';
    SELECT ordre_id INTO v_id FROM cloud_ecran_titulaire_envoyer_message(repeat('4', 64), '11111111-0000-0000-0000-000000000380', '  Inventaire lundi : caisse fermée à 17 h  ', 3, repeat('f', 64));
    ASSERT v_id IS NOT NULL, 'message envoyé';
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_envoyer_message(repeat('4', 64), '11111111-0000-0000-0000-000000000380', 'Autre', 1, repeat('f', 64))) = 'deja_en_cours',
           'un seul message ouvert par officine (avant le code)';
    ASSERT (SELECT string_agg(type || ':' || statut || ':' || acteur, ' ') FROM cloud_ecran_titulaire_ordres(repeat('4', 64), '11111111-0000-0000-0000-000000000380', 1, 20))
           = 'message:en_attente:vous', 'liste des ordres de A';
END $$;
RESET ROLE;
DO $$ DECLARE o ordre_central; d descendant; BEGIN
    PERFORM set_config('app.officine_id', '11111111-0000-0000-0000-000000000380', true);
    SELECT * INTO o FROM ordre_central WHERE type = 'message';
    ASSERT o.origine = 'titulaire' AND o.cible IS NULL AND o.statut = 'en_attente', 'ordre du titulaire';
    ASSERT o.donnees ->> 'texte' = 'Inventaire lundi : caisse fermée à 17 h', 'texte rogné';
    ASSERT (o.donnees ->> 'jusqu_au')::timestamptz BETWEEN now() + interval '3 days' - interval '1 second' AND now() + interval '3 days', 'fin d''affichage : 3 jours';
    ASSERT o.donnees ->> 'jusqu_au' ~ 'Z$', 'fin en UTC';
    ASSERT o.expire_le BETWEEN now() + interval '23 hours 59 minutes' AND now() + interval '24 hours', 'relevé dans les 24 heures';
    SELECT * INTO d FROM descendant WHERE nom_table = 'ordre_central' AND cle = o.id::text;
    ASSERT d.donnees ->> 'format' = 'ordre-v1' AND d.donnees ->> 'type' = 'message' AND d.donnees -> 'donnees' ->> 'texte' = o.donnees ->> 'texte', 'publié';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'ordre_cree' AND detail LIKE '%' || o.id || '%message de 39 caractère(s)%'), 'journal de sécurité';
    ASSERT NOT EXISTS (SELECT 1 FROM journal_securite WHERE detail LIKE '%Inventaire lundi%'), 'jamais le texte au journal de sécurité';
    PERFORM set_config('app.officine_id', '', true);
END $$;

-- ---- Retrait d'un rattachement par le titulaire ----
-- Un second ordre ouvert de A sur O1 (essai), puis : B ne retire pas O1 ; éditeur refusé ; confirmation ; code ; retrait
INSERT INTO code_second_facteur (compte_id, session_id, objet, empreinte_code, canal, expire_le)
SELECT s.compte_id, s.id, 'action', repeat('d', 64), 'fichier', now() + interval '5 minutes' FROM session_distante s WHERE s.empreinte_jeton = repeat('4', 64);
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ BEGIN
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_creer_ordre(repeat('4', 64), '11111111-0000-0000-0000-000000000380', 'essai', repeat('d', 64))) = 'ok', 'essai envoyé';
END $$;
RESET ROLE;
INSERT INTO code_second_facteur (compte_id, session_id, objet, empreinte_code, canal, expire_le)
SELECT s.compte_id, s.id, 'action', repeat('b', 64), 'fichier', now() + interval '5 minutes' FROM session_distante s WHERE s.empreinte_jeton = repeat('4', 64);
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ BEGIN
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_retirer_officine(repeat('5', 64), '11111111-0000-0000-0000-000000000380', 'Essai 0380 O1', repeat('c', 64))) = 'refuse',
           'B ne retire pas O1';
    BEGIN PERFORM cloud_ecran_titulaire_retirer_officine(repeat('6', 64), '11111111-0000-0000-0000-000000000380', 'Essai 0380 O1', NULL); RAISE EXCEPTION 'retrait par l''éditeur';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_retirer_officine(repeat('4', 64), '11111111-0000-0000-0000-000000000380', 'Essai 0380 O2', repeat('b', 64))) = 'confirmation',
           'nom d''une autre officine';
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_retirer_officine(repeat('4', 64), '11111111-0000-0000-0000-000000000380', ' essai 0380 o1 ', repeat('9', 64))) = 'faux', 'code faux';
    ASSERT (SELECT statut || ':' || ordres_annules FROM cloud_ecran_titulaire_retirer_officine(repeat('4', 64), '11111111-0000-0000-0000-000000000380', ' essai 0380 o1 ', repeat('b', 64)))
           = 'ok:2', 'retrait : message et essai annulés';
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_retirer_officine(repeat('4', 64), '11111111-0000-0000-0000-000000000380', 'Essai 0380 O1', repeat('b', 64))) = 'refuse', 'déjà retiré';
    -- Accès coupé tout de suite ; O2 gardée ; B garde O2
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_tableau(repeat('4', 64), '11111111-0000-0000-0000-000000000380')) = 0, 'tableau refusé';
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_ordres(repeat('4', 64), '11111111-0000-0000-0000-000000000380', 1, 20)) = 0, 'ordres refusés';
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_envoyer_message(repeat('4', 64), '11111111-0000-0000-0000-000000000380', 'Après retrait', 1, NULL)) = 'refuse', 'message refusé';
    ASSERT (SELECT string_agg(libelle, ',') FROM cloud_ecran_titulaire_ensemble(repeat('4', 64))) = 'Essai 0380 O2', 'O2 seulement';
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_ensemble(repeat('5', 64))) = 1, 'B garde O2';
END $$;
RESET ROLE;
DO $$ BEGIN
    PERFORM set_config('app.officine_id', '11111111-0000-0000-0000-000000000380', true);
    ASSERT (SELECT string_agg(type || ':' || statut, ',' ORDER BY type) FROM ordre_central) = 'essai:annule,message:annule', 'ordres ouverts annulés';
    ASSERT (SELECT count(*) FROM descendant d JOIN ordre_central o ON d.cle = o.id::text WHERE d.nom_table = 'ordre_central' AND d.donnees ->> 'statut' = 'annule') = 2,
           'annulations republiées';
    ASSERT EXISTS (SELECT 1 FROM ordre_central_journal WHERE statut = 'annule' AND par = 'titulaire à distance (rattachement retiré)'), 'journal des ordres';
    PERFORM set_config('app.officine_id', '', true);
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'retrait_compte_distant' AND officine_id = '11111111-0000-0000-0000-000000000380'
                   AND detail LIKE 'écran du titulaire%2 ordre(s) ouvert(s) annulé(s)'), 'journal de sécurité';
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'retrait_rattachement' AND resultat = 'ok' AND objet = 'ordres_annules:2'), 'journal des accès';
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'retrait_rattachement' AND resultat = 'refus' AND objet = 'confirmation'), 'refus noté';
    ASSERT (SELECT retire_le IS NULL FROM compte_officine x JOIN compte_distant c ON c.id = x.compte_id WHERE c.identifiant = 'titulaire.b380'), 'B intact';
END $$;

-- ---- Remise à zéro d'un mot de passe par l'éditeur ----
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ DECLARE a uuid; e1 uuid; b uuid; BEGIN
    SELECT id INTO a FROM cloud_ecran_facteur_comptes(repeat('6', 64), 1, 100) WHERE identifiant = 'titulaire.a380';
    SELECT id INTO e1 FROM cloud_ecran_facteur_comptes(repeat('6', 64), 1, 100) WHERE identifiant = 'editeur.e1380';
    BEGIN PERFORM cloud_ecran_remise_mot_de_passe(repeat('4', 64), NULL, a, 'Mot de passe oublié', 'titulaire.a380', '$2a$12$' || repeat('b', 53));
          RAISE EXCEPTION 'remise par un titulaire';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM cloud_ecran_remise_mot_de_passe(repeat('6', 64), NULL, a, 'Mot de passe oublié', 'titulaire.a380', 'pas-une-empreinte');
          RAISE EXCEPTION 'empreinte invalide acceptée';
    EXCEPTION WHEN invalid_parameter_value THEN NULL; END;
    ASSERT (SELECT statut FROM cloud_ecran_remise_mot_de_passe(repeat('6', 64), repeat('e', 64), e1, 'Mot de passe oublié', 'editeur.e1380', '$2a$12$' || repeat('b', 53))) = 'soi_meme',
           'jamais son propre compte';
    ASSERT (SELECT statut FROM cloud_ecran_remise_mot_de_passe(repeat('6', 64), repeat('e', 64), gen_random_uuid(), 'Mot de passe oublié', 'x', '$2a$12$' || repeat('b', 53))) = 'inconnu',
           'compte inconnu';
    ASSERT (SELECT statut FROM cloud_ecran_remise_mot_de_passe(repeat('6', 64), repeat('e', 64), a, 'abc', 'titulaire.a380', '$2a$12$' || repeat('b', 53))) = 'motif', 'motif court';
    ASSERT (SELECT statut FROM cloud_ecran_remise_mot_de_passe(repeat('6', 64), repeat('e', 64), a, 'Mot de passe oublié', 'titulaire.b380', '$2a$12$' || repeat('b', 53))) = 'confirmation',
           'autre identifiant ressaisi';
    ASSERT (SELECT statut FROM cloud_ecran_remise_mot_de_passe(repeat('6', 64), repeat('9', 64), a, 'Mot de passe oublié', 'titulaire.a380', '$2a$12$' || repeat('b', 53))) = 'faux', 'code faux';
    ASSERT (SELECT statut || ':' || sessions_fermees FROM cloud_ecran_remise_mot_de_passe(repeat('6', 64), repeat('e', 64), a, 'Mot de passe oublié (appel du titulaire)',
                                                                                         ' Titulaire.A380 ', '$2a$12$' || repeat('b', 53))) = 'ok:2', 'remise : deux sessions fermées';
    ASSERT (SELECT statut FROM cloud_ecran_remise_mot_de_passe(repeat('6', 64), repeat('e', 64), a, 'Mot de passe oublié', 'titulaire.a380', '$2a$12$' || repeat('c', 53))) = 'aucun',
           'code à usage unique';
    -- Sessions de A fermées : plus rien de ses écrans
    BEGIN PERFORM cloud_ecran_titulaire_ensemble(repeat('4', 64)); RAISE EXCEPTION 'session de A encore ouverte';
    EXCEPTION WHEN invalid_authorization_specification THEN NULL; END;
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_ecran_session(repeat('8', 64))), 'seconde session de A fermée';
    ASSERT EXISTS (SELECT 1 FROM cloud_ecran_session(repeat('6', 64))), 'session de l''éditeur gardée';
END $$;
RESET ROLE;
DO $$ DECLARE c compte_distant; BEGIN
    SELECT * INTO c FROM compte_distant WHERE identifiant = 'titulaire.a380';
    ASSERT c.mot_de_passe_provisoire AND c.empreinte_mot_de_passe = '$2a$12$' || repeat('b', 53), 'provisoire, nouvelle empreinte';
    ASSERT c.echecs = 0 AND c.verrouille_jusqu_au IS NULL, 'verrou levé';
    ASSERT NOT EXISTS (SELECT 1 FROM code_second_facteur k WHERE k.compte_id = c.id AND k.utilise_le IS NULL AND k.annule_le IS NULL), 'codes en cours annulés';
    ASSERT (SELECT count(*) FROM session_distante s WHERE s.compte_id = c.id AND s.motif_fin = 'revoquee') = 2, 'motif : revoquee';
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant j WHERE j.compte_id = c.id AND j.evenement = 'mot_de_passe_remis' AND j.objet = 'editeur'), 'noté au journal du compte';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'mot_de_passe_remis' AND detail LIKE '%' || left(c.id::text, 8) || '%appel du titulaire%'), 'journal de sécurité avec le motif';
    ASSERT NOT EXISTS (SELECT 1 FROM journal_securite WHERE detail LIKE '%$2%' OR detail LIKE '%titulaire.a380%'), 'jamais l''empreinte ni l''identifiant au journal de sécurité';
    ASSERT NOT EXISTS (SELECT 1 FROM journal_acces_distant WHERE objet LIKE '%$2%'), 'jamais l''empreinte au journal des accès';
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'mot_de_passe_remis' AND resultat = 'refus' AND objet = 'soi_meme'), 'refus noté';
    -- Compte désactivé : refusé
    PERFORM cloud_compte_distant_desactiver('titulaire.b380');
END $$;
INSERT INTO code_second_facteur (compte_id, session_id, objet, empreinte_code, canal, expire_le)
SELECT s.compte_id, s.id, 'action', repeat('a', 64), 'fichier', now() + interval '5 minutes' FROM session_distante s WHERE s.empreinte_jeton = repeat('6', 64);
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ DECLARE b uuid; BEGIN
    SELECT id INTO b FROM cloud_ecran_facteur_comptes(repeat('6', 64), 1, 100) WHERE identifiant = 'titulaire.b380';
    ASSERT (SELECT statut FROM cloud_ecran_remise_mot_de_passe(repeat('6', 64), repeat('a', 64), b, 'Mot de passe oublié', 'titulaire.b380', '$2a$12$' || repeat('b', 53))) = 'inactif',
           'compte désactivé';
END $$;
RESET ROLE;

DO $$ BEGIN
    ASSERT (SELECT premiere_rupture FROM cloud_ecran_verifier_journal_acces()) IS NULL, 'chaîne intacte';
END $$;

ROLLBACK;
\echo '038 : tous réussis'
