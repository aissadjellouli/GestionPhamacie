-- =====================================================================
-- Tests du script 161 (index des clés étrangères manquantes), en transaction ANNULÉE, depuis la racine du dépôt, après le script 160 :
--   psql -U pharma_app -d <base jetable> -v ON_ERROR_STOP=1 -f database/tests/161_index_cles_etrangeres_manquantes_tests.sql
-- Le script 161 est rejoué deux fois dans la transaction ; rien n'est gardé (ROLLBACK). Chaque index existe, commence par la colonne
-- attendue, et un second passage n'en crée aucun de plus.
-- =====================================================================
BEGIN;

\ir ../migration/161_index_cles_etrangeres_manquantes.sql
SELECT count(*) AS avant FROM pg_indexes WHERE schemaname = current_schema() \gset
\ir ../migration/161_index_cles_etrangeres_manquantes.sql
SELECT (count(*) = :avant) AS identique FROM pg_indexes WHERE schemaname = current_schema() \gset
\if :identique
\else
    DO $$ BEGIN RAISE EXCEPTION 'le second passage a créé des index'; END $$;
\endif

DO $$
DECLARE
    attendu text[] := ARRAY[
        'ix_ligne_commande_commande|ligne_commande|commande_id',
        'ix_produit_dci|produit|dci_id',
        'ix_facture_client_patient|facture_client|patient_id',
        'ix_commande_fournisseur_fournisseur|commande_fournisseur|fournisseur_id',
        'ix_journal_vente_session_caisse|journal_vente|session_caisse_id',
        'ix_evenement_caisse_session|evenement_caisse|session_caisse_id',
        'ix_ligne_retour_fournisseur_retour|ligne_retour_fournisseur|retour_id',
        'ix_sync_ordre_recu_message_ferme_par|sync_ordre_recu|message_ferme_par'];
    a text;
    p text[];
BEGIN
    FOREACH a IN ARRAY attendu LOOP
        p := string_to_array(a, '|');
        ASSERT EXISTS (
            SELECT 1 FROM pg_index i
            JOIN pg_class ic ON ic.oid = i.indexrelid
            JOIN pg_class tc ON tc.oid = i.indrelid
            JOIN pg_attribute at ON at.attrelid = tc.oid AND at.attnum = i.indkey[0]
            WHERE ic.relname = p[1] AND tc.relname = p[2] AND at.attname = p[3]), 'index ' || p[1] || ' sur ' || p[2] || '(' || p[3] || ')';
    END LOOP;
END $$;

ROLLBACK;
