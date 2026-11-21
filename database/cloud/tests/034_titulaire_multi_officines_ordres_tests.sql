-- =====================================================================
-- Tests du script 034 (multi-officines et ordres du titulaire), en transaction ANNULÉE :
--   psql -U <propriétaire> -d <base centrale de test> -v ON_ERROR_STOP=1 -f database/cloud/tests/034_titulaire_multi_officines_ordres_tests.sql
-- Titulaire A rattaché à O1 et O2, titulaire B à O2 seulement : B ne voit jamais O1 (vue d'ensemble, tableau, ordres) ; vue
-- d'ensemble (une ligne par officine, « non disponible » rendu NULL) ; droit « ordres » accordé par le poste seulement ; ordre
-- refusé sans ce droit, hors de la liste fermée, déjà en cours, code faux ; ordre créé (origine titulaire, 24 h, publié ordre-v1) ;
-- ordre expiré montré « expire » puis expiré par cloud_expirer_ordres ; retrait d'un compte par le poste : accès coupé tout de suite,
-- ordres ouverts annulés ; certificat d'une autre officine refusé ; journaux (chaîne intacte).
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

-- Droits : trois fonctions pour les écrans, trois pour l'API ; contrôles internes jamais accordés
DO $$ BEGIN
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_titulaire_ensemble(text)', 'EXECUTE'), 'vue d''ensemble';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_titulaire_ordres(text, uuid, int, int)', 'EXECUTE'), 'liste des ordres';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_titulaire_creer_ordre(text, uuid, text, text)', 'EXECUTE'), 'création d''un ordre';
    ASSERT has_function_privilege('pharma_cloud_api', 'cloud_poste_comptes_distants(text)', 'EXECUTE'), 'API : comptes';
    ASSERT has_function_privilege('pharma_cloud_api', 'cloud_poste_compte_droits(text, text, text[])', 'EXECUTE'), 'API : droits';
    ASSERT has_function_privilege('pharma_cloud_api', 'cloud_poste_compte_retirer(text, text)', 'EXECUTE'), 'API : retrait';
    ASSERT NOT has_function_privilege('pharma_cloud_ecrans', 'cloud_poste_compte_droits(text, text, text[])', 'EXECUTE'), 'droits : jamais par les écrans';
    ASSERT NOT has_function_privilege('pharma_cloud_ecrans', 'cloud_poste_compte_retirer(text, text)', 'EXECUTE'), 'retrait : jamais par les écrans';
    ASSERT NOT has_function_privilege('pharma_cloud_api', 'cloud_ecran_titulaire_creer_ordre(text, uuid, text, text)', 'EXECUTE'), 'API : pas les écrans';
    ASSERT NOT has_function_privilege('public', 'cloud_ecran_titulaire_creer_ordre(text, uuid, text, text)', 'EXECUTE'), 'pas pour PUBLIC';
    ASSERT NOT has_function_privilege('public', 'cloud_poste_compte_retirer(text, text)', 'EXECUTE'), 'retrait : pas pour PUBLIC';
    ASSERT NOT has_function_privilege('pharma_cloud_api', 'cloud_poste_officine(text)', 'EXECUTE'), 'contrôle interne (API)';
    ASSERT NOT has_function_privilege('pharma_cloud_ecrans', 'cloud_ordres_compte_annuler(uuid, uuid)', 'EXECUTE'), 'annulation interne (écrans)';
    ASSERT NOT has_function_privilege('pharma_cloud_api', 'cloud_ordres_compte_annuler(uuid, uuid)', 'EXECUTE'), 'annulation interne (API)';
    ASSERT NOT has_function_privilege('pharma_cloud_etat', 'cloud_poste_comptes_distants(text)', 'EXECUTE'), 'page d''état : rien';
    ASSERT cloud_ordre_titulaire_permis('sauvegarde') AND cloud_ordre_titulaire_permis('verification_integrite') AND cloud_ordre_titulaire_permis('essai'), 'liste fermée';
    ASSERT NOT cloud_ordre_titulaire_permis('prix_produit') AND NOT cloud_ordre_titulaire_permis('employe_bloquer') AND NOT cloud_ordre_titulaire_permis(NULL), 'hors liste';
END $$;

-- Données d'essai : O1 et O2 actives (certificats), titulaires A et B, un éditeur ; A rattaché à O1 et O2, B à O2
INSERT INTO officine (id, libelle) VALUES ('11111111-0000-0000-0000-000000000340', 'Essai 0340 O1'), ('22222222-0000-0000-0000-000000000340', 'Essai 0340 O2');
INSERT INTO certificat (empreinte, officine_id, serveur, numero_serie, echeance)
VALUES (repeat('1', 63) || '4', '11111111-0000-0000-0000-000000000340', 'principal', '3401', now() + interval '1 year'),
       (repeat('2', 63) || '4', '22222222-0000-0000-0000-000000000340', 'principal', '3402', now() + interval '1 year');
SELECT cloud_compte_distant_creer('titulaire.a340', 'Titulaire A 0340', 'titulaire', '$2a$12$' || repeat('a', 53), NULL, 'a340@exemple.invalid', 'tests 034') \g /dev/null
SELECT cloud_compte_distant_creer('titulaire.b340', 'Titulaire B 0340', 'titulaire', '$2a$12$' || repeat('a', 53), NULL, 'b340@exemple.invalid', 'tests 034') \g /dev/null
SELECT cloud_compte_distant_creer('editeur.340', 'Éditeur 0340', 'editeur', '$2a$12$' || repeat('a', 53), NULL, 'e340@exemple.invalid', 'tests 034') \g /dev/null
UPDATE compte_distant SET mot_de_passe_provisoire = false WHERE identifiant IN ('titulaire.a340', 'titulaire.b340', 'editeur.340');
INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le)
SELECT repeat(x.j, 64), c.id, 'ouverte', now() + interval '2 hours'
FROM (VALUES ('titulaire.a340', '4'), ('titulaire.b340', '5'), ('editeur.340', '6')) x(ident, j) JOIN compte_distant c ON c.identifiant = x.ident;
INSERT INTO compte_officine (compte_id, officine_id, accorde_par)
SELECT c.id, o.id, 'tests 034' FROM compte_distant c, (VALUES ('11111111-0000-0000-0000-000000000340'::uuid), ('22222222-0000-0000-0000-000000000340'::uuid)) o(id)
WHERE c.identifiant = 'titulaire.a340';
INSERT INTO compte_officine (compte_id, officine_id, accorde_par)
SELECT c.id, '22222222-0000-0000-0000-000000000340', 'tests 034' FROM compte_distant c WHERE c.identifiant = 'titulaire.b340';
DO $$ BEGIN
    ASSERT (SELECT droits FROM compte_officine x JOIN compte_distant c ON c.id = x.compte_id WHERE c.identifiant = 'titulaire.a340'
            AND x.officine_id = '11111111-0000-0000-0000-000000000340') = '{consulter,journal_acces}', 'droits par défaut : jamais « ordres »';
    PERFORM set_config('app.officine_id', '11111111-0000-0000-0000-000000000340', true);
    BEGIN INSERT INTO ordre_central (officine_id, type, auteur, expire_le, origine, compte_id)
          SELECT '11111111-0000-0000-0000-000000000340', 'prix_produit', 'x', now() + interval '1 hour', 'titulaire', c.id FROM compte_distant c WHERE c.identifiant = 'titulaire.a340';
          RAISE EXCEPTION 'ordre du titulaire hors liste accepté par la table';
    EXCEPTION WHEN check_violation THEN NULL; END;
    BEGIN INSERT INTO ordre_central (officine_id, type, auteur, expire_le, origine, compte_id)
          SELECT '11111111-0000-0000-0000-000000000340', 'sauvegarde', 'x', now() + interval '7 days', 'titulaire', c.id FROM compte_distant c WHERE c.identifiant = 'titulaire.a340';
          RAISE EXCEPTION 'ordre du titulaire de plus de 24 h accepté par la table';
    EXCEPTION WHEN check_violation THEN NULL; END;
    PERFORM set_config('app.officine_id', '', true);
END $$;

-- O1 : chargement complet terminé, ventes et retour (montants reconnaissables) ; O2 : vente reçue sans chargement terminé
SELECT set_config('app.officine_id', '11111111-0000-0000-0000-000000000340', true) \g /dev/null
INSERT INTO chargement (officine_id, numero, epoque, repere, motif, tables, etat, fin_le)
VALUES ('11111111-0000-0000-0000-000000000340', 1, 1, 0, 'activation', '{vente,retour_client}', 'termine', now());
INSERT INTO vente (officine_id, id, sync_epoque, sync_numero, numero, date_vente, montant_total, part_organisme, canal_organisme, statut)
VALUES ('11111111-0000-0000-0000-000000000340', 1, 1, 1, 'T-1', now(), 31313.13, 0, 'logiciel', 'validee');
SELECT set_config('app.officine_id', '22222222-0000-0000-0000-000000000340', true) \g /dev/null
INSERT INTO vente (officine_id, id, sync_epoque, sync_numero, numero, date_vente, montant_total, part_organisme, canal_organisme, statut)
VALUES ('22222222-0000-0000-0000-000000000340', 1, 1, 1, 'T-1', now(), 4242.42, 0, 'logiciel', 'validee');
SELECT set_config('app.officine_id', '', true) \g /dev/null

SET LOCAL ROLE pharma_cloud_ecrans;
-- Vue d'ensemble : A voit O1 et O2, B seulement O2 ; « non disponible » = NULL ; l'éditeur n'a rien
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_ensemble(repeat('4', 64))) = 2, 'A : deux officines';
    ASSERT (SELECT ca_jour FROM cloud_ecran_titulaire_ensemble(repeat('4', 64)) WHERE id = '11111111-0000-0000-0000-000000000340') = 31313.13, 'O1 : chiffre du jour';
    ASSERT (SELECT ca_jour IS NULL FROM cloud_ecran_titulaire_ensemble(repeat('4', 64)) WHERE id = '22222222-0000-0000-0000-000000000340'), 'O2 : non disponible (NULL, jamais 0)';
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_ensemble(repeat('5', 64))) = 1, 'B : une officine';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_ecran_titulaire_ensemble(repeat('5', 64)) WHERE id = '11111111-0000-0000-0000-000000000340'), 'B ne voit jamais O1';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_ecran_titulaire_ensemble(repeat('5', 64)) WHERE ca_jour = 31313.13 OR ventes_jour > 0), 'aucun montant de O1 chez B';
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_tableau(repeat('5', 64), '11111111-0000-0000-0000-000000000340')) = 0, 'B : tableau de O1 refusé';
    -- Officine posée à la main : rien ne change (la base pose l'officine elle-même)
    PERFORM set_config('app.officine_id', '11111111-0000-0000-0000-000000000340', true);
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_ensemble(repeat('5', 64))) = 1, 'B : officine posée à la main sans effet';
    PERFORM set_config('app.officine_id', '', true);
    BEGIN PERFORM cloud_ecran_titulaire_ensemble(repeat('6', 64)); RAISE EXCEPTION 'vue d''ensemble pour l''éditeur';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM cloud_ecran_titulaire_creer_ordre(repeat('6', 64), '11111111-0000-0000-0000-000000000340', 'essai', NULL); RAISE EXCEPTION 'ordre pour l''éditeur';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM cloud_ecran_titulaire_ensemble(repeat('0', 64)); RAISE EXCEPTION 'vue d''ensemble sans session';
    EXCEPTION WHEN invalid_authorization_specification THEN NULL; END;
    BEGIN PERFORM 1 FROM ordre_central; RAISE EXCEPTION 'lecture directe des ordres';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    -- Sans le droit « ordres » : liste vide et création refusée ; hors liste refusé avant tout
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_ordres(repeat('4', 64), '11111111-0000-0000-0000-000000000340', 1, 20)) = 0, 'liste sans droit';
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_creer_ordre(repeat('4', 64), '11111111-0000-0000-0000-000000000340', 'sauvegarde', NULL)) = 'refuse', 'ordre sans droit';
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_creer_ordre(repeat('4', 64), '11111111-0000-0000-0000-000000000340', 'prix_produit', NULL)) = 'hors_liste', 'prix : hors liste';
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_creer_ordre(repeat('4', 64), '11111111-0000-0000-0000-000000000340', 'employe_bloquer', NULL)) = 'hors_liste', 'employé : hors liste';
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_creer_ordre(repeat('5', 64), '11111111-0000-0000-0000-000000000340', 'essai', NULL)) = 'refuse', 'B : O1 non rattachée';
END $$;
RESET ROLE;

-- Poste de O1 (API) : comptes rattachés, droit « ordres » accordé à A ; autre certificat refusé ; droits invalides ; compte inconnu
SET LOCAL ROLE pharma_cloud_api;
DO $$ BEGIN
    BEGIN PERFORM cloud_poste_comptes_distants(repeat('1', 63) || '4'); RAISE EXCEPTION 'officine non posée acceptée';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    PERFORM set_config('app.officine_id', '11111111-0000-0000-0000-000000000340', true);
    BEGIN PERFORM cloud_poste_compte_droits(repeat('2', 63) || '4', 'titulaire.a340', '{consulter,ordres}'); RAISE EXCEPTION 'certificat de O2 accepté pour O1';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    ASSERT (SELECT string_agg(identifiant || ':' || droits, ' ') FROM cloud_poste_comptes_distants(repeat('1', 63) || '4')) = 'titulaire.a340:consulter,journal_acces',
           'O1 : seul A, jamais B';
    ASSERT (SELECT statut FROM cloud_poste_compte_droits(repeat('1', 63) || '4', 'titulaire.a340', '{consulter,admin}')) = 'droits_invalides', 'droit inconnu';
    ASSERT (SELECT statut FROM cloud_poste_compte_droits(repeat('1', 63) || '4', 'titulaire.a340', '{}')) = 'droits_invalides', 'aucun droit';
    ASSERT (SELECT statut FROM cloud_poste_compte_droits(repeat('1', 63) || '4', 'titulaire.b340', '{consulter,ordres}')) = 'inconnu', 'B non rattaché à O1';
    ASSERT (SELECT statut FROM cloud_poste_compte_droits(repeat('1', 63) || '4', 'Pas Un Identifiant', '{consulter}')) = 'inconnu', 'identifiant mal formé';
    ASSERT (SELECT statut FROM cloud_poste_compte_droits(repeat('1', 63) || '4', ' Titulaire.A340 ', '{ordres,consulter,journal_acces,ordres}')) = 'ok', 'A : ordres accordé';
    PERFORM set_config('app.officine_id', '', true);
END $$;
RESET ROLE;
DO $$ BEGIN
    ASSERT (SELECT droits FROM compte_officine x JOIN compte_distant c ON c.id = x.compte_id WHERE c.identifiant = 'titulaire.a340'
            AND x.officine_id = '11111111-0000-0000-0000-000000000340') = '{consulter,journal_acces,ordres}', 'droits triés, sans doublon';
    ASSERT (SELECT droits FROM compte_officine x JOIN compte_distant c ON c.id = x.compte_id WHERE c.identifiant = 'titulaire.a340'
            AND x.officine_id = '22222222-0000-0000-0000-000000000340') = '{consulter,journal_acces}', 'O2 inchangée';
END $$;

-- Codes de confirmation (empreintes HMAC simulées) pour la session de A
INSERT INTO code_second_facteur (compte_id, session_id, objet, empreinte_code, canal, expire_le)
SELECT s.compte_id, s.id, 'action', repeat('f', 64), 'fichier', now() + interval '5 minutes' FROM session_distante s WHERE s.empreinte_jeton = repeat('4', 64);
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ DECLARE v_id uuid; BEGIN
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_creer_ordre(repeat('4', 64), '11111111-0000-0000-0000-000000000340', 'sauvegarde', repeat('e', 64))) = 'faux', 'code faux';
    SELECT ordre_id INTO v_id FROM cloud_ecran_titulaire_creer_ordre(repeat('4', 64), '11111111-0000-0000-0000-000000000340', 'sauvegarde', repeat('f', 64));
    ASSERT v_id IS NOT NULL, 'ordre créé';
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_creer_ordre(repeat('4', 64), '11111111-0000-0000-0000-000000000340', 'sauvegarde', repeat('f', 64))) = 'deja_en_cours',
           'un seul ordre ouvert par type (contrôlé avant le code)';
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_creer_ordre(repeat('4', 64), '11111111-0000-0000-0000-000000000340', 'essai', repeat('f', 64))) = 'aucun', 'code à usage unique';
    ASSERT (SELECT string_agg(type || ':' || statut || ':' || acteur, ' ') FROM cloud_ecran_titulaire_ordres(repeat('4', 64), '11111111-0000-0000-0000-000000000340', 1, 20))
           = 'sauvegarde:en_attente:vous', 'liste de A';
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_ordres(repeat('5', 64), '11111111-0000-0000-0000-000000000340', 1, 20)) = 0, 'B ne voit pas les ordres de O1';
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_ordres(repeat('4', 64), '22222222-0000-0000-0000-000000000340', 1, 20)) = 0, 'O2 : droit « ordres » absent';
END $$;
RESET ROLE;
DO $$ DECLARE o ordre_central; d descendant; BEGIN
    PERFORM set_config('app.officine_id', '11111111-0000-0000-0000-000000000340', true);
    SELECT * INTO o FROM ordre_central WHERE origine = 'titulaire';
    ASSERT o.type = 'sauvegarde' AND o.statut = 'en_attente' AND o.donnees = '{}'::jsonb AND o.cible IS NULL, 'ordre sans donnée';
    ASSERT o.expire_le BETWEEN now() + interval '23 hours 59 minutes' AND now() + interval '24 hours', '24 heures';
    ASSERT o.auteur = 'titulaire à distance (titulaire.a340)' AND o.compte_id IS NOT NULL, 'auteur';
    SELECT * INTO d FROM descendant WHERE nom_table = 'ordre_central' AND cle = o.id::text;
    ASSERT d.donnees ->> 'format' = 'ordre-v1' AND d.donnees ->> 'type' = 'sauvegarde' AND d.donnees ->> 'statut' = 'en_attente', 'publié par la voie descendante';
    ASSERT EXISTS (SELECT 1 FROM ordre_central_journal j WHERE j.ordre_id = o.id AND j.statut = 'en_attente'), 'journal des ordres';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'ordre_cree' AND detail LIKE '%' || o.id || '%écran du titulaire%'), 'journal de sécurité';
    -- Expiré : montré « expire » dans la liste, puis passé « expire » (motif 24 heures) et republié
    UPDATE ordre_central SET cree_le = now() - interval '25 hours', expire_le = now() - interval '1 hour' WHERE id = o.id;
    PERFORM set_config('app.officine_id', '', true);
END $$;
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ BEGIN
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_ordres(repeat('4', 64), '11111111-0000-0000-0000-000000000340', 1, 20)) = 'expire', 'échu : montré expiré';
END $$;
RESET ROLE;
DO $$ BEGIN
    ASSERT cloud_expirer_ordres() >= 1, 'expiration';
    PERFORM set_config('app.officine_id', '11111111-0000-0000-0000-000000000340', true);
    ASSERT (SELECT statut || '|' || motif FROM ordre_central WHERE origine = 'titulaire') = 'expire|non traité dans les 24 heures', 'motif du délai réel';
    ASSERT (SELECT d.donnees ->> 'statut' FROM descendant d JOIN ordre_central o ON d.cle = o.id::text WHERE d.nom_table = 'ordre_central' AND o.origine = 'titulaire') = 'expire',
           'expiration republiée';
    PERFORM set_config('app.officine_id', '', true);
END $$;

-- Nouvel ordre ouvert de A, puis retrait de A par le poste de O1 : accès coupé tout de suite, ordre annulé
INSERT INTO code_second_facteur (compte_id, session_id, objet, empreinte_code, canal, expire_le)
SELECT s.compte_id, s.id, 'action', repeat('d', 64), 'fichier', now() + interval '5 minutes' FROM session_distante s WHERE s.empreinte_jeton = repeat('4', 64);
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ BEGIN
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_creer_ordre(repeat('4', 64), '11111111-0000-0000-0000-000000000340', 'verification_integrite', repeat('d', 64))) = 'ok',
           'vérification d''intégrité demandée';
END $$;
RESET ROLE;
SET LOCAL ROLE pharma_cloud_api;
DO $$ BEGIN
    PERFORM set_config('app.officine_id', '11111111-0000-0000-0000-000000000340', true);
    ASSERT (SELECT statut || ':' || ordres_annules FROM cloud_poste_compte_retirer(repeat('1', 63) || '4', 'titulaire.b340')) = 'inconnu:0', 'B non rattaché à O1';
    ASSERT (SELECT statut || ':' || ordres_annules FROM cloud_poste_compte_retirer(repeat('1', 63) || '4', 'titulaire.a340')) = 'ok:1', 'A retiré, un ordre annulé';
    ASSERT (SELECT statut FROM cloud_poste_compte_retirer(repeat('1', 63) || '4', 'titulaire.a340')) = 'inconnu', 'déjà retiré';
    ASSERT (SELECT count(*) FROM cloud_poste_comptes_distants(repeat('1', 63) || '4')) = 0, 'plus aucun compte rattaché à O1';
    PERFORM set_config('app.officine_id', '', true);
END $$;
RESET ROLE;
DO $$ BEGIN
    PERFORM set_config('app.officine_id', '11111111-0000-0000-0000-000000000340', true);
    ASSERT (SELECT statut FROM ordre_central WHERE type = 'verification_integrite') = 'annule', 'ordre ouvert annulé';
    ASSERT (SELECT d.donnees ->> 'statut' FROM descendant d JOIN ordre_central o ON d.cle = o.id::text WHERE o.type = 'verification_integrite') = 'annule', 'annulation republiée';
    PERFORM set_config('app.officine_id', '', true);
END $$;
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_tableau(repeat('4', 64), '11111111-0000-0000-0000-000000000340')) = 0, 'retrait : tableau refusé tout de suite';
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_ordres(repeat('4', 64), '11111111-0000-0000-0000-000000000340', 1, 20)) = 0, 'retrait : ordres refusés';
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_creer_ordre(repeat('4', 64), '11111111-0000-0000-0000-000000000340', 'essai', NULL)) = 'refuse', 'retrait : création refusée';
    ASSERT (SELECT string_agg(libelle, ',') FROM cloud_ecran_titulaire_ensemble(repeat('4', 64))) = 'Essai 0340 O2', 'retrait : O2 seulement dans la vue d''ensemble';
END $$;
RESET ROLE;

-- Retrait du droit « ordres » (O2 : accordé puis retiré) : ordre ouvert annulé
SET LOCAL ROLE pharma_cloud_api;
DO $$ BEGIN
    PERFORM set_config('app.officine_id', '22222222-0000-0000-0000-000000000340', true);
    ASSERT (SELECT statut FROM cloud_poste_compte_droits(repeat('2', 63) || '4', 'titulaire.b340', '{consulter,ordres}')) = 'ok', 'B : ordres sur O2';
    PERFORM set_config('app.officine_id', '', true);
END $$;
RESET ROLE;
INSERT INTO code_second_facteur (compte_id, session_id, objet, empreinte_code, canal, expire_le)
SELECT s.compte_id, s.id, 'action', repeat('c', 64), 'fichier', now() + interval '5 minutes' FROM session_distante s WHERE s.empreinte_jeton = repeat('5', 64);
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ BEGIN
    ASSERT (SELECT statut FROM cloud_ecran_titulaire_creer_ordre(repeat('5', 64), '22222222-0000-0000-0000-000000000340', 'essai', repeat('c', 64))) = 'ok', 'B : essai sur O2';
END $$;
RESET ROLE;
SET LOCAL ROLE pharma_cloud_api;
DO $$ BEGIN
    PERFORM set_config('app.officine_id', '22222222-0000-0000-0000-000000000340', true);
    ASSERT (SELECT statut || ':' || ordres_annules FROM cloud_poste_compte_droits(repeat('2', 63) || '4', 'titulaire.b340', '{consulter}')) = 'ok:1', 'droit retiré, ordre annulé';
    PERFORM set_config('app.officine_id', '', true);
END $$;
RESET ROLE;

-- Journaux : refus notés (hors liste, droit absent, code faux), actions du poste (acteur « poste »), chaîne intacte
DO $$ BEGIN
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'ordre_titulaire' AND resultat = 'refus' AND objet = 'hors_liste'), 'hors liste noté';
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'ordre_titulaire' AND resultat = 'refus' AND objet = 'droit:ordres'), 'droit absent noté';
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'ordre_titulaire' AND resultat = 'refus' AND objet = 'sauvegarde:faux'), 'code faux noté';
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'retrait_compte_poste' AND resultat = 'ok' AND objet = 'poste'), 'retrait noté';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'retrait_compte_distant' AND officine_id = '11111111-0000-0000-0000-000000000340'), 'retrait au journal de sécurité';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'droits_compte_distant' AND detail LIKE '%→ consulter, journal_acces, ordres%'), 'droits au journal de sécurité';
    ASSERT NOT EXISTS (SELECT 1 FROM journal_securite WHERE detail LIKE '%titulaire.a340%' OR detail LIKE '%titulaire.b340%'), 'jamais l''identifiant au journal de sécurité';
    ASSERT (SELECT premiere_rupture FROM cloud_ecran_verifier_journal_acces()) IS NULL, 'chaîne intacte';
END $$;

ROLLBACK;
\echo '034 : tous réussis'
