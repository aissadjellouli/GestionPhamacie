-- =====================================================================
-- Tests du script 168 (réception en attente abandonnée par un autre employé) et de son retour arrière, en transaction ANNULÉE,
-- depuis la racine du dépôt, après le script 167 :
--   psql -U pharma_app -d <base jetable> -v ON_ERROR_STOP=1 -f database/tests/168_controle_reception_abandonnee_tests.sql
-- Abandon par un autre employé : une anomalie « reception_abandonnee » ; abandon de sa propre réception, ou abandon noté avant le
-- script (sans identifiant) : aucune ; second calcul : aucun doublon ; puis aller, retour, aller à l'identique.
-- =====================================================================
BEGIN;

CREATE FUNCTION pg_temp.structure_168() RETURNS text LANGUAGE sql AS $f$
    SELECT coalesce((SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ck_alerte_surveillance_type'), '')
        || '|' || coalesce((SELECT md5(prosrc) FROM pg_proc WHERE proname = 'surveiller'), '')
        || '|' || coalesce((SELECT md5(prosrc) FROM pg_proc WHERE proname = 'surveillance_candidats_receptions'), '')
$f$;

\ir ../migration/168_controle_reception_abandonnee.sql
SELECT pg_temp.structure_168() AS structure_aller \gset

-- Deux employés actifs
SELECT id AS employe_a FROM utilisateur WHERE actif ORDER BY id LIMIT 1 \gset
SELECT id AS employe_b FROM utilisateur WHERE actif AND id <> :employe_a ORDER BY id LIMIT 1 \gset

INSERT INTO journal_audit (utilisateur_id, action, table_cible, enregistrement_id, details) VALUES
    (:employe_a, 'reception_attente_abandonnee', 'reception_attente', 990001,
     jsonb_build_object('libelle', 'BL collègue', 'lignes', 4, 'mise_en_attente_par', 'B', 'mise_en_attente_par_id', :employe_b)),
    (:employe_a, 'reception_attente_abandonnee', 'reception_attente', 990002,
     jsonb_build_object('libelle', 'BL à moi', 'lignes', 2, 'mise_en_attente_par', 'A', 'mise_en_attente_par_id', :employe_a)),
    (:employe_a, 'reception_attente_abandonnee', 'reception_attente', 990003,
     jsonb_build_object('libelle', 'BL ancien', 'lignes', 1, 'mise_en_attente_par', 'B'));

SELECT count(*) AS nouvelles FROM surveiller(current_date) \gset
SELECT count(*) = 1 AND bool_and(cle = '990001' AND utilisateur_id = :employe_a AND niveau = 'attention') AS ok
FROM alerte_surveillance WHERE type = 'reception_abandonnee' \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'une seule anomalie attendue, pour la réception du collègue'; END $$;
\endif
SELECT count(*) = 0 AS ok FROM surveiller(current_date) s JOIN alerte_surveillance a ON a.id = s.o_id WHERE a.type = 'reception_abandonnee' \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'second calcul : doublon'; END $$;
\endif

-- Retour : anomalies du type supprimées, fonction retirée ; aller : à l'identique
\ir ../migration/retour/168_controle_reception_abandonnee_retour.sql
SELECT count(*) = 0 AND to_regprocedure('surveillance_candidats_receptions(date)') IS NULL AS ok
FROM alerte_surveillance WHERE type = 'reception_abandonnee' \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'retour incomplet'; END $$;
\endif
\ir ../migration/168_controle_reception_abandonnee.sql
SELECT pg_temp.structure_168() = :'structure_aller' AS identique \gset
\if :identique
\else
    DO $$ BEGIN RAISE EXCEPTION 'aller, retour, aller : la structure n''est pas la même'; END $$;
\endif

ROLLBACK;
