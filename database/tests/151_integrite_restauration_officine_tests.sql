-- =====================================================================
-- Tests du script 151 (tickets après restauration, déclaration d'époque, prise d'acte), en transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/151_integrite_restauration_officine_tests.sql
-- Les séquences consommées par le test sont remises à leur valeur de départ avant le ROLLBACK (setval n'est pas annulé).
-- =====================================================================
BEGIN;

CREATE TEMP TABLE t151_seq ON COMMIT DROP AS
SELECT x.nom, s.last_value, s.is_called
FROM (VALUES (pg_get_serial_sequence('compteur_ticket_saut', 'id')), (pg_get_serial_sequence('integrite_prise_acte', 'id')),
             (pg_get_serial_sequence('sync_chargement', 'numero')), (pg_get_serial_sequence('sync_sortie', 'id'))) x(nom)
CROSS JOIN LATERAL (SELECT ps.last_value, ps.start_value FROM pg_sequences ps WHERE ps.schemaname || '.' || ps.sequencename = x.nom) s0
CROSS JOIN LATERAL (SELECT coalesce(s0.last_value, s0.start_value) AS last_value, s0.last_value IS NOT NULL AS is_called) s;

-- 1. Tickets : saut au-delà du plus grand n° du central, plage notée et reconnue par la continuité ; jamais en arrière
DO $$
DECLARE v_avant bigint; v_manquants_avant bigint; n bigint;
BEGIN
    SELECT dernier_numero INTO v_avant FROM compteur_ticket WHERE id = 1;
    SELECT count(*) INTO v_manquants_avant FROM journal_vente_continuite() WHERE anomalie = 'numero_manquant';
    ASSERT sync_avancer_compteur_ticket(v_avant) = 0, 'n° du central égal : rien à faire';
    ASSERT sync_avancer_compteur_ticket(v_avant - 1) = 0, 'n° du central plus petit : jamais en arrière';
    n := sync_avancer_compteur_ticket(v_avant + 5, 4);
    ASSERT n = 5, 'cinq n° sautés : ' || n;
    ASSERT (SELECT dernier_numero FROM compteur_ticket WHERE id = 1) = v_avant + 5, 'compteur avancé';
    ASSERT EXISTS (SELECT 1 FROM compteur_ticket_saut WHERE du = v_avant + 1 AND au = v_avant + 5 AND motif = 'restauration' AND epoque = 4), 'plage notée';
    ASSERT (SELECT count(*) FROM journal_vente_continuite() WHERE anomalie = 'numero_manquant') = v_manquants_avant,
        'la plage sautée n''est pas un n° manquant';
    ASSERT coalesce(current_setting('pharma.saut_ticket', true), '') = '', 'annonce du saut retirée après usage';
    -- Saut direct sans plage notée : refusé ; avance normale de +1 : permise
    BEGIN UPDATE compteur_ticket SET dernier_numero = dernier_numero + 10 WHERE id = 1; RAISE EXCEPTION 'saut sans plage accepté';
    EXCEPTION WHEN raise_exception THEN IF SQLERRM NOT LIKE '%une unité à la fois%' THEN RAISE; END IF; END;
    BEGIN PERFORM set_config('pharma.saut_ticket', (v_avant + 50)::text, true);
          UPDATE compteur_ticket SET dernier_numero = v_avant + 50 WHERE id = 1; RAISE EXCEPTION 'saut annoncé sans plage accepté';
    EXCEPTION WHEN raise_exception THEN IF SQLERRM NOT LIKE '%une unité à la fois%' THEN RAISE; END IF; END;
    PERFORM set_config('pharma.saut_ticket', '', true);
    UPDATE compteur_ticket SET dernier_numero = dernier_numero + 1 WHERE id = 1;
    -- Plage en ajout seul ; saut démesuré refusé
    BEGIN UPDATE compteur_ticket_saut SET au = au + 1; RAISE EXCEPTION 'plage modifiée';
    EXCEPTION WHEN raise_exception THEN IF SQLERRM NOT LIKE '%ajout seul%' THEN RAISE; END IF; END;
    BEGIN PERFORM sync_avancer_compteur_ticket(v_avant + 20000000); RAISE EXCEPTION 'saut démesuré accepté';
    EXCEPTION WHEN check_violation THEN NULL; END;
END $$;

-- 2. Déclaration : notée par la nouvelle époque (ancienne époque, lots, détection, titulaire, fins de chaîne), une seule fois
DO $$
DECLARE r record; d sync_declaration_epoque; v_ep int;
BEGIN
    UPDATE sync_chargement SET etat = 'remplace' WHERE etat NOT IN ('termine', 'remplace');
    SELECT epoque INTO v_ep FROM sync_parametre WHERE id = 1;
    UPDATE sync_parametre SET dernier_lot = 7, dernier_accuse = 7, retour_arriere_le = NULL, retour_arriere_central = NULL,
                              retour_arriere_sequences_le = NULL, retour_arriere_alerte_le = NULL WHERE id = 1;
    PERFORM sync_noter_retour_arriere_refus(9);
    SELECT * INTO r FROM sync_nouvelle_epoque(1, NULL, v_ep);
    SELECT * INTO d FROM sync_declaration_epoque WHERE nouvelle_epoque = r.epoque;
    ASSERT FOUND, 'déclaration notée';
    ASSERT d.ancienne_epoque = v_ep AND d.dernier_lot_local = 7 AND d.dernier_lot_central = 9 AND d.titulaire_id = 1
        AND d.detecte_le IS NOT NULL AND d.envoyee_le IS NULL, 'contenu de la déclaration : ' || row_to_json(d)::text;
    ASSERT jsonb_typeof(d.fins_chaine) = 'array'
        AND EXISTS (SELECT 1 FROM jsonb_array_elements(d.fins_chaine) f WHERE f ->> 'journal' = 'journal_vente' AND (f ->> 'rang')::bigint >= 0),
        'fins de chaîne : ' || d.fins_chaine::text;
    -- Essai S6.5.5 : seulement les journaux envoyés au central (integrite_essai, jamais envoyée, faisait refuser la déclaration)
    ASSERT NOT EXISTS (SELECT 1 FROM jsonb_array_elements(d.fins_chaine) f
                       WHERE NOT EXISTS (SELECT 1 FROM sync_table st WHERE st.nom_table = f ->> 'journal' AND st.sens IN ('montante', 'bidirectionnelle'))),
        'fin de chaîne d''un journal non envoyé : ' || d.fins_chaine::text;
    -- Fin de chaîne du journal des ventes : son dernier rang
    ASSERT (SELECT (f ->> 'rang')::bigint FROM jsonb_array_elements(d.fins_chaine) f WHERE f ->> 'journal' = 'journal_vente' LIMIT 1)
         = coalesce((SELECT max(rang) FROM journal_vente), 0), 'rang de fin = dernier rang du journal';
    -- Envoi noté une fois
    PERFORM sync_noter_declaration(r.epoque, true, 'envoyée');
    PERFORM sync_noter_declaration(r.epoque, false, 'autre');
    SELECT * INTO d FROM sync_declaration_epoque WHERE nouvelle_epoque = r.epoque;
    ASSERT d.envoyee_le IS NOT NULL AND d.avant_premier_lot AND d.resultat = 'envoyée', 'résultat noté une seule fois';
END $$;

-- 3. Prise d'acte : une par époque, cause obligatoire, ajout seul, classée montante (cause filtrée)
DO $$
DECLARE v_util bigint := (SELECT min(id) FROM utilisateur);
BEGIN
    INSERT INTO integrite_prise_acte (nouvelle_epoque, cause, utilisateur_id) VALUES (5, 'Panne du disque, sauvegarde de la veille', v_util);
    BEGIN INSERT INTO integrite_prise_acte (nouvelle_epoque, cause, utilisateur_id) VALUES (5, 'deuxième', v_util); RAISE EXCEPTION 'deux prises d''acte';
    EXCEPTION WHEN unique_violation THEN NULL; END;
    BEGIN INSERT INTO integrite_prise_acte (nouvelle_epoque, cause, utilisateur_id) VALUES (6, ' x ', v_util); RAISE EXCEPTION 'cause vide acceptée';
    EXCEPTION WHEN check_violation THEN NULL; END;
    BEGIN UPDATE integrite_prise_acte SET cause = 'autre cause'; RAISE EXCEPTION 'prise d''acte modifiée';
    EXCEPTION WHEN raise_exception THEN IF SQLERRM NOT LIKE '%ajout seul%' THEN RAISE; END IF; END;
    ASSERT (SELECT officine_id FROM integrite_prise_acte WHERE nouvelle_epoque = 5) = officine_courante(), 'code de l''officine posé';
    ASSERT (SELECT sens FROM sync_table WHERE nom_table = 'integrite_prise_acte') = 'montante'
       AND (SELECT colonnes_texte FROM sync_table WHERE nom_table = 'integrite_prise_acte') = '{cause}', 'montante, cause filtrée';
    ASSERT (SELECT sens FROM sync_table WHERE nom_table = 'compteur_ticket_saut') = 'jamais'
       AND (SELECT sens FROM sync_table WHERE nom_table = 'sync_declaration_epoque') = 'jamais', 'tables locales jamais envoyées';
END $$;

-- Séquences consommées par le test remises à leur valeur de départ
DO $$ BEGIN PERFORM setval(nom, last_value, is_called) FROM t151_seq WHERE nom IS NOT NULL; END $$;

ROLLBACK;
\echo '151 : tous réussis'
