-- =====================================================================
-- Tests du script 172 (activation de l'offre signée) et de son retour arrière, en transaction ANNULÉE, depuis la racine du dépôt, après 171 :
--   psql -U pharma_app -d <base jetable> -v ON_ERROR_STOP=1 -f database/tests/172_activation_offre_tests.sql
-- Table vide au départ, code unique, source cohérente avec l'ordre du central, refus noté une seule fois avec une ligne au journal,
-- classement « jamais », rejeu sans doublon, puis aller, retour, aller.
-- =====================================================================
BEGIN;

\ir ../migration/172_activation_offre.sql
\ir ../migration/172_activation_offre.sql

SELECT (SELECT count(*) FROM activation_offre) = 0
   AND (SELECT count(*) FROM sync_table WHERE nom_table = 'activation_offre' AND sens = 'jamais') = 1 AS ok \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'table non vide au départ ou classement faux'; END $$;
\endif

INSERT INTO activation_offre (code, emise_le, source) VALUES ('PO1.essai-du-script-172.signature', now(), 'code');
\set ON_ERROR_STOP 0
SAVEPOINT a; INSERT INTO activation_offre (code, emise_le, source) VALUES ('PO1.essai-du-script-172.signature', now(), 'code'); ROLLBACK TO SAVEPOINT a;
SAVEPOINT b; INSERT INTO activation_offre (code, emise_le, source) VALUES ('PO1.essai-central-sans-ordre.signature', now(), 'central'); ROLLBACK TO SAVEPOINT b;
SAVEPOINT c; INSERT INTO activation_offre (code, emise_le, source) VALUES ('PO1.source-inconnue-du-172.signature', now(), 'autre'); ROLLBACK TO SAVEPOINT c;
\set ON_ERROR_STOP 1
SELECT count(*) = 1 AS ok FROM activation_offre \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'un code en double, une source fausse ou un envoi du central sans ordre a été accepté'; END $$;
\endif

SELECT activation_offre_signaler_refus((SELECT id FROM activation_offre), 'Signature fausse') AS premiere,
       activation_offre_signaler_refus((SELECT id FROM activation_offre), 'Signature fausse') AS seconde \gset
SELECT :'premiere'::boolean AND NOT :'seconde'::boolean
   AND (SELECT count(*) FROM journal_audit WHERE action = 'offre_activation_refusee'
        AND enregistrement_id = (SELECT id FROM activation_offre)) = 1 AS ok \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'refus non noté, ou noté deux fois'; END $$;
\endif

\ir ../migration/retour/172_activation_offre_retour.sql
SELECT to_regclass('activation_offre') IS NULL AND to_regprocedure('activation_offre_signaler_refus(bigint,text)') IS NULL
   AND NOT EXISTS (SELECT 1 FROM sync_table WHERE nom_table = 'activation_offre') AS ok \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'retour incomplet'; END $$;
\endif
\ir ../migration/172_activation_offre.sql
SELECT to_regclass('activation_offre') IS NOT NULL AS ok \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'aller après retour en échec'; END $$;
\endif

ROLLBACK;
\echo '172_activation_offre_tests : OK'
