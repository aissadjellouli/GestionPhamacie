-- =====================================================================
-- Tests de la vérification d'intégrité au central (script 012), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/012_verification_integrite_central_tests.sql
-- Les empreintes sont calculées ici avec la forme canonique du central ; l'égalité avec celle de l'officine est
-- vérifiée par l'essai de bout en bout sur copies jetables (docs/conception-synchronisation.md § 22).
-- =====================================================================
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000012', 'Essai S4 A'), ('bbbbbbbb-0000-0000-0000-000000000012', 'Essai S4 B');

-- Chaîne d'essai de l'officine A : 3 lignes reçues par un lot (empreintes posées ensuite, déclencheur suspendu le temps du calcul)
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000012', true);
SELECT cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1', convert_to(
    '{"lignes":['
    '{"n":1,"table":"journal_vente","id":"1","op":"creation","donnees":{"id":1,"evenement":"ouverture","survenu_le":"2026-11-01T08:00:00.123456+00:00","enregistre_le":"2026-11-01T08:00:00.123456+00:00","montant_ttc":0.00,"cle_chaine":"officine","rang":1,"hash":"\\x00","hash_precedent":"\\x00"}},'
    '{"n":2,"table":"journal_vente","id":"2","op":"creation","donnees":{"id":2,"evenement":"annulation","numero_ticket":"T-000001","vente_id":1,"survenu_le":"2026-11-01T09:00:00+00:00","enregistre_le":"2026-11-01T09:00:00+00:00","montant_ttc":-12.50,"montant_ht":-10.50,"montant_tva":-2.00,"motif":"appel [TÉL]","cle_chaine":"officine","rang":2,"hash":"\\x00","hash_precedent":"\\x00"}},'
    '{"n":3,"table":"journal_vente","id":"3","op":"creation","donnees":{"id":3,"evenement":"vente","numero_ticket":"T-000002","vente_id":2,"survenu_le":"2026-11-01T10:00:00+00:00","enregistre_le":"2026-11-01T10:00:00+00:00","montant_ttc":30.00,"montant_ht":25.21,"montant_tva":4.79,"poste":"CAISSE-1","cle_chaine":"officine","rang":3,"hash":"\\x00","hash_precedent":"\\x00"}}'
    ']}', 'UTF8'));
RESET ROLE;

DO $$
DECLARE l record; v_prec bytea := decode(repeat('00', 32), 'hex'); v_can text; v_h bytea;
BEGIN
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000012', true);
    ALTER TABLE journal_vente DISABLE TRIGGER trg_cloud_integrite_ajout_seul;
    FOR l IN SELECT id, rang FROM journal_vente WHERE officine_id = 'aaaaaaaa-0000-0000-0000-000000000012' ORDER BY rang LOOP
        EXECUTE format('SELECT %s FROM journal_vente r WHERE r.officine_id = $1 AND r.id = $2', cloud_integrite_expression('journal_vente'))
            INTO v_can USING 'aaaaaaaa-0000-0000-0000-000000000012'::uuid, l.id;
        -- Ligne 2 : l'officine a chaîné le motif d'origine, le central n'a reçu que le motif filtré
        IF l.rang = 2 THEN v_can := replace(v_can, 'motif=11:appel [TÉL]', 'motif=16:appel 0661234567'); END IF;
        v_h := sha256(convert_to(v_can, 'UTF8') || v_prec);
        UPDATE journal_vente SET hash = v_h, hash_precedent = v_prec WHERE officine_id = 'aaaaaaaa-0000-0000-0000-000000000012' AND id = l.id;
        v_prec := v_h;
    END LOOP;
    ALTER TABLE journal_vente ENABLE TRIGGER trg_cloud_integrite_ajout_seul;
END $$;

DO $$
DECLARE r jsonb; e cloud_integrite_etat; v_h3 bytea; d descendant;
BEGIN
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000012', true);

    -- 1. Chaîne intacte : vérification complète sans anomalie, la ligne au motif filtré comptée « non vérifiable »
    r := cloud_verifier_integrite(true);
    SELECT * INTO e FROM cloud_integrite_etat WHERE journal = 'journal_vente' AND cle_chaine = 'officine';
    ASSERT e.statut = 'ok' AND e.dernier_rang = 3 AND e.algorithme = 'sha256', 'chaîne intacte : ' || e.statut || ' / ' || e.dernier_rang;
    ASSERT e.non_verifiables = 1, 'ligne au texte filtré non vérifiable : ' || e.non_verifiables;
    ASSERT (r ->> 'statut') = 'ok' AND (r ->> 'anomalies_total')::int = 0, 'résumé : ' || r::text;
    ASSERT r ->> 'format' = 'integrite-v1', 'format du résumé';
    SELECT * INTO d FROM descendant WHERE nom_table = 'reglage_central' AND cle = 'integrite_centrale';
    ASSERT FOUND AND d.donnees ->> 'statut' = 'ok', 'résultat publié vers l''officine';
    -- Incrémental : rien de nouveau, rien ne change
    r := cloud_verifier_integrite(false);
    ASSERT (SELECT dernier_rang FROM cloud_integrite_etat WHERE journal = 'journal_vente' AND cle_chaine = 'officine') = 3, 'incrémental sans nouvelle ligne';
    SELECT hash INTO v_h3 FROM journal_vente WHERE id = 3;

    -- 2. Ajout seul au central : modification reçue refusée (original gardé, anomalie), texte filtré seul différent : gardé sans anomalie
    SET LOCAL ROLE pharma_cloud_api;
    PERFORM cloud_appliquer_lot(1, 2, repeat('b', 64), 'sync-v1', convert_to(
        '{"lignes":[{"n":4,"table":"journal_vente","id":"3","op":"modification","donnees":{"id":3,"evenement":"vente","numero_ticket":"T-000002","vente_id":2,"survenu_le":"2026-11-01T10:00:00+00:00","enregistre_le":"2026-11-01T10:00:00+00:00","montant_ttc":3.00,"montant_ht":25.21,"montant_tva":4.79,"poste":"CAISSE-1","cle_chaine":"officine","rang":3,"hash":"\\x00","hash_precedent":"\\x00"}}]}', 'UTF8'));
    RESET ROLE;
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000012', true);
    ASSERT (SELECT montant_ttc FROM journal_vente WHERE id = 3) = 30.00 AND (SELECT hash FROM journal_vente WHERE id = 3) = v_h3, 'ligne d''origine gardée';
    ASSERT (SELECT count(*) FROM cloud_anomalie_integrite WHERE nature = 'modification_refusee' AND journal = 'journal_vente' AND rang = 3) = 1, 'modification refusée notée';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE evenement = 'integrite_anomalie' AND officine_id = 'aaaaaaaa-0000-0000-0000-000000000012'), 'journal de sécurité';
    -- Renvoi identique (chargement complet) : accepté sans anomalie
    UPDATE journal_vente SET sync_recu_le = sync_recu_le WHERE id = 3;
    ASSERT (SELECT count(*) FROM cloud_anomalie_integrite) = 1, 'renvoi identique sans anomalie';
    -- Seul le motif filtré diffère : gardé, sans anomalie
    UPDATE journal_vente SET motif = 'appel [N°]' WHERE id = 2;
    ASSERT (SELECT motif FROM journal_vente WHERE id = 2) = 'appel [TÉL]', 'motif d''origine gardé';
    ASSERT (SELECT count(*) FROM cloud_anomalie_integrite) = 1, 'texte refiltré sans anomalie';
    -- Suppression reçue : ligne gardée, anomalie
    DELETE FROM journal_vente WHERE id = 3;
    ASSERT EXISTS (SELECT 1 FROM journal_vente WHERE id = 3), 'suppression refusée';
    ASSERT (SELECT count(*) FROM cloud_anomalie_integrite WHERE nature = 'suppression_refusee') = 1, 'suppression notée';

    -- 3. Trou : rang 5 reçu sans le rang 4 ; récent : en attente ; au-delà du délai : anomalie
    SET LOCAL ROLE pharma_cloud_api;
    PERFORM cloud_appliquer_lot(1, 3, repeat('c', 64), 'sync-v1', convert_to(
        '{"lignes":[{"n":5,"table":"journal_vente","id":"5","op":"creation","donnees":{"id":5,"evenement":"vente","cle_chaine":"officine","rang":5,"hash":"\\x01","hash_precedent":"\\x02"}}]}', 'UTF8'));
    RESET ROLE;
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000012', true);
    r := cloud_verifier_integrite(false);
    ASSERT (SELECT statut FROM cloud_integrite_etat WHERE journal = 'journal_vente' AND cle_chaine = 'officine') = 'attente', 'trou récent : attente';
    ASSERT r ->> 'statut' = 'anomalie', 'des anomalies existent déjà (modification, suppression)';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_anomalie_integrite WHERE nature = 'ligne_manquante'), 'pas encore d''anomalie';
    UPDATE journal_vente SET sync_recu_le = now() - interval '25 hours' WHERE id = 5;
    r := cloud_verifier_integrite(false);
    ASSERT EXISTS (SELECT 1 FROM cloud_anomalie_integrite WHERE nature = 'ligne_manquante' AND rang = 4), 'rang 4 manquant après le délai';
    ASSERT (SELECT statut FROM cloud_integrite_etat WHERE journal = 'journal_vente' AND cle_chaine = 'officine') = 'rupture', 'chaîne en rupture';
    r := cloud_verifier_integrite(false);
    ASSERT (SELECT count(*) FROM cloud_anomalie_integrite WHERE nature = 'ligne_manquante') = 1, 'anomalie notée une seule fois';

    -- 4. Sceaux : ancrage juste, ancrage faux, ligne scellée pas encore reçue
    SET LOCAL ROLE pharma_cloud_api;
    PERFORM cloud_appliquer_lot(1, 4, repeat('d', 64), 'sync-v1', convert_to(format(
        '{"lignes":[{"n":6,"table":"sceau_integrite","id":"1","op":"creation","donnees":{"id":1,"journal":"journal_vente","cle_chaine":"officine","dernier_rang":3,"dernier_id":3,"dernier_hash":"\\x%s"}},'
        '{"n":7,"table":"sceau_integrite","id":"2","op":"creation","donnees":{"id":2,"journal":"journal_vente","cle_chaine":"officine","dernier_rang":3,"dernier_id":3,"dernier_hash":"\\xabcd"}},'
        '{"n":8,"table":"sceau_integrite","id":"3","op":"creation","donnees":{"id":3,"journal":"journal_vente","cle_chaine":"officine","dernier_rang":9,"dernier_id":9,"dernier_hash":"\\xabcd"}},'
        '{"n":9,"table":"sceau_integrite","id":"4","op":"creation","donnees":{"id":4,"journal":"integrite_essai","cle_chaine":"officine","dernier_rang":1,"dernier_hash":"\\xabcd"}}]}',
        encode(v_h3, 'hex')), 'UTF8'));
    RESET ROLE;
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000012', true);
    r := cloud_verifier_integrite(false);
    ASSERT (SELECT statut FROM cloud_integrite_sceau WHERE sceau_id = 1) = 'ok', 'sceau 1 ancré';
    ASSERT (SELECT statut FROM cloud_integrite_sceau WHERE sceau_id = 2) = 'incoherent', 'sceau 2 incohérent';
    ASSERT (SELECT statut FROM cloud_integrite_sceau WHERE sceau_id = 3) = 'attente', 'sceau 3 en attente';
    ASSERT (SELECT statut FROM cloud_integrite_sceau WHERE sceau_id = 4) = 'non_verifiable', 'journal hors référentiel';
    ASSERT EXISTS (SELECT 1 FROM cloud_anomalie_integrite WHERE nature = 'sceau_incoherent' AND cle_ligne = 'sceau 2'), 'sceau incohérent noté';
    UPDATE sceau_integrite SET sync_recu_le = now() - interval '25 hours' WHERE id = 3;
    r := cloud_verifier_integrite(false);
    ASSERT (SELECT statut FROM cloud_integrite_sceau WHERE sceau_id = 3) = 'incoherent', 'sceau 3 sans ligne après le délai';
    ASSERT EXISTS (SELECT 1 FROM cloud_anomalie_integrite WHERE nature = 'sceau_sans_ligne'), 'sceau sans ligne noté';

    -- 5. Tickets : T-000004 manquant (T-000005 reçu il y a plus que le délai), T-000005 validée sans ligne au journal
    SET LOCAL ROLE pharma_cloud_api;
    PERFORM cloud_appliquer_lot(1, 5, repeat('e', 64), 'sync-v1', convert_to(
        '{"lignes":[{"n":10,"table":"compteur_ticket","id":"1","op":"creation","donnees":{"id":1,"prefixe":"T-","dernier_numero":5,"premier_numero_continu":1,"mis_en_service_le":"2026-11-01T07:00:00+00:00"}},'
        '{"n":11,"table":"vente","id":"1","op":"creation","donnees":{"id":1,"numero":"T-000001","statut":"annulee"}},'
        '{"n":12,"table":"vente","id":"2","op":"creation","donnees":{"id":2,"numero":"T-000002","statut":"validee"}},'
        '{"n":13,"table":"vente","id":"3","op":"creation","donnees":{"id":3,"numero":"T-000003","statut":"en_cours"}},'
        '{"n":14,"table":"vente","id":"5","op":"creation","donnees":{"id":5,"numero":"T-000005","statut":"validee"}}]}', 'UTF8'));
    RESET ROLE;
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000012', true);
    r := cloud_verifier_integrite(false);
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_anomalie_integrite WHERE nature = 'ticket_manquant'), 'ticket manquant récent : attente';
    -- (même transaction : now() ne bouge pas, la fenêtre des ventes déjà contrôlées ne peut pas avancer ; la vérification complète les reprend toutes)
    UPDATE vente SET sync_recu_le = now() - interval '25 hours';
    r := cloud_verifier_integrite(true);
    ASSERT EXISTS (SELECT 1 FROM cloud_anomalie_integrite WHERE nature = 'ticket_manquant' AND cle_ligne = 'T-000004'), 'T-000004 manquant';
    ASSERT EXISTS (SELECT 1 FROM cloud_anomalie_integrite WHERE nature = 'vente_sans_ligne' AND cle_ligne = 'T-000005'), 'vente sans ligne au journal';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_anomalie_integrite WHERE nature = 'vente_sans_ligne' AND cle_ligne = 'T-000002'), 'T-000002 a sa ligne';
    ASSERT (SELECT tickets_jusqu_a FROM cloud_integrite_officine) = 5, 'point de reprise des tickets : ' || (SELECT tickets_jusqu_a FROM cloud_integrite_officine);

    -- 6. Vérification croisée : la fin de chaîne de l'officine (rang 3) porte une autre empreinte que celle reçue
    SET LOCAL ROLE pharma_cloud_api;
    PERFORM cloud_appliquer_lot(1, 6, repeat('f', 64), 'sync-v1', convert_to(
        '{"lignes":[{"n":15,"table":"verification_integrite","id":"1","op":"creation","donnees":{"id":1,"journal":"journal_vente","cle_chaine":"officine","statut":"ok","verifie_le":"2026-11-02T03:00:00+00:00","dernier_rang":3,"dernier_hash":"\\xbeef"}},'
        '{"n":16,"table":"verification_integrite","id":"2","op":"creation","donnees":{"id":2,"journal":"journal_vente","cle_chaine":"numerotation","statut":"ok","verifie_le":"2026-11-02T03:00:00+00:00"}}]}', 'UTF8'));
    RESET ROLE;
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000012', true);
    r := cloud_verifier_integrite(false);
    ASSERT EXISTS (SELECT 1 FROM cloud_anomalie_integrite WHERE nature = 'reecriture_apres_envoi' AND rang = 3), 'réécriture après envoi détectée';
    ASSERT (SELECT derniere_verification_id FROM cloud_integrite_officine) = 2, 'point de reprise des vérifications';
    ASSERT (r ->> 'derniere_anomalie_id')::bigint = (SELECT max(id) FROM cloud_anomalie_integrite), 'dernière anomalie publiée';
    ASSERT jsonb_array_length(r -> 'anomalies') = (SELECT count(*) FROM cloud_anomalie_integrite), 'anomalies publiées';

    -- 7. Altération directe de la base centrale (déclencheur contourné) : la vérification complète la trouve
    ALTER TABLE journal_vente DISABLE TRIGGER trg_cloud_integrite_ajout_seul;
    UPDATE journal_vente SET montant_ttc = 0.01 WHERE id = 1;
    ALTER TABLE journal_vente ENABLE TRIGGER trg_cloud_integrite_ajout_seul;
    r := cloud_verifier_integrite(true);
    ASSERT EXISTS (SELECT 1 FROM cloud_anomalie_integrite WHERE nature = 'ligne_modifiee' AND rang = 1), 'ligne modifiée au central détectée';
    ASSERT (SELECT rang_rupture FROM cloud_integrite_etat WHERE journal = 'journal_vente' AND cle_chaine = 'officine') = 1, 'rupture au rang 1';

    -- 8. Cloisonnement : l'officine B ne voit rien de A
    PERFORM set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000012', true);
    ASSERT (SELECT count(*) FROM cloud_anomalie_integrite) = 0 AND (SELECT count(*) FROM cloud_integrite_etat) = 0, 'cloisonnement';
    r := cloud_verifier_integrite(false);
    ASSERT r ->> 'statut' = 'ok' AND (r ->> 'anomalies_total')::int = 0, 'officine B sans données : ok';
END $$;

-- 9. Droits : l'API ne lance pas la vérification, ne modifie ni ne supprime une anomalie
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000012', true);
DO $$
BEGIN
    BEGIN
        PERFORM cloud_verifier_integrite(false);
        RAISE EXCEPTION 'vérification lancée par l''API';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;
    BEGIN
        UPDATE cloud_anomalie_integrite SET detail = 'x';
        RAISE EXCEPTION 'anomalie modifiée par l''API';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;
END $$;
RESET ROLE;
DO $$
BEGIN
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000012', true);
    BEGIN
        DELETE FROM cloud_anomalie_integrite;
        RAISE EXCEPTION 'anomalie supprimée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%ajout seul%', SQLERRM;
    END;
END $$;

\echo 'Tests 012 : vérification d''intégrité au central réussis'
ROLLBACK;
