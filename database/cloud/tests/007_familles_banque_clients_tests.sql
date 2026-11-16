-- =====================================================================
-- Tests du script central 007 (familles complétées), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/007_familles_banque_clients_tests.sql
-- =====================================================================
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-00000000000a', 'Essai J');

SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-00000000000a', true);
DO $$
DECLARE r record;
BEGIN
    -- Défauts : banque et clients facturés non envoyés, pharmacie oui
    SELECT * INTO r FROM cloud_descendants(0);
    ASSERT r.donnees = '{"familles":["courriel_commandes","fournisseurs","pharmacie"]}', r.donnees::text;
    -- Double sécurité : n° de compte, prêteur, adresse du client effacés à la réception ; nom et NIF de la pharmacie gardés
    SELECT * INTO r FROM cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1', convert_to(
        '{"lignes":[{"n":1,"table":"compte_tresorerie","id":"1","op":"creation","donnees":{"id":1,"nom":"Compte","numero_compte":"0001"}},'
        '{"n":2,"table":"pret_bancaire","id":"1","op":"creation","donnees":{"id":1,"preteur":"Banque Y","montant":10}},'
        '{"n":3,"table":"client_facture","id":"1","op":"creation","donnees":{"id":1,"raison_sociale":"Z","adresse":"Rue 2"}},'
        '{"n":4,"table":"pharmacie","id":"1","op":"creation","donnees":{"id":1,"nom":"Pharmacie X","nif":"123"}}]}', 'UTF8'));
    ASSERT r.statut = 'accepte', r.statut;
    ASSERT (SELECT numero_compte IS NULL AND nom = 'Compte' FROM compte_tresorerie WHERE id = 1), 'n° de compte effacé';
    ASSERT (SELECT preteur IS NULL FROM pret_bancaire WHERE id = 1), 'prêteur effacé';
    ASSERT (SELECT adresse IS NULL AND raison_sociale = 'Z' FROM client_facture WHERE id = 1), 'adresse du client effacée';
    ASSERT (SELECT nom = 'Pharmacie X' AND nif = '123' FROM pharmacie WHERE id = 1), 'nom et NIF de la pharmacie gardés';
    ASSERT (SELECT colonnes_effacees FROM lot_recu WHERE numero = 1) = '{client_facture.adresse,compte_tresorerie.numero_compte,pret_bancaire.preteur}', 'lot brut expurgé';
END $$;
RESET ROLE;

-- Cochées pour l'officine : nouvelle version descendue, plus d'effacement
DO $$ BEGIN
    PERFORM cloud_regler_famille('banque', true, 'aaaaaaaa-0000-0000-0000-00000000000a');
    PERFORM cloud_regler_famille('clients_factures', true, 'aaaaaaaa-0000-0000-0000-00000000000a');
END $$;
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-00000000000a', true);
DO $$
DECLARE r record;
BEGIN
    SELECT * INTO r FROM cloud_descendants(1);
    ASSERT r.version = 2 AND r.donnees = '{"familles":["banque","clients_factures","courriel_commandes","fournisseurs","pharmacie"]}', r.donnees::text;
    PERFORM cloud_appliquer_lot(1, 2, repeat('b', 64), 'sync-v1', convert_to(
        '{"lignes":[{"n":5,"table":"compte_tresorerie","id":"1","op":"modification","donnees":{"id":1,"nom":"Compte","numero_compte":"0001"}}]}', 'UTF8'));
    ASSERT (SELECT numero_compte FROM compte_tresorerie WHERE id = 1) = '0001', 'n° de compte reçu une fois coché';
END $$;
RESET ROLE;

\echo 'Tests du script 007 : tous réussis (transaction annulée).'
ROLLBACK;
