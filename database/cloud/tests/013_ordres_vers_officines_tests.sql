-- =====================================================================
-- Tests des ordres envoyés aux officines (script 013), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/013_ordres_vers_officines_tests.sql
-- =====================================================================
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000013', 'Essai S5 A'), ('bbbbbbbb-0000-0000-0000-000000000013', 'Essai S5 B');

-- Rôles, employés et droits de l'officine A, reçus par un lot
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000013', true);
SELECT cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1', convert_to(
    '{"lignes":['
    '{"n":1,"table":"role","id":"1","op":"creation","donnees":{"id":1,"code":"titulaire","nom":"Titulaire","systeme":true}},'
    '{"n":2,"table":"role","id":"2","op":"creation","donnees":{"id":2,"code":"vendeur","nom":"Vendeur","systeme":false}},'
    '{"n":3,"table":"role","id":"3","op":"creation","donnees":{"id":3,"code":"assistant","nom":"Assistant","systeme":false}},'
    '{"n":4,"table":"utilisateur","id":"10","op":"creation","donnees":{"id":10,"nom_complet":"Dr Essai","actif":true,"role_id":1}},'
    '{"n":5,"table":"utilisateur","id":"11","op":"creation","donnees":{"id":11,"actif":true,"role_id":2}},'
    '{"n":6,"table":"utilisateur","id":"12","op":"creation","donnees":{"id":12,"actif":false,"role_id":2}},'
    '{"n":7,"table":"role_permission","id":"2|vente.creer","op":"creation","donnees":{"role_id":2,"permission_code":"vente.creer"}},'
    '{"n":8,"table":"role_permission","id":"2|stock.voir","op":"creation","donnees":{"role_id":2,"permission_code":"stock.voir"}}'
    ']}', 'UTF8'));
-- L'API ne crée jamais d'ordre
DO $$
BEGIN
    BEGIN
        PERFORM cloud_creer_ordre('aaaaaaaa-0000-0000-0000-000000000013', 'essai', NULL, '{}', 'api');
        RAISE EXCEPTION 'ordre créé par l''API';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;
END $$;
RESET ROLE;

DO $$
DECLARE
    a uuid := 'aaaaaaaa-0000-0000-0000-000000000013';
    v_essai uuid; v_creer uuid; v_modif uuid; v_bloq uuid; v_debloq uuid; v_droits uuid;
    o ordre_central; d descendant;
    PROCEDURE_OK boolean;
BEGIN
    -- 1. Création : un ordre de chaque type, valeur attendue lue dans le miroir
    v_essai := cloud_creer_ordre(a, 'essai', NULL, '{}', 'admin.essai');
    v_creer := cloud_creer_ordre(a, 'employe_creer', NULL, '{"nom":" Nouvel Employé ","identifiant":"nouvel.employe","role":"vendeur","mot_de_passe":"x"}', 'admin.essai');
    v_modif := cloud_creer_ordre(a, 'employe_modifier', '11', '{"role":"assistant"}', 'admin.essai');
    v_bloq := cloud_creer_ordre(a, 'employe_bloquer', '11', '{}', 'admin.essai');
    v_debloq := cloud_creer_ordre(a, 'employe_debloquer', '12', '{}', 'admin.essai');
    v_droits := cloud_creer_ordre(a, 'droits_role', 'vendeur', '{"permissions":["stock.voir","vente.creer","caisse.ouvrir","caisse.ouvrir"]}', 'admin.essai');
    PERFORM set_config('app.officine_id', a::text, true);
    ASSERT (SELECT count(*) FROM ordre_central) = 6 AND (SELECT count(*) FROM ordre_central WHERE statut = 'en_attente') = 6, 'six ordres en attente';
    SELECT * INTO o FROM ordre_central WHERE id = v_creer;
    ASSERT o.donnees = '{"nom":"Nouvel Employé","identifiant":"nouvel.employe","role":"vendeur"}'::jsonb, 'jamais de mot de passe : ' || o.donnees::text;
    ASSERT o.expire_le > now() + interval '6 days 23 hours', 'expiration à 7 jours';
    SELECT * INTO o FROM ordre_central WHERE id = v_modif;
    ASSERT o.attendu = '{"role":"vendeur","actif":true}'::jsonb, 'attendu (nom non reçu) : ' || o.attendu::text;
    SELECT * INTO o FROM ordre_central WHERE id = v_droits;
    ASSERT o.attendu = '{"permissions":["stock.voir","vente.creer"]}'::jsonb, 'droits attendus : ' || o.attendu::text;
    ASSERT o.donnees = '{"permissions":["caisse.ouvrir","stock.voir","vente.creer"]}'::jsonb, 'droits voulus triés sans doublon : ' || o.donnees::text;
    SELECT * INTO d FROM descendant WHERE nom_table = 'ordre_central' AND cle = v_bloq::text;
    ASSERT FOUND AND d.donnees ->> 'format' = 'ordre-v1' AND d.donnees ->> 'type' = 'employe_bloquer' AND d.donnees ->> 'statut' = 'en_attente', 'ordre publié';
    ASSERT (SELECT count(*) FROM ordre_central_journal) = 6, 'journal de création';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'ordre_cree' AND officine_id = a), 'journal de sécurité';

    -- 2. Refus à la création
    BEGIN PERFORM cloud_creer_ordre(a, 'employe_bloquer', '10', '{}', 'x'); RAISE EXCEPTION 'titulaire bloqué';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%titulaire%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'employe_creer', NULL, '{"nom":"X","identifiant":"xx","role":"titulaire"}', 'x'); RAISE EXCEPTION 'titulaire créé';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%titulaire%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'droits_role', 'titulaire', '{"permissions":[]}', 'x'); RAISE EXCEPTION 'droits du titulaire';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%titulaire%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'employe_bloquer', '12', '{}', 'x'); RAISE EXCEPTION 'déjà bloqué';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%déjà bloqué%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'essai', NULL, '{}', ' '); RAISE EXCEPTION 'sans auteur';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%Auteur%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'prix', '1', '{}', 'x'); RAISE EXCEPTION 'type inconnu';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%inconnu%', SQLERRM; END;
    BEGIN PERFORM cloud_creer_ordre(a, 'employe_modifier', '99', '{"nom":"Y"}', 'x'); RAISE EXCEPTION 'employé inconnu';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%inconnu%', SQLERRM; END;

    -- 3. Annulation (republiée), puis refus d'annuler un ordre déjà traité
    PERFORM cloud_annuler_ordre(a, v_essai, 'admin.essai');
    ASSERT (SELECT statut FROM ordre_central WHERE id = v_essai) = 'annule', 'annulé';
    ASSERT (SELECT donnees ->> 'statut' FROM descendant WHERE nom_table = 'ordre_central' AND cle = v_essai::text) = 'annule', 'annulation publiée';
    BEGIN PERFORM cloud_annuler_ordre(a, v_essai, 'admin.essai'); RAISE EXCEPTION 'annulé deux fois';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%déjà traité%', SQLERRM; END;

    -- 4. Expiration
    UPDATE ordre_central SET cree_le = now() - interval '8 days', expire_le = now() - interval '1 day' WHERE id = v_debloq;
    ASSERT cloud_expirer_ordres() = 1, 'un ordre expiré';
    PERFORM set_config('app.officine_id', a::text, true);
    ASSERT (SELECT statut FROM ordre_central WHERE id = v_debloq) = 'expire', 'expiré';
    ASSERT (SELECT donnees ->> 'statut' FROM descendant WHERE nom_table = 'ordre_central' AND cle = v_debloq::text) = 'expire', 'expiration publiée';
END $$;

-- 5. Résultats renvoyés par l'officine : l'ordre prend l'état constaté (identifiants relevés avant de prendre le rôle de l'API)
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000013', true) \g /dev/null
SELECT (SELECT id FROM ordre_central WHERE type = 'employe_bloquer') AS bloq, (SELECT id FROM ordre_central WHERE type = 'droits_role') AS droits \gset
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000013', true);
SELECT cloud_appliquer_lot(1, 2, repeat('b', 64), 'sync-v1', convert_to(format(
    '{"lignes":[{"n":20,"table":"sync_ordre_resultat","id":"%s","op":"creation","donnees":{"ordre_id":"%s","statut":"applique","motif":null,"traite_le":"2026-11-18T10:00:00+00:00"}},'
    '{"n":21,"table":"sync_ordre_resultat","id":"%s","op":"creation","donnees":{"ordre_id":"%s","statut":"a_confirmer","motif":null,"traite_le":"2026-11-18T10:00:00+00:00"}}]}',
    :'bloq', :'bloq', :'droits', :'droits'), 'UTF8'));
SELECT cloud_appliquer_lot(1, 3, repeat('c', 64), 'sync-v1', convert_to(format(
    '{"lignes":[{"n":22,"table":"sync_ordre_resultat","id":"%s","op":"modification","donnees":{"ordre_id":"%s","statut":"refuse","motif":"modifié dans l''officine","traite_le":"2026-11-18T11:00:00+00:00"}}]}',
    :'droits', :'droits'), 'UTF8'));
RESET ROLE;
DO $$
BEGIN
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000013', true);
    ASSERT (SELECT statut FROM ordre_central WHERE type = 'employe_bloquer') = 'applique', 'blocage appliqué';
    ASSERT (SELECT statut || ' / ' || motif FROM ordre_central WHERE type = 'droits_role') = 'refuse / modifié dans l''officine', 'droits refusés après confirmation demandée';
    ASSERT (SELECT count(*) FROM ordre_central_journal WHERE par = 'officine') = 3, 'résultats journalisés';
    -- Cloisonnement
    PERFORM set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000013', true);
    ASSERT (SELECT count(*) FROM ordre_central) = 0 AND (SELECT count(*) FROM ordre_central_journal) = 0, 'cloisonnement';
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000013', true);
    BEGIN DELETE FROM ordre_central_journal; RAISE EXCEPTION 'journal supprimé';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%ajout seul%', SQLERRM; END;
END $$;

\echo 'Tests 013 : ordres vers les officines réussis'
ROLLBACK;
