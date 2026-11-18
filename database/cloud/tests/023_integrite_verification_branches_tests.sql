-- =====================================================================
-- Tests du script 023 (lot S6.5.2 : vérification par branches, constat de restauration), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/023_integrite_verification_branches_tests.sql
-- Empreintes calculées ici avec la forme canonique du central (comme les tests du 012) ; tout est fait par le propriétaire.
--   A : restauration au rang 3, une vente en collision (n° 4 repris), deux nouvelles ventes → « remplacée après restauration »
--   B : nouvelle suite datée AVANT la dernière réception de l'ancienne (réécriture du passé) → refusée, chainage_rompu
--   C : restauration qui fait disparaître une ligne scellée → « restauration à justifier »
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000023', 'Essai 023 A'),
    ('bbbbbbbb-0000-0000-0000-000000000023', 'Essai 023 B'), ('cccccccc-0000-0000-0000-000000000023', 'Essai 023 C');

-- Ligne du journal des ventes avec son empreinte (sha256 de la forme canonique || empreinte précédente)
CREATE FUNCTION pg_temp.jv(p_id bigint, p_rang bigint, p_evt text, p_date timestamptz, p_montant numeric, p_prec bytea) RETURNS jsonb
LANGUAGE plpgsql AS $$
DECLARE v jsonb; v_can text;
BEGIN
    v := jsonb_build_object('id', p_id, 'evenement', p_evt, 'survenu_le', p_date, 'enregistre_le', p_date, 'montant_ttc', p_montant,
                            'cle_chaine', 'officine', 'rang', p_rang, 'hash_precedent', p_prec);
    EXECUTE format('SELECT %s FROM jsonb_populate_record(NULL::public.journal_vente, $1) r', cloud_integrite_expression('journal_vente')) INTO v_can USING v;
    RETURN v || jsonb_build_object('hash', sha256(convert_to(v_can, 'UTF8') || p_prec));
END $$;
-- Empreinte d'une ligne construite par jv
CREATE FUNCTION pg_temp.h(p jsonb) RETURNS bytea LANGUAGE sql AS $$ SELECT decode(substr(p ->> 'hash', 3), 'hex') $$;
-- Lot : liste de (n, op, donnees)
CREATE FUNCTION pg_temp.lot(p_epoque int, p_numero bigint, p_lignes jsonb) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    PERFORM cloud_appliquer_lot(p_epoque, p_numero, md5(p_lignes::text) || md5(p_numero::text), 'sync-v1',
        convert_to(jsonb_build_object('lignes', p_lignes)::text, 'UTF8'));
END $$;
CREATE FUNCTION pg_temp.l(p_n int, p_op text, p jsonb) RETURNS jsonb LANGUAGE sql AS $$
    SELECT jsonb_build_object('n', p_n, 'table', 'journal_vente', 'id', (p ->> 'id'), 'op', p_op, 'donnees', p)
$$;

-- ===================== Officine A =====================
DO $$
DECLARE z bytea := decode(repeat('00', 32), 'hex'); r1 jsonb; r2 jsonb; r3 jsonb; r4 jsonb; r5 jsonb; n4 jsonb; n5 jsonb; n6 jsonb; v jsonb;
        b cloud_integrite_bifurcation; e cloud_integrite_etat;
BEGIN
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000023', true);
    -- Époque 1 : rangs 1 à 5 (n° 1 à 5), reçus il y a deux heures
    r1 := pg_temp.jv(1, 1, 'ouverture', now() - interval '3 hours', 0, z);
    r2 := pg_temp.jv(2, 2, 'vente', now() - interval '170 minutes', 10, pg_temp.h(r1));
    r3 := pg_temp.jv(3, 3, 'vente', now() - interval '160 minutes', 20, pg_temp.h(r2));
    r4 := pg_temp.jv(4, 4, 'vente', now() - interval '150 minutes', 30, pg_temp.h(r3));
    r5 := pg_temp.jv(5, 5, 'vente', now() - interval '140 minutes', 40, pg_temp.h(r4));
    PERFORM pg_temp.lot(1, 1, jsonb_build_array(pg_temp.l(1, 'creation', r1), pg_temp.l(2, 'creation', r2), pg_temp.l(3, 'creation', r3),
                                                pg_temp.l(4, 'creation', r4), pg_temp.l(5, 'creation', r5)));
    UPDATE journal_vente SET sync_recu_le = now() - interval '2 hours' WHERE officine_id = 'aaaaaaaa-0000-0000-0000-000000000023';
    v := cloud_verifier_integrite(true);
    ASSERT v ->> 'statut' = 'ok', 'A époque 1 : chaîne intacte : ' || (v ->> 'statut');

    -- Restauration au rang 3 ; une vente faite avant la détection reprend le n° 4 (collision) ; déclaration de l'époque 2
    PERFORM cloud_declarer_changement_epoque(1, 2, 0, 1, now() - interval '30 minutes', now() - interval '20 minutes', 7,
        '[{"journal":"journal_vente","cle_chaine":"officine","rang":4}]');
    n4 := pg_temp.jv(4, 4, 'vente', now() - interval '25 minutes', 99, pg_temp.h(r3));
    n5 := pg_temp.jv(6, 5, 'vente', now() - interval '5 minutes', 11, pg_temp.h(n4));
    n6 := pg_temp.jv(7, 6, 'vente', now() - interval '4 minutes', 12, pg_temp.h(n5));
    PERFORM cloud_debut_chargement(1, 2, 1, 0, 'demande', ARRAY['journal_vente']);
    PERFORM pg_temp.lot(2, 1, jsonb_build_array(pg_temp.l(1, 'instantane', r1), pg_temp.l(2, 'instantane', r2), pg_temp.l(3, 'instantane', r3),
                                                pg_temp.l(4, 'instantane', n4), pg_temp.l(5, 'instantane', n5), pg_temp.l(6, 'instantane', n6)));
    ASSERT (SELECT montant_ttc FROM journal_vente WHERE id = 4) = 30, 'A : la ligne d''origine n° 4 reste en place';
    ASSERT (SELECT count(*) FROM cloud_ligne_divergente) = 1, 'A : version en collision gardée à part';

    -- Chargement pas terminé : rien n'est classé (attente)
    v := cloud_verifier_integrite(true);
    SELECT * INTO e FROM cloud_integrite_etat WHERE journal = 'journal_vente' AND cle_chaine = 'officine';
    ASSERT e.statut = 'attente', 'A avant la fin du chargement : attente : ' || e.statut;
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_integrite_bifurcation), 'A avant la fin du chargement : aucune bifurcation';

    -- Fin du chargement : la ligne n° 5 de l'ancienne suite est absente
    PERFORM cloud_fin_chargement(1, '{"journal_vente":6}');
    v := cloud_verifier_integrite(true);
    SELECT * INTO b FROM cloud_integrite_bifurcation;
    ASSERT FOUND, 'A : bifurcation constatée';
    ASSERT b.classe = 'restauration_constatee', 'A : classe : ' || b.classe;
    ASSERT b.rang_commun = 3 AND b.fin_ancienne_rang = 5 AND b.lignes_ancienne = 2, 'A : rangs 4 et 5 remplacés : ' || b.rang_commun || '/' || b.fin_ancienne_rang || '/' || b.lignes_ancienne;
    ASSERT b.montant_ttc = 70, 'A : montant de l''ancienne suite (30 + 40) : ' || b.montant_ttc;
    ASSERT b.ancienne_epoque = 1 AND b.nouvelle_epoque = 2, 'A : époques';
    SELECT * INTO e FROM cloud_integrite_etat WHERE journal = 'journal_vente' AND cle_chaine = 'officine';
    ASSERT e.statut = 'ok' AND e.dernier_rang = 6 AND e.epoque_branche = 2, 'A : nouvelle suite vérifiée jusqu''au rang 6 : ' || e.statut || '/' || e.dernier_rang;
    ASSERT EXISTS (SELECT 1 FROM cloud_anomalie_integrite WHERE nature = 'remplacee_apres_restauration' AND rang = 4), 'A : une anomalie de restauration';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_anomalie_integrite WHERE nature IN ('chainage_rompu', 'ligne_manquante', 'modification_refusee')),
        'A : aucune fausse anomalie';
    ASSERT v ->> 'statut' = 'restauration', 'A : statut du résumé « restauration » (pas de prise d''acte) : ' || (v ->> 'statut');
    ASSERT jsonb_array_length(v -> 'restaurations') = 1 AND (v -> 'restaurations' -> 0 ->> 'lignes')::int = 2, 'A : restauration listée';
    ASSERT (v ->> 'anomalies_classees')::int = 1, 'A : une anomalie classée : ' || (v ->> 'anomalies_classees');
    -- Repassage : rien n'est doublé
    v := cloud_verifier_integrite(false);
    ASSERT (SELECT count(*) FROM cloud_integrite_bifurcation) = 1 AND (SELECT count(*) FROM cloud_anomalie_integrite) = 1, 'A : repassage sans doublon';
    -- Une nouvelle vente de la suite vivante est vérifiée au passage suivant
    PERFORM pg_temp.lot(2, 2, jsonb_build_array(pg_temp.l(7, 'creation', pg_temp.jv(8, 7, 'vente', now(), 13, pg_temp.h(n6)))));
    v := cloud_verifier_integrite(false);
    ASSERT (SELECT dernier_rang FROM cloud_integrite_etat WHERE journal = 'journal_vente') = 7, 'A : suite vivante prolongée';
END $$;

-- ===================== Officine B : réécriture du passé =====================
DO $$
DECLARE z bytea := decode(repeat('00', 32), 'hex'); r1 jsonb; r2 jsonb; r3 jsonb; n3 jsonb; v jsonb; e cloud_integrite_etat;
BEGIN
    PERFORM set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000023', true);
    r1 := pg_temp.jv(1, 1, 'ouverture', now() - interval '3 hours', 0, z);
    r2 := pg_temp.jv(2, 2, 'vente', now() - interval '170 minutes', 10, pg_temp.h(r1));
    r3 := pg_temp.jv(3, 3, 'vente', now() - interval '160 minutes', 20, pg_temp.h(r2));
    PERFORM pg_temp.lot(1, 1, jsonb_build_array(pg_temp.l(1, 'creation', r1), pg_temp.l(2, 'creation', r2), pg_temp.l(3, 'creation', r3)));
    UPDATE journal_vente SET sync_recu_le = now() - interval '2 hours' WHERE officine_id = 'bbbbbbbb-0000-0000-0000-000000000023';
    PERFORM cloud_declarer_changement_epoque(1, 2, 0, 1, now() - interval '30 minutes', now() - interval '20 minutes', 7,
        '[{"journal":"journal_vente","cle_chaine":"officine","rang":2}]');
    -- Nouvelle vente au rang 3, n° nouveau, mais datée de la veille (avant la dernière réception de l'ancienne suite)
    n3 := pg_temp.jv(10, 3, 'vente', now() - interval '1 day', 5, pg_temp.h(r2));
    PERFORM cloud_debut_chargement(1, 2, 1, 0, 'demande', ARRAY['journal_vente']);
    PERFORM pg_temp.lot(2, 1, jsonb_build_array(pg_temp.l(1, 'instantane', r1), pg_temp.l(2, 'instantane', r2), pg_temp.l(3, 'instantane', n3)));
    PERFORM cloud_fin_chargement(1, '{"journal_vente":3}');
    v := cloud_verifier_integrite(true);
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_integrite_bifurcation), 'B : aucune restauration reconnue';
    SELECT * INTO e FROM cloud_integrite_etat WHERE journal = 'journal_vente' AND cle_chaine = 'officine';
    ASSERT e.statut = 'rupture', 'B : chaîne en rupture : ' || e.statut;
    ASSERT (SELECT detail FROM cloud_anomalie_integrite WHERE nature = 'chainage_rompu') LIKE '%condition 6%', 'B : condition 6 citée : '
        || coalesce((SELECT detail FROM cloud_anomalie_integrite WHERE nature = 'chainage_rompu'), 'aucune anomalie');
    ASSERT v ->> 'statut' = 'anomalie', 'B : statut anomalie';
END $$;

-- ===================== Officine C : ligne scellée perdue =====================
DO $$
DECLARE z bytea := decode(repeat('00', 32), 'hex'); r1 jsonb; r2 jsonb; r3 jsonb; n3 jsonb; v jsonb; b cloud_integrite_bifurcation;
BEGIN
    PERFORM set_config('app.officine_id', 'cccccccc-0000-0000-0000-000000000023', true);
    r1 := pg_temp.jv(1, 1, 'ouverture', now() - interval '3 hours', 0, z);
    r2 := pg_temp.jv(2, 2, 'vente', now() - interval '170 minutes', 10, pg_temp.h(r1));
    r3 := pg_temp.jv(3, 3, 'vente', now() - interval '160 minutes', 20, pg_temp.h(r2));
    PERFORM pg_temp.lot(1, 1, jsonb_build_array(pg_temp.l(1, 'creation', r1), pg_temp.l(2, 'creation', r2), pg_temp.l(3, 'creation', r3),
        jsonb_build_object('n', 4, 'table', 'sceau_integrite', 'id', '1', 'op', 'creation', 'donnees', jsonb_build_object('id', 1, 'journal', 'journal_vente',
            'cle_chaine', 'officine', 'dernier_rang', 3, 'dernier_id', 3, 'dernier_hash', pg_temp.h(r3), 'algorithme', 'sha256'))));
    UPDATE journal_vente SET sync_recu_le = now() - interval '2 hours' WHERE officine_id = 'cccccccc-0000-0000-0000-000000000023';
    UPDATE sceau_integrite SET sync_recu_le = now() - interval '2 hours' WHERE officine_id = 'cccccccc-0000-0000-0000-000000000023';
    PERFORM cloud_declarer_changement_epoque(1, 2, 0, 1, now() - interval '30 minutes', now() - interval '20 minutes', 7,
        '[{"journal":"journal_vente","cle_chaine":"officine","rang":2}]');
    n3 := pg_temp.jv(10, 3, 'vente', now() - interval '5 minutes', 5, pg_temp.h(r2));
    PERFORM cloud_debut_chargement(1, 2, 1, 0, 'demande', ARRAY['journal_vente']);
    PERFORM pg_temp.lot(2, 1, jsonb_build_array(pg_temp.l(1, 'instantane', r1), pg_temp.l(2, 'instantane', r2), pg_temp.l(3, 'instantane', n3)));
    PERFORM cloud_fin_chargement(1, '{"journal_vente":3}');
    v := cloud_verifier_integrite(true);
    SELECT * INTO b FROM cloud_integrite_bifurcation;
    ASSERT FOUND AND b.classe = 'restauration_a_justifier' AND b.sceaux_perdus = 1, 'C : à justifier, un sceau perdu : ' || coalesce(b.classe, 'aucune');
    ASSERT EXISTS (SELECT 1 FROM cloud_anomalie_integrite WHERE nature = 'sceau_perdu_restauration'), 'C : sceau perdu noté';
    ASSERT (SELECT statut FROM cloud_integrite_sceau WHERE sceau_id = 1) = 'ok', 'C : le sceau reste ancré dans l''ancienne suite (n°, rang, empreinte)';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_anomalie_integrite WHERE nature = 'sceau_incoherent'), 'C : aucun faux sceau incohérent';
END $$;

-- Cloisonnement : A ne voit que sa restauration
DO $$ BEGIN
    PERFORM set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000023', true);
    ASSERT (SELECT count(*) FROM cloud_restaurations_integrite()) = 1, 'A : une restauration visible';
END $$;

ROLLBACK;
\echo '023 : tous réussis'
