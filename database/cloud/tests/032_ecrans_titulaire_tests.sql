-- =====================================================================
-- Tests du script 032 (écrans du titulaire), en transaction ANNULÉE :
--   psql -U <propriétaire> -d <base centrale de test> -v ON_ERROR_STOP=1 -f database/cloud/tests/032_ecrans_titulaire_tests.sql
-- Deux titulaires, deux officines : non-fuite (A ne voit jamais B), rattachement par code (expiré, réutilisé, destiné à un autre
-- compte : refusés ; 5 refus par quart d'heure), droits (consulter, journal des accès), éditeur sans accès aux écrans du titulaire,
-- agrégats exacts et « non disponible » (NULL) quand la donnée n'est pas montée, aucune colonne nominative, journaux.
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

-- Droits : quatre fonctions pour les écrans, rien pour PUBLIC, aucune colonne interdite rendue, table des codes inaccessible
DO $$ BEGIN
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_titulaire_officines(text)', 'EXECUTE'), 'mes officines';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_titulaire_tableau(text, uuid)', 'EXECUTE'), 'tableau';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_titulaire_journal(text, uuid, int, int)', 'EXECUTE'), 'journal';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_rattacher(text, text)', 'EXECUTE'), 'rattacher';
    ASSERT NOT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_titulaire(text)', 'EXECUTE'), 'contrôle interne réservé';
    ASSERT NOT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_ouvrir_titulaire(text, uuid, text, text)', 'EXECUTE'), 'ouverture interne réservée';
    ASSERT NOT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_miroir_disponible(text, text[])', 'EXECUTE'), 'disponibilité interne réservée';
    ASSERT NOT has_function_privilege('pharma_cloud_ecrans', 'cloud_compte_distant_code_rattachement(text, uuid, text, text)', 'EXECUTE'), 'code : console seulement';
    ASSERT NOT has_function_privilege('public', 'cloud_ecran_titulaire_tableau(text, uuid)', 'EXECUTE'), 'pas pour PUBLIC';
    ASSERT NOT has_function_privilege('public', 'cloud_compte_distant_code_rattachement(text, uuid, text, text)', 'EXECUTE'), 'code : pas pour PUBLIC';
    ASSERT NOT has_function_privilege('pharma_cloud_api', 'cloud_ecran_titulaire_tableau(text, uuid)', 'EXECUTE'), 'API : pas les écrans';
    ASSERT NOT has_table_privilege('pharma_cloud_ecrans', 'code_rattachement', 'SELECT'), 'aucun droit direct sur les codes';
    ASSERT NOT has_table_privilege('pharma_cloud_api', 'code_rattachement', 'SELECT'), 'API : aucun droit sur les codes';
    ASSERT NOT EXISTS (
        SELECT 1 FROM pg_proc p, unnest(p.proargnames) WITH ORDINALITY AS a(nom, n)
        WHERE p.pronamespace = 'public'::regnamespace AND has_function_privilege('pharma_cloud_ecrans', p.oid, 'EXECUTE')
          AND (p.proname LIKE 'cloud\_ecran\_titulaire\_%' OR p.proname = 'cloud_ecran_rattacher')
          AND p.proargmodes IS NOT NULL AND p.proargmodes[a.n] IN ('o', 't')
          AND a.nom IN ('donnees', 'contenu', 'attendu', 'trouve', 'cle_ligne', 'empreinte_code', 'code', 'empreinte', 'motif', 'cible', 'auteur',
                        'telephone', 'email', 'nom', 'identifiant', 'patient_id', 'utilisateur_id', 'nom_complet', 'numero', 'message',
                        'titulaire', 'adresse', 'compte_id', 'cree_par', 'accorde_par', 'produit_id', 'lot_id')),
        'colonne interdite rendue par une fonction du titulaire';
END $$;

-- Données d'essai : officines A et B (actives), C (partie) ; titulaires A et B, un éditeur ; sessions ouvertes
INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000320', 'Essai 0320 A'), ('bbbbbbbb-0000-0000-0000-000000000320', 'Essai 0320 B');
INSERT INTO officine (id, libelle, resiliee_le) VALUES ('cccccccc-0000-0000-0000-000000000320', 'Essai 0320 C', now() - interval '1 day');
INSERT INTO certificat (empreinte, officine_id, serveur, numero_serie, echeance)
VALUES (repeat('a', 63) || '1', 'aaaaaaaa-0000-0000-0000-000000000320', 'principal', '3201', now() + interval '1 year'),
       (repeat('b', 63) || '1', 'bbbbbbbb-0000-0000-0000-000000000320', 'principal', '3202', now() + interval '1 year');
SELECT cloud_compte_distant_creer('titulaire.a320', 'Titulaire A 0320', 'titulaire', '$2a$12$' || repeat('a', 53), NULL, 'a320@exemple.invalid', 'tests 032') \g /dev/null
SELECT cloud_compte_distant_creer('titulaire.b320', 'Titulaire B 0320', 'titulaire', '$2a$12$' || repeat('a', 53), NULL, 'b320@exemple.invalid', 'tests 032') \g /dev/null
SELECT cloud_compte_distant_creer('editeur.320', 'Éditeur 0320', 'editeur', '$2a$12$' || repeat('a', 53), NULL, 'e320@exemple.invalid', 'tests 032') \g /dev/null
UPDATE compte_distant SET mot_de_passe_provisoire = false WHERE identifiant IN ('titulaire.a320', 'titulaire.b320', 'editeur.320');
INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le)
SELECT repeat(x.j, 64), c.id, 'ouverte', now() + interval '2 hours'
FROM (VALUES ('titulaire.a320', '1'), ('titulaire.b320', '2'), ('editeur.320', '3')) x(ident, j) JOIN compte_distant c ON c.identifiant = x.ident;

-- Officine A : chargement complet terminé (sans la table patient), ventes du jour, du mois précédent, annulée, absente ; retour ;
-- alertes ; paramètre fiscal ; une anomalie, un ordre en attente
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000320', true) \g /dev/null
INSERT INTO chargement (officine_id, numero, epoque, repere, motif, tables, etat, fin_le)
VALUES ('aaaaaaaa-0000-0000-0000-000000000320', 1, 1, 0, 'activation', '{vente,retour_client,alerte,parametre_fiscal}', 'termine', now());
INSERT INTO officine_epoque (officine_id, epoque, dernier_lot, dernier_recu_le) VALUES ('aaaaaaaa-0000-0000-0000-000000000320', 1, 12, now());
INSERT INTO vente (officine_id, id, sync_epoque, sync_numero, numero, date_vente, montant_total, part_organisme, canal_organisme, statut, patient_id, sync_absente_le)
VALUES ('aaaaaaaa-0000-0000-0000-000000000320', 1, 1, 1, 'T-NOMINATIF0320', now(), 1000, 600, 'chifa', 'validee', 'P-NOMINATIF0320', NULL),
       ('aaaaaaaa-0000-0000-0000-000000000320', 2, 1, 2, 'T-2', now(), 500, 0, 'logiciel', 'validee', NULL, NULL),
       ('aaaaaaaa-0000-0000-0000-000000000320', 3, 1, 3, 'T-3', now(), 999, 0, 'logiciel', 'annulee', NULL, NULL),
       ('aaaaaaaa-0000-0000-0000-000000000320', 4, 1, 4, 'T-4', now(), 5000, 0, 'logiciel', 'validee', NULL, now()),
       ('aaaaaaaa-0000-0000-0000-000000000320', 5, 1, 5, 'T-5',
        (date_trunc('month', now() AT TIME ZONE 'Africa/Algiers') - interval '10 days') AT TIME ZONE 'Africa/Algiers', 300, 0, 'logiciel', 'validee', NULL, NULL);
INSERT INTO retour_client (officine_id, id, sync_epoque, sync_numero, vente_id, montant_rembourse, cree_le)
VALUES ('aaaaaaaa-0000-0000-0000-000000000320', 1, 1, 6, 2, 100, now());
INSERT INTO patient (officine_id, id, sync_epoque, sync_numero, solde_credit)
VALUES ('aaaaaaaa-0000-0000-0000-000000000320', 1, 1, 7, 250), ('aaaaaaaa-0000-0000-0000-000000000320', 2, 1, 8, -10);
INSERT INTO alerte (officine_id, id, sync_epoque, sync_numero, type, niveau, statut, message)
VALUES ('aaaaaaaa-0000-0000-0000-000000000320', 1, 1, 9, 'rupture', 'urgent', 'nouvelle', 'NOMINATIF0320'),
       ('aaaaaaaa-0000-0000-0000-000000000320', 2, 1, 10, 'peremption', 'attention', 'vue', 'NOMINATIF0320'),
       ('aaaaaaaa-0000-0000-0000-000000000320', 3, 1, 11, 'perime', 'urgent', 'traitee', 'NOMINATIF0320');
INSERT INTO parametre_fiscal (officine_id, id, sync_epoque, sync_numero, fuseau) VALUES ('aaaaaaaa-0000-0000-0000-000000000320', 1, 1, 12, 'Africa/Algiers');
INSERT INTO cloud_anomalie_integrite (officine_id, journal, cle_chaine, rang, cle_ligne, nature, detail)
VALUES ('aaaaaaaa-0000-0000-0000-000000000320', 'journal_vente', 'global', 3, 'NOMINATIF0320', 'chainage_rompu', 'NOMINATIF0320');
INSERT INTO ordre_central (officine_id, type, cible, donnees, auteur, expire_le)
VALUES ('aaaaaaaa-0000-0000-0000-000000000320', 'essai', 'NOMINATIF0320', '{"nom":"NOMINATIF0320"}', 'NOMINATIF0320', now() + interval '7 days');
-- Officine B : ventes reçues mais AUCUN chargement complet terminé ; montants reconnaissables (non-fuite)
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000320', true) \g /dev/null
INSERT INTO vente (officine_id, id, sync_epoque, sync_numero, numero, date_vente, montant_total, part_organisme, canal_organisme, statut)
VALUES ('bbbbbbbb-0000-0000-0000-000000000320', 1, 1, 1, 'T-B', now(), 77777.77, 7777.77, 'chifa', 'validee');
INSERT INTO patient (officine_id, id, sync_epoque, sync_numero, solde_credit) VALUES ('bbbbbbbb-0000-0000-0000-000000000320', 1, 1, 2, 66666.66);
SELECT set_config('app.officine_id', '', true) \g /dev/null

-- Console : codes de rattachement (empreintes seulement)
DO $$ BEGIN
    ASSERT (SELECT statut FROM cloud_compte_distant_code_rattachement('titulaire.a320', 'aaaaaaaa-0000-0000-0000-000000000320', repeat('a', 64), 'tests 032')) = 'ok', 'code A';
    ASSERT (SELECT statut FROM cloud_compte_distant_code_rattachement('titulaire.b320', 'bbbbbbbb-0000-0000-0000-000000000320', repeat('b', 64), 'tests 032')) = 'ok', 'code B';
    ASSERT (SELECT statut FROM cloud_compte_distant_code_rattachement('titulaire.a320', 'bbbbbbbb-0000-0000-0000-000000000320', repeat('c', 64), 'tests 032')) = 'ok', 'code A vers B';
    ASSERT (SELECT statut FROM cloud_compte_distant_code_rattachement('editeur.320', 'aaaaaaaa-0000-0000-0000-000000000320', repeat('d', 64), 'tests 032')) = 'pas_titulaire', 'éditeur refusé';
    ASSERT (SELECT statut FROM cloud_compte_distant_code_rattachement('inconnu.320', 'aaaaaaaa-0000-0000-0000-000000000320', repeat('d', 64), 'tests 032')) = 'compte_inconnu', 'compte inconnu';
    ASSERT (SELECT statut FROM cloud_compte_distant_code_rattachement('titulaire.a320', gen_random_uuid(), repeat('d', 64), 'tests 032')) = 'officine_inconnue', 'officine inconnue';
    ASSERT (SELECT statut FROM cloud_compte_distant_code_rattachement('titulaire.a320', 'cccccccc-0000-0000-0000-000000000320', repeat('d', 64), 'tests 032')) = 'partie', 'officine partie';
    ASSERT (SELECT expire_le FROM code_rattachement WHERE empreinte_code = repeat('a', 64)) BETWEEN now() + interval '29 minutes' AND now() + interval '30 minutes', '30 minutes';
    BEGIN PERFORM cloud_compte_distant_code_rattachement('titulaire.a320', 'aaaaaaaa-0000-0000-0000-000000000320', 'PAS-UNE-EMPREINTE', 'tests 032');
          RAISE EXCEPTION 'empreinte invalide acceptée';
    EXCEPTION WHEN invalid_parameter_value THEN NULL; END;
    -- Un nouveau code pour le même compte et la même officine annule le précédent
    ASSERT (SELECT statut FROM cloud_compte_distant_code_rattachement('titulaire.b320', 'bbbbbbbb-0000-0000-0000-000000000320', repeat('e', 64), 'tests 032')) = 'ok', 'code B bis';
    ASSERT (SELECT annule_le IS NOT NULL FROM code_rattachement WHERE empreinte_code = repeat('b', 64)), 'code B annulé par le suivant';
    -- Code de A vers B expiré (dates reculées : 40 min)
    UPDATE code_rattachement SET cree_le = now() - interval '40 minutes', expire_le = now() - interval '10 minutes' WHERE empreinte_code = repeat('c', 64);
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'code_rattachement_cree' AND detail LIKE '%par tests 032'), 'journal de sécurité';
    ASSERT (SELECT count(*) FROM journal_acces_distant j JOIN compte_distant c ON c.id = j.compte_id
            WHERE c.identifiant IN ('titulaire.a320', 'titulaire.b320') AND j.evenement = 'code_rattachement_cree' AND j.objet = 'console') = 4, 'journal des accès';
END $$;

SET LOCAL ROLE pharma_cloud_ecrans;
-- Rien sans session ; l'éditeur n'a aucun écran du titulaire ; aucun droit direct
DO $$ BEGIN
    BEGIN PERFORM cloud_ecran_titulaire_officines(repeat('0', 64)); RAISE EXCEPTION 'liste sans session';
    EXCEPTION WHEN invalid_authorization_specification THEN NULL; END;
    BEGIN PERFORM cloud_ecran_titulaire_officines(repeat('3', 64)); RAISE EXCEPTION 'liste du titulaire pour l''éditeur';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM cloud_ecran_titulaire_tableau(repeat('3', 64), 'aaaaaaaa-0000-0000-0000-000000000320'); RAISE EXCEPTION 'tableau pour l''éditeur';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM cloud_ecran_titulaire_journal(repeat('3', 64), 'aaaaaaaa-0000-0000-0000-000000000320', 1, 20); RAISE EXCEPTION 'journal pour l''éditeur';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM cloud_ecran_rattacher(repeat('3', 64), repeat('a', 64)); RAISE EXCEPTION 'rattachement pour l''éditeur';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM cloud_ecran_officines(repeat('1', 64), NULL, NULL, 1, 20); RAISE EXCEPTION 'écran éditeur pour un titulaire';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM 1 FROM code_rattachement; RAISE EXCEPTION 'lecture directe des codes';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM 1 FROM compte_officine; RAISE EXCEPTION 'lecture directe des rattachements';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    -- Avant tout rattachement : aucune officine, tableau refusé
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_officines(repeat('1', 64))) = 0, 'aucune officine avant rattachement';
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_tableau(repeat('1', 64), 'aaaaaaaa-0000-0000-0000-000000000320')) = 0, 'tableau avant rattachement';
END $$;

-- Rattachement : code d'un autre compte, expiré, réutilisé refusés
DO $$ BEGIN
    ASSERT (SELECT statut FROM cloud_ecran_rattacher(repeat('2', 64), repeat('a', 64))) = 'inconnu', 'code d''un autre compte : même réponse qu''un code inconnu';
    ASSERT (SELECT statut FROM cloud_ecran_rattacher(repeat('1', 64), repeat('c', 64))) = 'expire', 'code expiré';
    ASSERT (SELECT statut FROM cloud_ecran_rattacher(repeat('2', 64), repeat('b', 64))) = 'utilise', 'code annulé par un plus récent';
    ASSERT (SELECT statut FROM cloud_ecran_rattacher(repeat('1', 64), 'pas une empreinte')) = 'inconnu', 'empreinte mal formée';
    ASSERT (SELECT statut FROM cloud_ecran_rattacher(repeat('1', 64), repeat('a', 64))) = 'ok', 'A rattache A';
    ASSERT (SELECT statut FROM cloud_ecran_rattacher(repeat('1', 64), repeat('a', 64))) = 'utilise', 'usage unique';
    ASSERT (SELECT statut FROM cloud_ecran_rattacher(repeat('2', 64), repeat('e', 64))) = 'ok', 'B rattache B';
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_officines(repeat('1', 64))) = 1, 'A : une officine';
    ASSERT (SELECT id FROM cloud_ecran_titulaire_officines(repeat('1', 64))) = 'aaaaaaaa-0000-0000-0000-000000000320', 'A : son officine seulement';
    ASSERT (SELECT droits FROM cloud_ecran_titulaire_officines(repeat('1', 64))) = 'consulter,journal_acces', 'droits accordés';
    ASSERT (SELECT id FROM cloud_ecran_titulaire_officines(repeat('2', 64))) = 'bbbbbbbb-0000-0000-0000-000000000320', 'B : son officine seulement';
END $$;

-- Tableau de A : agrégats exacts ; données non montées : NULL
DO $$
DECLARE t record; tout text;
BEGIN
    SELECT * INTO t FROM cloud_ecran_titulaire_tableau(repeat('1', 64), 'aaaaaaaa-0000-0000-0000-000000000320');
    ASSERT t.id = 'aaaaaaaa-0000-0000-0000-000000000320' AND t.libelle = 'Essai 0320 A' AND t.etat = 'active' AND t.fuseau = 'Africa/Algiers', 'en-tête';
    ASSERT t.ca_jour = 1400 AND t.ventes_jour = 2, 'CA du jour : validées moins retour, sans annulée ni absente (' || t.ca_jour || ')';
    ASSERT t.ca_mois = 1400 AND t.ventes_mois = 2, 'CA du mois';
    ASSERT t.ca_mois_precedent = 300, 'CA du mois précédent';
    ASSERT t.part_chifa_mois = 600, 'part Chifa';
    ASSERT t.credits_en_cours IS NULL, 'crédits : table patient hors du chargement complet, non disponible';
    ASSERT t.alertes_stock = 1 AND t.alertes_peremption = 1, 'alertes non traitées';
    ASSERT t.anomalies_ouvertes = 1 AND t.ordres_en_attente = 1, 'anomalies et ordres';
    ASSERT t.dernier_lot = 12 AND NOT t.en_retard AND t.chargement_complet_le IS NOT NULL AND NOT t.chargement_en_cours, 'synchronisation';
    SELECT string_agg(x::text, ' ') INTO tout FROM cloud_ecran_titulaire_tableau(repeat('1', 64), 'aaaaaaaa-0000-0000-0000-000000000320') x;
    ASSERT tout NOT LIKE '%NOMINATIF0320%', 'valeur nominative rendue';
    ASSERT tout NOT LIKE '%77777.77%' AND tout NOT LIKE '%66666.66%' AND tout NOT LIKE '%7777.77%', 'valeur de B rendue à A';
    ASSERT current_setting('app.officine_id') = 'aaaaaaaa-0000-0000-0000-000000000320', 'officine posée par la base';
    -- Officine posée à la main sur B : A ne lit toujours que A ; tableau de B refusé à A
    PERFORM set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000320', true);
    ASSERT (SELECT ca_jour FROM cloud_ecran_titulaire_tableau(repeat('1', 64), 'aaaaaaaa-0000-0000-0000-000000000320')) = 1400, 'officine revérifiée';
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_tableau(repeat('1', 64), 'bbbbbbbb-0000-0000-0000-000000000320')) = 0, 'A ne voit jamais B';
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_journal(repeat('1', 64), 'bbbbbbbb-0000-0000-0000-000000000320', 1, 20)) = 0, 'journal de B refusé à A';
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_tableau(repeat('2', 64), 'aaaaaaaa-0000-0000-0000-000000000320')) = 0, 'B ne voit jamais A';
    -- Tableau de B : aucun chargement complet terminé, tout est « non disponible » (rien d'inventé)
    SELECT * INTO t FROM cloud_ecran_titulaire_tableau(repeat('2', 64), 'bbbbbbbb-0000-0000-0000-000000000320');
    ASSERT t.ca_jour IS NULL AND t.ventes_jour IS NULL AND t.ca_mois IS NULL AND t.part_chifa_mois IS NULL AND t.credits_en_cours IS NULL
           AND t.alertes_stock IS NULL AND t.chargement_complet_le IS NULL, 'B : données non montées rendues NULL';
    ASSERT t.anomalies_ouvertes = 0 AND t.ordres_en_attente = 0, 'B : rien de A';
END $$;
RESET ROLE;

-- Données montées ensuite : second chargement avec la table patient ; colonne d'une famille non envoyée : non disponible
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000320', true) \g /dev/null
INSERT INTO chargement (officine_id, numero, epoque, repere, motif, tables, etat, fin_le)
VALUES ('aaaaaaaa-0000-0000-0000-000000000320', 2, 1, 10, 'demande', '{patient}', 'termine', now());
SELECT set_config('app.officine_id', '', true) \g /dev/null
INSERT INTO cloud_famille_colonne (famille, nom_table, colonne) VALUES ('employes', 'alerte', 'statut');
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$
DECLARE t record;
BEGIN
    SELECT * INTO t FROM cloud_ecran_titulaire_tableau(repeat('1', 64), 'aaaaaaaa-0000-0000-0000-000000000320');
    ASSERT t.credits_en_cours = 250, 'crédits en cours : soldes positifs de A seulement (' || coalesce(t.credits_en_cours::text, 'NULL') || ')';
    ASSERT t.alertes_stock IS NULL AND t.alertes_peremption IS NULL, 'colonne d''une famille non envoyée : non disponible';
END $$;
RESET ROLE;
DELETE FROM cloud_famille_colonne WHERE famille = 'employes' AND nom_table = 'alerte';

-- Journal des accès de l'officine : droit « journal_acces » exigé ; acteur sans identifiant ni nom
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$
DECLARE tout text;
BEGIN
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_journal(repeat('1', 64), 'aaaaaaaa-0000-0000-0000-000000000320', 1, 100)) >= 3, 'journal de A';
    ASSERT EXISTS (SELECT 1 FROM cloud_ecran_titulaire_journal(repeat('1', 64), 'aaaaaaaa-0000-0000-0000-000000000320', 1, 100) WHERE acteur = 'vous' AND evenement = 'rattachement'),
           'mon rattachement';
    ASSERT EXISTS (SELECT 1 FROM cloud_ecran_titulaire_journal(repeat('1', 64), 'aaaaaaaa-0000-0000-0000-000000000320', 1, 100) WHERE acteur = 'console' AND evenement = 'code_rattachement_cree' AND objet = 'console'),
           'code créé par la console';
    SELECT string_agg(j::text, ' ') INTO tout FROM cloud_ecran_titulaire_journal(repeat('1', 64), 'aaaaaaaa-0000-0000-0000-000000000320', 1, 100) j;
    ASSERT tout NOT LIKE '%titulaire.a320%' AND tout NOT LIKE '%Titulaire A%' AND tout NOT LIKE '%tests 032%', 'ni identifiant, ni nom, ni auteur';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_ecran_titulaire_journal(repeat('1', 64), 'aaaaaaaa-0000-0000-0000-000000000320', 1, 100)
                       WHERE evenement = 'tableau_titulaire' AND resultat = 'refus'), 'aucun refus sur A';
END $$;
RESET ROLE;
UPDATE compte_officine SET droits = '{consulter}' WHERE officine_id = 'aaaaaaaa-0000-0000-0000-000000000320';
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_journal(repeat('1', 64), 'aaaaaaaa-0000-0000-0000-000000000320', 1, 20)) = 0, 'journal sans le droit';
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_tableau(repeat('1', 64), 'aaaaaaaa-0000-0000-0000-000000000320')) = 1, 'tableau gardé';
    -- 5 refus de rattachement par quart d'heure (A en a déjà 2 : expiré, usage unique ; plus l'empreinte mal formée)
    PERFORM cloud_ecran_rattacher(repeat('1', 64), repeat('9', 64));
    PERFORM cloud_ecran_rattacher(repeat('1', 64), repeat('8', 64));
    ASSERT (SELECT statut FROM cloud_ecran_rattacher(repeat('1', 64), repeat('7', 64))) = 'trop_d_essais', 'trop d''essais';
END $$;
RESET ROLE;
-- Rattachement retiré : plus rien
UPDATE compte_officine SET retire_le = now() WHERE officine_id = 'bbbbbbbb-0000-0000-0000-000000000320';
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_officines(repeat('2', 64))) = 0, 'rattachement retiré : liste vide';
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_tableau(repeat('2', 64), 'bbbbbbbb-0000-0000-0000-000000000320')) = 0, 'rattachement retiré : tableau refusé';
END $$;
RESET ROLE;

-- Journaux : refus notés, jamais l'empreinte d'un code ; chaîne intacte
DO $$ BEGIN
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'rattachement' AND resultat = 'refus' AND objet = 'autre_compte' AND officine_id IS NULL),
           'code d''un autre compte noté sans l''officine';
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'rattachement' AND resultat = 'refus' AND objet = 'expire'), 'expiré noté';
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'journal_officine_titulaire' AND resultat = 'refus' AND objet = 'droit:journal_acces'), 'droit manquant noté';
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant WHERE evenement = 'ouverture_officine' AND resultat = 'refus'), 'ouverture refusée notée';
    ASSERT (SELECT count(*) FROM journal_securite WHERE evenement = 'rattachement_titulaire'
            AND officine_id IN ('aaaaaaaa-0000-0000-0000-000000000320', 'bbbbbbbb-0000-0000-0000-000000000320')) = 2, 'deux rattachements au journal de sécurité';
    ASSERT NOT EXISTS (SELECT 1 FROM journal_securite WHERE detail LIKE '%' || repeat('a', 64) || '%'), 'empreinte d''un code jamais écrite';
    ASSERT (SELECT accorde_par FROM compte_officine WHERE officine_id = 'aaaaaaaa-0000-0000-0000-000000000320') LIKE 'code de rattachement n° %', 'accordé par le code';
    ASSERT (SELECT premiere_rupture FROM cloud_ecran_verifier_journal_acces()) IS NULL, 'chaîne intacte';
END $$;

ROLLBACK;
\echo '032 : tous réussis'
