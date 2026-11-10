-- =====================================================================
-- Pharma Officine : REMISE À VIERGE D'UNE BASE (tout est supprimé)
--
-- Supprime TOUTES les tables, vues, fonctions et données (schémas public et
-- import), ventes, stock, patients, comptes, journal compris. IRRÉVERSIBLE :
-- faire une sauvegarde avant (Administration › Sauvegardes ou pg_dump).
--
-- Garde-fou : le nom de la base doit être confirmé, sinon rien n'est fait.
--
--   psql -h <serveur> -U pharma_app -d <base> -v ON_ERROR_STOP=1 -v confirmer=<base> -f database/init/clean.sql
--
-- Ensuite : schema.sql, puis seed_referentiel.sql.
-- L'utilisateur doit être propriétaire de la base (pharma_app, voir
-- docs/mise-en-production.md) ; les extensions pg_trgm et pgcrypto sont
-- recréées par schema.sql.
-- =====================================================================

\if :{?confirmer}
\else
    \echo 'Refusé : ajoutez -v confirmer=<nom de la base> pour confirmer la suppression de toutes les données.'
    \quit
\endif

SELECT current_database() = :'confirmer' AS confirme, current_database() AS base \gset
\if :confirme
\else
    \echo 'Refusé : confirmer=' :'confirmer' ' ne correspond pas à la base connectée (' :'base' ').'
    \quit
\endif

\echo 'Suppression de tout le contenu de la base' :'base'

BEGIN;
DROP SCHEMA IF EXISTS import CASCADE;
DROP SCHEMA IF EXISTS public CASCADE;
CREATE SCHEMA public;
GRANT USAGE ON SCHEMA public TO PUBLIC;
COMMIT;

\echo 'Base vide. Étapes suivantes : schema.sql puis seed_referentiel.sql.'
