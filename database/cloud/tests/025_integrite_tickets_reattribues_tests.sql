-- =====================================================================
-- Tests du script 025 (n° de tickets réattribués après une restauration), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/025_integrite_tickets_reattribues_tests.sql
-- Même construction que le cas A des tests du 023 (empreintes calculées avec la forme canonique du central), lignes avec n° de ticket :
--   restauration au rang 3 ; deux ventes faites avant la détection reprennent les n° T-000003 et T-000004 (l'une en collision d'id,
--   l'autre sur un nouvel id), une vente après la détection prend un n° nouveau (compteur avancé) → deux n° signalés, rattachés et classés.
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('eeeeeeee-0000-0000-0000-000000000025', 'Essai 025');

CREATE FUNCTION pg_temp.jv(p_id bigint, p_rang bigint, p_evt text, p_ticket text, p_date timestamptz, p_montant numeric, p_prec bytea) RETURNS jsonb
LANGUAGE plpgsql AS $$
DECLARE v jsonb; v_can text;
BEGIN
    v := jsonb_build_object('id', p_id, 'evenement', p_evt, 'numero_ticket', p_ticket, 'survenu_le', p_date, 'enregistre_le', p_date,
                            'montant_ttc', p_montant, 'cle_chaine', 'officine', 'rang', p_rang, 'hash_precedent', p_prec);
    EXECUTE format('SELECT %s FROM jsonb_populate_record(NULL::public.journal_vente, $1) r', cloud_integrite_expression('journal_vente')) INTO v_can USING v;
    RETURN v || jsonb_build_object('hash', sha256(convert_to(v_can, 'UTF8') || p_prec));
END $$;
CREATE FUNCTION pg_temp.h(p jsonb) RETURNS bytea LANGUAGE sql AS $$ SELECT decode(substr(p ->> 'hash', 3), 'hex') $$;
CREATE FUNCTION pg_temp.lot(p_epoque int, p_numero bigint, p_lignes jsonb) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    PERFORM cloud_appliquer_lot(p_epoque, p_numero, md5(p_lignes::text) || md5(p_numero::text), 'sync-v1',
        convert_to(jsonb_build_object('lignes', p_lignes)::text, 'UTF8'));
END $$;
CREATE FUNCTION pg_temp.l(p_n int, p_op text, p jsonb) RETURNS jsonb LANGUAGE sql AS $$
    SELECT jsonb_build_object('n', p_n, 'table', 'journal_vente', 'id', (p ->> 'id'), 'op', p_op, 'donnees', p)
$$;

DO $$
DECLARE z bytea := decode(repeat('00', 32), 'hex'); r1 jsonb; r2 jsonb; r3 jsonb; r4 jsonb; r5 jsonb; n4 jsonb; n5 jsonb; n6 jsonb; v jsonb;
        b cloud_integrite_bifurcation; v_rest jsonb;
BEGIN
    PERFORM set_config('app.officine_id', 'eeeeeeee-0000-0000-0000-000000000025', true);
    -- Époque 1 : ouverture puis quatre ventes T-000001 à T-000004 (rangs 2 à 5), reçues il y a deux heures
    r1 := pg_temp.jv(1, 1, 'ouverture', NULL, now() - interval '3 hours', 0, z);
    r2 := pg_temp.jv(2, 2, 'vente', 'T-000001', now() - interval '170 minutes', 10, pg_temp.h(r1));
    r3 := pg_temp.jv(3, 3, 'vente', 'T-000002', now() - interval '160 minutes', 20, pg_temp.h(r2));
    r4 := pg_temp.jv(4, 4, 'vente', 'T-000003', now() - interval '150 minutes', 30, pg_temp.h(r3));
    r5 := pg_temp.jv(5, 5, 'vente', 'T-000004', now() - interval '140 minutes', 40, pg_temp.h(r4));
    PERFORM pg_temp.lot(1, 1, jsonb_build_array(pg_temp.l(1, 'creation', r1), pg_temp.l(2, 'creation', r2), pg_temp.l(3, 'creation', r3),
                                                pg_temp.l(4, 'creation', r4), pg_temp.l(5, 'creation', r5)));
    UPDATE journal_vente SET sync_recu_le = now() - interval '2 hours' WHERE officine_id = 'eeeeeeee-0000-0000-0000-000000000025';
    v := cloud_verifier_integrite(true);
    ASSERT v ->> 'statut' = 'ok', 'époque 1 : chaîne intacte : ' || (v ->> 'statut');
    ASSERT cloud_integrite_tickets_reattribues() = 0, 'sans restauration : rien';

    -- Restauration au rang 3 ; avant la détection, deux ventes reprennent T-000003 (id 4 en collision) et T-000004 (id 6) ;
    -- après la détection, compteur avancé : T-000010
    PERFORM cloud_declarer_changement_epoque(1, 2, 0, 1, now() - interval '30 minutes', now() - interval '20 minutes', 7,
        '[{"journal":"journal_vente","cle_chaine":"officine","rang":4}]');
    n4 := pg_temp.jv(4, 4, 'vente', 'T-000003', now() - interval '25 minutes', 99, pg_temp.h(r3));
    n5 := pg_temp.jv(6, 5, 'vente', 'T-000004', now() - interval '24 minutes', 11, pg_temp.h(n4));
    n6 := pg_temp.jv(7, 6, 'vente', 'T-000010', now() - interval '4 minutes', 12, pg_temp.h(n5));
    PERFORM cloud_debut_chargement(1, 2, 1, 0, 'demande', ARRAY['journal_vente']);
    PERFORM pg_temp.lot(2, 1, jsonb_build_array(pg_temp.l(1, 'instantane', r1), pg_temp.l(2, 'instantane', r2), pg_temp.l(3, 'instantane', r3),
                                                pg_temp.l(4, 'instantane', n4), pg_temp.l(5, 'instantane', n5), pg_temp.l(6, 'instantane', n6)));
    PERFORM cloud_fin_chargement(1, '{"journal_vente":6}');
    v := cloud_verifier_integrite(true);
    SELECT * INTO b FROM cloud_integrite_bifurcation;
    ASSERT FOUND AND b.classe = 'restauration_constatee', 'restauration constatée';

    -- Deux n° signalés (un par vente de la nouvelle suite), rattachés à la restauration, classés comme elle
    ASSERT (SELECT count(*) FROM cloud_anomalie_integrite WHERE nature = 'ticket_reattribue_restauration') = 2,
        'deux n° réattribués : ' || (SELECT count(*) FROM cloud_anomalie_integrite WHERE nature = 'ticket_reattribue_restauration');
    ASSERT (SELECT array_agg(attendu ORDER BY attendu) FROM cloud_anomalie_integrite WHERE nature = 'ticket_reattribue_restauration') = '{T-000003,T-000004}',
        'n° signalés';
    ASSERT (SELECT array_agg(cle_ligne || '@' || rang ORDER BY rang) FROM cloud_anomalie_integrite WHERE nature = 'ticket_reattribue_restauration') = '{4@4,6@5}',
        'lignes de la nouvelle suite visées';
    ASSERT (SELECT detail FROM cloud_anomalie_integrite WHERE nature = 'ticket_reattribue_restauration' AND attendu = 'T-000004') LIKE 'n° T-000004 repris%rang 5, époque 1%',
        'détail : ' || (SELECT detail FROM cloud_anomalie_integrite WHERE nature = 'ticket_reattribue_restauration' AND attendu = 'T-000004');
    ASSERT (SELECT count(*) FROM cloud_anomalie_classement c JOIN cloud_anomalie_integrite a ON a.id = c.anomalie_id
             WHERE a.nature = 'ticket_reattribue_restauration' AND c.bifurcation_id = b.id AND c.classe = 'remplacee_apres_restauration') = 2,
        'rattachées à la restauration';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_anomalie_integrite WHERE nature = 'ticket_reattribue_restauration' AND attendu = 'T-000010'),
        'n° pris après l''avance du compteur : non signalé';

    -- Résumé : restauration avec ses n° réattribués, anomalies classées, statut « restauration » (pas « anomalie »)
    v_rest := v -> 'restaurations' -> 0;
    ASSERT v_rest -> 'tickets_reattribues' = '["T-000003", "T-000004"]'::jsonb, 'résumé : ' || (v_rest -> 'tickets_reattribues')::text;
    ASSERT (v ->> 'anomalies_classees')::int = (v ->> 'anomalies_total')::int, 'toutes classées : ' || (v ->> 'anomalies_classees') || '/' || (v ->> 'anomalies_total');
    ASSERT v ->> 'statut' = 'restauration', 'statut : ' || (v ->> 'statut');

    -- Repassages : rien n'est doublé
    v := cloud_verifier_integrite(false);
    v := cloud_verifier_integrite(true);
    ASSERT (SELECT count(*) FROM cloud_anomalie_integrite WHERE nature = 'ticket_reattribue_restauration') = 2, 'repassage : pas de doublon';
    ASSERT (SELECT count(*) FROM cloud_anomalie_classement c JOIN cloud_anomalie_integrite a ON a.id = c.anomalie_id
             WHERE a.nature = 'ticket_reattribue_restauration') = 2, 'repassage : rattachement unique';
END $$;

ROLLBACK;
\echo '025 : tous réussis'
