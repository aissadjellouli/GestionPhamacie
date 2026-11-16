-- =====================================================================
-- Tests du script 140 (supervision et purge de la file d'envoi), en transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/140_synchronisation_supervision_purge_tests.sql
-- Base de départ : synchronisation jamais utilisée (aucun lot, file vide) ; rien n'est conservé (ROLLBACK).
-- =====================================================================
BEGIN;

DO $$
DECLARE
    v_ok boolean;
    r    record;
    n    bigint;
BEGIN
    ASSERT (SELECT count(*) FROM sync_lot) = 0 AND (SELECT count(*) FROM sync_sortie) = 0, 'base de départ : aucun lot, file vide';

    -- ---- Valeurs de départ ----
    ASSERT (SELECT (bandeau_minutes, alerte_retard_heures, alerte_erreur_minutes, purge_active, purge_jours, purge_dernier_lot)
            FROM sync_parametre) = ROW(60, 24, 60, true, 30, 0::bigint), 'valeurs de départ 60 / 24 / 60 / purge active / 30 jours';

    -- ---- Constitution de 6 lots de 3 lignes ----
    FOR i IN 1..6 LOOP
        INSERT INTO sync_sortie (nom_table, id_local, operation, donnees, version_contrat, epoque)
        SELECT 'vente', (i * 10 + g)::text, 'creation', '{"id":1}'::jsonb, 'sync-v1', 1 FROM generate_series(1, 3) g;
        ASSERT sync_enregistrer_lot(i, ARRAY(SELECT id FROM sync_sortie WHERE lot_numero IS NULL ORDER BY id), 100, repeat('a', 64)) = i,
               'lot ' || i || ' constitué';
    END LOOP;
    -- Lignes hors lot (en attente)
    INSERT INTO sync_sortie (nom_table, id_local, operation, donnees, version_contrat, epoque)
    VALUES ('vente', '900', 'creation', '{"id":9}'::jsonb, 'sync-v1', 1);

    -- Accusés : lots 1 et 2 il y a 40 jours, lot 3 il y a 5 jours, lots 4 à 6 pas accusés (NULL -> date : seul changement permis)
    UPDATE sync_lot SET accuse_le = now() - interval '40 days' WHERE numero IN (1, 2);
    UPDATE sync_lot SET accuse_le = now() - interval '5 days' WHERE numero = 3;
    UPDATE sync_parametre SET dernier_accuse = 3 WHERE id = 1;

    -- ---- Purge à 30 jours : lots 1 et 2 seulement ----
    SELECT * INTO r FROM sync_purger(30, 20);
    ASSERT (r.lots, r.lignes, r.jusqu_au_lot) = (2, 6::bigint, 2::bigint), 'purge à 30 jours : lots 1 et 2, 6 lignes : ' || r::text;
    ASSERT r.octets = 200, 'octets = taille des lots retirés';
    ASSERT (SELECT count(*) FROM sync_sortie WHERE lot_numero IN (1, 2)) = 0, 'lignes des lots 1 et 2 retirées';
    ASSERT (SELECT count(*) FROM sync_sortie WHERE lot_numero IN (3, 4, 5, 6)) = 12, 'lignes des lots 3 à 6 intactes';
    ASSERT (SELECT count(*) FROM sync_sortie WHERE lot_numero IS NULL) = 1, 'ligne en attente intacte';
    ASSERT (SELECT count(*) FROM sync_lot) = 6, 'les 6 lignes de lot restent (numéros, empreinte)';
    ASSERT (SELECT (purge_epoque, purge_dernier_lot, derniere_purge_lignes, purge_lignes_total) FROM sync_parametre) = ROW(1, 2::bigint, 6::bigint, 6::bigint),
           'état de la purge mémorisé';

    -- Rejeu : rien de plus (lot 3 accusé il y a 5 jours seulement)
    SELECT * INTO r FROM sync_purger(30, 20);
    ASSERT (r.lots, r.lignes, r.jusqu_au_lot) = (0, 0::bigint, 0::bigint), 'rejeu : rien';
    ASSERT (SELECT purge_dernier_lot FROM sync_parametre) = 2, 'repère inchangé par un appel sans purge';

    -- ---- Délai de 1 jour : lot 3 (accusé), jamais les lots 4 à 6 (non accusés) ni la ligne en attente ----
    SELECT * INTO r FROM sync_purger(1, 20);
    ASSERT (r.lots, r.lignes, r.jusqu_au_lot) = (1, 3::bigint, 3::bigint), 'délai de 1 jour : lot 3 seulement : ' || r::text;
    ASSERT (SELECT count(*) FROM sync_sortie WHERE lot_numero IN (4, 5, 6)) = 9, 'lots non accusés : jamais purgés';
    ASSERT (SELECT count(*) FROM sync_sortie WHERE lot_numero IS NULL) = 1, 'ligne en attente jamais purgée';
    ASSERT (SELECT purge_lignes_total FROM sync_parametre) = 9, 'cumul des lignes purgées';

    -- ---- La garde de la file reste en vigueur : suppression directe d'un lot non accusé refusée ----
    v_ok := false;
    BEGIN
        DELETE FROM sync_sortie WHERE lot_numero = 4;
    EXCEPTION WHEN OTHERS THEN
        v_ok := SQLERRM LIKE '%suppression permise seulement après accusé%';
    END;
    ASSERT v_ok, 'suppression directe d''une ligne d''un lot non accusé refusée par la garde';
    v_ok := false;
    BEGIN
        DELETE FROM sync_lot WHERE numero = 1;
    EXCEPTION WHEN OTHERS THEN
        v_ok := SQLERRM LIKE '%lot figé%';
    END;
    ASSERT v_ok, 'un lot n''est jamais supprimé';

    -- ---- Chargement en cours : ses lignes ne sont jamais purgées, même accusées depuis longtemps ----
    INSERT INTO sync_chargement (motif, etat, epoque, repere) VALUES ('demande', 'annonce', 1, 0) RETURNING numero INTO n;
    INSERT INTO sync_sortie (nom_table, id_local, operation, donnees, version_contrat, epoque, ordre, chargement)
    VALUES ('vente', '700', 'instantane', '{"id":7}'::jsonb, 'sync-v1', 1, 0, n);
    PERFORM sync_enregistrer_lot(7, ARRAY(SELECT id FROM sync_sortie WHERE lot_numero IS NULL AND chargement IS NOT NULL), 50, repeat('b', 64));
    UPDATE sync_lot SET accuse_le = now() - interval '90 days' WHERE numero IN (4, 5, 6, 7);
    UPDATE sync_parametre SET dernier_accuse = 7 WHERE id = 1;
    SELECT * INTO r FROM sync_purger(30, 20);
    ASSERT (r.lots, r.jusqu_au_lot) = (3, 6::bigint), 'lots 4 à 6 purgés, arrêt avant le lot du chargement en cours : ' || r::text;
    ASSERT (SELECT count(*) FROM sync_sortie WHERE lot_numero = 7) = 1, 'ligne du chargement en cours gardée';
    -- Chargement terminé : le lot 7 peut partir
    UPDATE sync_chargement SET etat = 'termine', termine_le = now() WHERE numero = n;
    SELECT * INTO r FROM sync_purger(30, 20);
    ASSERT (r.lots, r.jusqu_au_lot) = (1, 7::bigint), 'chargement terminé : lot 7 purgé : ' || r::text;
    ASSERT (SELECT count(*) FROM sync_sortie WHERE lot_numero IS NOT NULL) = 0 AND (SELECT count(*) FROM sync_sortie) = 1,
           'il ne reste que la ligne en attente';

    -- ---- Paramètres invalides ----
    v_ok := false; BEGIN PERFORM sync_purger(0, 20); EXCEPTION WHEN OTHERS THEN v_ok := SQLERRM LIKE '%délai de conservation invalide%'; END;
    ASSERT v_ok, 'délai 0 refusé';
    v_ok := false; BEGIN PERFORM sync_purger(30, 0); EXCEPTION WHEN OTHERS THEN v_ok := SQLERRM LIKE '%nombre de lots%'; END;
    ASSERT v_ok, 'nombre de lots 0 refusé';

    -- ---- Passage d'envoi : début et nature de l'erreur ----
    PERFORM sync_noter_passage('autre');
    ASSERT (SELECT erreur_nature FROM sync_parametre) = 'autre' AND (SELECT erreur_depuis_le FROM sync_parametre) IS NOT NULL, 'erreur notée';
    UPDATE sync_parametre SET erreur_depuis_le = now() - interval '3 hours' WHERE id = 1;
    PERFORM sync_noter_passage('quota');
    ASSERT (SELECT erreur_nature FROM sync_parametre) = 'quota', 'nature mise à jour';
    ASSERT (SELECT now() - erreur_depuis_le FROM sync_parametre) > interval '2 hours 59 minutes', 'le début de l''erreur est gardé tant qu''elle dure';
    PERFORM sync_noter_passage(NULL);
    ASSERT (SELECT (erreur_nature, erreur_depuis_le) IS NOT DISTINCT FROM (NULL::text, NULL::timestamptz) FROM sync_parametre), 'passage réussi : erreur effacée';
    v_ok := false; BEGIN PERFORM sync_noter_passage('inconnue'); EXCEPTION WHEN OTHERS THEN v_ok := true; END;
    ASSERT v_ok, 'nature inconnue refusée';

    -- ---- Alertes : dernière date par sujet ----
    PERFORM sync_noter_alerte('retard', now() - interval '2 hours');
    PERFORM sync_noter_alerte('quota', now());
    ASSERT (SELECT alerte_retard_le IS NOT NULL AND alerte_quota_le IS NOT NULL AND alerte_erreur_le IS NULL AND alerte_certificat_le IS NULL FROM sync_parametre),
           'une date par sujet, les autres inchangées';
    v_ok := false; BEGIN PERFORM sync_noter_alerte('autre_sujet', now()); EXCEPTION WHEN OTHERS THEN v_ok := true; END;
    ASSERT v_ok, 'sujet inconnu refusé';

    -- ---- Réglages et bornes ----
    PERFORM sync_regler_supervision(30, 12, 45, false, 90);
    ASSERT (SELECT (bandeau_minutes, alerte_retard_heures, alerte_erreur_minutes, purge_active, purge_jours) FROM sync_parametre) = ROW(30, 12, 45, false, 90),
           'réglages enregistrés';
    v_ok := false; BEGIN PERFORM sync_regler_supervision(0, 12, 45, true, 90); EXCEPTION WHEN check_violation THEN v_ok := true; END;
    ASSERT v_ok, 'bandeau 0 refusé';
    v_ok := false; BEGIN PERFORM sync_regler_supervision(30, 12, 45, true, 366); EXCEPTION WHEN check_violation THEN v_ok := true; END;
    ASSERT v_ok, 'conservation de 366 jours refusée';

    -- ---- Droit et sujet de notification ----
    ASSERT EXISTS (SELECT 1 FROM permission WHERE code = 'sync.suivre' AND NOT sensible), 'droit sync.suivre';
    ASSERT EXISTS (SELECT 1 FROM role_permission rp JOIN role ro ON ro.id = rp.role_id WHERE ro.code = 'titulaire' AND rp.permission_code = 'sync.suivre'),
           'titulaire a le droit sync.suivre';
    ASSERT NOT EXISTS (SELECT 1 FROM role_permission rp JOIN role ro ON ro.id = rp.role_id WHERE ro.code <> 'titulaire' AND rp.permission_code = 'sync.suivre'),
           'aucun autre rôle par défaut';
    INSERT INTO destinataire_notification (nom, email, canal_email, types) VALUES ('Essai 140', 'essai140@example.test', true, ARRAY['synchronisation']);
    ASSERT (SELECT count(*) FROM destinataire_notification WHERE nom = 'Essai 140') = 1, 'sujet synchronisation accepté pour un destinataire';
END $$;

ROLLBACK;
