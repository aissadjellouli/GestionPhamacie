-- =====================================================================
-- Tests du script central 008 (famille banque élargie), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/008_famille_banque_elargie_tests.sql
-- =====================================================================
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-00000000000b', 'Essai K');
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM cloud_famille_colonne WHERE famille = 'banque') = 8, 'banque : 8 colonnes';
    ASSERT NOT (SELECT envoyee FROM cloud_famille WHERE famille = 'banque'), 'défaut non';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_famille_colonne f JOIN cloud_table_miroir m ON m.nom_table = f.nom_table WHERE f.colonne = ANY (m.cle)),
        'colonne de famille dans une clé du central';
END $$;

SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-00000000000b', true);
DO $$
DECLARE r record;
BEGIN
    SELECT * INTO r FROM cloud_appliquer_lot(1, 1, repeat('a', 64), 'sync-v1', convert_to(
        '{"lignes":[{"n":1,"table":"reglement_fournisseur","id":"1","op":"creation","donnees":{"id":1,"montant":5,"banque":"B","reference":"CH-1"}},'
        '{"n":2,"table":"paiement_effet","id":"1","op":"creation","donnees":{"id":1,"reference":"V"}},'
        '{"n":3,"table":"remise_cheques","id":"1","op":"creation","donnees":{"id":1,"reference":"R"}},'
        '{"n":4,"table":"pret_bancaire","id":"1","op":"creation","donnees":{"id":1,"reference":"P"}},'
        '{"n":5,"table":"compte_tresorerie","id":"1","op":"creation","donnees":{"id":1,"nom":"C","banque":"B"}}]}', 'UTF8'));
    ASSERT r.statut = 'accepte', r.statut;
    ASSERT (SELECT banque IS NULL AND reference IS NULL AND montant = 5 FROM reglement_fournisseur WHERE id = 1), 'règlement effacé';
    ASSERT (SELECT reference IS NULL FROM paiement_effet WHERE id = 1) AND (SELECT reference IS NULL FROM remise_cheques WHERE id = 1)
       AND (SELECT reference IS NULL FROM pret_bancaire WHERE id = 1) AND (SELECT banque IS NULL AND nom = 'C' FROM compte_tresorerie WHERE id = 1), 'références et banque effacées';
END $$;
RESET ROLE;

\echo 'Tests du script 008 : tous réussis (transaction annulée).'
ROLLBACK;
