-- =====================================================================
-- Tests du script 035 (réglages communs du titulaire), en transaction ANNULÉE :
--   psql -U <propriétaire> -d <base centrale de test> -v ON_ERROR_STOP=1 -f database/cloud/tests/035_reglages_communs_titulaire_tests.sql
-- Liste fermée et bornes (fonction et contrainte de la table) ; titulaire A rattaché à O1 et O2, B à O2 seulement : B n'envoie
-- jamais vers O1 et ne voit jamais les envois de A ; envoi refusé sans le droit « ordres » sur CHACUNE des officines (tout ou rien,
-- rien de créé) ; hors liste, hors bornes, incohérent, sans officine refusés avant le code ; code faux ; envoi à deux officines (un
-- ordre par officine, même envoi, 24 h, publié ordre-v1, journaux) ; un seul jeu ouvert par officine (rejeu refusé) ; résultat remonté
-- (accepté sur O1, refusé sur O2) et expiration visibles dans la liste ; chaîne du journal des accès intacte.
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

-- Droits et liste fermée
DO $$ BEGIN
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_titulaire_envoyer_reglages(text, uuid[], jsonb, text)', 'EXECUTE'), 'envoi';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_titulaire_reglages_envois(text, int, int)', 'EXECUTE'), 'liste';
    ASSERT NOT has_function_privilege('pharma_cloud_api', 'cloud_ecran_titulaire_envoyer_reglages(text, uuid[], jsonb, text)', 'EXECUTE'), 'API : pas l''envoi';
    ASSERT NOT has_function_privilege('public', 'cloud_ecran_titulaire_envoyer_reglages(text, uuid[], jsonb, text)', 'EXECUTE'), 'pas pour PUBLIC';
    ASSERT NOT has_function_privilege('pharma_cloud_etat', 'cloud_ecran_titulaire_reglages_envois(text, int, int)', 'EXECUTE'), 'page d''état : rien';
    ASSERT cloud_reglages_communs_controle('{"format":"reglages-v1","reglages":{"jours_orange":120,"jours_rouge":60,"jours_securite":0}}') IS NULL, 'jeu valide';
    ASSERT cloud_reglages_communs_controle('{"format":"reglages-v1","reglages":{}}') = 'vide', 'vide';
    ASSERT cloud_reglages_communs_controle('{"format":"autre","reglages":{"jours_orange":120}}') = 'format', 'format';
    ASSERT cloud_reglages_communs_controle('{"format":"reglages-v1","reglages":{"jours_orange":120},"x":1}') = 'format', 'champ en trop';
    ASSERT cloud_reglages_communs_controle('["reglages-v1"]') = 'format', 'pas un objet';
    ASSERT cloud_reglages_communs_controle(NULL) = 'format', 'NULL';
    ASSERT cloud_reglages_communs_controle('{"format":"reglages-v1","reglages":{"taux_tva":19}}') = 'hors_liste', 'TVA : hors liste';
    ASSERT cloud_reglages_communs_controle('{"format":"reglages-v1","reglages":{"prix_public":100}}') = 'hors_liste', 'prix : hors liste';
    ASSERT cloud_reglages_communs_controle('{"format":"reglages-v1","reglages":{"conservation_annees":6}}') = 'hors_liste', 'fiscal : hors liste';
    ASSERT cloud_reglages_communs_controle('{"format":"reglages-v1","reglages":{"jours_orange":120,"permissions":1}}') = 'hors_liste', 'droits : hors liste';
    ASSERT cloud_reglages_communs_controle('{"format":"reglages-v1","reglages":{"jours_orange":0}}') = 'hors_bornes', 'sous la borne';
    ASSERT cloud_reglages_communs_controle('{"format":"reglages-v1","reglages":{"jours_orange":1096}}') = 'hors_bornes', 'au-dessus de la borne';
    ASSERT cloud_reglages_communs_controle('{"format":"reglages-v1","reglages":{"delai_fournisseur_jours":-1}}') = 'hors_bornes', 'négatif';
    ASSERT cloud_reglages_communs_controle('{"format":"reglages-v1","reglages":{"jours_orange":12.5}}') = 'hors_bornes', 'non entier';
    ASSERT cloud_reglages_communs_controle('{"format":"reglages-v1","reglages":{"jours_orange":"120"}}') = 'hors_bornes', 'texte';
    ASSERT cloud_reglages_communs_controle('{"format":"reglages-v1","reglages":{"jours_orange":99999999999}}') = 'hors_bornes', 'trop grand';
    ASSERT cloud_reglages_communs_controle('{"format":"reglages-v1","reglages":{"jours_orange":60,"jours_rouge":60}}') = 'incoherent', 'rouge >= orange';
    ASSERT (SELECT count(*) FROM (VALUES ('jours_orange'), ('jours_rouge'), ('horizon_risque_jours'), ('delai_fournisseur_jours'), ('jours_securite'),
            ('jours_cible_commande'), ('jours_dormant'), ('seuil_lot_delaisse'), ('seuil_lot_delaisse_collectif')) k(c)
            WHERE (cloud_reglage_commun_bornes(k.c)).minimum IS NOT NULL) = 9, 'neuf réglages dans la liste fermée';
END $$;

-- Données d'essai : O1, O2 ; titulaires A (O1, O2) et B (O2) ; A a le droit « ordres » sur O1 seulement
INSERT INTO officine (id, libelle) VALUES ('11111111-0000-0000-0000-000000000350', 'Essai 0350 O1'), ('22222222-0000-0000-0000-000000000350', 'Essai 0350 O2');
SELECT cloud_compte_distant_creer('titulaire.a350', 'Titulaire A 0350', 'titulaire', '$2a$12$' || repeat('a', 53), NULL, 'a350@exemple.invalid', 'tests 035') \g /dev/null
SELECT cloud_compte_distant_creer('titulaire.b350', 'Titulaire B 0350', 'titulaire', '$2a$12$' || repeat('a', 53), NULL, 'b350@exemple.invalid', 'tests 035') \g /dev/null
UPDATE compte_distant SET mot_de_passe_provisoire = false WHERE identifiant IN ('titulaire.a350', 'titulaire.b350');
INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le)
SELECT repeat(x.j, 64), c.id, 'ouverte', now() + interval '2 hours'
FROM (VALUES ('titulaire.a350', '7'), ('titulaire.b350', '8')) x(ident, j) JOIN compte_distant c ON c.identifiant = x.ident;
INSERT INTO compte_officine (compte_id, officine_id, accorde_par, droits)
SELECT c.id, o.id, 'tests 035', o.d::text[] FROM compte_distant c,
       (VALUES ('11111111-0000-0000-0000-000000000350'::uuid, '{consulter,journal_acces,ordres}'), ('22222222-0000-0000-0000-000000000350'::uuid, '{consulter,journal_acces}')) o(id, d)
WHERE c.identifiant = 'titulaire.a350';
INSERT INTO compte_officine (compte_id, officine_id, accorde_par, droits)
SELECT c.id, '22222222-0000-0000-0000-000000000350', 'tests 035', '{consulter,ordres}' FROM compte_distant c WHERE c.identifiant = 'titulaire.b350';

-- La table elle-même : « reglages » jamais par la console, jamais hors liste
DO $$ BEGIN
    PERFORM set_config('app.officine_id', '11111111-0000-0000-0000-000000000350', true);
    BEGIN INSERT INTO ordre_central (officine_id, type, auteur, expire_le, donnees, envoi_id)
          VALUES ('11111111-0000-0000-0000-000000000350', 'reglages', 'console', now() + interval '1 hour',
                  '{"format":"reglages-v1","reglages":{"jours_orange":120}}', gen_random_uuid());
          RAISE EXCEPTION 'réglages communs acceptés depuis la console';
    EXCEPTION WHEN check_violation THEN NULL; END;
    BEGIN INSERT INTO ordre_central (officine_id, type, auteur, expire_le, donnees, origine, compte_id, envoi_id)
          SELECT '11111111-0000-0000-0000-000000000350', 'reglages', 'x', now() + interval '1 hour', '{"format":"reglages-v1","reglages":{"taux_tva":9}}',
                 'titulaire', c.id, gen_random_uuid() FROM compte_distant c WHERE c.identifiant = 'titulaire.a350';
          RAISE EXCEPTION 'réglage hors liste accepté par la table';
    EXCEPTION WHEN check_violation THEN NULL; END;
    BEGIN INSERT INTO ordre_central (officine_id, type, auteur, expire_le, donnees, origine, compte_id, envoi_id)
          SELECT '11111111-0000-0000-0000-000000000350', 'reglages', 'x', now() + interval '1 hour', '{"format":"reglages-v1","reglages":{"jours_orange":5000}}',
                 'titulaire', c.id, gen_random_uuid() FROM compte_distant c WHERE c.identifiant = 'titulaire.a350';
          RAISE EXCEPTION 'valeur hors bornes acceptée par la table';
    EXCEPTION WHEN check_violation THEN NULL; END;
    BEGIN INSERT INTO ordre_central (officine_id, type, auteur, expire_le, origine, compte_id, envoi_id)
          SELECT '11111111-0000-0000-0000-000000000350', 'essai', 'x', now() + interval '1 hour', 'titulaire', c.id, gen_random_uuid()
          FROM compte_distant c WHERE c.identifiant = 'titulaire.a350';
          RAISE EXCEPTION 'envoi sur un autre type accepté';
    EXCEPTION WHEN check_violation THEN NULL; END;
    PERFORM set_config('app.officine_id', '', true);
END $$;

-- Refus avant le code : tout ou rien, rien n'est créé
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ DECLARE j jsonb := '{"format":"reglages-v1","reglages":{"jours_orange":120,"jours_rouge":60}}'; BEGIN
    ASSERT (SELECT statut || ':' || officine_id FROM cloud_ecran_titulaire_envoyer_reglages(repeat('7', 64),
            '{11111111-0000-0000-0000-000000000350,22222222-0000-0000-0000-000000000350}', j, NULL)) = 'refuse:22222222-0000-0000-0000-000000000350',
           'O2 sans droit « ordres » : tout l''envoi refusé';
    ASSERT (SELECT statut || ':' || officine_id FROM cloud_ecran_titulaire_envoyer_reglages(repeat('8', 64), '{11111111-0000-0000-0000-000000000350}', j, NULL))
           = 'refuse:11111111-0000-0000-0000-000000000350', 'B : O1 non rattachée';
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_envoyer_reglages(repeat('7', 64), '{11111111-0000-0000-0000-000000000350}',
            '{"format":"reglages-v1","reglages":{"taux_tva":19}}', NULL)) = 'hors_liste', 'hors liste';
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_envoyer_reglages(repeat('7', 64), '{11111111-0000-0000-0000-000000000350}',
            '{"format":"reglages-v1","reglages":{"jours_orange":0}}', NULL)) = 'hors_bornes', 'hors bornes';
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_envoyer_reglages(repeat('7', 64), '{11111111-0000-0000-0000-000000000350}',
            '{"format":"reglages-v1","reglages":{"jours_orange":60,"jours_rouge":90}}', NULL)) = 'incoherent', 'incohérent';
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_envoyer_reglages(repeat('7', 64), '{}', j, NULL)) = 'aucune_officine', 'aucune officine';
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_envoyer_reglages(repeat('7', 64), '{11111111-0000-0000-0000-000000000350}', j, repeat('e', 64))) = 'aucun',
           'aucun code demandé';
    BEGIN PERFORM 1 FROM ordre_central; RAISE EXCEPTION 'lecture directe des ordres';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM cloud_ecran_titulaire_envoyer_reglages(repeat('0', 64), '{11111111-0000-0000-0000-000000000350}', j, NULL); RAISE EXCEPTION 'envoi sans session';
    EXCEPTION WHEN invalid_authorization_specification THEN NULL; END;
END $$;
RESET ROLE;
DO $$ BEGIN
    ASSERT NOT EXISTS (SELECT 1 FROM ordre_central WHERE type = 'reglages'), 'rien de créé (propriétaire, toutes officines)';
END $$;

-- A reçoit le droit « ordres » sur O2 (au poste en service) ; codes de confirmation simulés (empreintes HMAC)
UPDATE compte_officine x SET droits = '{consulter,journal_acces,ordres}' FROM compte_distant c
WHERE c.id = x.compte_id AND c.identifiant = 'titulaire.a350' AND x.officine_id = '22222222-0000-0000-0000-000000000350';
INSERT INTO code_second_facteur (compte_id, session_id, objet, empreinte_code, canal, expire_le)
SELECT s.compte_id, s.id, 'action', repeat('f', 64), 'fichier', now() + interval '5 minutes' FROM session_distante s WHERE s.empreinte_jeton = repeat('7', 64);
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ DECLARE j jsonb := '{"format":"reglages-v1","reglages":{"jours_orange":120,"jours_rouge":60}}'; BEGIN
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_envoyer_reglages(repeat('7', 64),
            '{11111111-0000-0000-0000-000000000350,22222222-0000-0000-0000-000000000350}', j, repeat('e', 64))) = 'faux', 'code faux';
    ASSERT (SELECT statut || ':' || ordres FROM cloud_ecran_titulaire_envoyer_reglages(repeat('7', 64),
            '{22222222-0000-0000-0000-000000000350,11111111-0000-0000-0000-000000000350,11111111-0000-0000-0000-000000000350}', j, repeat('f', 64))) = 'ok:2',
           'envoi à deux officines (doublon ignoré)';
    -- Rejeu du même envoi : refusé avant le code (un jeu ouvert par officine)
    ASSERT (SELECT statut || ':' || officine_id FROM cloud_ecran_titulaire_envoyer_reglages(repeat('7', 64), '{11111111-0000-0000-0000-000000000350}', j, repeat('f', 64)))
           = 'deja_en_cours:11111111-0000-0000-0000-000000000350', 'rejeu : un seul jeu ouvert';
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_reglages_envois(repeat('7', 64), 1, 10)) = 2, 'A : deux lignes';
    ASSERT (SELECT string_agg(officine || ':' || statut, ' ' ORDER BY officine) FROM cloud_ecran_titulaire_reglages_envois(repeat('7', 64), 1, 10))
           = 'Essai 0350 O1:en_attente Essai 0350 O2:en_attente', 'statut en attente';
    ASSERT (SELECT reglages FROM cloud_ecran_titulaire_reglages_envois(repeat('7', 64), 1, 10) LIMIT 1) = '{"jours_orange":120,"jours_rouge":60}'::jsonb, 'réglages';
    -- B a le droit « ordres » sur O2, où A a envoyé : il ne voit pas les envois de A
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_reglages_envois(repeat('8', 64), 1, 10)) = 0, 'B ne voit pas les envois de A';
    ASSERT (SELECT string_agg(type || ':' || acteur, ' ') FROM cloud_ecran_titulaire_ordres(repeat('8', 64), '22222222-0000-0000-0000-000000000350', 1, 20))
           = 'reglages:titulaire', 'B : type seulement, jamais les données';
END $$;
RESET ROLE;
DO $$ DECLARE o ordre_central; d descendant; v_envoi uuid; BEGIN
    PERFORM set_config('app.officine_id', '11111111-0000-0000-0000-000000000350', true);
    SELECT * INTO o FROM ordre_central WHERE type = 'reglages';
    v_envoi := o.envoi_id;
    ASSERT o.origine = 'titulaire' AND o.statut = 'en_attente' AND o.cible IS NULL AND o.attendu = '{}'::jsonb, 'ordre du titulaire';
    ASSERT o.donnees = '{"format":"reglages-v1","reglages":{"jours_orange":120,"jours_rouge":60}}'::jsonb, 'données';
    ASSERT o.expire_le BETWEEN now() + interval '23 hours 59 minutes' AND now() + interval '24 hours', '24 heures';
    ASSERT o.auteur = 'titulaire à distance (titulaire.a350)', 'auteur';
    SELECT * INTO d FROM descendant WHERE nom_table = 'ordre_central' AND cle = o.id::text;
    ASSERT d.donnees ->> 'format' = 'ordre-v1' AND d.donnees ->> 'type' = 'reglages' AND d.donnees -> 'donnees' = o.donnees, 'publié ordre-v1';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'ordre_cree' AND detail LIKE '%' || o.id || '%reglages : jours_orange, jours_rouge%'), 'journal de sécurité';
    -- Résultat remonté par O1 : accepté
    INSERT INTO sync_ordre_resultat (officine_id, ordre_id, sync_epoque, sync_numero, statut, motif, traite_le)
    VALUES (o.officine_id, o.id, 1, 1, 'applique', NULL, now());
    PERFORM set_config('app.officine_id', '22222222-0000-0000-0000-000000000350', true);
    SELECT * INTO o FROM ordre_central WHERE type = 'reglages';
    ASSERT o.envoi_id = v_envoi, 'même envoi pour les deux officines';
    -- Résultat remonté par O2 : refusé par le titulaire local
    INSERT INTO sync_ordre_resultat (officine_id, ordre_id, sync_epoque, sync_numero, statut, motif, traite_le)
    VALUES (o.officine_id, o.id, 1, 1, 'refuse', 'refusé par le titulaire : pas maintenant', now());
    PERFORM set_config('app.officine_id', '', true);
END $$;
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ BEGIN
    ASSERT (SELECT string_agg(officine || ':' || statut, ' ' ORDER BY officine) FROM cloud_ecran_titulaire_reglages_envois(repeat('7', 64), 1, 10))
           = 'Essai 0350 O1:applique Essai 0350 O2:refuse', 'accepté / refusé visibles au central';
END $$;
RESET ROLE;

-- Nouveau jeu vers O1 (le précédent est traité), puis échu : montré expiré, puis expiré par cloud_expirer_ordres
INSERT INTO code_second_facteur (compte_id, session_id, objet, empreinte_code, canal, expire_le)
SELECT s.compte_id, s.id, 'action', repeat('d', 64), 'fichier', now() + interval '5 minutes' FROM session_distante s WHERE s.empreinte_jeton = repeat('7', 64);
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ BEGIN
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_envoyer_reglages(repeat('7', 64), '{11111111-0000-0000-0000-000000000350}',
            '{"format":"reglages-v1","reglages":{"jours_dormant":120}}', repeat('d', 64))) = 'ok', 'nouveau jeu vers O1';
END $$;
RESET ROLE;
DO $$ BEGIN
    PERFORM set_config('app.officine_id', '11111111-0000-0000-0000-000000000350', true);
    UPDATE ordre_central SET cree_le = now() - interval '25 hours', expire_le = now() - interval '1 hour' WHERE type = 'reglages' AND statut = 'en_attente';
    PERFORM set_config('app.officine_id', '', true);
END $$;
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ BEGIN
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_reglages_envois(repeat('7', 64), 1, 10) WHERE reglages ? 'jours_dormant') = 'expire', 'échu : montré expiré';
END $$;
RESET ROLE;
DO $$ BEGIN
    ASSERT cloud_expirer_ordres() >= 1, 'expiration';
    PERFORM set_config('app.officine_id', '11111111-0000-0000-0000-000000000350', true);
    ASSERT (SELECT statut || '|' || motif FROM ordre_central WHERE type = 'reglages' AND donnees -> 'reglages' ? 'jours_dormant') = 'expire|non traité dans les 24 heures',
           'expiré au central';
    PERFORM set_config('app.officine_id', '', true);
END $$;

-- Journaux : refus notés, envoi noté, chaîne intacte ; jamais l'identifiant au journal de sécurité
DO $$ BEGIN
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'envoi_reglages_titulaire' AND resultat = 'refus' AND objet = 'hors_liste'), 'hors liste noté';
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'reglages_titulaire' AND resultat = 'refus' AND objet = 'droit:ordres'), 'droit absent noté';
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'envoi_reglages_titulaire' AND resultat = 'refus' AND objet = 'code:faux'), 'code faux noté';
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'reglages_titulaire' AND resultat = 'refus' AND objet = 'deja_en_cours'), 'rejeu noté';
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'envoi_reglages_titulaire' AND resultat = 'ok' AND objet = 'officines:2'), 'envoi noté';
    ASSERT (SELECT count(*) FROM journal_acces_distant WHERE evenement = 'reglages_titulaire' AND resultat = 'ok'
            AND officine_id::text LIKE '%-000000000350') = 3, 'un ordre noté par officine';   -- officines du test seulement (base réelle : d''autres envois existent)
    ASSERT NOT EXISTS (SELECT 1 FROM journal_securite WHERE detail LIKE '%titulaire.a350%'), 'jamais l''identifiant au journal de sécurité';
    ASSERT (SELECT premiere_rupture FROM cloud_ecran_verifier_journal_acces()) IS NULL, 'chaîne intacte';
END $$;

ROLLBACK;
\echo '035 : tous réussis'
