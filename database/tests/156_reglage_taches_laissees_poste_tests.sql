-- =====================================================================
-- Tests du script 156 (réglage « facture et retour fournisseur laissés » : colonne, défaut, valeurs permises), en transaction
-- ANNULÉE, depuis la racine du dépôt :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/156_reglage_taches_laissees_poste_tests.sql
-- Le script 156 est rejoué deux fois dans la transaction ; rien n'est gardé (ROLLBACK).
-- =====================================================================
BEGIN;

\ir ../migration/156_reglage_taches_laissees_poste.sql
\ir ../migration/156_reglage_taches_laissees_poste.sql

DO $$
DECLARE
    v_ok boolean;
BEGIN
    -- 1. Colonne et contrainte présentes une seule fois (rejeu sans erreur ni doublon)
    ASSERT (SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'parametre_caisse' AND column_name = 'taches_laissees_poste'
              AND data_type = 'text' AND is_nullable = 'NO') = 1, 'colonne text NOT NULL';
    ASSERT (SELECT count(*) FROM pg_constraint
            WHERE conname = 'ck_parametre_caisse_taches_laissees_poste' AND conrelid = 'parametre_caisse'::regclass) = 1, 'une seule contrainte';
    ASSERT (SELECT col_description('parametre_caisse'::regclass, attnum) FROM pg_attribute
            WHERE attrelid = 'parametre_caisse'::regclass AND attname = 'taches_laissees_poste') LIKE '%(156)%', 'commentaire';

    -- 2. Défaut : chiffré (ligne existante et nouvelle ligne)
    ASSERT (SELECT taches_laissees_poste FROM parametre_caisse WHERE id = 1) IS NOT NULL, 'ligne 1 renseignée';
    ASSERT (SELECT column_default FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'parametre_caisse' AND column_name = 'taches_laissees_poste') LIKE '''chiffre''%',
        'défaut chiffre';

    -- 3. Deux valeurs seulement
    UPDATE parametre_caisse SET taches_laissees_poste = 'desactive' WHERE id = 1;
    ASSERT (SELECT taches_laissees_poste FROM parametre_caisse WHERE id = 1) = 'desactive', 'desactive accepté';
    UPDATE parametre_caisse SET taches_laissees_poste = 'chiffre' WHERE id = 1;
    ASSERT (SELECT taches_laissees_poste FROM parametre_caisse WHERE id = 1) = 'chiffre', 'chiffre accepté';

    v_ok := false;
    BEGIN
        UPDATE parametre_caisse SET taches_laissees_poste = 'clair' WHERE id = 1;
    EXCEPTION WHEN check_violation THEN v_ok := true;
    END;
    ASSERT v_ok, 'valeur « clair » refusée (jamais d''écriture en clair)';

    v_ok := false;
    BEGIN
        UPDATE parametre_caisse SET taches_laissees_poste = NULL WHERE id = 1;
    EXCEPTION WHEN not_null_violation THEN v_ok := true;
    END;
    ASSERT v_ok, 'valeur vide refusée';
END $$;

ROLLBACK;
\echo '156 : tous réussis'
