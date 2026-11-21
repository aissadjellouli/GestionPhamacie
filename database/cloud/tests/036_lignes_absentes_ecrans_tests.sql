-- =====================================================================
-- Tests du script 036 (lignes absentes vues par les écrans), en transaction ANNULÉE :
--   psql -U <propriétaire> -d <base centrale de test> -v ON_ERROR_STOP=1 -f database/cloud/tests/036_lignes_absentes_ecrans_tests.sql
-- Droits (écrans seulement, internes à personne, rien pour PUBLIC) ; index partiels posés ; regroupement par table, chargement et
-- cause probable (restauration constatée, nouvelle époque, non expliquée) ; lignes reçues de nouveau non comptées ; éditeur : des
-- NOMBRES seulement (aucune valeur d'une ligne, aucune clé), après ouverture par la base ; titulaire : ses officines seulement
-- (non-fuite : B ne voit jamais O1, même en la demandant), « non disponible » (NULL) sans chargement complet terminé ; rôles
-- croisés refusés ; journal des accès (chaîne intacte).
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

-- Droits
DO $$ BEGIN
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_lignes_absentes(text, int, int)', 'EXECUTE'), 'éditeur : tableau';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_lignes_absentes_resume(text)', 'EXECUTE'), 'éditeur : résumé';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_titulaire_lignes_absentes(text, uuid)', 'EXECUTE'), 'titulaire : compteur';
    ASSERT NOT has_function_privilege('pharma_cloud_ecrans', 'cloud_lignes_absentes_brut()', 'EXECUTE'), 'interne (brut) : jamais aux écrans';
    ASSERT NOT has_function_privilege('pharma_cloud_ecrans', 'cloud_lignes_absentes_groupes()', 'EXECUTE'), 'interne (groupes) : jamais aux écrans';
    ASSERT NOT has_function_privilege('public', 'cloud_ecran_lignes_absentes(text, int, int)', 'EXECUTE'), 'pas pour PUBLIC';
    ASSERT NOT has_function_privilege('public', 'cloud_ecran_titulaire_lignes_absentes(text, uuid)', 'EXECUTE'), 'pas pour PUBLIC (titulaire)';
    ASSERT NOT has_function_privilege('public', 'cloud_lignes_absentes_groupes()', 'EXECUTE'), 'interne : pas pour PUBLIC';
    ASSERT NOT has_function_privilege('pharma_cloud_api', 'cloud_ecran_lignes_absentes_resume(text)', 'EXECUTE'), 'API : rien';
    ASSERT NOT has_function_privilege('pharma_cloud_etat', 'cloud_ecran_lignes_absentes(text, int, int)', 'EXECUTE'), 'page d''état : rien';
    -- Index partiel posé sur chaque table miroir qui a la colonne
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_table_miroir m
                       WHERE to_regclass('public.' || quote_ident(m.nom_table)) IS NOT NULL
                         AND NOT EXISTS (SELECT 1 FROM pg_indexes i WHERE i.schemaname = 'public' AND i.tablename = m.nom_table
                                         AND i.indexname LIKE 'ix\_abs\_%' AND i.indexdef LIKE '%WHERE (sync_absente_le IS NOT NULL)%')),
           'table miroir sans index des lignes absentes';
    -- Aucune colonne interdite rendue (même liste que 031) ; ni contenu ni clé de ligne
    ASSERT NOT EXISTS (
        SELECT 1 FROM pg_proc p, unnest(p.proargnames) WITH ORDINALITY AS a(nom, n)
        WHERE p.oid IN ('cloud_ecran_lignes_absentes(text, int, int)'::regprocedure, 'cloud_ecran_lignes_absentes_resume(text)'::regprocedure,
                        'cloud_ecran_titulaire_lignes_absentes(text, uuid)'::regprocedure)
          AND p.proargmodes[a.n] IN ('o', 't')
          AND a.nom IN ('donnees', 'contenu', 'attendu', 'trouve', 'cle_ligne', 'cle', 'ligne', 'ancienne', 'motif', 'nom', 'identifiant')),
        'colonne interdite rendue';
END $$;

-- Données d'essai : O1 (restauration constatée, époque 2), O2 (nouvelle époque sans restauration constatée), O3 (aucun chargement
-- terminé) ; titulaires A (O1, O2) et B (O2, O3) ; un éditeur
INSERT INTO officine (id, libelle) VALUES ('11111111-0000-0000-0000-000000000360', 'Essai 0360 O1'), ('22222222-0000-0000-0000-000000000360', 'Essai 0360 O2'),
                                          ('33333333-0000-0000-0000-000000000360', 'Essai 0360 O3');
SELECT cloud_compte_distant_creer('titulaire.a360', 'Titulaire A 0360', 'titulaire', '$2a$12$' || repeat('a', 53), NULL, 'a360@exemple.invalid', 'tests 036') \g /dev/null
SELECT cloud_compte_distant_creer('titulaire.b360', 'Titulaire B 0360', 'titulaire', '$2a$12$' || repeat('a', 53), NULL, 'b360@exemple.invalid', 'tests 036') \g /dev/null
SELECT cloud_compte_distant_creer('editeur.360', 'Éditeur 0360', 'editeur', '$2a$12$' || repeat('a', 53), NULL, 'e360@exemple.invalid', 'tests 036') \g /dev/null
UPDATE compte_distant SET mot_de_passe_provisoire = false WHERE identifiant IN ('titulaire.a360', 'titulaire.b360', 'editeur.360');
INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le)
SELECT repeat(x.j, 64), c.id, 'ouverte', now() + interval '2 hours'
FROM (VALUES ('titulaire.a360', '7'), ('titulaire.b360', '8'), ('editeur.360', '9')) x(ident, j) JOIN compte_distant c ON c.identifiant = x.ident;
INSERT INTO compte_officine (compte_id, officine_id, accorde_par)
SELECT c.id, o.id, 'tests 036' FROM compte_distant c
JOIN (VALUES ('titulaire.a360', '11111111-0000-0000-0000-000000000360'::uuid), ('titulaire.a360', '22222222-0000-0000-0000-000000000360'::uuid),
             ('titulaire.b360', '22222222-0000-0000-0000-000000000360'::uuid), ('titulaire.b360', '33333333-0000-0000-0000-000000000360'::uuid)) o(ident, id)
  ON o.ident = c.identifiant;

-- O1 : chargement 1 (époque 1, 15/11) puis 2 (époque 2, 21/11) ; restauration constatée et changement d'époque déclaré (détecté le 18/11)
SELECT set_config('app.officine_id', '11111111-0000-0000-0000-000000000360', true) \g /dev/null
INSERT INTO chargement (officine_id, numero, epoque, repere, motif, tables, etat, debut_le, fin_le)
VALUES ('11111111-0000-0000-0000-000000000360', 1, 1, 10, 'activation', '{vente,alerte}', 'termine', '2026-11-15 22:00+00', '2026-11-15 22:40+00'),
       ('11111111-0000-0000-0000-000000000360', 2, 2, 5, 'reactivation', '{vente,alerte}', 'termine', '2026-11-21 13:00+00', '2026-11-21 13:53+00');
INSERT INTO cloud_epoque_changement (officine_id, ancienne_epoque, nouvelle_epoque, dernier_lot_local, detecte_le, decide_le, avant_premier_lot)
VALUES ('11111111-0000-0000-0000-000000000360', 1, 2, 7, '2026-11-18 09:12+00', '2026-11-18 09:30+00', true);
INSERT INTO cloud_integrite_bifurcation (officine_id, journal, cle_chaine, ancienne_epoque, nouvelle_epoque, rang_commun, classe)
VALUES ('11111111-0000-0000-0000-000000000360', 'journal_vente', 'global', 1, 2, 4, 'restauration_constatee');
INSERT INTO vente (officine_id, id, sync_epoque, sync_numero, numero, statut, sync_chargement, sync_absente_le) VALUES
    -- constatée absente au chargement 1, même époque : non expliquée
    ('11111111-0000-0000-0000-000000000360', 1, 1, 1, 'T-NOMINATIF036', 'validee', NULL, '2026-11-15 22:40+00'),
    -- ancienne époque, constatées au chargement 2 (époque 2, restauration constatée) : restauration
    ('11111111-0000-0000-0000-000000000360', 2, 1, 2, 'T-NOMINATIF036', 'validee', 1, '2026-11-21 13:53+00'),
    ('11111111-0000-0000-0000-000000000360', 3, 1, 3, 'T-NOMINATIF036', 'validee', 1, '2026-11-21 13:53+00'),
    -- présente (vue par le chargement 2) : jamais comptée
    ('11111111-0000-0000-0000-000000000360', 4, 2, 1, 'T-NOMINATIF036', 'validee', 2, NULL);
INSERT INTO alerte (officine_id, id, sync_epoque, sync_numero, sync_chargement, sync_absente_le)
VALUES ('11111111-0000-0000-0000-000000000360', 1, 1, 4, 1, '2026-11-21 13:53+00');
-- O2 : époque 2 sans restauration constatée ; une ligne constatée à une date sans chargement retrouvé
SELECT set_config('app.officine_id', '22222222-0000-0000-0000-000000000360', true) \g /dev/null
INSERT INTO chargement (officine_id, numero, epoque, repere, motif, tables, etat, debut_le, fin_le)
VALUES ('22222222-0000-0000-0000-000000000360', 1, 2, 3, 'reactivation', '{vente}', 'termine', '2026-11-20 10:00+00', '2026-11-20 10:05+00');
INSERT INTO vente (officine_id, id, sync_epoque, sync_numero, numero, statut, sync_chargement, sync_absente_le) VALUES
    ('22222222-0000-0000-0000-000000000360', 1, 1, 1, 'T-NOMINATIF036', 'validee', NULL, '2026-11-20 10:05+00'),
    ('22222222-0000-0000-0000-000000000360', 2, 2, 1, 'T-NOMINATIF036', 'validee', NULL, '2026-11-19 08:00+00');
-- O3 : une ligne reçue, aucun chargement complet terminé
SELECT set_config('app.officine_id', '33333333-0000-0000-0000-000000000360', true) \g /dev/null
INSERT INTO vente (officine_id, id, sync_epoque, sync_numero, numero, statut) VALUES ('33333333-0000-0000-0000-000000000360', 1, 1, 1, 'T-NOMINATIF036', 'validee');
SELECT set_config('app.officine_id', '', true) \g /dev/null

SET LOCAL ROLE pharma_cloud_ecrans;
-- Rôles croisés et officine posée à la main
DO $$ BEGIN
    BEGIN PERFORM cloud_ecran_lignes_absentes(repeat('9', 64), 1, 20); RAISE EXCEPTION 'éditeur sans officine ouverte';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    PERFORM set_config('app.officine_id', '11111111-0000-0000-0000-000000000360', true);
    BEGIN PERFORM cloud_ecran_lignes_absentes(repeat('7', 64), 1, 20); RAISE EXCEPTION 'tableau de l''éditeur pour un titulaire';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM cloud_ecran_lignes_absentes_resume(repeat('0', 64)); RAISE EXCEPTION 'résumé sans session';
    EXCEPTION WHEN invalid_authorization_specification THEN NULL; END;
    PERFORM set_config('app.officine_id', '', true);
    BEGIN PERFORM cloud_ecran_titulaire_lignes_absentes(repeat('9', 64), NULL); RAISE EXCEPTION 'compteur du titulaire pour l''éditeur';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM cloud_lignes_absentes_groupes(); RAISE EXCEPTION 'fonction interne appelée par les écrans';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
END $$;

-- Éditeur, O1 : groupes, causes, résumé ; nombres seulement
DO $$
DECLARE r record; tout text;
BEGIN
    ASSERT cloud_ecran_ouvrir(repeat('9', 64), '11111111-0000-0000-0000-000000000360'), 'ouverture de O1 par la base';
    ASSERT (SELECT count(*) FROM cloud_ecran_lignes_absentes(repeat('9', 64), 1, 20)) = 3, 'trois groupes (vente x2, alerte)';
    SELECT * INTO r FROM cloud_ecran_lignes_absentes(repeat('9', 64), 1, 20) LIMIT 1;
    ASSERT r.total = 3 AND r.nom_table = 'vente' AND r.lignes = 2 AND r.chargement = 2 AND r.cause = 'restauration'
           AND r.premiere_constatation = '2026-11-21 13:53+00', 'vente : 2 lignes, chargement 2, restauration : ' || r::text;
    ASSERT (SELECT cause FROM cloud_ecran_lignes_absentes(repeat('9', 64), 1, 20) WHERE nom_table = 'vente' AND chargement = 1) = 'non_expliquee',
           'même époque : non expliquée';
    ASSERT (SELECT cause || ':' || lignes FROM cloud_ecran_lignes_absentes(repeat('9', 64), 1, 20) WHERE nom_table = 'alerte') = 'restauration:1', 'alerte';
    ASSERT (SELECT count(*) FROM cloud_ecran_lignes_absentes(repeat('9', 64), 2, 10)) = 0, 'page 2 vide';
    SELECT * INTO r FROM cloud_ecran_lignes_absentes_resume(repeat('9', 64));
    ASSERT r.lignes = 4 AND r.tables = 2 AND r.dernier_chargement = 2 AND r.dernier_chargement_le = '2026-11-21 13:53+00'
           AND r.cause_frequente = 'restauration' AND r.cause_lignes = 3 AND r.retour_le = '2026-11-18 09:12+00', 'résumé de O1 : ' || r::text;
    SELECT concat_ws(' ', (SELECT string_agg(x::text, ' ') FROM cloud_ecran_lignes_absentes(repeat('9', 64), 1, 100) x),
                     (SELECT string_agg(x::text, ' ') FROM cloud_ecran_lignes_absentes_resume(repeat('9', 64)) x)) INTO tout;
    ASSERT tout NOT LIKE '%NOMINATIF036%', 'valeur d''une ligne rendue : ' || left(tout, 300);
    -- O2 : nouvelle époque sans restauration constatée ; date sans chargement retrouvé : non expliquée
    ASSERT cloud_ecran_ouvrir(repeat('9', 64), '22222222-0000-0000-0000-000000000360'), 'ouverture de O2';
    ASSERT (SELECT string_agg(cause || ':' || coalesce(chargement::text, '-'), ',' ORDER BY cause)
            FROM cloud_ecran_lignes_absentes(repeat('9', 64), 1, 20)) = 'non_expliquee:-,nouvelle_epoque:1', 'causes de O2';
    SELECT * INTO r FROM cloud_ecran_lignes_absentes_resume(repeat('9', 64));
    ASSERT r.lignes = 2 AND r.tables = 1 AND r.cause_frequente = 'nouvelle_epoque' AND r.retour_le IS NULL, 'résumé de O2 (égalité : nouvelle époque d''abord)';
    -- O3 : rien de O1 ni de O2 ; aucun chargement terminé
    ASSERT cloud_ecran_ouvrir(repeat('9', 64), '33333333-0000-0000-0000-000000000360'), 'ouverture de O3';
    ASSERT (SELECT count(*) FROM cloud_ecran_lignes_absentes(repeat('9', 64), 1, 20)) = 0, 'O3 : aucune ligne absente (cloisonnement)';
    SELECT * INTO r FROM cloud_ecran_lignes_absentes_resume(repeat('9', 64));
    ASSERT r.lignes = 0 AND r.tables = 0 AND r.dernier_chargement IS NULL AND r.cause_frequente IS NULL, 'résumé de O3';
    PERFORM set_config('app.officine_id', '', true);
END $$;

-- Une ligne reçue de nouveau perd sa marque : plus comptée
RESET ROLE;
SELECT set_config('app.officine_id', '11111111-0000-0000-0000-000000000360', true) \g /dev/null
UPDATE vente SET sync_absente_le = NULL, sync_chargement = 2 WHERE officine_id = '11111111-0000-0000-0000-000000000360' AND id = 3;
SELECT set_config('app.officine_id', '', true) \g /dev/null
SET LOCAL ROLE pharma_cloud_ecrans;

-- Titulaire : ses officines seulement ; NULL sans chargement terminé ; non-fuite
DO $$
DECLARE tout text;
BEGIN
    ASSERT (SELECT string_agg(left(id::text, 8) || ':' || coalesce(lignes::text, 'nd') || ':' || coalesce(tables::text, 'nd'), ',' ORDER BY id)
            FROM cloud_ecran_titulaire_lignes_absentes(repeat('7', 64), NULL)) = '11111111:3:2,22222222:2:1', 'A : O1 (3 lignes, 2 tables) et O2';
    ASSERT (SELECT string_agg(left(id::text, 8) || ':' || coalesce(lignes::text, 'nd'), ',' ORDER BY id)
            FROM cloud_ecran_titulaire_lignes_absentes(repeat('8', 64), NULL)) = '22222222:2,33333333:nd', 'B : O2 et O3 (non disponible)';
    ASSERT (SELECT lignes FROM cloud_ecran_titulaire_lignes_absentes(repeat('7', 64), '11111111-0000-0000-0000-000000000360')) = 3, 'A : O1 seule';
    ASSERT (SELECT count(*) FROM cloud_ecran_titulaire_lignes_absentes(repeat('8', 64), '11111111-0000-0000-0000-000000000360')) = 0,
           'B ne voit jamais O1, même en la demandant';
    -- Officine posée à la main sur O1 : B n'en voit toujours rien
    PERFORM set_config('app.officine_id', '11111111-0000-0000-0000-000000000360', true);
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_ecran_titulaire_lignes_absentes(repeat('8', 64), NULL) WHERE id = '11111111-0000-0000-0000-000000000360'),
           'B : O1 jamais rendue';
    ASSERT current_setting('app.officine_id', true) = '11111111-0000-0000-0000-000000000360', 'officine courante rendue telle quelle';
    PERFORM set_config('app.officine_id', '', true);
    SELECT string_agg(x::text, ' ') INTO tout FROM cloud_ecran_titulaire_lignes_absentes(repeat('7', 64), NULL) x;
    ASSERT tout NOT LIKE '%NOMINATIF036%', 'valeur d''une ligne rendue au titulaire';
END $$;
RESET ROLE;

-- Journal des accès : ouvertures notées (refus de B sur O1 compris), chaîne intacte
DO $$ BEGIN
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant j JOIN compte_distant c ON c.id = j.compte_id
                   WHERE c.identifiant = 'titulaire.b360' AND j.evenement = 'ouverture_officine' AND j.resultat = 'refus'), 'refus de B noté';
    ASSERT EXISTS (SELECT 1 FROM journal_acces_distant j JOIN compte_distant c ON c.id = j.compte_id
                   WHERE c.identifiant = 'titulaire.a360' AND j.evenement = 'ouverture_officine' AND j.resultat = 'ok'
                     AND j.officine_id = '11111111-0000-0000-0000-000000000360'), 'ouverture de A notée';
    ASSERT (SELECT premiere_rupture FROM cloud_ecran_verifier_journal_acces()) IS NULL, 'chaîne du journal des accès intacte';
END $$;

ROLLBACK;
\echo '036 : tous réussis'
