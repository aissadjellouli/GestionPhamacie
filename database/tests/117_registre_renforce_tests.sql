-- =====================================================================
-- Tests du registre renforcé (script 117), en transaction annulée : rien
-- n'est conservé. Lancer :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/117_registre_renforce_tests.sql
-- Chaque test lève une exception en cas d'échec ; « Tests 117 : OK » à la fin.
-- Les falsifications désactivent un déclencheur DANS la transaction
-- (possible tant que pharma_app est propriétaire, voir 111b).
-- =====================================================================
BEGIN;

DO $$
DECLARE
    v_lot   bigint;
    v_user  bigint;
    v_reg   bigint;
    v_rang  bigint;
    v       record;
BEGIN
    SELECT id INTO v_lot FROM lot ORDER BY id LIMIT 1;
    SELECT id INTO v_user FROM utilisateur ORDER BY id LIMIT 1;
    IF v_lot IS NULL OR v_user IS NULL THEN RAISE EXCEPTION 'Tests 117 : base sans lot ni utilisateur'; END IF;
    SELECT max(rang) INTO v_rang FROM journal_registre_psychotrope WHERE cle_chaine = 'officine';

    -- 1. Une ligne ajoutée au registre est recopiée dans le journal chaîné (rang suivant), registre et journal concordent
    INSERT INTO registre_psychotrope (lot_id, type_mouvement, quantite, solde_apres, utilisateur_id, reference)
    VALUES (v_lot, 'entree', 1, 1, v_user, 'TEST-117') RETURNING id INTO v_reg;
    SELECT * INTO v FROM journal_registre_psychotrope WHERE nature = 'inscription' AND registre_id = v_reg;
    IF NOT FOUND OR v.rang <> v_rang + 1 OR v.reference <> 'TEST-117' THEN RAISE EXCEPTION 'Test 1 : ligne non recopiée au rang suivant'; END IF;
    SELECT * INTO v FROM registre_concordance();
    IF v.motif IS NOT NULL THEN RAISE EXCEPTION 'Test 1 : discordance inattendue : %', v.motif; END IF;
    SELECT * INTO v FROM integrite_verifier('journal_registre_psychotrope', 'officine');
    IF v.rang_rupture IS NOT NULL THEN RAISE EXCEPTION 'Test 1 : chaîne rompue : %', v.motif; END IF;

    -- 2. Annulation d'écriture : ajout seul, recopiée ; ni modifiable ni supprimable
    INSERT INTO annulation_registre (registre_id, motif, utilisateur_id) VALUES (v_reg, 'Erreur de saisie (test)', v_user);
    IF NOT EXISTS (SELECT 1 FROM journal_registre_psychotrope WHERE nature = 'annulation' AND registre_id = v_reg) THEN
        RAISE EXCEPTION 'Test 2 : annulation non recopiée';
    END IF;
    BEGIN
        UPDATE annulation_registre SET motif = 'autre motif' WHERE registre_id = v_reg;
        RAISE EXCEPTION 'Test 2 : modification acceptée';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE 'Table annulation_registre en ajout seul%' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO annulation_registre (registre_id, motif, utilisateur_id) VALUES (v_reg, 'Deuxième annulation', v_user);
        RAISE EXCEPTION 'Test 2 : double annulation acceptée';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    SELECT * INTO v FROM registre_concordance();
    IF v.motif IS NOT NULL THEN RAISE EXCEPTION 'Test 2 : discordance inattendue : %', v.motif; END IF;

    -- 3. Journal chaîné : ni modification ni suppression
    BEGIN
        UPDATE journal_registre_psychotrope SET quantite = 9 WHERE registre_id = v_reg;
        RAISE EXCEPTION 'Test 3 : modification du journal acceptée';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE 'Journal journal_registre_psychotrope chaîné%' THEN RAISE; END IF;
    END;
END;
$$;

-- 4. Falsifications simulées (déclencheurs désactivés dans la transaction) : chacune est détectée
SAVEPOINT s4;
ALTER TABLE registre_psychotrope DISABLE TRIGGER trg_registre_immuable;
UPDATE registre_psychotrope SET quantite = quantite + 1 WHERE reference = 'TEST-117';
DO $$ BEGIN
    IF (SELECT motif FROM registre_concordance()) NOT LIKE 'ligne du registre modifiée%' THEN
        RAISE EXCEPTION 'Test 4a : modification d''une ligne recopiée non détectée';
    END IF;
END $$;
ROLLBACK TO SAVEPOINT s4;

ALTER TABLE registre_psychotrope DISABLE TRIGGER trg_registre_immuable;
UPDATE registre_psychotrope SET quantite = quantite + 1
WHERE id = (SELECT min(r.id) FROM registre_psychotrope r
            WHERE NOT EXISTS (SELECT 1 FROM journal_registre_psychotrope j WHERE j.nature = 'inscription' AND j.registre_id = r.id));
DO $$ BEGIN
    IF (SELECT lignes_anterieures FROM journal_registre_psychotrope WHERE nature = 'ouverture') > 0
       AND COALESCE((SELECT motif FROM registre_concordance()), '') NOT LIKE 'lignes antérieures%' THEN
        RAISE EXCEPTION 'Test 4b : modification d''une ligne antérieure non détectée';
    END IF;
END $$;
ROLLBACK TO SAVEPOINT s4;

ALTER TABLE registre_psychotrope DISABLE TRIGGER zz_registre_journal_chaine;
INSERT INTO registre_psychotrope (lot_id, type_mouvement, quantite, solde_apres, utilisateur_id, reference)
SELECT min(id), 'entree', 1, 1, (SELECT min(id) FROM utilisateur), 'TEST-117-HORS' FROM lot;
DO $$ BEGIN
    IF COALESCE((SELECT motif FROM registre_concordance()), '') NOT LIKE 'lignes antérieures%' THEN
        RAISE EXCEPTION 'Test 4c : ligne ajoutée hors journal non détectée';
    END IF;
END $$;
ROLLBACK TO SAVEPOINT s4;

-- 5. Règles d'alerte présentes, forçage en ajout seul, motif obligatoire
DO $$
DECLARE v_user bigint := (SELECT min(id) FROM utilisateur);
BEGIN
    IF (SELECT count(*) FROM regle_alerte_psychotrope) <> 4 THEN RAISE EXCEPTION 'Test 5 : 4 règles attendues'; END IF;
    BEGIN
        INSERT INTO forcage_alerte_psychotrope (utilisateur_id, codes, messages, motif, produits) VALUES (v_user, ARRAY['patient_repete'], 'x', 'ok', ARRAY[1::bigint]);
        RAISE EXCEPTION 'Test 5 : motif trop court accepté';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    INSERT INTO forcage_alerte_psychotrope (utilisateur_id, codes, messages, motif, produits) VALUES (v_user, ARRAY['patient_repete'], 'x', 'Traitement long connu', ARRAY[1::bigint]);
    BEGIN
        DELETE FROM forcage_alerte_psychotrope WHERE motif = 'Traitement long connu';
        RAISE EXCEPTION 'Test 5 : suppression acceptée';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE 'Table forcage_alerte_psychotrope en ajout seul%' THEN RAISE; END IF;
    END;
END $$;

SELECT 'Tests 117 : OK' AS resultat;
ROLLBACK;
