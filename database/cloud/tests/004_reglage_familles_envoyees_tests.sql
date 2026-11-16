-- =====================================================================
-- Tests des réglages du serveur central (script 004), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/004_reglage_familles_envoyees_tests.sql
-- =====================================================================
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000005', 'Essai E'), ('bbbbbbbb-0000-0000-0000-000000000006', 'Essai F');

-- ---- Réglage 1 : familles nominatives ----
DO $$
BEGIN
    ASSERT cloud_familles_envoyees('aaaaaaaa-0000-0000-0000-000000000005') = '{courriel_commandes,fournisseurs,pharmacie}',
        'défauts : ' || cloud_familles_envoyees('aaaaaaaa-0000-0000-0000-000000000005')::text;
    ASSERT (SELECT count(*) FROM cloud_famille_colonne) >= 10, 'colonnes des familles (004, complétées par 007)';
    -- Surcharge par officine, puis retour à la valeur globale
    PERFORM cloud_regler_famille('employes', true, 'bbbbbbbb-0000-0000-0000-000000000006');
    PERFORM cloud_regler_famille('fournisseurs', false, 'bbbbbbbb-0000-0000-0000-000000000006');
    ASSERT cloud_familles_envoyees('bbbbbbbb-0000-0000-0000-000000000006') = '{courriel_commandes,employes,pharmacie}', 'surcharge';
    ASSERT cloud_familles_envoyees('aaaaaaaa-0000-0000-0000-000000000005') = '{courriel_commandes,fournisseurs,pharmacie}', 'autre officine inchangée';
    PERFORM cloud_regler_famille('employes', NULL, 'bbbbbbbb-0000-0000-0000-000000000006');
    ASSERT cloud_familles_envoyees('bbbbbbbb-0000-0000-0000-000000000006') = '{courriel_commandes,pharmacie}', 'retour au global';
    ASSERT (SELECT count(*) FROM journal_securite WHERE evenement = 'reglage_famille'
            AND officine_id = 'bbbbbbbb-0000-0000-0000-000000000006') = 3, 'réglages journalisés';
    BEGIN
        PERFORM cloud_regler_famille('inconnue', true);
        RAISE EXCEPTION 'famille inconnue acceptée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE 'Famille inconnue%', SQLERRM;
    END;
END $$;

-- Réception par l'API : employés non autorisés pour E (défaut) -> effacés du miroir et du lot brut, journal une fois par jour
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000005', true);
DO $$
DECLARE r record;
BEGIN
    ASSERT cloud_familles_officine_courante() = '{courriel_commandes,fournisseurs,pharmacie}', 'route : familles de l''officine courante';
    SELECT * INTO r FROM cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1', convert_to(
        '{"lignes":[{"n":1,"table":"utilisateur","id":"3","op":"creation","donnees":{"id":3,"nom_complet":"Nom Prénom","identifiant":"np","role_id":2}},'
        '{"n":2,"table":"fournisseur","id":"7","op":"creation","donnees":{"id":7,"raison_sociale":"Grossiste","telephone":"0550000000"}}]}', 'UTF8'));
    ASSERT r.statut = 'accepte', r.statut;
    ASSERT (SELECT nom_complet IS NULL AND identifiant IS NULL AND role_id = 2 FROM utilisateur WHERE id = 3), 'employé sans nom au miroir';
    ASSERT (SELECT telephone FROM fournisseur WHERE id = 7) = '0550000000', 'fournisseur autorisé gardé';
    ASSERT (SELECT colonnes_effacees = '{utilisateur.identifiant,utilisateur.nom_complet}' AND expurge_le IS NOT NULL
                   AND position('Nom Pr' IN convert_from(contenu, 'UTF8')) = 0 AND empreinte = repeat('a', 64)
            FROM lot_recu WHERE numero = 1), 'lot brut expurgé, empreinte reçue gardée';
    SELECT * INTO r FROM cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1', convert_to('{"lignes":[]}', 'UTF8'));
    ASSERT r.statut = 'deja_recu', 'renvoi identique : déjà reçu';
    SELECT * INTO r FROM cloud_appliquer_lot(1, 2, repeat('b', 64), 'sync-v1', convert_to(
        '{"lignes":[{"n":3,"table":"utilisateur","id":"4","op":"creation","donnees":{"id":4,"nom_complet":"Autre","role_id":1}}]}', 'UTF8'));
    ASSERT (SELECT nom_complet FROM utilisateur WHERE id = 4) IS NULL, 'deuxième lot effacé aussi';
    SELECT * INTO r FROM cloud_appliquer_lot(1, 3, repeat('c', 64), 'sync-v1', convert_to(
        '{"lignes":[{"n":4,"table":"utilisateur","id":"5","op":"creation","donnees":{"id":5,"role_id":1}}]}', 'UTF8'));
    ASSERT (SELECT colonnes_effacees IS NULL AND expurge_le IS NULL FROM lot_recu WHERE numero = 3), 'lot déjà filtré par l''officine : intact';
    -- Le rôle applicatif ne peut ni modifier ni effacer un lot brut, ni régler
    BEGIN
        UPDATE lot_recu SET expurge_le = now() WHERE numero = 1;
        RAISE EXCEPTION 'lot modifié par l''API';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;
    BEGIN
        PERFORM cloud_regler_famille('employes', true);
        RAISE EXCEPTION 'réglage par l''API';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;
END $$;
RESET ROLE;

DO $$
BEGIN
    ASSERT (SELECT count(*) FROM journal_securite WHERE evenement = 'familles_effacees_reception'
            AND officine_id = 'aaaaaaaa-0000-0000-0000-000000000005') = 1, 'effacement à la réception journalisé une fois par jour';
END $$;

-- Effacement de ce qui a été reçu pour une famille décochée ensuite
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000005', true);
DO $$
DECLARE n bigint;
BEGIN
    BEGIN
        PERFORM cloud_effacer_famille('fournisseurs', 'aaaaaaaa-0000-0000-0000-000000000005', true);
        RAISE EXCEPTION 'effacement d''une famille encore envoyée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%encore envoyée%', SQLERRM;
    END;
    PERFORM cloud_regler_famille('fournisseurs', false, 'aaaaaaaa-0000-0000-0000-000000000005');
    -- Simulation : rien ne change
    SELECT sum(lignes) INTO n FROM cloud_effacer_famille('fournisseurs', 'aaaaaaaa-0000-0000-0000-000000000005');
    ASSERT n = 1, 'simulation : une valeur';
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000005', true);
    ASSERT (SELECT telephone FROM fournisseur WHERE id = 7) = '0550000000', 'simulation sans effet';
    SELECT sum(lots) INTO n FROM cloud_effacer_famille('fournisseurs', 'aaaaaaaa-0000-0000-0000-000000000005', true);
    ASSERT n = 1, 'un lot brut expurgé : ' || n;
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000005', true);
    ASSERT (SELECT telephone IS NULL AND raison_sociale = 'Grossiste' FROM fournisseur WHERE id = 7), 'téléphone effacé, reste gardé';
    ASSERT (SELECT colonnes_effacees = '{fournisseur.adresse,fournisseur.email,fournisseur.telephone,utilisateur.identifiant,utilisateur.nom_complet}'
                   AND position('0550000000' IN convert_from(contenu, 'UTF8')) = 0 FROM lot_recu WHERE numero = 1), 'lot brut expurgé';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'famille_effacee' AND officine_id = 'aaaaaaaa-0000-0000-0000-000000000005'), 'journal';
    -- Hors de la fonction : lot brut toujours en ajout seul (même opération posée, contenu non conforme)
    BEGIN
        UPDATE lot_recu SET nb_lignes = 9 WHERE numero = 1;
        RAISE EXCEPTION 'lot brut modifié';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%ajout seul%', SQLERRM;
    END;
    PERFORM set_config('cloud.lot_recu_operation', 'expurger', true);
    BEGIN
        UPDATE lot_recu SET contenu = convert_to('{"lignes":[]}', 'UTF8'), expurge_le = now() WHERE numero = 1;
        RAISE EXCEPTION 'contenu remplacé';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%ajout seul%', SQLERRM;
    END;
    PERFORM set_config('cloud.lot_recu_operation', '', true);
END $$;

-- ---- Réglage 2 : conservation des lots bruts ----
DO $$
DECLARE n bigint; r record;
BEGIN
    ASSERT (SELECT conservation_lots_ans = 6 AND NOT purge_lots_active FROM cloud_parametre WHERE id = 1), 'défauts : 6 ans, effacement désactivé';
    BEGIN
        PERFORM cloud_regler_conservation(5);
        RAISE EXCEPTION '5 ans acceptés';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%6 ans minimum%', SQLERRM;
    END;
    BEGIN
        PERFORM cloud_regler_conservation(3, 'bbbbbbbb-0000-0000-0000-000000000006');
        RAISE EXCEPTION '3 ans acceptés pour une officine';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%6 ans minimum%', SQLERRM;
    END;
    BEGIN
        UPDATE cloud_parametre SET conservation_lots_ans = 2 WHERE id = 1;
        RAISE EXCEPTION 'contrainte absente';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    PERFORM cloud_regler_conservation(50);
    ASSERT cloud_conservation_ans('aaaaaaaa-0000-0000-0000-000000000005') = 50, 'pas de maximum';
    PERFORM cloud_regler_conservation(6);
    PERFORM cloud_regler_conservation(10, 'bbbbbbbb-0000-0000-0000-000000000006');
    ASSERT cloud_conservation_ans('bbbbbbbb-0000-0000-0000-000000000006') = 10, 'surcharge 10 ans';

    -- Partition mensuelle vide d'il y a 8 ans (supprimée par l'effacement) et d'il y a 5 ans (gardée : moins de 6 ans)
    EXECUTE format('CREATE TABLE lot_recu_%s PARTITION OF lot_recu FOR VALUES FROM (%L) TO (%L)', to_char(now() - interval '8 years', 'YYYY_MM'),
                   date_trunc('month', now() - interval '8 years'), date_trunc('month', now() - interval '8 years') + interval '1 month');
    EXECUTE format('CREATE TABLE lot_recu_%s PARTITION OF lot_recu FOR VALUES FROM (%L) TO (%L)', to_char(now() - interval '5 years', 'YYYY_MM'),
                   date_trunc('month', now() - interval '5 years'), date_trunc('month', now() - interval '5 years') + interval '1 month');
    -- Lots reçus il y a 7 ans : E (6 ans) dépassée, F (10 ans) non
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000005', true);
    INSERT INTO lot_recu (officine_id, epoque, numero, empreinte, version_contrat, nb_lignes, octets, contenu, recu_le)
    VALUES ('aaaaaaaa-0000-0000-0000-000000000005', 9, 1, repeat('d', 64), 'sync-v1', 0, 2, '\x7b7d', now() - interval '7 years');
    PERFORM set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000006', true);
    INSERT INTO lot_recu (officine_id, epoque, numero, empreinte, version_contrat, nb_lignes, octets, contenu, recu_le)
    VALUES ('bbbbbbbb-0000-0000-0000-000000000006', 9, 1, repeat('d', 64), 'sync-v1', 0, 2, '\x7b7d', now() - interval '7 years');

    -- Désactivé par défaut : refus, et suppression directe refusée même avec l'opération posée
    BEGIN
        PERFORM cloud_purger_lots(false);
        RAISE EXCEPTION 'effacement actif par défaut';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%désactivé%', SQLERRM;
    END;
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000005', true);
    PERFORM set_config('cloud.lot_recu_operation', 'purge', true);
    BEGIN
        DELETE FROM lot_recu WHERE epoque = 9;
        RAISE EXCEPTION 'suppression sans activation';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%ajout seul%', SQLERRM;
    END;
    PERFORM set_config('cloud.lot_recu_operation', '', true);
    -- Simulation : seule E est concernée
    SELECT count(*), sum(lots) INTO r FROM cloud_purger_lots(true);
    ASSERT r.count = 1 AND r.sum = 1, 'simulation : un lot de E';
    ASSERT NOT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'lots_purges'), 'simulation non journalisée';

    -- Activation par commande, effacement, journal ; le lot récent et celui de F restent ; hors fonction, toujours refusé
    PERFORM cloud_regler_purge(true);
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000005', true);
    BEGIN
        DELETE FROM lot_recu WHERE epoque = 9;
        RAISE EXCEPTION 'suppression sans opération posée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%ajout seul%', SQLERRM;
    END;
    PERFORM set_config('cloud.lot_recu_operation', 'purge', true);
    BEGIN
        DELETE FROM lot_recu WHERE epoque = 1;
        RAISE EXCEPTION 'lot récent supprimé';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%ajout seul%', SQLERRM;
    END;
    PERFORM set_config('cloud.lot_recu_operation', '', true);
    SELECT sum(lots) INTO n FROM cloud_purger_lots(false);
    ASSERT n = 1, 'un lot effacé';
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000005', true);
    ASSERT NOT EXISTS (SELECT 1 FROM lot_recu WHERE epoque = 9) AND (SELECT count(*) FROM lot_recu WHERE epoque = 1) = 3, 'E : ancien effacé, récents gardés';
    PERFORM set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000006', true);
    ASSERT EXISTS (SELECT 1 FROM lot_recu WHERE epoque = 9), 'F (10 ans) gardé';
    ASSERT (SELECT detail FROM journal_securite WHERE evenement = 'lots_purges' AND officine_id = 'aaaaaaaa-0000-0000-0000-000000000005')
           LIKE '1 lot(s) brut(s) effacé(s), reçus du % au % (conservation 6 ans)', 'journal : nombre, période, officine';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'reglage_purge'), 'activation journalisée';
    ASSERT to_regclass('lot_recu_' || to_char(now() - interval '8 years', 'YYYY_MM')) IS NULL, 'partition vide de plus de 6 ans supprimée';
    ASSERT to_regclass('lot_recu_' || to_char(now() - interval '5 years', 'YYYY_MM')) IS NOT NULL, 'partition de moins de 6 ans gardée';
    ASSERT (SELECT count(*) FROM journal_securite WHERE evenement = 'partition_supprimee') = 1, 'suppression de partition journalisée';
END $$;

\echo 'Tests du script 004 : tous réussis (transaction annulée).'
ROLLBACK;
