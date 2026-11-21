-- =====================================================================
-- Tests du script 033 (code de rattachement demandé par le poste), en transaction ANNULÉE :
--   psql -U <propriétaire> -d <base centrale de test> -v ON_ERROR_STOP=1 -f database/cloud/tests/033_code_rattachement_poste_tests.sql
-- Officine du certificat seulement (autre officine, certificat révoqué, officine partie, officine non posée : refusés),
-- réponse neutre (compte inconnu, éditeur, désactivé, identifiant mal formé : même « refus »), limite de 5 demandes par heure et
-- par officine (une autre officine n'est pas touchée), mêmes règles que la console (30 min, usage unique, code précédent
-- annulé, empreinte seule), code utilisable par l'écran « Rattacher », journaux (acteur « poste », chaîne intacte).
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

-- Droits : l'API centrale n'a que la fonction du poste ; rien pour PUBLIC ni pour les écrans ; enregistrement commun jamais accordé
DO $$ BEGIN
    ASSERT has_function_privilege('pharma_cloud_api', 'cloud_poste_code_rattachement(text, text, text)', 'EXECUTE'), 'API : fonction du poste';
    ASSERT NOT has_function_privilege('public', 'cloud_poste_code_rattachement(text, text, text)', 'EXECUTE'), 'pas pour PUBLIC';
    ASSERT NOT has_function_privilege('pharma_cloud_ecrans', 'cloud_poste_code_rattachement(text, text, text)', 'EXECUTE'), 'pas pour les écrans';
    ASSERT NOT has_function_privilege('pharma_cloud_etat', 'cloud_poste_code_rattachement(text, text, text)', 'EXECUTE'), 'pas pour la page d''état';
    ASSERT NOT has_function_privilege('pharma_cloud_api', 'cloud_code_rattachement_enregistrer(uuid, uuid, text, text, text)', 'EXECUTE'), 'enregistrement interne (API)';
    ASSERT NOT has_function_privilege('pharma_cloud_ecrans', 'cloud_code_rattachement_enregistrer(uuid, uuid, text, text, text)', 'EXECUTE'), 'enregistrement interne (écrans)';
    ASSERT NOT has_function_privilege('public', 'cloud_code_rattachement_enregistrer(uuid, uuid, text, text, text)', 'EXECUTE'), 'enregistrement : pas pour PUBLIC';
    ASSERT NOT has_function_privilege('pharma_cloud_api', 'cloud_compte_distant_code_rattachement(text, uuid, text, text)', 'EXECUTE'), 'console : pas pour l''API';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_titulaire_journal(text, uuid, int, int)', 'EXECUTE'), 'journal du titulaire toujours accordé';
    ASSERT NOT has_table_privilege('pharma_cloud_api', 'code_rattachement', 'SELECT'), 'API : aucun droit direct sur les codes';
    ASSERT NOT has_table_privilege('pharma_cloud_api', 'compte_distant', 'SELECT'), 'API : aucun droit direct sur les comptes';
END $$;

-- Données d'essai : officines A et B (actives, certificat valable), C (partie) ; certificat révoqué de A ; comptes
INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000330', 'Essai 0330 A'), ('bbbbbbbb-0000-0000-0000-000000000330', 'Essai 0330 B');
INSERT INTO officine (id, libelle, resiliee_le) VALUES ('cccccccc-0000-0000-0000-000000000330', 'Essai 0330 C', now() - interval '1 day');
INSERT INTO certificat (empreinte, officine_id, serveur, numero_serie, echeance)
VALUES (repeat('a', 63) || '3', 'aaaaaaaa-0000-0000-0000-000000000330', 'principal', '3301', now() + interval '1 year'),
       (repeat('b', 63) || '3', 'bbbbbbbb-0000-0000-0000-000000000330', 'principal', '3302', now() + interval '1 year'),
       (repeat('c', 63) || '3', 'cccccccc-0000-0000-0000-000000000330', 'principal', '3303', now() + interval '1 year');
INSERT INTO certificat (empreinte, officine_id, serveur, numero_serie, echeance, revoque_le)
VALUES (repeat('d', 63) || '3', 'aaaaaaaa-0000-0000-0000-000000000330', 'second', '3304', now() + interval '1 year', now());
SELECT cloud_compte_distant_creer('titulaire.a330', 'Titulaire A 0330', 'titulaire', '$2a$12$' || repeat('a', 53), NULL, 'a330@exemple.invalid', 'tests 033') \g /dev/null
SELECT cloud_compte_distant_creer('titulaire.i330', 'Titulaire inactif 0330', 'titulaire', '$2a$12$' || repeat('a', 53), NULL, 'i330@exemple.invalid', 'tests 033') \g /dev/null
SELECT cloud_compte_distant_creer('editeur.330', 'Éditeur 0330', 'editeur', '$2a$12$' || repeat('a', 53), NULL, 'e330@exemple.invalid', 'tests 033') \g /dev/null
UPDATE compte_distant SET mot_de_passe_provisoire = false WHERE identifiant IN ('titulaire.a330', 'titulaire.i330', 'editeur.330');
UPDATE compte_distant SET actif = false WHERE identifiant = 'titulaire.i330';
INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le)
SELECT repeat('7', 64), c.id, 'ouverte', now() + interval '2 hours' FROM compte_distant c WHERE c.identifiant = 'titulaire.a330';

SET LOCAL ROLE pharma_cloud_api;
-- Officine du certificat seulement : officine non posée, certificat d'une autre officine, révoqué, inconnu, officine partie
DO $$ BEGIN
    BEGIN PERFORM cloud_poste_code_rattachement(repeat('a', 63) || '3', 'titulaire.a330', md5('033-x') || md5('033-x2'));
          RAISE EXCEPTION 'officine non posée acceptée';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000330', true);
    BEGIN PERFORM cloud_poste_code_rattachement(repeat('b', 63) || '3', 'titulaire.a330', md5('033-x') || md5('033-x2'));
          RAISE EXCEPTION 'certificat d''une autre officine accepté';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM cloud_poste_code_rattachement(repeat('d', 63) || '3', 'titulaire.a330', md5('033-x') || md5('033-x2'));
          RAISE EXCEPTION 'certificat révoqué accepté';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM cloud_poste_code_rattachement(repeat('e', 64), 'titulaire.a330', md5('033-x') || md5('033-x2'));
          RAISE EXCEPTION 'certificat inconnu accepté';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM cloud_poste_code_rattachement(NULL, 'titulaire.a330', md5('033-x') || md5('033-x2'));
          RAISE EXCEPTION 'sans certificat accepté';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    PERFORM set_config('app.officine_id', 'cccccccc-0000-0000-0000-000000000330', true);
    BEGIN PERFORM cloud_poste_code_rattachement(repeat('c', 63) || '3', 'titulaire.a330', md5('033-x') || md5('033-x2'));
          RAISE EXCEPTION 'officine partie acceptée';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000330', true);
    BEGIN PERFORM cloud_poste_code_rattachement(repeat('a', 63) || '3', 'titulaire.a330', 'PAS-UNE-EMPREINTE');
          RAISE EXCEPTION 'empreinte invalide acceptée';
    EXCEPTION WHEN invalid_parameter_value THEN NULL; END;
END $$;

-- Officine A : 1 code délivré, 4 refus neutres (même réponse), puis limite atteinte (5 demandes par heure)
DO $$
DECLARE r record;
BEGIN
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000330', true);
    SELECT * INTO r FROM cloud_poste_code_rattachement(repeat('a', 63) || '3', ' Titulaire.A330 ', md5('033-a') || md5('033-a2'));
    ASSERT r.statut = 'ok', 'titulaire actif : code délivré';
    ASSERT r.expire_le BETWEEN now() + interval '29 minutes' AND now() + interval '30 minutes', '30 minutes';
    FOR r IN SELECT x.ident, p.statut, p.expire_le
             FROM (VALUES ('inconnu.330'), ('editeur.330'), ('titulaire.i330'), ('PAS VALIDE !')) x(ident),
                  LATERAL cloud_poste_code_rattachement(repeat('a', 63) || '3', x.ident, md5('033-r' || x.ident) || md5('033-r2' || x.ident)) p LOOP
        ASSERT r.statut = 'refus' AND r.expire_le IS NULL, 'réponse neutre attendue pour ' || r.ident;
    END LOOP;
    SELECT * INTO r FROM cloud_poste_code_rattachement(repeat('a', 63) || '3', 'titulaire.a330', md5('033-l') || md5('033-l2'));
    ASSERT r.statut = 'trop_de_codes' AND r.expire_le IS NULL, 'limite de 5 demandes par heure et par officine';
    -- La limite ne touche pas une autre officine ; un nouveau code annule le précédent (même compte, même officine)
    PERFORM set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000330', true);
    ASSERT (SELECT statut FROM cloud_poste_code_rattachement(repeat('b', 63) || '3', 'titulaire.a330', md5('033-b') || md5('033-b2'))) = 'ok', 'officine B : code 1';
    ASSERT (SELECT statut FROM cloud_poste_code_rattachement(repeat('b', 63) || '3', 'titulaire.a330', md5('033-c') || md5('033-c2'))) = 'ok', 'officine B : code 2';
    PERFORM set_config('app.officine_id', '', true);
END $$;
RESET ROLE;

-- Mêmes règles que la console : un compte, l'officine du certificat, empreinte seule, auteur « poste de l'officine » ; journaux
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM code_rattachement k JOIN compte_distant c ON c.id = k.compte_id
            WHERE k.officine_id = 'aaaaaaaa-0000-0000-0000-000000000330') = 1, 'officine A : un seul code (les refus n''en créent pas)';
    ASSERT (SELECT c.identifiant || '|' || k.cree_par || '|' || (k.utilise_le IS NULL AND k.annule_le IS NULL)::text
            FROM code_rattachement k JOIN compte_distant c ON c.id = k.compte_id
            WHERE k.empreinte_code = md5('033-a') || md5('033-a2')) = 'titulaire.a330|poste de l''officine|true', 'code de A';
    ASSERT (SELECT annule_le IS NOT NULL FROM code_rattachement WHERE empreinte_code = md5('033-b') || md5('033-b2')), 'code B 1 annulé par le suivant';
    ASSERT (SELECT annule_le IS NULL FROM code_rattachement WHERE empreinte_code = md5('033-c') || md5('033-c2')), 'code B 2 en cours';
    -- Journal des accès : 5 demandes comptées (1 délivrée, 4 refusées, sans compte), 1 refus pour la limite
    ASSERT (SELECT string_agg(j.resultat || ':' || j.objet || ':' || (j.compte_id IS NULL)::text, ',' ORDER BY j.id) FROM journal_acces_distant j
            WHERE j.officine_id = 'aaaaaaaa-0000-0000-0000-000000000330' AND j.evenement = 'code_rattachement_cree')
           = 'ok:poste:false,refus:poste:true,refus:poste:true,refus:poste:true,refus:poste:true,refus:poste:limite:true', 'journal des accès';
    -- Journal de sécurité : délivrance et motifs réels, jamais l'identifiant saisi ni le code
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE officine_id = 'aaaaaaaa-0000-0000-0000-000000000330' AND evenement = 'code_rattachement_cree'
                   AND detail LIKE 'poste de l''officine : code de rattachement n° % (30 min) pour le compte n° %, par poste de l''officine'), 'sécurité : délivrance';
    ASSERT (SELECT string_agg(detail, ',' ORDER BY id) FROM journal_securite WHERE officine_id = 'aaaaaaaa-0000-0000-0000-000000000330'
            AND evenement = 'code_rattachement_refuse')
           = 'poste de l''officine : code de rattachement non délivré (compte_inconnu),poste de l''officine : code de rattachement non délivré (pas_titulaire),'
             || 'poste de l''officine : code de rattachement non délivré (compte_inactif),poste de l''officine : code de rattachement non délivré (compte_inconnu)',
           'sécurité : motifs réels';
    ASSERT NOT EXISTS (SELECT 1 FROM journal_securite WHERE detail ILIKE '%inconnu.330%' OR detail ILIKE '%titulaire.i330%' OR detail ILIKE '%editeur.330%'),
           'aucun identifiant au journal de sécurité';
END $$;

-- Le code du poste rattache l'officine dans l'écran « Rattacher » ; le journal de l'officine montre l'acteur « poste »
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ BEGIN
    ASSERT (SELECT statut || '|' || officine_id::text FROM cloud_ecran_rattacher(repeat('7', 64), md5('033-a') || md5('033-a2')))
           = 'ok|aaaaaaaa-0000-0000-0000-000000000330', 'code du poste utilisé';
    ASSERT (SELECT statut FROM cloud_ecran_rattacher(repeat('7', 64), md5('033-a') || md5('033-a2'))) = 'utilise', 'usage unique';
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_journal(repeat('7', 64), 'aaaaaaaa-0000-0000-0000-000000000330', 1, 100)
            WHERE acteur = 'poste' AND evenement = 'code_rattachement_cree') = 6, 'acteur « poste » (demandes du poste)';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_ecran_titulaire_journal(repeat('7', 64), 'aaaaaaaa-0000-0000-0000-000000000330', 1, 100)
                       WHERE acteur = 'console'), 'aucune demande du poste attribuée à la console';
    ASSERT EXISTS (SELECT 1 FROM cloud_ecran_titulaire_journal(repeat('7', 64), 'aaaaaaaa-0000-0000-0000-000000000330', 1, 100)
                   WHERE acteur = 'vous' AND evenement = 'rattachement' AND resultat = 'ok'), 'rattachement par le titulaire';
END $$;
RESET ROLE;

DO $$ BEGIN
    ASSERT (SELECT premiere_rupture FROM cloud_ecran_verifier_journal_acces()) IS NULL, 'chaîne du journal des accès intacte';
END $$;

ROLLBACK;
