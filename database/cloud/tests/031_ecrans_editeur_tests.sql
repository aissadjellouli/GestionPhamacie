-- =====================================================================
-- Tests du script 031 (écrans de l'éditeur), en transaction ANNULÉE :
--   psql -U <propriétaire> -d <base centrale de test> -v ON_ERROR_STOP=1 -f database/cloud/tests/031_ecrans_editeur_tests.sql
-- Rien sans session ni pour un titulaire ; liste filtrée et paginée ; fiche après ouverture par la base (poser app.officine_id
-- soi-même ne donne rien) ; aucune colonne ni valeur interdite (données d'ordre, détail d'anomalie, contenu de lot, codes,
-- motif de révocation) ; actions : contrôles avant le code, code faux compté, confirmation, journaux des accès et de sécurité.
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

-- Aucune fonction des écrans ne rend une colonne interdite (hors connexion : empreinte BCrypt et contact du compte lui-même)
DO $$ BEGIN
    ASSERT NOT EXISTS (
        SELECT 1 FROM pg_proc p, unnest(p.proargnames) WITH ORDINALITY AS a(nom, n)
        WHERE p.pronamespace = 'public'::regnamespace AND has_function_privilege('pharma_cloud_ecrans', p.oid, 'EXECUTE')
          AND p.proname NOT IN ('cloud_ecran_debut_connexion', 'cloud_ecran_creer_code', 'cloud_ecran_session')
          AND p.proargmodes IS NOT NULL AND p.proargmodes[a.n] IN ('o', 't')
          AND a.nom IN ('donnees', 'contenu', 'attendu', 'trouve', 'cle_ligne', 'empreinte_code', 'code', 'empreinte', 'motif_revocation',
                        'motif', 'cible', 'auteur', 'fins_chaine', 'titulaire_id', 'montant_ttc', 'telephone', 'email', 'nom',
                        'empreinte_mot_de_passe', 'ligne', 'ancienne', 'identifiant')),
        'colonne interdite rendue par une fonction des écrans';
    ASSERT (SELECT count(*) FROM pg_proc p WHERE p.pronamespace = 'public'::regnamespace AND has_function_privilege('pharma_cloud_ecrans', p.oid, 'EXECUTE')
            AND p.proname LIKE 'cloud\_ecran\_%') = 27, 'fonctions des écrans : 8 (030) + 19 (031)';
END $$;

-- Données d'essai : deux officines (A active avec un lot, une anomalie et un ordre ; B en attente), un éditeur et un titulaire
INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000032', 'Essai 032 A'), ('bbbbbbbb-0000-0000-0000-000000000032', 'Essai 032 B');
INSERT INTO certificat (empreinte, officine_id, serveur, numero_serie, echeance)
VALUES ('abcdef012345' || repeat('e', 52), 'aaaaaaaa-0000-0000-0000-000000000032', 'principal', '32', now() + interval '1 year'),
       ('fedcba012345' || repeat('f', 52), 'aaaaaaaa-0000-0000-0000-000000000032', 'second', '33', now() + interval '1 year');
INSERT INTO code_activation (empreinte_code, officine_id, serveur, expire_le)
VALUES (repeat('c', 64), 'bbbbbbbb-0000-0000-0000-000000000032', 'principal', now() + interval '1 day');
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000032', true) \g /dev/null
SELECT cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1', convert_to(
    '{"lignes":[{"n":1,"table":"vente","id":"1","op":"creation","donnees":{"id":1,"numero":"T-NOMINATIF032"}}]}', 'UTF8')) \g /dev/null
RESET ROLE;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000032', true) \g /dev/null
INSERT INTO cloud_anomalie_integrite (officine_id, journal, cle_chaine, rang, cle_ligne, nature, detail, attendu, trouve)
VALUES ('aaaaaaaa-0000-0000-0000-000000000032', 'journal_vente', 'global', 7, 'NOMINATIF032', 'chainage_rompu', 'NOMINATIF032', 'NOMINATIF032', 'NOMINATIF032');
INSERT INTO ordre_central (officine_id, type, cible, donnees, auteur, expire_le)
VALUES ('aaaaaaaa-0000-0000-0000-000000000032', 'essai', 'NOMINATIF032', '{"nom":"NOMINATIF032"}', 'NOMINATIF032', now() + interval '7 days');
SELECT set_config('app.officine_id', '', true) \g /dev/null
SELECT cloud_compte_distant_creer('editeur.032', 'Éditeur 032', 'editeur', '$2a$12$' || repeat('a', 53), NULL, 'e032@exemple.invalid', 'tests 032') \g /dev/null
SELECT cloud_compte_distant_creer('titulaire.032', 'Titulaire 032', 'titulaire', '$2a$12$' || repeat('a', 53), NULL, 't032@exemple.invalid', 'tests 032') \g /dev/null
UPDATE compte_distant SET mot_de_passe_provisoire = false WHERE identifiant IN ('editeur.032', 'titulaire.032');
INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le)
SELECT repeat('e', 64), id, 'ouverte', now() + interval '2 hours' FROM compte_distant WHERE identifiant = 'editeur.032';
INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le)
SELECT repeat('d', 64), id, 'ouverte', now() + interval '2 hours' FROM compte_distant WHERE identifiant = 'titulaire.032';

SET LOCAL ROLE pharma_cloud_ecrans;
-- Rien sans session ; rien pour un titulaire (écrans de l'éditeur) ; titulaire sans officine rattachée : ouverture refusée
DO $$ BEGIN
    BEGIN PERFORM cloud_ecran_officines(repeat('0', 64), NULL, NULL, 1, 20); RAISE EXCEPTION 'liste sans session';
    EXCEPTION WHEN invalid_authorization_specification THEN NULL; END;
    BEGIN PERFORM cloud_ecran_officines(repeat('d', 64), NULL, NULL, 1, 20); RAISE EXCEPTION 'liste pour un titulaire';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    ASSERT NOT cloud_ecran_ouvrir(repeat('d', 64), 'aaaaaaaa-0000-0000-0000-000000000032'), 'titulaire sans rattachement : refus';
    -- Officine posée à la main : les fonctions revérifient le jeton
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000032', true);
    BEGIN PERFORM cloud_ecran_lots(repeat('d', 64)); RAISE EXCEPTION 'lots pour un titulaire';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM cloud_ecran_lots(repeat('0', 64)); RAISE EXCEPTION 'lots sans session';
    EXCEPTION WHEN invalid_authorization_specification THEN NULL; END;
    PERFORM set_config('app.officine_id', '', true);
    BEGIN PERFORM cloud_ecran_fiche(repeat('e', 64)); RAISE EXCEPTION 'fiche sans officine ouverte';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    ASSERT NOT cloud_ecran_ouvrir(repeat('e', 64), gen_random_uuid()), 'officine inconnue : refus';
END $$;

-- Liste : filtre, état, page, total
DO $$
DECLARE r record;
BEGIN
    SELECT * INTO r FROM cloud_ecran_officines(repeat('e', 64), 'essai 032 a', 'toutes', 1, 20) WHERE id IS NOT NULL;
    ASSERT r.libelle = 'Essai 032 A' AND r.etat = 'active' AND r.total = 1, 'filtre sur le libellé (casse ignorée)';
    ASSERT r.dernier_lot = 1 AND r.derniere_epoque = 1 AND r.anomalies = 1 AND NOT r.chargement_en_cours, 'éléments cloisonnés lus officine par officine';
    ASSERT r.echeance_certificat > now() AND NOT r.en_retard, 'échéance et retard';
    ASSERT (SELECT etat FROM cloud_ecran_officines(repeat('e', 64), 'Essai 032 B', 'en_attente', 1, 20) WHERE id IS NOT NULL) = 'en_attente', 'état en attente';
    ASSERT (SELECT count(*) FROM cloud_ecran_officines(repeat('e', 64), 'Essai 032', 'partie', 1, 20) WHERE id IS NOT NULL) = 0, 'aucune officine partie';
    ASSERT (SELECT total FROM cloud_ecran_officines(repeat('e', 64), 'Essai 032', 'partie', 1, 20)) = 0, 'page vide : total quand même';
    ASSERT (SELECT count(*) FROM cloud_ecran_officines(repeat('e', 64), 'Essai 032', NULL, 2, 10) WHERE id IS NOT NULL) = 0, 'page 2 vide';
    ASSERT current_setting('app.officine_id', true) = '', 'officine courante rendue après la liste';
    BEGIN PERFORM cloud_ecran_officines(repeat('e', 64), NULL, 'nimporte', 1, 20); RAISE EXCEPTION 'état inconnu accepté';
    EXCEPTION WHEN invalid_parameter_value THEN NULL; END;
END $$;

-- Fiche de A : chaque sous-rubrique, aucune valeur interdite
DO $$
DECLARE tout text;
BEGIN
    ASSERT cloud_ecran_ouvrir(repeat('e', 64), 'aaaaaaaa-0000-0000-0000-000000000032'), 'ouverture par la base';
    ASSERT current_setting('app.officine_id') = 'aaaaaaaa-0000-0000-0000-000000000032', 'officine posée par la base';
    ASSERT (SELECT count(*) FROM cloud_ecran_lots(repeat('e', 64))) = 1, 'lot de A';
    ASSERT (SELECT count(*) FROM cloud_ecran_certificats(repeat('e', 64))) = 2, 'certificats de A';
    ASSERT (SELECT empreinte_courte FROM cloud_ecran_certificats(repeat('e', 64)) WHERE serveur = 'principal') = 'abcdef012345', 'début d''empreinte';
    ASSERT (SELECT count(*) FROM cloud_ecran_anomalies(repeat('e', 64), 1, 20)) = 1, 'anomalie de A';
    ASSERT (SELECT count(*) FROM cloud_ecran_ordres(repeat('e', 64), 1, 20)) = 1, 'ordre de A';
    ASSERT (SELECT count(*) FROM cloud_ecran_epoques(repeat('e', 64))) = 1, 'époque de A';
    ASSERT (SELECT count(*) FROM cloud_ecran_codes_activation(repeat('e', 64))) = 0, 'aucun code de B vu depuis A';
    SELECT concat_ws(' ', (SELECT string_agg(f::text, ' ') FROM cloud_ecran_fiche(repeat('e', 64)) f),
                     (SELECT string_agg(c::text, ' ') FROM cloud_ecran_certificats(repeat('e', 64)) c),
                     (SELECT string_agg(l::text, ' ') FROM cloud_ecran_lots(repeat('e', 64)) l),
                     (SELECT string_agg(a::text, ' ') FROM cloud_ecran_anomalies(repeat('e', 64), 1, 20) a),
                     (SELECT string_agg(o::text, ' ') FROM cloud_ecran_ordres(repeat('e', 64), 1, 20) o),
                     (SELECT string_agg(j::text, ' ') FROM cloud_ecran_journal_securite(repeat('e', 64), 1, 20) j),
                     (SELECT string_agg(x::text, ' ') FROM cloud_ecran_chargements(repeat('e', 64)) x),
                     (SELECT string_agg(x::text, ' ') FROM cloud_ecran_volumes(repeat('e', 64)) x),
                     (SELECT string_agg(x::text, ' ') FROM cloud_ecran_integrite_chaines(repeat('e', 64)) x),
                     (SELECT string_agg(x::text, ' ') FROM cloud_ecran_restaurations(repeat('e', 64)) x),
                     (SELECT string_agg(x::text, ' ') FROM cloud_ecran_changements_epoque(repeat('e', 64)) x)) INTO tout;
    ASSERT tout NOT LIKE '%NOMINATIF032%', 'valeur interdite rendue : ' || left(tout, 300);
    ASSERT tout NOT LIKE '%' || repeat('e', 52) || '%', 'empreinte complète d''un certificat rendue';
    -- Une autre officine ouverte : rien de A
    ASSERT cloud_ecran_ouvrir(repeat('e', 64), 'bbbbbbbb-0000-0000-0000-000000000032'), 'ouverture de B';
    ASSERT (SELECT count(*) FROM cloud_ecran_lots(repeat('e', 64))) = 0, 'B : aucun lot de A';
    ASSERT (SELECT count(*) FROM cloud_ecran_anomalies(repeat('e', 64), 1, 20)) = 0, 'B : aucune anomalie de A';
    ASSERT (SELECT count(*) FROM cloud_ecran_codes_activation(repeat('e', 64))) = 1, 'B : son code (sans le code)';
END $$;

-- Actions : un code d'action d'abord
DO $$ BEGIN
    ASSERT (SELECT statut FROM cloud_ecran_creer_code(repeat('e', 64), 'action', repeat('b', 64), 'fichier')) = 'envoye', 'code d''action';
    -- Créer une officine : contrôles de saisie AVANT le code (aucun essai consommé)
    ASSERT (SELECT statut FROM cloud_ecran_creer_officine(repeat('e', 64), repeat('b', 64), 'bref', 'Nouvelle 032', 'Nouvelle 032', 'principal', repeat('1', 64))) = 'motif', 'motif trop court';
    ASSERT (SELECT statut FROM cloud_ecran_creer_officine(repeat('e', 64), repeat('b', 64), 'Ouverture du réseau', 'Nouvelle 032', 'Nouvelle', 'principal', repeat('1', 64))) = 'confirmation', 'confirmation fausse';
    ASSERT (SELECT statut FROM cloud_ecran_creer_officine(repeat('e', 64), repeat('b', 64), 'Ouverture du réseau', 'Nouvelle 032', 'Nouvelle 032', 'tiers', repeat('1', 64))) = 'serveur', 'serveur inconnu';
    ASSERT (SELECT statut FROM cloud_ecran_creer_officine(repeat('e', 64), repeat('f', 64), 'Ouverture du réseau', 'Nouvelle 032', 'Nouvelle 032', 'principal', repeat('1', 64))) = 'faux', 'code faux';
    ASSERT (SELECT statut FROM cloud_ecran_creer_officine(repeat('e', 64), repeat('b', 64), 'Ouverture du réseau', 'Nouvelle 032', 'Nouvelle 032', 'principal', repeat('1', 64))) = 'ok', 'officine créée';
    ASSERT (SELECT statut FROM cloud_ecran_creer_officine(repeat('e', 64), repeat('b', 64), 'Ouverture du réseau', 'Nouvelle 032 bis', 'Nouvelle 032 bis', 'principal', repeat('2', 64))) = 'aucun', 'code à usage unique';
END $$;
RESET ROLE;
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM code_activation a JOIN officine o ON o.id = a.officine_id WHERE o.libelle = 'Nouvelle 032' AND a.empreinte_code = repeat('1', 64)
            AND a.serveur = 'principal' AND a.expire_le > now() + interval '23 hours') = 1, 'code d''activation de 24 h';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'officine_creee_ecran' AND detail LIKE '%Ouverture du réseau%'), 'journal de sécurité avec le motif';
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'officine_creee_ecran' AND resultat = 'ok' AND objet = 'principal'), 'journal des accès';
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'officine_creee_ecran' AND resultat = 'refus' AND objet = 'faux'), 'refus journalisé';
    ASSERT (SELECT premiere_rupture FROM cloud_ecran_verifier_journal_acces()) IS NULL, 'chaîne intacte';
END $$;

SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ BEGIN
    PERFORM cloud_ecran_creer_code(repeat('e', 64), 'action', repeat('b', 64), 'fichier');
    PERFORM cloud_ecran_ouvrir(repeat('e', 64), 'aaaaaaaa-0000-0000-0000-000000000032');
    -- Nouveau code d'activation : confirmation par le nom de l'officine
    ASSERT (SELECT statut FROM cloud_ecran_nouveau_code_activation(repeat('e', 64), repeat('b', 64), 'Poste remplacé', 'Essai 032', 'second', repeat('3', 64))) = 'confirmation', 'nom à ressaisir';
    ASSERT (SELECT statut FROM cloud_ecran_nouveau_code_activation(repeat('e', 64), repeat('b', 64), 'Poste remplacé', 'Essai 032 A', 'second', repeat('3', 64))) = 'ok', 'nouveau code';
    -- Révoquer un certificat : confirmation par le début d'empreinte
    PERFORM cloud_ecran_creer_code(repeat('e', 64), 'action', repeat('b', 64), 'fichier');
    ASSERT (SELECT statut FROM cloud_ecran_revoquer_certificat(repeat('e', 64), repeat('b', 64), 'Poste volé', '000000000000', '000000000000')) = 'inconnu', 'certificat inconnu';
    ASSERT (SELECT statut FROM cloud_ecran_revoquer_certificat(repeat('e', 64), repeat('b', 64), 'Poste volé', 'abcdef012345', 'abcdef01234')) = 'confirmation', 'empreinte à ressaisir';
    ASSERT (SELECT statut FROM cloud_ecran_revoquer_certificat(repeat('e', 64), repeat('b', 64), 'Poste volé', 'abcdef012345', 'ABCDEF012345')) = 'ok', 'certificat révoqué';
    -- Révoquer un serveur
    PERFORM cloud_ecran_creer_code(repeat('e', 64), 'action', repeat('b', 64), 'fichier');
    ASSERT (SELECT statut FROM cloud_ecran_revoquer_serveur(repeat('e', 64), repeat('b', 64), 'Serveur retiré', 'principal', 'Essai 032 A')) = 'inconnu', 'plus de certificat valable sur principal';
    ASSERT (SELECT revoques FROM cloud_ecran_revoquer_serveur(repeat('e', 64), repeat('b', 64), 'Serveur retiré', 'second', 'Essai 032 A')) = 1, 'serveur révoqué';
END $$;
RESET ROLE;
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM certificat WHERE officine_id = 'aaaaaaaa-0000-0000-0000-000000000032' AND revoque_le IS NOT NULL) = 2, 'deux certificats révoqués';
    ASSERT (SELECT motif_revocation FROM certificat WHERE empreinte LIKE 'abcdef012345%') = 'écran éditeur : Poste volé', 'motif de révocation gardé';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'revocation' AND detail LIKE 'serveur second : écran éditeur : Serveur retiré%'), 'révocation du serveur au journal';
    ASSERT (SELECT count(*) FROM code_activation WHERE officine_id = 'aaaaaaaa-0000-0000-0000-000000000032' AND serveur = 'second') = 1, 'code du second serveur';
    ASSERT (SELECT premiere_rupture FROM cloud_ecran_verifier_journal_acces()) IS NULL, 'chaîne intacte après les actions';
END $$;

-- Mon journal : les accès du compte seulement
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM cloud_ecran_mon_journal(repeat('e', 64), 1, 100)) >= 10, 'mon journal';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_ecran_mon_journal(repeat('d', 64), 1, 100) WHERE evenement LIKE '%ecran'), 'le titulaire ne voit pas les actions de l''éditeur';
END $$;
RESET ROLE;

ROLLBACK;
\echo '031 : tous réussis'
