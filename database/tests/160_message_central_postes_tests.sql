-- =====================================================================
-- Tests du script 160 (message du titulaire à distance affiché sur les postes), en transaction ANNULÉE, depuis la racine du
-- dépôt, après le script 159 :
--   psql -U pharma_app -d <base jetable> -v ON_ERROR_STOP=1 -f database/tests/160_message_central_postes_tests.sql
-- Le script 160 est rejoué deux fois dans la transaction ; rien n'est gardé (ROLLBACK). Fermeture : seulement un message
-- appliqué et pas encore fermé, auteur obligatoire, journal des actions sans le texte ; la contrainte refuse une fermeture
-- posée sur un autre ordre ; le message reçu par la voie descendante arrive « à appliquer ».
-- =====================================================================
BEGIN;

\ir ../migration/160_message_central_postes.sql
\ir ../migration/160_message_central_postes.sql

DO $$
DECLARE
    v_user bigint := (SELECT min(id) FROM utilisateur);
    m uuid := 'aaaaaaaa-0000-4000-8000-000000000160';
    e uuid := 'bbbbbbbb-0000-4000-8000-000000000160';
BEGIN
    ASSERT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'sync_ordre_recu' AND column_name = 'message_ferme_le'), 'colonne message_ferme_le';
    ASSERT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'ix_sync_ordre_recu_message'), 'index des messages';
    ASSERT (SELECT sens FROM sync_table WHERE nom_table = 'sync_ordre_recu') = 'jamais', 'table jamais envoyée : miroir du central inchangé';

    INSERT INTO sync_ordre_recu (id, type, donnees, auteur, cree_le, expire_le, version, statut)
    VALUES (m, 'message', jsonb_build_object('format', 'message-v1', 'texte', 'Inventaire lundi', 'jusqu_au', to_char((now() + interval '2 days') AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"')),
            'titulaire à distance (essai.160)', now(), now() + interval '1 day', 1, 'a_appliquer'),
           (e, 'essai', '{}', 'console', now(), now() + interval '1 day', 1, 'applique');

    -- Pas encore appliqué : rien à fermer
    ASSERT NOT sync_message_central_fermer(m, v_user), 'message pas encore appliqué';
    PERFORM sync_ordre_noter(m, 'applique', 'affiché sur les postes', NULL);
    -- Sans auteur : refusé
    BEGIN PERFORM sync_message_central_fermer(m, NULL); RAISE EXCEPTION 'fermeture sans auteur acceptée';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE '%sans auteur%' THEN RAISE; END IF;
    END;
    -- Un ordre qui n'est pas un message
    ASSERT NOT sync_message_central_fermer(e, v_user), 'pas un message';
    BEGIN UPDATE sync_ordre_recu SET message_ferme_le = now(), message_ferme_par = v_user WHERE id = e; RAISE EXCEPTION 'fermeture d''un autre ordre acceptée';
    EXCEPTION WHEN check_violation THEN NULL; END;
    -- Fermé une fois, journal sans le texte
    ASSERT sync_message_central_fermer(m, v_user), 'message fermé';
    ASSERT NOT sync_message_central_fermer(m, v_user), 'déjà fermé';
    ASSERT (SELECT message_ferme_par FROM sync_ordre_recu WHERE id = m) = v_user, 'auteur de la fermeture';
    ASSERT (SELECT count(*) FROM journal_audit WHERE action = 'message_central_ferme' AND details ->> 'ordre' = m::text) = 1, 'une ligne de journal';
    ASSERT NOT EXISTS (SELECT 1 FROM journal_audit WHERE action = 'message_central_ferme' AND details::text LIKE '%Inventaire%'), 'jamais le texte au journal';
    ASSERT (SELECT statut FROM sync_ordre_recu WHERE id = m) = 'applique', 'le statut remonté ne change pas';
END $$;

ROLLBACK;
\echo '160 : tous réussis'
