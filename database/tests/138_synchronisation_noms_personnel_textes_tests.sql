-- =====================================================================
-- Tests du script 138 (noms du personnel dans les textes libres), en transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/138_synchronisation_noms_personnel_textes_tests.sql
-- =====================================================================
BEGIN;

DO $$
DECLARE
    v_id  bigint := (SELECT min(id) FROM utilisateur);
    v_txt text;
BEGIN
    UPDATE utilisateur SET nom_complet = 'Zorglub Essaiqx (adj.)' WHERE id = v_id;
    DELETE FROM reglage_central WHERE cle = 'familles_envoyees';
    INSERT INTO reglage_central (cle, version, valeur) VALUES ('familles_envoyees', 1, '{"familles":["pharmacie"]}');

    -- Famille « employes » non envoyée : le nom complet disparaît (casse indifférente), le reste est gardé
    v_txt := sync_filtrer_texte('3 ventes avec remise, accordées par Zorglub Essaiqx (adj.), et par ZORGLUB essaiqx (ADJ.).');
    ASSERT v_txt NOT ILIKE '%zorglub%' AND v_txt NOT ILIKE '%essaiqx%', 'nom du personnel masqué : ' || v_txt;
    ASSERT v_txt LIKE '3 ventes avec remise, accordées par [PERSONNEL], et par [PERSONNEL].', 'texte autour conservé : ' || v_txt;

    -- Un nom absent du texte : texte inchangé ; texte vide ou NULL : inchangé
    ASSERT sync_filtrer_texte('aucun nom ici') = 'aucun nom ici', 'texte sans nom inchangé';
    ASSERT sync_filtrer_texte(NULL) IS NULL, 'NULL';

    -- Famille « employes » envoyée : le texte part tel quel
    UPDATE reglage_central SET valeur = '{"familles":["employes","pharmacie"]}' WHERE cle = 'familles_envoyees';
    v_txt := sync_filtrer_texte('accordées par Zorglub Essaiqx (adj.)');
    ASSERT v_txt = 'accordées par Zorglub Essaiqx (adj.)', 'famille autorisée : texte intact : ' || v_txt;

    -- Nom vide ou trop court : jamais de masquage de tout le texte
    DELETE FROM reglage_central WHERE cle = 'familles_envoyees';
    UPDATE utilisateur SET nom_complet = ' ' WHERE id = v_id;
    ASSERT sync_filtrer_texte('un texte quelconque') = 'un texte quelconque', 'nom vide ignoré';
END $$;

\echo 'Tests du script 138 : tous réussis (transaction annulée).'
ROLLBACK;
