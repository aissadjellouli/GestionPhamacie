-- =====================================================================
-- Tests du script 135 (famille banque élargie), en transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/135_synchronisation_famille_banque_elargie_tests.sql
-- =====================================================================
BEGIN;

DO $$
DECLARE
    rf jsonb := '{"id":1,"montant":5,"banque":"B","reference":"CH-1","moyen":"cheque"}';
    ct jsonb := '{"id":1,"nom":"Compte","banque":"B","numero_compte":"0001"}';
BEGIN
    ASSERT (SELECT count(*) FROM sync_famille_colonne WHERE famille = 'banque') = 8, 'banque : 8 colonnes';
    -- Aucune colonne de famille dans la clé de sa table
    ASSERT NOT EXISTS (SELECT 1 FROM sync_famille_colonne f JOIN sync_table s ON s.nom_table = f.nom_table WHERE f.colonne = ANY (s.cle_colonnes)),
        'colonne de famille dans une clé';
    ASSERT NOT EXISTS (SELECT 1 FROM sync_famille_colonne WHERE nom_table = 'correspondance_echeancier_preteur'), 'clé preteur jamais retirée';
    ASSERT sync_retirer_familles('reglement_fournisseur', rf, '{}') = '{"id":1,"montant":5,"moyen":"cheque"}', 'règlement : banque et référence retirées';
    ASSERT sync_retirer_familles('compte_tresorerie', ct, '{}') = '{"id":1,"nom":"Compte"}', 'compte : banque et n° retirés';
    ASSERT sync_retirer_familles('paiement_effet', '{"id":1,"reference":"V"}', '{}') = '{"id":1}', 'paiement_effet';
    ASSERT sync_retirer_familles('remise_cheques', '{"id":1,"reference":"R"}', '{}') = '{"id":1}', 'remise_cheques';
    ASSERT sync_retirer_familles('pret_bancaire', '{"id":1,"reference":"P","preteur":"Y"}', '{}') = '{"id":1}', 'pret_bancaire';
    ASSERT sync_retirer_familles('reglement_fournisseur', rf, '{banque}') = rf, 'banque autorisée : tout part';
END $$;

\echo 'Tests du script 135 : tous réussis (transaction annulée).'
ROLLBACK;
