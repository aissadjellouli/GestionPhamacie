-- =====================================================================
-- Tests du script 164 (saut des compteurs à la promotion) et de son retour arrière, en transaction ANNULÉE,
-- depuis la racine du dépôt, après le script 163, sur une base JETABLE (jamais sur une base de travail) :
--   psql -U pharma_app -d <base jetable> -v ON_ERROR_STOP=1 -f database/tests/164_bascule_saut_compteurs_promotion_tests.sql
-- Un setval de séquence (bons de commande) n'est pas annulé par ROLLBACK : la base jetable en garde la trace (sans effet sur les tests).
-- Aller, retour, aller : structure identique ; saut fait une seule fois par timeline ; numérotation continue après le saut ;
-- retour refusé après un saut.
-- =====================================================================
BEGIN;

-- Empreinte de la structure (fonction temporaire, annulée avec la transaction)
CREATE FUNCTION pg_temp.structure_bascule() RETURNS text LANGUAGE sql AS $f$
    SELECT (SELECT coalesce(string_agg(table_name || '.' || column_name || ':' || data_type || ':' || is_nullable || ':' || coalesce(column_default, ''), ',' ORDER BY table_name, ordinal_position), '')
            FROM information_schema.columns WHERE table_schema = current_schema() AND table_name IN ('bascule_promotion', 'bascule_saut'))
        || '|' || (SELECT coalesce(string_agg(conrelid::regclass::text || ':' || conname || ':' || pg_get_constraintdef(oid), ',' ORDER BY conrelid::regclass::text, conname), '')
                   FROM pg_constraint WHERE conrelid IN (to_regclass('bascule_promotion'), to_regclass('bascule_saut'))
                      OR conname IN ('ck_compteur_ticket_saut_motif', 'ck_journal_vente_evenement'))
        || '|' || (SELECT coalesce(string_agg(tgname, ',' ORDER BY tgname), '')
                   FROM pg_trigger WHERE tgrelid IN (to_regclass('bascule_promotion'), to_regclass('bascule_saut')) AND NOT tgisinternal)
        || '|' || (SELECT coalesce(string_agg(indexname, ',' ORDER BY indexname), '')
                   FROM pg_indexes WHERE schemaname = current_schema() AND tablename IN ('bascule_promotion', 'bascule_saut'))
        || '|' || (SELECT coalesce(string_agg(proname, ',' ORDER BY proname), '') FROM pg_proc WHERE proname LIKE 'bascule\_%')
        || '|' || (SELECT coalesce(string_agg(nom_table || sens || cle_colonnes::text, ',' ORDER BY nom_table), '') FROM sync_table WHERE nom_table LIKE 'bascule\_%')
$f$;

\ir ../migration/164_bascule_saut_compteurs_promotion.sql
SELECT pg_temp.structure_bascule() AS structure_aller \gset

-- Rejeu : même structure
\ir ../migration/164_bascule_saut_compteurs_promotion.sql
SELECT pg_temp.structure_bascule() = :'structure_aller' AS identique \gset
\if :identique
\else
    DO $$ BEGIN RAISE EXCEPTION 'le second passage a changé la structure'; END $$;
\endif

-- Classement « jamais », motif « bascule » admis, évènement « saut_compteur » admis
DO $$
BEGIN
    ASSERT (SELECT count(*) FROM sync_table WHERE nom_table IN ('bascule_promotion', 'bascule_saut') AND sens = 'jamais') = 2, 'les deux tables doivent être classées « jamais »';
    ASSERT (SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ck_compteur_ticket_saut_motif') LIKE '%restauration%'
       AND (SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ck_compteur_ticket_saut_motif') LIKE '%bascule%', 'motif bascule admis, restauration gardé';
    ASSERT (SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ck_journal_vente_evenement') LIKE '%saut_compteur%', 'évènement saut_compteur admis';
END $$;

-- Retour avant tout saut (deux fois : rejouable), même après la première note sans saut
SELECT * FROM bascule_saut_promotion(1);
\ir ../migration/retour/164_bascule_saut_compteurs_promotion_retour.sql
\ir ../migration/retour/164_bascule_saut_compteurs_promotion_retour.sql
DO $$
BEGIN
    ASSERT to_regclass('bascule_promotion') IS NULL AND to_regclass('bascule_saut') IS NULL, 'les tables doivent disparaître après le retour arrière';
    ASSERT NOT EXISTS (SELECT 1 FROM sync_table WHERE nom_table LIKE 'bascule\_%'), 'le classement doit disparaître après le retour arrière';
    ASSERT NOT EXISTS (SELECT 1 FROM pg_proc WHERE proname LIKE 'bascule\_%'), 'les fonctions doivent disparaître après le retour arrière';
    ASSERT (SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ck_compteur_ticket_saut_motif') NOT LIKE '%bascule%', 'motif bascule retiré par le retour';
    ASSERT (SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ck_journal_vente_evenement') NOT LIKE '%saut_compteur%', 'évènement saut_compteur retiré par le retour';
END $$;

-- Aller de nouveau : même structure qu'au premier passage
\ir ../migration/164_bascule_saut_compteurs_promotion.sql
SELECT pg_temp.structure_bascule() = :'structure_aller' AS identique \gset
\if :identique
\else
    DO $$ BEGIN RAISE EXCEPTION 'aller, retour, aller : la structure n''est pas la même'; END $$;
\endif

-- ---- Comportement ----
-- Départ : compteur des tickets là où il est (v0 : il n'avance que d'une unité à la fois), une facture et un avoir déjà émis cette année, une année précédente, bons de commande à 12
SELECT set_config('test.v0', dernier_numero::text, false) FROM compteur_ticket WHERE id = 1 \gset
DO $$
DECLARE v_annee int := extract(year FROM (now() AT TIME ZONE 'Africa/Algiers'))::int;
BEGIN
    DELETE FROM facture_compteur;
    INSERT INTO facture_compteur (type, annee, dernier) VALUES ('facture', v_annee, 7), ('avoir', v_annee, 2), ('facture', v_annee - 1, 40);
END $$;
SELECT setval('seq_numero_commande', 12, true) \gset

-- 1. Première note : timeline gardée SANS saut
DO $$
DECLARE r record;
BEGIN
    SELECT * INTO r FROM bascule_saut_promotion(1);
    ASSERT r.timeline_notee = 1 AND NOT r.saut_fait AND r.compteurs_sautes = 0, 'la première note ne saute rien';
    ASSERT (SELECT dernier_numero FROM compteur_ticket WHERE id = 1) = current_setting('test.v0')::bigint, 'tickets inchangés à la première note';
    ASSERT (SELECT count(*) FROM bascule_saut) = 0, 'aucun saut noté à la première note';
    -- Même timeline : rien
    SELECT * INTO r FROM bascule_saut_promotion(1);
    ASSERT NOT r.saut_fait, 'même timeline : pas de saut';
    ASSERT (SELECT count(*) FROM bascule_promotion) = 1, 'une seule ligne par timeline';
END $$;

-- 2. Promotion (timeline 2) : tout saute, une seule fois
DO $$
DECLARE r record; v_annee int := extract(year FROM (now() AT TIME ZONE 'Africa/Algiers'))::int; v0 bigint := current_setting('test.v0')::bigint;
    v0t text := lpad(current_setting('test.v0'), 6, '0'); v1t text := lpad((current_setting('test.v0')::bigint + 100000)::text, 6, '0');
BEGIN
    SELECT * INTO r FROM bascule_saut_promotion(2);
    ASSERT r.timeline_notee = 2 AND r.saut_fait, 'la promotion doit sauter les compteurs';
    ASSERT r.compteurs_sautes = 5, format('5 sauts attendus (tickets, 2 années de factures, avoirs, commandes), obtenu %s', r.compteurs_sautes);
    ASSERT (SELECT dernier_numero FROM compteur_ticket WHERE id = 1) = v0 + 100000, 'tickets : +100 000';
    ASSERT (SELECT dernier FROM facture_compteur WHERE type = 'facture' AND annee = v_annee) = 1007, 'factures : +1 000';
    ASSERT (SELECT dernier FROM facture_compteur WHERE type = 'avoir' AND annee = v_annee) = 1002, 'avoirs : +1 000';
    ASSERT (SELECT dernier FROM facture_compteur WHERE type = 'facture' AND annee = v_annee - 1) = 1040, 'année précédente ouverte : +1 000 aussi';
    ASSERT (SELECT last_value FROM seq_numero_commande) = 1012, 'bons de commande : +1 000';
    ASSERT (SELECT count(*) FROM compteur_ticket_saut WHERE motif = 'bascule' AND du = v0 + 1 AND au = v0 + 100000) = 1, 'plage de tickets notée, motif bascule';
    ASSERT (SELECT count(*) FROM bascule_saut s JOIN bascule_promotion p ON p.id = s.promotion_id WHERE p.timeline = 2) = 5, 'cinq sauts notés';
    ASSERT (SELECT ancienne || '>' || nouvelle FROM bascule_saut WHERE compteur = 'ticket') = v0 || '>' || (v0 + 100000), 'ancienne et nouvelle valeur des tickets';
    ASSERT (SELECT count(*) FROM journal_vente WHERE evenement = 'saut_compteur' AND motif LIKE '%T-' || v0t || '%T-' || v1t || '%') = 1, 'journal fiscal : saut des tickets inscrit';
    ASSERT (SELECT count(*) FROM journal_audit WHERE action = 'saut_compteur_bascule' AND details ->> 'motif' = 'bascule') = 5, 'journal des actions : cinq sauts, motif bascule';

    -- Relancer ne resaute pas (même timeline, puis plus ancienne)
    SELECT * INTO r FROM bascule_saut_promotion(2);
    ASSERT NOT r.saut_fait AND r.compteurs_sautes = 0, 'relance : pas de second saut';
    SELECT * INTO r FROM bascule_saut_promotion(1);
    ASSERT NOT r.saut_fait, 'timeline plus ancienne : pas de saut';
    ASSERT (SELECT dernier_numero FROM compteur_ticket WHERE id = 1) = v0 + 100000 AND (SELECT count(*) FROM bascule_saut) = 5, 'rien ne bouge à la relance';
END $$;

-- 3. Numérotation continue après le saut, contrôle de continuité sans anomalie due au saut
DO $$
DECLARE v_annee int := extract(year FROM (now() AT TIME ZONE 'Africa/Algiers'))::int; v_dernier int; v0 bigint := current_setting('test.v0')::bigint;
BEGIN
    ASSERT prochain_numero_vente() = 'T-' || lpad((v0 + 100001)::text, 6, '0'), 'le prochain ticket suit la réserve';
    ASSERT prochain_numero_vente() = 'T-' || lpad((v0 + 100002)::text, 6, '0'), 'puis +1 à la fois';
    ASSERT prochain_numero_commande() = 'C-001013', 'le prochain bon de commande suit la réserve';
    INSERT INTO facture_compteur (type, annee, dernier) VALUES ('facture', v_annee, 1)
    ON CONFLICT (type, annee) DO UPDATE SET dernier = facture_compteur.dernier + 1 RETURNING dernier INTO v_dernier;
    ASSERT v_dernier = 1008, 'la prochaine facture suit la réserve';
    ASSERT NOT EXISTS (SELECT 1 FROM journal_vente_continuite() WHERE anomalie = 'numero_manquant' AND numero IN ('T-' || lpad((v0 + 1)::text, 6, '0'), 'T-' || lpad((v0 + 500)::text, 6, '0'), 'T-' || lpad((v0 + 100000)::text, 6, '0'))),
           'les n° de la plage sautée ne sont pas des n° manquants';
END $$;

-- 4. Troisième timeline (nouvelle promotion) : nouveau saut, une seule fois
DO $$
DECLARE r record; v0 bigint := current_setting('test.v0')::bigint;
BEGIN
    SELECT * INTO r FROM bascule_saut_promotion(3);
    ASSERT r.saut_fait AND r.compteurs_sautes = 5, 'une nouvelle promotion saute de nouveau';
    ASSERT (SELECT dernier_numero FROM compteur_ticket WHERE id = 1) = v0 + 200002, 'tickets : +100 000 de nouveau, depuis v0 + 100002';
    SELECT * INTO r FROM bascule_saut_promotion(3);
    ASSERT NOT r.saut_fait, 'une seule fois par promotion';
END $$;

-- 5. Ajout seul et refus hors fonction
DO $$
BEGIN
    BEGIN UPDATE bascule_promotion SET motif = 'x'; RAISE EXCEPTION 'REFUS_ATTENDU : modification de bascule_promotion acceptée';
    EXCEPTION WHEN raise_exception THEN IF SQLERRM LIKE 'REFUS_ATTENDU%' THEN RAISE; END IF; END;
    BEGIN DELETE FROM bascule_promotion; RAISE EXCEPTION 'REFUS_ATTENDU : suppression de bascule_promotion acceptée';
    EXCEPTION WHEN raise_exception THEN IF SQLERRM LIKE 'REFUS_ATTENDU%' THEN RAISE; END IF; END;
    BEGIN UPDATE bascule_saut SET reserve = reserve; RAISE EXCEPTION 'REFUS_ATTENDU : modification de bascule_saut acceptée';
    EXCEPTION WHEN raise_exception THEN IF SQLERRM LIKE 'REFUS_ATTENDU%' THEN RAISE; END IF; END;
    BEGIN TRUNCATE bascule_saut; RAISE EXCEPTION 'REFUS_ATTENDU : vidage de bascule_saut accepté';
    EXCEPTION WHEN raise_exception THEN IF SQLERRM LIKE 'REFUS_ATTENDU%' THEN RAISE; END IF; END;
    -- Le compteur des tickets ne saute toujours pas hors de la fonction (garde du script 151)
    BEGIN UPDATE compteur_ticket SET dernier_numero = dernier_numero + 5000 WHERE id = 1; RAISE EXCEPTION 'REFUS_ATTENDU : saut des tickets hors fonction accepté';
    EXCEPTION WHEN raise_exception THEN IF SQLERRM LIKE 'REFUS_ATTENDU%' THEN RAISE; END IF; END;
    -- Timeline invalide
    BEGIN PERFORM bascule_saut_promotion(0); RAISE EXCEPTION 'REFUS_ATTENDU : timeline 0 acceptée';
    EXCEPTION WHEN check_violation THEN NULL; END;
END $$;

-- 6. Retour refusé après un saut (trace fiscale) : les erreurs du retour ne stoppent pas le script, la variable ERROR le dit
SAVEPOINT avant_retour;
\set ON_ERROR_STOP off
\ir ../migration/retour/164_bascule_saut_compteurs_promotion_retour.sql
\set retour_en_erreur :ERROR
\set ON_ERROR_STOP on
ROLLBACK TO SAVEPOINT avant_retour;
\if :retour_en_erreur
\else
    DO $$ BEGIN RAISE EXCEPTION 'le retour arrière aurait dû être refusé après un saut'; END $$;
\endif
DO $$
BEGIN
    ASSERT to_regclass('bascule_promotion') IS NOT NULL AND EXISTS (SELECT 1 FROM pg_proc WHERE proname = 'bascule_saut_promotion'),
           'après un saut, le retour est refusé : tables et fonction restent';
END $$;

ROLLBACK;
