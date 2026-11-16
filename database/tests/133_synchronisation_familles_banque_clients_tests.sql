-- =====================================================================
-- Tests du script 133 (familles complétées), en transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/133_synchronisation_familles_banque_clients_tests.sql
-- =====================================================================
BEGIN;

DO $$
DECLARE
    ph jsonb := '{"id":1,"nom":"Pharmacie X","titulaire":"T","nif":"1","nis":"2","rc":"3","article_imposition":"4","chifa_officine":"5"}';
    ct jsonb := '{"id":1,"type":"banque","nom":"Compte","banque":"B","numero_compte":"0001"}';
    pr jsonb := '{"id":1,"preteur":"Banque Y","montant":10}';
    cf jsonb := '{"id":1,"raison_sociale":"Société Z","adresse":"Rue 2","nif":"9"}';
    sms jsonb := '{"id":1,"texte":"Bonjour"}';
BEGIN
    ASSERT (SELECT count(*) FROM sync_famille_colonne WHERE famille = 'pharmacie') = 9, 'pharmacie : 9 colonnes';
    -- Aucune famille : identifiants de la pharmacie, n° de compte, prêteur, adresse du client retirés ; le reste part
    ASSERT sync_retirer_familles('pharmacie', ph, '{}') = '{"id":1,"chifa_officine":"5"}', 'pharmacie : ' || sync_retirer_familles('pharmacie', ph, '{}')::text;
    ASSERT sync_retirer_familles('compte_tresorerie', ct, '{}') = '{"id":1,"type":"banque","nom":"Compte"}', 'n° de compte (et banque, script 135) retirés';
    ASSERT sync_retirer_familles('pret_bancaire', pr, '{}') = '{"id":1,"montant":10}', 'prêteur retiré';
    ASSERT sync_retirer_familles('client_facture', cf, '{}') = '{"id":1,"raison_sociale":"Société Z","nif":"9"}', 'adresse du client retirée';
    ASSERT sync_retirer_familles('parametre_formulaire_sms', sms, '{}') = sms, 'texte SMS toujours envoyé';
    -- Familles autorisées : tout part
    ASSERT sync_retirer_familles('pharmacie', ph, '{pharmacie}') = ph, 'pharmacie autorisée';
    ASSERT sync_retirer_familles('compte_tresorerie', ct, '{banque}') = ct AND sync_retirer_familles('pret_bancaire', pr, '{banque}') = pr, 'banque autorisée';
    ASSERT sync_retirer_familles('client_facture', cf, '{clients_factures}') = cf, 'clients facturés autorisés';
    -- Réglage descendu : les nouvelles familles sont reconnues
    PERFORM sync_appliquer_descendant('reglage_central', 'familles_envoyees', 999999, '{"familles":["banque","clients_factures","pharmacie"]}');
    ASSERT sync_familles_effectives() = '{banque,clients_factures,pharmacie}', 'nouvelles familles reconnues';
    BEGIN
        INSERT INTO sync_famille_colonne VALUES ('inconnue', 'x', 'y');
        RAISE EXCEPTION 'famille inconnue acceptée';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
END $$;

\echo 'Tests du script 133 : tous réussis (transaction annulée).'
ROLLBACK;
