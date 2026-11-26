-- =====================================================================
-- Tests du script 171 (taux de TVA réglables) et de son retour arrière, en transaction ANNULÉE, depuis la racine du dépôt, après 170 :
--   psql -U pharma_app -d <base jetable> -v ON_ERROR_STOP=1 -f database/tests/171_taux_tva_liste_tests.sql
-- Liste de départ 0 % et 19 %, défauts Médicament 0 % et Parapharmacie 19 %, taux unique et borné, défaut d'une catégorie limité à un
-- taux de la liste, rejeu sans doublon, puis aller, retour, aller.
-- =====================================================================
BEGIN;

\ir ../migration/171_taux_tva_liste.sql
\ir ../migration/171_taux_tva_liste.sql

SELECT (SELECT string_agg(taux::text || ':' || libelle, ',' ORDER BY taux) FROM taux_tva) = '0.00:Exonéré,19.00:Taux normal'
   AND (SELECT string_agg(categorie_code || ':' || taux, ',' ORDER BY categorie_code) FROM taux_tva_categorie) = 'medicament:0.00,parapharmacie:19.00'
   AND (SELECT count(*) FROM sync_table WHERE nom_table IN ('taux_tva', 'taux_tva_categorie') AND sens = 'jamais') = 2 AS ok \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'liste ou défauts de départ faux'; END $$;
\endif

\set ON_ERROR_STOP 0
SAVEPOINT a; INSERT INTO taux_tva (taux, libelle) VALUES (19, 'Doublon'); ROLLBACK TO SAVEPOINT a;
SAVEPOINT b; INSERT INTO taux_tva (taux, libelle) VALUES (101, 'Trop'); ROLLBACK TO SAVEPOINT b;
SAVEPOINT c; INSERT INTO taux_tva_categorie (categorie_code, taux) VALUES ('hygiene', 7); ROLLBACK TO SAVEPOINT c;
\set ON_ERROR_STOP 1
SELECT count(*) = 2 AS ok FROM taux_tva \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'un taux en double, hors bornes ou absent de la liste a été accepté'; END $$;
\endif

\ir ../migration/retour/171_taux_tva_liste_retour.sql
SELECT to_regclass('taux_tva') IS NULL AND to_regclass('taux_tva_categorie') IS NULL AS ok \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'retour incomplet'; END $$;
\endif
\ir ../migration/171_taux_tva_liste.sql
SELECT count(*) = 2 AS ok FROM taux_tva \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'aller, retour, aller : liste absente'; END $$;
\endif

ROLLBACK;
