-- =====================================================================
-- Tests du script 149 (base revenue en arrière, avance des n°, nouvelle époque), en transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/149_synchronisation_retour_arriere_nouvelle_epoque_tests.sql
-- Les séquences ne suivent pas l'annulation : celles que le test consomme (sync_chargement, sync_sortie) sont remises à leur
-- valeur de départ avant le ROLLBACK ; l'avance des n° est essayée sur une table créée dans la transaction (annulée avec elle).
-- =====================================================================
BEGIN;

CREATE TEMP TABLE t149_seq ON COMMIT DROP AS
SELECT s.nom, s.last_value, s.is_called FROM (
    SELECT pg_get_serial_sequence('sync_chargement', 'numero') AS nom, last_value, is_called FROM sync_chargement_numero_seq
    UNION ALL SELECT pg_get_serial_sequence('sync_sortie', 'id'), last_value, is_called FROM sync_sortie_id_seq) s;

-- 1. Colonnes présentes, CHECK posé une seule fois
DO $$
BEGIN
    ASSERT (SELECT count(*) FROM information_schema.columns WHERE table_name = 'sync_parametre'
            AND column_name IN ('retour_arriere_le', 'retour_arriere_central', 'retour_arriere_sequences_le', 'retour_arriere_alerte_le')
            AND is_nullable = 'YES') = 4, 'quatre colonnes facultatives';
    ASSERT (SELECT count(*) FROM pg_constraint WHERE conname = 'sync_parametre_retour_arriere_check') = 1, 'CHECK posé une seule fois';
    BEGIN UPDATE sync_parametre SET retour_arriere_le = now() WHERE id = 1; RAISE EXCEPTION 'date sans n° du central acceptée';
    EXCEPTION WHEN check_violation THEN NULL; END;
    BEGIN UPDATE sync_parametre SET retour_arriere_sequences_le = now() WHERE id = 1; RAISE EXCEPTION 'avance sans détection acceptée';
    EXCEPTION WHEN check_violation THEN NULL; END;
END $$;

-- Départ connu : époque 3, 5 lots formés dont 4 accusés (le 5e non accusé), une ligne non envoyée, un chargement annoncé
DO $$
DECLARE n int;
BEGIN
    UPDATE sync_chargement SET etat = 'remplace' WHERE etat NOT IN ('termine', 'remplace');
    UPDATE sync_parametre SET epoque = 3, dernier_lot = 5, dernier_accuse = 4, purge_epoque = 3, purge_dernier_lot = 2,
                              retour_arriere_le = NULL, retour_arriere_central = NULL, retour_arriere_sequences_le = NULL,
                              retour_arriere_alerte_le = NULL WHERE id = 1;
    INSERT INTO sync_lot (epoque, numero, version_contrat, nb_lignes, octets, empreinte, accuse_le)
    SELECT 3, g, 'sync-v1', 1, 10, repeat('a', 64), CASE WHEN g <= 4 THEN now() END FROM generate_series(1, 5) g;
    INSERT INTO sync_sortie (nom_table, id_local, operation, donnees, version_contrat, epoque)
    VALUES ('vente', '999999', 'creation', '{"id": 999999}', 'sync-v1', 3);
    INSERT INTO sync_chargement (motif, etat, epoque, repere) VALUES ('demande', 'annonce', 3, 0) RETURNING numero INTO n;
    PERFORM set_config('t149.chargement', n::text, true);
END $$;

-- 2. Nouvelle époque refusée sans détection
DO $$
BEGIN
    BEGIN PERFORM sync_nouvelle_epoque(1, NULL, 3); RAISE EXCEPTION 'nouvelle époque acceptée sans détection';
    EXCEPTION WHEN check_violation THEN NULL; END;
    ASSERT (SELECT epoque FROM sync_parametre WHERE id = 1) = 3, 'époque inchangée après le refus';
END $$;

-- 3a. Variante sur refus « empreinte différente » (correction 2026-11-18) : posée même si le central n'est pas en avance sur dernier_lot
DO $$
BEGIN
    ASSERT NOT sync_noter_retour_arriere_refus(NULL), 'central inconnu : rien';
    ASSERT NOT sync_noter_retour_arriere_refus(0), 'n° nul : rien';
    ASSERT sync_noter_retour_arriere_refus(5), 'lot 5 refusé, central = local : détection posée quand même';
    ASSERT NOT sync_noter_retour_arriere_refus(6), 'détection déjà posée : false';
    ASSERT (SELECT retour_arriere_central FROM sync_parametre WHERE id = 1) = 6, 'plus grand n° vu au central gardé';
    UPDATE sync_parametre SET retour_arriere_le = NULL, retour_arriere_central = NULL WHERE id = 1;
END $$;

-- 3. Détection : rien si le central n'est pas en avance ; posée une fois ; n° du central gardé au plus grand
DO $$
BEGIN
    ASSERT NOT sync_noter_retour_arriere(5), 'central = local : pas de retour en arrière';
    ASSERT NOT sync_noter_retour_arriere(2), 'central en retard : pas de retour en arrière';
    ASSERT NOT sync_noter_retour_arriere(NULL), 'central inconnu : rien';
    ASSERT (SELECT retour_arriere_le FROM sync_parametre WHERE id = 1) IS NULL, 'rien noté';
    ASSERT sync_noter_retour_arriere(9), 'première détection : true';
    ASSERT NOT sync_noter_retour_arriere(12), 'détection déjà posée : false';
    ASSERT NOT sync_noter_retour_arriere(7), 'n° plus petit : false';
    ASSERT (SELECT retour_arriere_central FROM sync_parametre WHERE id = 1) = 12, 'plus grand n° vu au central gardé';
    ASSERT (SELECT retour_arriere_le FROM sync_parametre WHERE id = 1) IS NOT NULL, 'détection posée';
END $$;

-- 4. Avance des n° : jamais en arrière, seulement les tables envoyées à clé {id} avec séquence ; un échec par table n'arrête pas les autres
CREATE TABLE public.t149_essai (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, libelle text);
CREATE TABLE public.t149_petite (id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY);
INSERT INTO sync_table (nom_table, sens, motif) VALUES ('t149_essai', 'montante', 'essai du script 149'), ('t149_petite', 'montante', 'essai du script 149');
INSERT INTO public.t149_essai (libelle) VALUES ('a'), ('b'), ('c');
DO $$
DECLARE r record; v bigint; n int := 0;
BEGIN
    -- Valeur au-delà d'une séquence integer : la table est rendue en erreur, l'autre est avancée, l'avance n'est pas notée faite
    FOR r IN SELECT * FROM sync_avancer_sequences('{"t149_essai": 300, "t149_petite": 3000000000}') LOOP
        n := n + 1;
        IF r.nom_table = 't149_petite' THEN
            ASSERT r.erreur IS NOT NULL AND r.apres IS NULL, format('échec attendu pour t149_petite (%s)', r.erreur);
        ELSE
            ASSERT r.erreur IS NULL AND r.avant = 3 AND r.apres = 300, format('avance 3 -> 300 attendue (%s -> %s, %s)', r.avant, r.apres, r.erreur);
        END IF;
    END LOOP;
    ASSERT n = 2, 'deux tables rendues';
    ASSERT (SELECT retour_arriere_sequences_le FROM sync_parametre WHERE id = 1) IS NULL, 'avance non notée faite après un échec (nouvel essai)';

    SELECT * INTO r FROM sync_avancer_sequences('{"t149_essai": 500, "table_inconnue": 900, "sync_lot": 900, "pharmacie": 900, "vente": -4}') x;
    ASSERT (SELECT count(*) FROM sync_avancer_sequences('{"t149_essai": 500, "table_inconnue": 900, "sync_lot": 900, "pharmacie": 900}')) = 1,
        'table inconnue, jamais envoyée ou sans séquence : ignorée';
    ASSERT r.nom_table = 't149_essai' AND r.avant = 300 AND r.apres = 500 AND r.erreur IS NULL,
        format('avance 300 -> 500 attendue (%s, %s -> %s)', r.nom_table, r.avant, r.apres);
    INSERT INTO public.t149_essai (libelle) VALUES ('d') RETURNING id INTO v;
    ASSERT v = 501, format('n° suivant après l''avance : 501 attendu (%s)', v);
    SELECT * INTO r FROM sync_avancer_sequences('{"t149_essai": 200}') x;
    ASSERT r.avant = 501 AND r.apres = 501, 'jamais en arrière';
    SELECT * INTO r FROM sync_avancer_sequences('{"t149_essai": "600"}') x;
    ASSERT r IS NULL OR r.nom_table IS NULL, 'valeur non numérique ignorée';
    ASSERT (SELECT retour_arriere_sequences_le FROM sync_parametre WHERE id = 1) IS NOT NULL, 'avance notée pour la détection';
    BEGIN PERFORM sync_avancer_sequences('[1, 2]'); RAISE EXCEPTION 'tableau accepté';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE '%maximums attendus%' THEN RAISE; END IF;
    END;
END $$;

-- 5. Alerte notée pour la détection en cours
DO $$
BEGIN
    PERFORM sync_noter_alerte_retour_arriere(now());
    ASSERT (SELECT retour_arriere_alerte_le FROM sync_parametre WHERE id = 1) = now(), 'alerte notée';
    -- Horloge du poste en retard sur la base : l'alerte n'est jamais notée avant la détection (sinon une alerte par minute)
    UPDATE sync_parametre SET retour_arriere_le = now() + interval '10 minutes' WHERE id = 1;
    PERFORM sync_noter_alerte_retour_arriere(now());
    ASSERT (SELECT retour_arriere_alerte_le = retour_arriere_le FROM sync_parametre WHERE id = 1), 'alerte notée à la date de la détection au plus tôt';
END $$;

-- 6. Nouvelle époque : époque attendue contrôlée, au-delà de la plus grande époque du central, compteurs à 0, détection effacée,
--    ancienne époque gardée, chargement demandé
DO $$
DECLARE r record; c sync_chargement;
BEGIN
    BEGIN PERFORM sync_nouvelle_epoque(1, NULL, 2); RAISE EXCEPTION 'époque attendue périmée acceptée';
    EXCEPTION WHEN check_violation THEN NULL; END;
    BEGIN PERFORM sync_nouvelle_epoque(1, -1, 3); RAISE EXCEPTION 'époque du central négative acceptée';
    EXCEPTION WHEN check_violation THEN NULL; END;
    ASSERT (SELECT epoque FROM sync_parametre WHERE id = 1) = 3, 'époque inchangée après les refus';
    SELECT * INTO r FROM sync_nouvelle_epoque(1, 6, 3);
    ASSERT r.epoque = 7 AND r.ancienne_epoque = 3 AND r.chargement IS NOT NULL, format('époque 3 -> 7 (central à 6) et chargement demandé (%s)', r.epoque);
    ASSERT (SELECT epoque = 7 AND dernier_lot = 0 AND dernier_accuse = 0 AND purge_epoque IS NULL AND purge_dernier_lot = 0
                   AND retour_arriere_le IS NULL AND retour_arriere_central IS NULL AND retour_arriere_sequences_le IS NULL
                   AND derniere_erreur IS NULL FROM sync_parametre WHERE id = 1), 'réglages de la nouvelle époque';
    ASSERT (SELECT count(*) FROM sync_lot WHERE epoque = 3) = 5, 'lots de l''ancienne époque gardés';
    ASSERT (SELECT count(*) FROM sync_lot WHERE epoque = 3 AND accuse_le IS NULL) = 1, 'lot non accusé gardé (signalé « autres époques »)';
    ASSERT EXISTS (SELECT 1 FROM sync_sortie WHERE epoque = 3 AND id_local = '999999' AND lot_numero IS NULL), 'ligne non envoyée gardée';
    ASSERT (SELECT etat FROM sync_chargement WHERE numero = current_setting('t149.chargement')::int) = 'remplace', 'ancien chargement remplacé';
    SELECT * INTO c FROM sync_chargement WHERE numero = r.chargement;
    ASSERT c.motif = 'demande' AND c.etat = 'demande' AND c.demande_par = 1, 'chargement complet demandé par le titulaire';
    BEGIN PERFORM sync_nouvelle_epoque(1, NULL, 7); RAISE EXCEPTION 'deuxième nouvelle époque acceptée sans nouvelle détection';
    EXCEPTION WHEN check_violation THEN NULL; END;
    -- Après la nouvelle époque, le central (époque 7) n'a rien : pas de détection
    ASSERT NOT sync_noter_retour_arriere(0), 'nouvelle époque : rien au central, pas de détection';
    -- Central en retard sur l'époque locale, puis central sans ce renseignement : époque locale + 1
    PERFORM sync_noter_retour_arriere_refus(1);
    SELECT * INTO r FROM sync_nouvelle_epoque(1, 2, 7);
    ASSERT r.epoque = 8, format('central à 2, local à 7 : époque 8 attendue (%s)', r.epoque);
    PERFORM sync_noter_retour_arriere_refus(1);
    SELECT * INTO r FROM sync_nouvelle_epoque(1, NULL, 8);
    ASSERT r.epoque = 9, format('central sans époque : 9 attendue (%s)', r.epoque);
    ASSERT to_regprocedure('sync_nouvelle_epoque(bigint)') IS NULL, 'ancienne signature retirée';
END $$;

-- Séquences consommées par le test remises à leur valeur de départ (setval n'est pas annulé par le ROLLBACK)
DO $$ BEGIN PERFORM setval(nom, last_value, is_called) FROM t149_seq; END $$;

ROLLBACK;
