-- =====================================================================
-- Tests du script 101b (suppression de vente.num_facture_chifa), en
-- transaction ANNULÉE. À lancer UNIQUEMENT sur une COPIE JETABLE de la base
-- (le script supprime une colonne : même annulée, ne jamais viser une base
-- de recette ou de production) :
--   createdb -T pharmacie_dev copie_101b
--   psql -U pharma_app -d copie_101b -v ON_ERROR_STOP=1 -f database/tests/101b_suppression_num_facture_chifa_tests.sql
--   dropdb copie_101b
-- Nécessite psql 15 ou plus (variables ERROR et LAST_ERROR_MESSAGE).
-- Pré-requis : script 101 exécuté (colonne num_facture_chifa encore présente).
-- =====================================================================
\set ON_ERROR_STOP on
BEGIN;

DO $$
BEGIN
    ASSERT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'vente' AND column_name = 'num_facture_chifa' AND table_schema = 'public'),
        'pré-requis : la colonne vente.num_facture_chifa doit exister sur la copie de test';
END $$;

-- Ventes d'essai : les trois premières ; valeurs de test hors plage réelle (9xxxxxxx)
CREATE TEMP TABLE t101b_vente AS
SELECT id, row_number() OVER (ORDER BY id) AS rang FROM (SELECT id FROM vente ORDER BY id LIMIT 3) x;
CREATE TEMP TABLE t101b_res (cas text, erreur boolean, message text);

-- Situation de départ : aucun écart
UPDATE vente SET num_facture_chifa = NULL, chifa_facture = NULL WHERE id IN (SELECT id FROM t101b_vente);
DELETE FROM vente_chifa_facture_conflit;

-- ---- Cas 1 : conflit non tranché -> refus ----
INSERT INTO vente_chifa_facture_conflit (vente_id, num_facture_chifa, chifa_facture)
SELECT id, '91111111', '92222222' FROM t101b_vente WHERE rang = 1;
UPDATE vente SET num_facture_chifa = '91111111', chifa_facture = '92222222' WHERE id = (SELECT id FROM t101b_vente WHERE rang = 1);

\set ON_ERROR_STOP off
SAVEPOINT s1;
\ir ../migration/101b_suppression_num_facture_chifa.sql
\set err :ERROR
\set msg :'LAST_ERROR_MESSAGE'
ROLLBACK TO SAVEPOINT s1;
\set ON_ERROR_STOP on
INSERT INTO t101b_res VALUES ('non_tranche', :err, :msg);

-- ---- Cas 2 : valeur qui serait perdue (num renseigné, chifa vide) -> refus ----
UPDATE vente_chifa_facture_conflit SET tranche_le = now(), valeur_retenue = '92222222';
UPDATE vente SET num_facture_chifa = '93333333', chifa_facture = NULL WHERE id = (SELECT id FROM t101b_vente WHERE rang = 2);

\set ON_ERROR_STOP off
SAVEPOINT s2;
\ir ../migration/101b_suppression_num_facture_chifa.sql
\set err :ERROR
\set msg :'LAST_ERROR_MESSAGE'
ROLLBACK TO SAVEPOINT s2;
\set ON_ERROR_STOP on
INSERT INTO t101b_res VALUES ('valeur_perdue', :err, :msg);

-- ---- Cas 3 : valeurs différentes sans conflit tranché -> refus ----
UPDATE vente SET chifa_facture = '93333333' WHERE id = (SELECT id FROM t101b_vente WHERE rang = 2);
UPDATE vente SET num_facture_chifa = '94444444', chifa_facture = '95555555' WHERE id = (SELECT id FROM t101b_vente WHERE rang = 3);

\set ON_ERROR_STOP off
SAVEPOINT s3;
\ir ../migration/101b_suppression_num_facture_chifa.sql
\set err :ERROR
\set msg :'LAST_ERROR_MESSAGE'
ROLLBACK TO SAVEPOINT s3;
\set ON_ERROR_STOP on
INSERT INTO t101b_res VALUES ('divergence', :err, :msg);

-- ---- Cas 4 : objet dépendant (vue) -> refus, puis retrait de la vue d'essai ----
INSERT INTO vente_chifa_facture_conflit (vente_id, num_facture_chifa, chifa_facture, tranche_le, valeur_retenue)
SELECT id, '94444444', '95555555', now(), '95555555' FROM t101b_vente WHERE rang = 3;
CREATE VIEW v_t101b_dependante AS SELECT id, num_facture_chifa FROM vente;

\set ON_ERROR_STOP off
SAVEPOINT s4;
\ir ../migration/101b_suppression_num_facture_chifa.sql
\set err :ERROR
\set msg :'LAST_ERROR_MESSAGE'
ROLLBACK TO SAVEPOINT s4;
\set ON_ERROR_STOP on
INSERT INTO t101b_res VALUES ('dependance', :err, :msg);
DROP VIEW v_t101b_dependante;

-- ---- Cas 5 : une chaîne d'intégrité citerait la colonne -> refus (jamais de suppression qui casserait les sceaux) ----
CREATE TEMP TABLE t101b_etat_chaine AS SELECT count(*) AS nb FROM chaine_integrite;
\set ON_ERROR_STOP off
SAVEPOINT s5;
INSERT INTO chaine_integrite (journal, libelle, colonnes) VALUES ('t101b_journal_essai', 'essai 101b', ARRAY['id', 'num_facture_chifa']);
\ir ../migration/101b_suppression_num_facture_chifa.sql
\set err :ERROR
\set msg :'LAST_ERROR_MESSAGE'
ROLLBACK TO SAVEPOINT s5;
\set ON_ERROR_STOP on
INSERT INTO t101b_res VALUES ('chaine', :err, :msg);

-- Etat « tout tranché » : lot de synchronisation constitué AVANT la suppression, contenant la colonne
DO $$
DECLARE p sync_parametre; s1 bigint; v_lot bigint; v_cols text[]; v_vente bigint;
BEGIN
    SELECT id INTO v_vente FROM t101b_vente WHERE rang = 1;
    UPDATE sync_parametre SET descendant_version = 0 WHERE id = 1;
    v_cols := sync_colonnes_interdites();
    SELECT * INTO p FROM sync_parametre WHERE id = 1;
    INSERT INTO sync_sortie (nom_table, id_local, operation, donnees, version_contrat, epoque)
    VALUES ('vente', v_vente::text, 'modification',
            jsonb_build_object('id', v_vente, 'num_facture_chifa', '91111111', 'chifa_facture', '92222222'),
            p.version_contrat, p.epoque) RETURNING id INTO s1;
    v_lot := sync_constituer_lot(p.dernier_lot + 1, ARRAY[s1], 10, repeat('a', 64), v_cols);
    ASSERT v_lot IS NOT NULL, 'lot constitué avant la suppression';
    CREATE TEMP TABLE t101b_lot AS SELECT p.epoque AS epoque, v_lot AS numero, s1 AS sortie_id,
        (SELECT donnees::text FROM sync_sortie WHERE id = s1) AS donnees_avant,
        (SELECT row(l.*)::text FROM sync_lot l WHERE l.epoque = p.epoque AND l.numero = v_lot) AS lot_avant;
END $$;

-- Dernière vérification avant succès : état de la structure
CREATE TEMP TABLE t101b_vues_avant AS SELECT count(*) AS nb FROM pg_views WHERE schemaname = 'public';
CREATE TEMP TABLE t101b_audit_avant AS SELECT count(*) AS nb FROM journal_audit WHERE action = 'colonne_supprimee';

-- ---- Cas 6 : tout est tranché -> succès ----
SAVEPOINT s6;
\ir ../migration/101b_suppression_num_facture_chifa.sql
-- (ON_ERROR_STOP actif : une erreur ici arrête le test)

-- ---- Cas 7 : deuxième exécution -> sans effet ----
\ir ../migration/101b_suppression_num_facture_chifa.sql

-- ---- Vérifications finales ----
DO $$
DECLARE r record; n bigint; v_lot t101b_lot;
BEGIN
    -- Refus : bons messages, bonne cause
    ASSERT (SELECT erreur FROM t101b_res WHERE cas = 'non_tranche'), 'non tranché : doit être refusé';
    ASSERT (SELECT message FROM t101b_res WHERE cas = 'non_tranche') LIKE '%non tranché%', 'message non tranché : ' || (SELECT message FROM t101b_res WHERE cas = 'non_tranche');
    ASSERT (SELECT erreur FROM t101b_res WHERE cas = 'valeur_perdue'), 'valeur perdue : doit être refusé';
    ASSERT (SELECT message FROM t101b_res WHERE cas = 'valeur_perdue') LIKE '%chifa_facture vide%', 'message valeur perdue : ' || (SELECT message FROM t101b_res WHERE cas = 'valeur_perdue');
    ASSERT (SELECT erreur FROM t101b_res WHERE cas = 'divergence'), 'divergence : doit être refusé';
    ASSERT (SELECT message FROM t101b_res WHERE cas = 'divergence') LIKE '%sans conflit tranché%', 'message divergence : ' || (SELECT message FROM t101b_res WHERE cas = 'divergence');
    ASSERT (SELECT erreur FROM t101b_res WHERE cas = 'chaine'), 'chaîne d''intégrité : doit être refusé';
    ASSERT (SELECT message FROM t101b_res WHERE cas = 'chaine') LIKE '%empreinte%', 'message chaîne : ' || (SELECT message FROM t101b_res WHERE cas = 'chaine');
    ASSERT (SELECT erreur FROM t101b_res WHERE cas = 'dependance'), 'dépendance : doit être refusé';
    ASSERT (SELECT message FROM t101b_res WHERE cas = 'dependance') LIKE '%dépendent de la colonne%', 'message dépendance : ' || (SELECT message FROM t101b_res WHERE cas = 'dependance');

    -- Succès : colonne supprimée, une seule ligne de journal, rien d'autre perdu
    ASSERT NOT EXISTS (SELECT 1 FROM information_schema.columns
                       WHERE table_schema = 'public' AND table_name = 'vente' AND column_name = 'num_facture_chifa'),
        'colonne supprimée';
    ASSERT (SELECT count(*) FROM journal_audit WHERE action = 'colonne_supprimee') = (SELECT nb FROM t101b_audit_avant) + 1,
        'une seule ligne de journal_audit (la 2e exécution n''en ajoute pas)';
    ASSERT (SELECT details ->> 'script' FROM journal_audit WHERE action = 'colonne_supprimee' ORDER BY id DESC LIMIT 1) = '101b', 'journal : script';
    ASSERT (SELECT details::text FROM journal_audit WHERE action = 'colonne_supprimee' ORDER BY id DESC LIMIT 1) !~ '[0-9]{8}', 'journal : aucune valeur de n° de facture';
    ASSERT (SELECT chifa_facture FROM vente WHERE id = (SELECT id FROM t101b_vente WHERE rang = 1)) = '92222222', 'chifa_facture conservée';
    ASSERT (SELECT chifa_facture FROM vente WHERE id = (SELECT id FROM t101b_vente WHERE rang = 2)) = '93333333', 'chifa_facture conservée (2)';
    ASSERT (SELECT num_facture_chifa FROM vente_chifa_facture_conflit WHERE vente_id = (SELECT id FROM t101b_vente WHERE rang = 1)) = '91111111',
        'la table des conflits garde sa copie';

    -- Vues : toutes encore lisibles ; aucune vue perdue
    ASSERT (SELECT count(*) FROM pg_views WHERE schemaname = 'public') = (SELECT nb FROM t101b_vues_avant), 'aucune vue supprimée';
    FOR r IN SELECT viewname FROM pg_views WHERE schemaname = 'public' LOOP
        EXECUTE format('SELECT count(*) FROM (SELECT * FROM %I LIMIT 1) q', r.viewname);
    END LOOP;
    PERFORM count(*) FROM (SELECT * FROM v_parcours_boite LIMIT 1) q;

    -- Fonctions : toutes sont encore valides (liaison des corps vérifiée par plpgsql_check absent : on appelle les points d'entrée sensibles)
    ASSERT NOT EXISTS (SELECT sync_tables_non_classees()), 'sync : toutes les tables classées';

    -- Synchronisation : le lot constitué avant la suppression est inchangé et reste renvoyable à l'identique
    SELECT * INTO v_lot FROM t101b_lot;
    ASSERT (SELECT donnees::text FROM sync_sortie WHERE id = v_lot.sortie_id) = v_lot.donnees_avant, 'lot : contenu de la ligne figé';
    ASSERT (SELECT row(l.*)::text FROM sync_lot l WHERE l.epoque = v_lot.epoque AND l.numero = v_lot.numero) = v_lot.lot_avant, 'lot : en-tête figé';
    ASSERT sync_retirer_colonnes('vente', (SELECT donnees FROM sync_sortie WHERE id = v_lot.sortie_id),
                                 (SELECT colonnes_retirees FROM sync_lot WHERE epoque = v_lot.epoque AND numero = v_lot.numero))
           = (SELECT donnees FROM sync_sortie WHERE id = v_lot.sortie_id), 'lot : retrait des colonnes figées sans effet sur l''ancienne clé';
    PERFORM sync_enregistrer_accuse(v_lot.epoque, v_lot.numero);

    -- Synchronisation : nouvelle ligne et nouveau lot après la suppression
    DECLARE p sync_parametre; s2 bigint; v_lot2 bigint; v_cols text[]; v_id bigint;
    BEGIN
        v_cols := sync_colonnes_interdites();
        SELECT * INTO p FROM sync_parametre WHERE id = 1;
        SELECT id INTO v_id FROM t101b_vente WHERE rang = 1;
        INSERT INTO sync_sortie (nom_table, id_local, operation, donnees, version_contrat, epoque)
        VALUES ('vente', v_id::text, 'modification', (SELECT to_jsonb(v) FROM vente v WHERE v.id = v_id), p.version_contrat, p.epoque)
        RETURNING id INTO s2;
        ASSERT NOT ((SELECT donnees FROM sync_sortie WHERE id = s2) ? 'num_facture_chifa'), 'nouvelle ligne sans l''ancienne colonne';
        v_lot2 := sync_constituer_lot(p.dernier_lot + 1, ARRAY[s2], 10, repeat('b', 64), v_cols);
        ASSERT v_lot2 IS NOT NULL, 'nouveau lot constitué après la suppression';
    END;

    -- Intégrité des journaux : toutes les chaînes inscrites se vérifient encore
    FOR r IN SELECT journal FROM chaine_integrite LOOP
        SELECT count(*) INTO n FROM integrite_verifier(r.journal) v WHERE v.motif IS NOT NULL;
        ASSERT n = 0, 'chaîne d''intégrité invalide : ' || r.journal;
    END LOOP;
    ASSERT (SELECT count(*) FROM chaine_integrite) = (SELECT nb FROM t101b_etat_chaine), 'registre des chaînes inchangé';
END $$;

\echo 'Tests du script 101b : tous réussis (transaction annulée).'
ROLLBACK;
