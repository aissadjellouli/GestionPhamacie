-- =====================================================================
-- Tests du script 039 (création d'un compte distant par l'éditeur depuis l'écran « Comptes »), en transaction ANNULÉE :
--   psql -U <propriétaire> -d <base centrale de test> -v ON_ERROR_STOP=1 -f database/cloud/tests/039_creation_compte_ecran_tests.sql
-- Droits (écrans seulement) ; refus au titulaire ; contrôles AVANT le code (identifiant, doublon, nom, rôle, contact, formes) ; code
-- exigé et à usage unique ; compte créé (provisoire, contact, rôle, auteur « écran ») ; journaux sans identifiant, nom ni contact ;
-- chaîne intacte.
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

DO $$ BEGIN
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_compte_creer(text, text, text, text, text, text, text, text)', 'EXECUTE'), 'création : écrans';
    ASSERT NOT has_function_privilege('public', 'cloud_ecran_compte_creer(text, text, text, text, text, text, text, text)', 'EXECUTE'), 'pas pour PUBLIC';
    ASSERT NOT has_function_privilege('pharma_cloud_api', 'cloud_ecran_compte_creer(text, text, text, text, text, text, text, text)', 'EXECUTE'), 'API : rien';
END $$;

-- Données d'essai : éditeur E (session 6) et titulaire T (session 4), codes de confirmation (E : e, T : f)
SELECT cloud_compte_distant_creer('editeur.e390', 'Éditeur 0390', 'editeur', '$2a$12$' || repeat('a', 53), NULL, 'e390@exemple.invalid', 'tests 039') \g /dev/null
SELECT cloud_compte_distant_creer('titulaire.t390', 'Titulaire 0390', 'titulaire', '$2a$12$' || repeat('a', 53), NULL, 't390@exemple.invalid', 'tests 039') \g /dev/null
UPDATE compte_distant SET mot_de_passe_provisoire = false WHERE identifiant LIKE '%390';
INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le)
SELECT repeat(x.j, 64), c.id, 'ouverte', now() + interval '2 hours'
FROM (VALUES ('editeur.e390', '6'), ('titulaire.t390', '4')) x(ident, j) JOIN compte_distant c ON c.identifiant = x.ident;
INSERT INTO code_second_facteur (compte_id, session_id, objet, empreinte_code, canal, expire_le)
SELECT s.compte_id, s.id, 'action', repeat(x.k, 64), 'fichier', now() + interval '5 minutes'
FROM (VALUES ('6', 'e'), ('4', 'f')) x(j, k) JOIN session_distante s ON s.empreinte_jeton = repeat(x.j, 64);

SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ DECLARE m text := '$2a$12$' || repeat('b', 53); BEGIN
    BEGIN PERFORM cloud_ecran_compte_creer(repeat('4', 64), repeat('f', 64), 'nouveau.390', 'Nouveau', 'titulaire', NULL, 'n390@exemple.invalid', m);
          RAISE EXCEPTION 'création par un titulaire';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM cloud_ecran_compte_creer(repeat('6', 64), repeat('e', 64), 'nouveau.390', 'Nouveau', 'titulaire', NULL, 'n390@exemple.invalid', 'pas-une-empreinte');
          RAISE EXCEPTION 'empreinte invalide acceptée';
    EXCEPTION WHEN invalid_parameter_value THEN NULL; END;
    -- Contrôles AVANT le code : le code « e » reste utilisable
    ASSERT (SELECT statut FROM cloud_ecran_compte_creer(repeat('6', 64), repeat('e', 64), 'X', 'Nouveau', 'titulaire', NULL, 'n390@exemple.invalid', m)) = 'identifiant', 'identifiant trop court';
    ASSERT (SELECT statut FROM cloud_ecran_compte_creer(repeat('6', 64), repeat('e', 64), 'nouveau 390', 'Nouveau', 'titulaire', NULL, 'n390@exemple.invalid', m)) = 'identifiant', 'espace';
    ASSERT (SELECT statut FROM cloud_ecran_compte_creer(repeat('6', 64), repeat('e', 64), ' Titulaire.T390 ', 'Nouveau', 'titulaire', NULL, 'n390@exemple.invalid', m)) = 'existe', 'doublon (casse et espaces ignorés)';
    ASSERT (SELECT statut FROM cloud_ecran_compte_creer(repeat('6', 64), repeat('e', 64), 'nouveau.390', '  ', 'titulaire', NULL, 'n390@exemple.invalid', m)) = 'nom', 'nom vide';
    ASSERT (SELECT statut FROM cloud_ecran_compte_creer(repeat('6', 64), repeat('e', 64), 'nouveau.390', 'Nouveau', 'admin', NULL, 'n390@exemple.invalid', m)) = 'role', 'rôle hors liste';
    ASSERT (SELECT statut FROM cloud_ecran_compte_creer(repeat('6', 64), repeat('e', 64), 'nouveau.390', 'Nouveau', 'titulaire', ' ', '', m)) = 'contact', 'aucun contact';
    ASSERT (SELECT statut FROM cloud_ecran_compte_creer(repeat('6', 64), repeat('e', 64), 'nouveau.390', 'Nouveau', 'titulaire', NULL, 'pas-une-adresse', m)) = 'email', 'adresse';
    ASSERT (SELECT statut FROM cloud_ecran_compte_creer(repeat('6', 64), repeat('e', 64), 'nouveau.390', 'Nouveau', 'titulaire', '05-55', NULL, m)) = 'telephone', 'téléphone';
    ASSERT (SELECT statut FROM cloud_ecran_compte_creer(repeat('6', 64), repeat('9', 64), 'nouveau.390', 'Nouveau', 'titulaire', NULL, 'n390@exemple.invalid', m)) = 'faux', 'code faux';
    ASSERT (SELECT statut FROM cloud_ecran_compte_creer(repeat('6', 64), repeat('e', 64), ' Nouveau.390 ', ' Nouveau Titulaire ', 'titulaire', '0555 000 390', 'n390@exemple.invalid', m)) = 'ok', 'créé';
    ASSERT (SELECT statut FROM cloud_ecran_compte_creer(repeat('6', 64), repeat('e', 64), 'autre.390', 'Autre', 'editeur', NULL, 'o390@exemple.invalid', m)) = 'aucun', 'code à usage unique';
END $$;
RESET ROLE;

DO $$ DECLARE c compte_distant; BEGIN
    SELECT * INTO c FROM compte_distant WHERE identifiant = 'nouveau.390';
    ASSERT c.id IS NOT NULL, 'compte présent (identifiant normalisé)';
    ASSERT c.nom = 'Nouveau Titulaire' AND c.role = 'titulaire' AND c.actif, 'nom rogné, rôle, actif';
    ASSERT c.mot_de_passe_provisoire AND c.empreinte_mot_de_passe = '$2a$12$' || repeat('b', 53), 'mot de passe provisoire';
    ASSERT c.email = 'n390@exemple.invalid' AND c.telephone = '0555000390', 'contact (espaces du téléphone retirés)';
    ASSERT c.cree_par LIKE 'écran : éditeur n° %', 'auteur : écran';
    ASSERT NOT EXISTS (SELECT 1 FROM compte_distant WHERE identifiant = 'autre.390'), 'rien créé sans code';
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant j WHERE j.compte_id = c.id AND j.evenement = 'compte_cree' AND j.objet = 'editeur:titulaire'), 'journal du compte créé';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'compte_cree' AND detail LIKE '%' || left(c.id::text, 8) || '%'), 'journal de sécurité';
    ASSERT NOT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'compte_cree' AND (detail LIKE '%@%' OR detail LIKE '%nouveau%' OR detail LIKE '%0555000390%')),
           'jamais l''identifiant, le nom ni le contact au journal de sécurité';
    ASSERT NOT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'compte_cree' AND (objet LIKE '%@%' OR objet LIKE '%nouveau%')), 'ni au journal des accès';
    ASSERT (SELECT premiere_rupture FROM cloud_ecran_verifier_journal_acces()) IS NULL, 'chaîne intacte';
END $$;

ROLLBACK;
\echo '039 : tous réussis'
