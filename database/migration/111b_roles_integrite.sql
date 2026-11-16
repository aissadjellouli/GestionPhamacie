-- =====================================================================
-- 111b — Séparation des rôles PostgreSQL pour les journaux inaltérables
-- (2026-11-16, lot 2). SCRIPT PRÉVU, NON EXÉCUTÉ : À FAIRE PAR
-- L'ADMINISTRATEUR (superutilisateur PostgreSQL), voir
-- docs/mise-en-production.md, « Séparation des rôles (journaux
-- inaltérables) ».
--
-- Pourquoi : aujourd'hui pharma_app (compte de l'API) est PROPRIÉTAIRE
-- de la base et des tables ; un propriétaire peut désactiver les
-- déclencheurs d'immuabilité (ALTER TABLE … DISABLE TRIGGER). Après ce
-- script :
--   * pharma_proprietaire (sans connexion) possède la base, les tables,
--     les fonctions et les déclencheurs ;
--   * pharma_app garde lecture, ajout, modification, suppression sur les
--     tables métier, mais n'a PLUS UPDATE, DELETE ni TRUNCATE sur les
--     journaux inaltérables (tables chaînées de chaine_integrite,
--     sceau_integrite, verification_integrite, journal_audit,
--     journal_sauvegarde, boite_evenement, registre_psychotrope) et ne
--     peut plus désactiver un déclencheur.
--
-- Mode d'emploi (une seule fois par base, API arrêtée, sauvegarde faite) :
--   psql -U postgres -d pharmacie_prod -v ON_ERROR_STOP=1 -f 111b_roles_integrite.sql
-- Ensuite, les scripts de mise à jour (database/migration/1xx) se lancent
-- avec « SET ROLE pharma_proprietaire; » en tête de session, par un
-- compte membre de pharma_proprietaire (l'administrateur), jamais par
-- pharma_app. La restauration (pg_restore) se fait aussi avec ce rôle.
-- Retour arrière : ALTER DATABASE … OWNER TO pharma_app puis
-- REASSIGN OWNED BY pharma_proprietaire TO pharma_app.
-- Idempotent.
-- =====================================================================

DO $$
BEGIN
    IF NOT (SELECT rolsuper FROM pg_roles WHERE rolname = current_user) THEN
        RAISE EXCEPTION '111b : à exécuter par un superutilisateur PostgreSQL (pas par pharma_app)';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pharma_proprietaire') THEN
        CREATE ROLE pharma_proprietaire NOLOGIN;
    END IF;
END;
$$;

-- 1. Propriété : base, schéma, objets
DO $$
BEGIN
    EXECUTE format('ALTER DATABASE %I OWNER TO pharma_proprietaire', current_database());
END;
$$;
ALTER SCHEMA public OWNER TO pharma_proprietaire;
REASSIGN OWNED BY pharma_app TO pharma_proprietaire;

-- 2. Droits du compte applicatif
DO $$
BEGIN
    EXECUTE format('GRANT CONNECT, TEMPORARY ON DATABASE %I TO pharma_app', current_database());
END;
$$;
GRANT USAGE ON SCHEMA public TO pharma_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO pharma_app;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO pharma_app;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO pharma_app;
ALTER DEFAULT PRIVILEGES FOR ROLE pharma_proprietaire IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO pharma_app;
ALTER DEFAULT PRIVILEGES FOR ROLE pharma_proprietaire IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO pharma_app;
ALTER DEFAULT PRIVILEGES FOR ROLE pharma_proprietaire IN SCHEMA public GRANT EXECUTE ON FUNCTIONS TO pharma_app;

-- 3. Journaux inaltérables : ajout et lecture seulement
DO $$
DECLARE t text;
BEGIN
    FOR t IN
        SELECT journal FROM chaine_integrite
        UNION SELECT unnest(ARRAY['sceau_integrite', 'verification_integrite', 'journal_audit', 'journal_sauvegarde',
                                  'boite_evenement', 'registre_psychotrope'])
    LOOP
        IF to_regclass(quote_ident(t)) IS NOT NULL THEN
            EXECUTE format('REVOKE UPDATE, DELETE, TRUNCATE ON %I FROM pharma_app', t);
        END IF;
    END LOOP;
END;
$$;
-- Le registre des journaux chaînés ne change que par un script (rôle propriétaire)
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON chaine_integrite FROM pharma_app;

-- 4. Contrôle (à lire dans la sortie) : pharma_app ne doit plus rien posséder
SELECT count(*) AS objets_encore_a_pharma_app
FROM pg_class c JOIN pg_roles r ON r.oid = c.relowner
WHERE r.rolname = 'pharma_app' AND c.relnamespace = 'public'::regnamespace;
