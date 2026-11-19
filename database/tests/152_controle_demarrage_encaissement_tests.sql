-- =====================================================================
-- Tests du script 152 (encaissement suspendu au démarrage jusqu'au contrôle du serveur central), en transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/152_controle_demarrage_encaissement_tests.sql
-- Le compteur des tickets n'avance pas (ROLLBACK) ; aucune séquence consommée.
-- =====================================================================
BEGIN;

DO $$
DECLARE v_avant bigint; v_numero text;
BEGIN
    SELECT dernier_numero INTO v_avant FROM compteur_ticket WHERE id = 1;
    UPDATE sync_parametre SET active = true, controle_demarrage_delai_s = 120, demarrage_le = NULL, controle_demarrage_le = NULL,
           retour_arriere_le = NULL, retour_arriere_central = NULL, retour_arriere_sequences_le = NULL WHERE id = 1;

    -- 1. Aucun démarrage noté : rien n'est attendu
    ASSERT encaissement_attente_secondes() = 0, 'sans démarrage noté : permis';

    -- 2. Démarrage il y a 30 s, contrôle pas fait : attente d'environ 90 s, n° refusé, compteur inchangé
    UPDATE sync_parametre SET demarrage_le = now() - interval '30 seconds' WHERE id = 1;
    ASSERT encaissement_attente_secondes() BETWEEN 89 AND 91, 'attente restante : ' || encaissement_attente_secondes();
    BEGIN PERFORM prochain_numero_vente(); RAISE EXCEPTION 'n° donné pendant l''attente';
    EXCEPTION WHEN raise_exception THEN IF SQLERRM NOT LIKE 'Encaissement en attente du contrôle de démarrage%' THEN RAISE; END IF; END;
    ASSERT (SELECT dernier_numero FROM compteur_ticket WHERE id = 1) = v_avant, 'compteur inchangé par le refus';

    -- 3. Contrôle fait : permis, n° suivant donné
    PERFORM sync_noter_controle_demarrage();
    ASSERT (SELECT controle_demarrage_le FROM sync_parametre WHERE id = 1) IS NOT NULL, 'contrôle noté';
    ASSERT encaissement_attente_secondes() = 0, 'contrôle fait : permis';
    v_numero := prochain_numero_vente();
    ASSERT (SELECT dernier_numero FROM compteur_ticket WHERE id = 1) = v_avant + 1, 'n° donné après le contrôle : ' || v_numero;

    -- 4. Délai écoulé sans contrôle (central injoignable) : permis
    UPDATE sync_parametre SET demarrage_le = now() - interval '121 seconds', controle_demarrage_le = NULL WHERE id = 1;
    ASSERT encaissement_attente_secondes() = 0, 'délai écoulé : permis';

    -- 5. Délai réglé à 0 : jamais d'attente ; réglage hors bornes refusé
    UPDATE sync_parametre SET demarrage_le = now() WHERE id = 1;
    ASSERT encaissement_attente_secondes() = 120, 'démarrage à l''instant : 120 s';
    PERFORM sync_regler_controle_demarrage(0);
    ASSERT encaissement_attente_secondes() = 0, 'délai 0 : permis';
    BEGIN PERFORM sync_regler_controle_demarrage(601); RAISE EXCEPTION 'délai de 601 s accepté';
    EXCEPTION WHEN raise_exception THEN IF SQLERRM NOT LIKE 'Attente au démarrage%' THEN RAISE; END IF; END;
    PERFORM sync_regler_controle_demarrage(120);

    -- 6. Retour en arrière détecté, avance des n° pas faite : attente même contrôle fait ; avance faite : permis
    UPDATE sync_parametre SET controle_demarrage_le = now(), retour_arriere_le = now() - interval '20 seconds', retour_arriere_central = 5 WHERE id = 1;
    ASSERT encaissement_attente_secondes() BETWEEN 99 AND 101, 'retour en arrière sans avance : attente';
    UPDATE sync_parametre SET retour_arriere_sequences_le = now() WHERE id = 1;
    ASSERT encaissement_attente_secondes() = 0, 'avance faite : permis';

    -- 7. Synchronisation désactivée : jamais d'attente ; sync_noter_demarrage n'attend rien
    UPDATE sync_parametre SET active = false, demarrage_le = now(), controle_demarrage_le = NULL WHERE id = 1;
    ASSERT encaissement_attente_secondes() = 0, 'synchronisation désactivée : permis';
    ASSERT sync_noter_demarrage() = false, 'désactivée : rien à attendre';
    ASSERT (SELECT demarrage_le FROM sync_parametre WHERE id = 1) IS NULL, 'désactivée : pas de démarrage noté';

    -- 8. Synchronisation active et serveur activé : démarrage noté, contrôle précédent effacé
    UPDATE sync_parametre SET active = true, adresse_serveur = coalesce(adresse_serveur, 'https://central.essai'),
           inscrite_le = coalesce(inscrite_le, now()), controle_demarrage_le = now() WHERE id = 1;
    ASSERT sync_noter_demarrage() = true, 'active et activée : contrôle attendu';
    ASSERT (SELECT controle_demarrage_le IS NULL AND demarrage_le = now() FROM sync_parametre WHERE id = 1), 'démarrage noté, contrôle effacé';
    ASSERT encaissement_attente_secondes() = 120, 'attente pleine après le démarrage';
END $$;

ROLLBACK;
\echo '152 : tous réussis'
