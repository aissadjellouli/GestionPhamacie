-- =====================================================================
-- Tests de la brique d'intégrité (script 111), en transaction annulée :
-- rien n'est conservé. Lancer :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/111_integrite_hachage_tests.sql
-- Chaque test lève une exception en cas d'échec ; « Tests 111 : OK » à la fin.
-- Les tests de modification et de suppression désactivent le déclencheur
-- d'immuabilité DANS la transaction (possible tant que pharma_app est
-- propriétaire : voir database/init/111b_roles_integrite.sql).
-- =====================================================================
BEGIN;

DO $$
DECLARE
    v       record;
    v_h1    bytea;
    v_canon text;
    v_n     bigint;
BEGIN
    -- 1. Chaînage : rangs 1, 2, 3 et empreintes reliées (clé de chaîne propre au test)
    INSERT INTO integrite_essai (libelle, montant, cle_chaine) VALUES ('a', 10.50, 'essai-sql'), ('b', NULL, 'essai-sql'), ('c', 3, 'essai-sql');
    SELECT count(*) INTO v_n FROM integrite_essai WHERE cle_chaine = 'essai-sql' AND rang BETWEEN 1 AND 3;
    IF v_n <> 3 THEN RAISE EXCEPTION 'Test 1 : rangs attendus 1 à 3'; END IF;
    SELECT hash INTO v_h1 FROM integrite_essai WHERE cle_chaine = 'essai-sql' AND rang = 1;
    IF (SELECT hash_precedent FROM integrite_essai WHERE cle_chaine = 'essai-sql' AND rang = 2) <> v_h1 THEN
        RAISE EXCEPTION 'Test 1 : la ligne 2 ne pointe pas sur la ligne 1';
    END IF;
    IF (SELECT hash_precedent FROM integrite_essai WHERE cle_chaine = 'essai-sql' AND rang = 1) <> integrite_origine('sha256') THEN
        RAISE EXCEPTION 'Test 1 : origine de la chaîne attendue (octets nuls)';
    END IF;

    -- 2. Format canonique documenté : recalcul à la main de la ligne 1
    SELECT * INTO v FROM integrite_essai WHERE cle_chaine = 'essai-sql' AND rang = 1;
    v_canon := 'v1|integrite_essai|cle_chaine=9:essai-sql|rang=1:1|id=' || char_length(v.id::text) || ':' || v.id
        || '|libelle=1:a|montant=5:10.50|cree_le=27:' || to_char(v.cree_le AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"');
    IF sha256(convert_to(v_canon, 'UTF8') || integrite_origine('sha256')) <> v.hash THEN
        RAISE EXCEPTION 'Test 2 : empreinte différente du format canonique documenté';
    END IF;
    -- Indépendant du fuseau de la session
    PERFORM set_config('timezone', 'America/New_York', true);
    SELECT * INTO v FROM integrite_verifier('integrite_essai', 'essai-sql');
    IF v.rang_rupture IS NOT NULL OR v.lignes <> 3 THEN RAISE EXCEPTION 'Test 2 : vérification dépendante du fuseau'; END IF;
    PERFORM set_config('timezone', 'Africa/Algiers', true);

    -- 3. Refus de UPDATE, DELETE, TRUNCATE
    BEGIN
        UPDATE integrite_essai SET libelle = 'x' WHERE cle_chaine = 'essai-sql';
        RAISE EXCEPTION 'Test 3 : UPDATE accepté';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE 'Journal integrite_essai chaîné%' THEN RAISE; END IF;
    END;
    BEGIN
        DELETE FROM integrite_essai WHERE cle_chaine = 'essai-sql';
        RAISE EXCEPTION 'Test 3 : DELETE accepté';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE 'Journal integrite_essai chaîné%' THEN RAISE; END IF;
    END;
    BEGIN
        TRUNCATE integrite_essai;
        RAISE EXCEPTION 'Test 3 : TRUNCATE accepté';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE 'Journal integrite_essai chaîné%' THEN RAISE; END IF;
    END;

    -- 4. Chaîne intacte
    SELECT * INTO v FROM integrite_verifier('integrite_essai', 'essai-sql');
    IF v.rang_rupture IS NOT NULL OR v.dernier_rang <> 3 THEN RAISE EXCEPTION 'Test 4 : chaîne intacte signalée rompue'; END IF;
END;
$$;

-- 5. Modification simulée (déclencheur désactivé dans la transaction) : rupture au rang 2
SAVEPOINT modification;
ALTER TABLE integrite_essai DISABLE TRIGGER trg_integrite_immuable;
UPDATE integrite_essai SET montant = 999 WHERE cle_chaine = 'essai-sql' AND rang = 2;
DO $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM integrite_verifier('integrite_essai', 'essai-sql');
    IF v.rang_rupture IS DISTINCT FROM 2 OR v.motif NOT LIKE 'ligne modifiée%' THEN
        RAISE EXCEPTION 'Test 5 : modification non détectée (%, %)', v.rang_rupture, v.motif;
    END IF;
END;
$$;
ROLLBACK TO SAVEPOINT modification;

-- 6. Suppression simulée : rupture au rang 2 (ligne manquante)
SAVEPOINT suppression;
ALTER TABLE integrite_essai DISABLE TRIGGER trg_integrite_immuable;
DELETE FROM integrite_essai WHERE cle_chaine = 'essai-sql' AND rang = 2;
DO $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM integrite_verifier('integrite_essai', 'essai-sql');
    IF v.rang_rupture IS DISTINCT FROM 2 OR v.motif NOT LIKE 'ligne manquante%' THEN
        RAISE EXCEPTION 'Test 6 : suppression non détectée (%, %)', v.rang_rupture, v.motif;
    END IF;
END;
$$;
ROLLBACK TO SAVEPOINT suppression;

-- 7. Registre immuable et isolation exigée
DO $$
BEGIN
    BEGIN
        UPDATE chaine_integrite SET colonnes = ARRAY['id'] WHERE journal = 'integrite_essai';
        RAISE EXCEPTION 'Test 7 : registre modifié';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE 'Le registre des journaux chaînés%' THEN RAISE; END IF;
    END;
END;
$$;

DO $$ BEGIN RAISE NOTICE 'Tests 111 : OK'; END; $$;
ROLLBACK;
