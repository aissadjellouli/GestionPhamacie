-- =====================================================================
-- Tests du script central 010 (mutualisation des réglages), en transaction ANNULÉE :
--   psql -U <propriétaire> -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database/cloud/tests/010_mutualisation_reglages_tests.sql
-- =====================================================================
BEGIN;

INSERT INTO officine (id, libelle) VALUES ('aaaaaaaa-0000-0000-0000-000000000021', 'Essai M'), ('bbbbbbbb-0000-0000-0000-000000000022', 'Essai N');

-- ---- 1. État de départ : 11 tables, un seul périmètre « tous », AUCUNE valeur publiée (D12) ----
DO $$
BEGIN
    ASSERT cardinality(cloud_reglage_tables()) = 11, '11 tables';
    ASSERT (SELECT count(*) FROM cloud_reglage_table) = 11, '11 tables décrites';
    ASSERT (SELECT count(*) FROM cloud_reglage_modele) = 11 AND (SELECT count(DISTINCT perimetre) FROM cloud_reglage_modele) = 1
           AND (SELECT min(perimetre) FROM cloud_reglage_modele) = 'tous', 'un modèle par table, périmètre « tous »';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_reglage_modele WHERE valeur IS NOT NULL OR version_modele <> 0), 'rien publié (D12)';
    ASSERT (SELECT array_agg(nom_table ORDER BY nom_table) FROM cloud_reglage_modele WHERE mode = 'impose')
           = ARRAY['parametre_fiscal', 'parametre_serialisation', 'parametre_tracabilite'], 'modes de départ du § 16 : 3 imposés';
    ASSERT (SELECT count(*) FROM cloud_reglage_modele WHERE mode = 'propose') = 8, '8 proposés';
    ASSERT (SELECT array_agg(nom_table ORDER BY nom_table) FROM cloud_reglage_table) = (SELECT array_agg(x ORDER BY x) FROM unnest(cloud_reglage_tables()) x), 'liste fermée';
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_reglage_modele WHERE ajouts_locaux IS NOT TRUE), 'ajouts locaux permis par défaut (D5)';
    -- Table du groupe 3 refusée par la structure
    BEGIN
        INSERT INTO cloud_reglage_table (nom_table, libelle, mode_depart) VALUES ('parametre_caisse', 'Caisse', 'propose');
        RAISE EXCEPTION 'table du groupe 3 acceptée';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    -- Miroir de l'état montant de l'officine (002 régénéré) : sécurité par ligne forcée, valeur du modèle jamais reçue
    ASSERT EXISTS (SELECT 1 FROM cloud_table_miroir WHERE nom_table = 'reglage_mutualise' AND cle = '{nom_table}'), 'miroir catalogué';
    ASSERT (SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid = 'reglage_mutualise'::regclass), 'sécurité par ligne forcée sur le miroir';
    ASSERT NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'reglage_mutualise' AND column_name = 'modele'), 'colonne modele absente du miroir';
    ASSERT 'modele_code_central' = ANY (SELECT unnest(colonnes) FROM cloud_table_miroir WHERE nom_table = 'motif_reglable'), 'repérage des motifs reçu';
    ASSERT 'modele_cle' = ANY (SELECT unnest(colonnes) FROM cloud_table_miroir WHERE nom_table = 'remise_automatique'), 'repérage des remises reçu';
END $$;

-- ---- 2. Droits : l'API ne fait que LIRE les modèles ; rien sur le journal ; aucune fonction d'administration ----
DO $$
BEGIN
    ASSERT has_table_privilege('pharma_cloud_api', 'cloud_reglage_modele', 'SELECT'), 'lecture des modèles';
    ASSERT NOT has_table_privilege('pharma_cloud_api', 'cloud_reglage_modele', 'INSERT, UPDATE, DELETE, TRUNCATE'), 'aucune écriture des modèles';
    ASSERT NOT has_table_privilege('pharma_cloud_api', 'cloud_journal_reglage', 'SELECT, INSERT, UPDATE, DELETE, TRUNCATE'), 'aucun droit sur le journal';
    ASSERT NOT has_function_privilege('pharma_cloud_api', 'cloud_publier_reglage(text, jsonb, text, text, boolean, text, text, text, text)', 'EXECUTE'), 'publier : pas pour l''API';
    ASSERT NOT has_function_privilege('pharma_cloud_api', 'cloud_regler_mode_reglage(text, text, text, text, text, text, text)', 'EXECUTE'), 'mode : pas pour l''API';
    ASSERT NOT has_function_privilege('pharma_cloud_api', 'cloud_liberer_reglages(uuid, text, text, text, text, text)', 'EXECUTE'), 'libérer : pas pour l''API';
    ASSERT NOT has_function_privilege('pharma_cloud_api', 'cloud_retablir_reglages(uuid, text, text, text, text, text)', 'EXECUTE'), 'rétablir : pas pour l''API';
    ASSERT NOT has_function_privilege('pharma_cloud_api', 'cloud_etat_reglages(text)', 'EXECUTE'), 'état des officines : pas pour l''API';
    ASSERT has_function_privilege('pharma_cloud_api', 'cloud_descendants(bigint)', 'EXECUTE'), 'cloud_descendants reste appelable par l''API';
    ASSERT NOT has_function_privilege('public', 'cloud_publier_reglage(text, jsonb, text, text, boolean, text, text, text, text)', 'EXECUTE'), 'publier : pas pour PUBLIC';
END $$;

SET LOCAL ROLE pharma_cloud_api;
DO $$
BEGIN
    ASSERT (SELECT count(*) FROM cloud_reglage_modele) = 11, 'l''API lit les 11 modèles (politique de lecture)';
    BEGIN UPDATE cloud_reglage_modele SET valeur = '{"x":1}', version_modele = 1; RAISE EXCEPTION 'écriture acceptée';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN INSERT INTO cloud_reglage_modele (perimetre, nom_table, mode, modifie_par) VALUES ('autre', 'parametre_fiscal', 'impose', 'api'); RAISE EXCEPTION 'insertion acceptée';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN DELETE FROM cloud_reglage_modele; RAISE EXCEPTION 'suppression acceptée';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM count(*) FROM cloud_journal_reglage; RAISE EXCEPTION 'journal lisible';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM cloud_publier_reglage('parametre_fiscal', '{"x":1}', 'api'); RAISE EXCEPTION 'publication par l''API acceptée';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
    BEGIN PERFORM cloud_liberer_reglages('aaaaaaaa-0000-0000-0000-000000000021', 'api'); RAISE EXCEPTION 'libération par l''API acceptée';
    EXCEPTION WHEN insufficient_privilege THEN NULL; END;
END $$;
RESET ROLE;

-- ---- 3. Rien publié : l'échange ne porte QUE le réglage des familles (comportement de 009 inchangé) ----
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000021', true);
DO $$
DECLARE r record;
BEGIN
    SELECT count(*), max(version) INTO r FROM cloud_descendants(0);
    ASSERT r.count = 1 AND r.max = 1, 'rien publié : un seul élément (familles) : ' || r.count;
    ASSERT NOT EXISTS (SELECT 1 FROM descendant WHERE cle LIKE 'mutu\_%'), 'aucun élément de mutualisation';
END $$;
RESET ROLE;

-- ---- 4. Contrôles à la publication ----
DO $$
BEGIN
    BEGIN PERFORM cloud_publier_reglage('parametre_substitution', '{"equivalent_exact_seulement":true}', ''); RAISE EXCEPTION 'auteur vide accepté';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%auteur est obligatoire%', SQLERRM; END;
    BEGIN PERFORM cloud_publier_reglage('parametre_substitution', '{"equivalent_exact_seulement":true}', NULL); RAISE EXCEPTION 'auteur absent accepté';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%auteur est obligatoire%', SQLERRM; END;
    BEGIN PERFORM cloud_publier_reglage('parametre_caisse', '{"x":1}', 'admin-1'); RAISE EXCEPTION 'groupe 3 accepté';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%non mutualisable%', SQLERRM; END;
    BEGIN PERFORM cloud_publier_reglage('parametre_substitution', '[1]', 'admin-1'); RAISE EXCEPTION 'valeur non objet acceptée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%objet JSON%', SQLERRM; END;
    BEGIN PERFORM cloud_publier_reglage('parametre_substitution', NULL, 'admin-1'); RAISE EXCEPTION 'valeur absente acceptée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%objet JSON%', SQLERRM; END;
    BEGIN PERFORM cloud_publier_reglage('parametre_substitution', jsonb_build_object('x', repeat('a', 61000)), 'admin-1'); RAISE EXCEPTION 'valeur trop grosse acceptée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%trop volumineuse%', SQLERRM; END;
    BEGIN PERFORM cloud_publier_reglage('parametre_substitution', '{"x":1}', 'admin-1', 'obligatoire'); RAISE EXCEPTION 'mode inconnu accepté';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%mode inconnu%', SQLERRM; END;
    BEGIN PERFORM cloud_publier_reglage('parametre_substitution', '{"x":1}', 'admin-1', 'propose', NULL, NULL, NULL, NULL, 'groupe_inconnu'); RAISE EXCEPTION 'périmètre inconnu accepté';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%périmètre inconnu%', SQLERRM; END;
    ASSERT NOT EXISTS (SELECT 1 FROM cloud_reglage_modele WHERE valeur IS NOT NULL), 'aucun refus n''a rien publié';
    ASSERT (SELECT count(*) FROM cloud_journal_reglage) = 0, 'aucun refus n''a rien journalisé';
END $$;

-- ---- 5. Publication : version, journal avant / après, idempotence ----
DO $$
DECLARE v int;
BEGIN
    v := cloud_publier_reglage('parametre_substitution', '{"equivalent_exact_seulement":true}', 'admin-1', 'propose', NULL, 'DOMAINE\admin', 'POSTE-1', 'première publication');
    ASSERT v = 1, 'version 1';
    ASSERT (SELECT version_modele = 1 AND valeur = '{"equivalent_exact_seulement":true}' AND mode = 'propose' AND modifie_par = 'admin-1'
            FROM cloud_reglage_modele WHERE nom_table = 'parametre_substitution'), 'modèle publié';
    ASSERT (SELECT action = 'publication' AND valeur_avant IS NULL AND valeur_apres = '{"equivalent_exact_seulement":true}' AND mode_avant = 'propose'
                   AND mode_apres = 'propose' AND version_modele = 1 AND auteur = 'admin-1' AND compte_systeme = 'DOMAINE\admin' AND poste = 'POSTE-1'
            FROM cloud_journal_reglage WHERE nom_table = 'parametre_substitution'), 'journal : avant (aucun) / après, qui, compte, poste';
    ASSERT cloud_publier_reglage('parametre_substitution', '{"equivalent_exact_seulement":true}', 'admin-1') = 1, 'même valeur : même version';
    ASSERT (SELECT count(*) FROM cloud_journal_reglage) = 1, 'même valeur : rien de plus au journal';
    v := cloud_publier_reglage('parametre_substitution', '{"equivalent_exact_seulement":false}', 'admin-2');
    ASSERT v = 2, 'nouvelle valeur : version 2';
    ASSERT (SELECT valeur_avant = '{"equivalent_exact_seulement":true}' AND valeur_apres = '{"equivalent_exact_seulement":false}'
            FROM cloud_journal_reglage WHERE version_modele = 2), 'journal : avant / après';
    -- Mode de la table non publiée : seulement noté, version inchangée
    ASSERT cloud_regler_mode_reglage('parametre_fiscal', 'propose', 'admin-1') = 0, 'table non publiée : version 0';
    ASSERT (SELECT mode = 'propose' AND valeur IS NULL FROM cloud_reglage_modele WHERE nom_table = 'parametre_fiscal'), 'mode noté, rien publié';
    -- Table publiée : le changement de mode est une nouvelle version
    ASSERT cloud_regler_mode_reglage('parametre_substitution', 'impose', 'admin-1', NULL, NULL, 'obligation') = 3, 'changement de mode : version 3';
    ASSERT cloud_regler_mode_reglage('parametre_substitution', 'impose', 'admin-1') = 3, 'même mode : même version';
    ASSERT (SELECT action = 'mode' AND mode_avant = 'propose' AND mode_apres = 'impose' AND motif = 'obligation'
            FROM cloud_journal_reglage WHERE nom_table = 'parametre_substitution' AND action = 'mode'), 'journal du mode';
    BEGIN PERFORM cloud_regler_mode_reglage('parametre_substitution', 'local', 'admin-1'); RAISE EXCEPTION 'mode local accepté';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%mode inconnu%', SQLERRM; END;
END $$;

-- ---- 6. Le journal est en ajout seul ----
DO $$
BEGIN
    BEGIN UPDATE cloud_journal_reglage SET auteur = 'x'; RAISE EXCEPTION 'modification acceptée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%ajout seul%', SQLERRM; END;
    BEGIN DELETE FROM cloud_journal_reglage; RAISE EXCEPTION 'suppression acceptée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%ajout seul%', SQLERRM; END;
    BEGIN TRUNCATE cloud_journal_reglage; RAISE EXCEPTION 'vidage accepté';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%ajout seul%', SQLERRM; END;
    BEGIN INSERT INTO cloud_journal_reglage (action, auteur) VALUES ('publication', '  '); RAISE EXCEPTION 'auteur vide au journal';
    EXCEPTION WHEN check_violation THEN NULL; END;
END $$;

-- ---- 7. Voie descendante : l'élément mutu_ n'apparaît qu'une fois publié, une nouvelle version seulement si l'élément change ----
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000021', true);
DO $$
DECLARE r record; v1 bigint; v_max bigint;
BEGIN
    SELECT * INTO r FROM cloud_descendants(0) WHERE cle = 'mutu_parametre_substitution';
    ASSERT r.cle IS NOT NULL, 'élément publié descendu';
    ASSERT r.nom_table = 'reglage_central', 'table locale de destination';
    ASSERT r.donnees ->> 'format' = 'mutu-v1' AND r.donnees ->> 'mode' = 'impose' AND (r.donnees ->> 'version_modele')::int = 3
           AND (r.donnees ->> 'ajouts_locaux')::boolean AND r.donnees ->> 'auteur' = 'admin-1'
           AND r.donnees -> 'valeur' = '{"equivalent_exact_seulement":false}' AND r.donnees ->> 'publie_le' ~ '^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$', 'format mutu-v1 : ' || r.donnees::text;
    v1 := r.version;
    SELECT max(version) INTO v_max FROM cloud_descendants(0);
    ASSERT v_max = v1 OR v_max > v1, 'versions monotones';
    -- Autres tables : rien (non publiées)
    ASSERT (SELECT count(*) FROM cloud_descendants(0) WHERE cle LIKE 'mutu\_%') = 1, 'une seule table publiée';
    -- À l'identique : aucune nouvelle version à chaque échange
    ASSERT (SELECT count(*) FROM cloud_descendants(v_max)) = 0, 'rien de plus à l''échange suivant';
    ASSERT (SELECT version_max FROM officine_descendant) = v_max, 'version maximale stable';
END $$;
RESET ROLE;

-- Un changement de mode redonne une version ; une autre officine reçoit le même modèle (périmètre « tous »)
DO $$ BEGIN PERFORM cloud_regler_mode_reglage('parametre_substitution', 'propose', 'admin-1'); END $$;
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000021', true);
DO $$
DECLARE v_avant bigint; r record;
BEGIN
    SELECT version_max INTO v_avant FROM officine_descendant;
    SELECT * INTO r FROM cloud_descendants(v_avant) WHERE cle = 'mutu_parametre_substitution';
    ASSERT r.cle IS NOT NULL AND r.version = v_avant + 1 AND r.donnees ->> 'mode' = 'propose' AND (r.donnees ->> 'version_modele')::int = 4, 'nouvelle version après changement de mode';
END $$;
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000022', true);
DO $$
DECLARE r record;
BEGIN
    SELECT * INTO r FROM cloud_descendants(0) WHERE cle = 'mutu_parametre_substitution';
    ASSERT r.cle IS NOT NULL AND r.donnees ->> 'mode' = 'propose', 'autre officine : même modèle';
    ASSERT (SELECT count(*) FROM descendant) = 2, 'cloisonnement : 2 éléments pour cette officine (familles, substitution)';
END $$;
RESET ROLE;

-- ---- 8. Libération (D3) : mode « local » pour l'officine concernée seulement, puis rétablissement ----
DO $$
BEGIN
    ASSERT cloud_liberer_reglages('aaaaaaaa-0000-0000-0000-000000000021', 'admin-1', NULL, NULL, 'départ') = 1, 'une table publiée libérée';
    ASSERT cloud_liberer_reglages('aaaaaaaa-0000-0000-0000-000000000021', 'admin-1') = 0, 'déjà libérée';
    ASSERT (SELECT mutualisation_liberee_le IS NOT NULL FROM officine WHERE id = 'aaaaaaaa-0000-0000-0000-000000000021'), 'date de libération';
    ASSERT (SELECT action = 'liberation' AND mode_apres = 'local' AND officine_id = 'aaaaaaaa-0000-0000-0000-000000000021' AND motif = 'départ'
            FROM cloud_journal_reglage WHERE action = 'liberation'), 'journal de la libération';
    BEGIN PERFORM cloud_liberer_reglages('cccccccc-0000-0000-0000-000000000099', 'admin-1'); RAISE EXCEPTION 'officine inconnue acceptée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%Officine inconnue%', SQLERRM; END;
END $$;
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000021', true);
DO $$
DECLARE v_avant bigint; r record;
BEGIN
    SELECT version_max INTO v_avant FROM officine_descendant;
    SELECT * INTO r FROM cloud_descendants(v_avant) WHERE cle = 'mutu_parametre_substitution';
    ASSERT r.cle IS NOT NULL AND r.donnees ->> 'mode' = 'local' AND r.donnees ? 'valeur' = false AND (r.donnees ->> 'version_modele')::int = 4, 'officine libérée : mode local sans valeur : ' || r.donnees::text;
END $$;
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000022', true);
DO $$
BEGIN
    ASSERT (SELECT donnees ->> 'mode' FROM descendant WHERE cle = 'mutu_parametre_substitution') = 'propose', 'l''autre officine garde son mode';
END $$;
RESET ROLE;
DO $$
BEGIN
    ASSERT cloud_retablir_reglages('aaaaaaaa-0000-0000-0000-000000000021', 'admin-1') = 1, 'rétablie';
    ASSERT cloud_retablir_reglages('aaaaaaaa-0000-0000-0000-000000000021', 'admin-1') = 0, 'déjà rétablie';
    ASSERT (SELECT mutualisation_liberee_le IS NULL FROM officine WHERE id = 'aaaaaaaa-0000-0000-0000-000000000021'), 'libération levée';
END $$;
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000021', true);
DO $$
DECLARE v_avant bigint; r record;
BEGIN
    SELECT version_max INTO v_avant FROM officine_descendant;
    SELECT * INTO r FROM cloud_descendants(v_avant) WHERE cle = 'mutu_parametre_substitution';
    ASSERT r.cle IS NOT NULL AND r.donnees ->> 'mode' = 'propose' AND r.donnees ? 'valeur', 'après rétablissement : le modèle redescend';
END $$;
RESET ROLE;

-- ---- 9. Central restauré (logique de 009 conservée) : les éléments mutu_ sont republiés au-dessus ----
SET LOCAL ROLE pharma_cloud_api;
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000022', true);
DO $$
DECLARE n int;
BEGIN
    SELECT count(*) INTO n FROM cloud_descendants(50);
    ASSERT n = 2, 'les deux éléments republiés au-dessus de 50 : ' || n;
    ASSERT (SELECT min(version) FROM descendant) = 51 AND (SELECT version_max FROM officine_descendant) = 52, 'versions remontées';
END $$;
RESET ROLE;

-- ---- 10. État des officines (miroir de l'état montant, sécurité par ligne) ----
SELECT set_config('app.officine_id', 'aaaaaaaa-0000-0000-0000-000000000021', true);
INSERT INTO reglage_mutualise (officine_id, nom_table, mode, etat, mode_recu, version_modele_recue, sync_epoque, sync_numero)
VALUES ('aaaaaaaa-0000-0000-0000-000000000021', 'parametre_substitution', 'propose', 'applique', 'propose', 4, 1, 1);
SELECT set_config('app.officine_id', 'bbbbbbbb-0000-0000-0000-000000000022', true);
INSERT INTO reglage_mutualise (officine_id, nom_table, mode, etat, mode_recu, version_modele_recue, motif_rejet, sync_epoque, sync_numero)
VALUES ('bbbbbbbb-0000-0000-0000-000000000022', 'parametre_substitution', 'local', 'rejete', 'impose', 4, 'seuil hors bornes', 1, 1);
SELECT set_config('app.officine_id', '', true);
DO $$
DECLARE r record;
BEGIN
    SELECT * INTO r FROM cloud_etat_reglages('parametre_substitution') WHERE officine_id = 'bbbbbbbb-0000-0000-0000-000000000022';
    ASSERT r.etat = 'rejete' AND r.motif_rejet = 'seuil hors bornes' AND r.mode_recu = 'impose', 'rejet visible au central';
    ASSERT (SELECT count(*) FROM cloud_etat_reglages('parametre_substitution') WHERE officine_id IN ('aaaaaaaa-0000-0000-0000-000000000021', 'bbbbbbbb-0000-0000-0000-000000000022')) = 2, 'les deux officines';
    ASSERT (SELECT count(*) FROM cloud_etat_reglages('parametre_fiscal') WHERE officine_id IN ('aaaaaaaa-0000-0000-0000-000000000021', 'bbbbbbbb-0000-0000-0000-000000000022')) = 0, 'filtre par table';
    ASSERT coalesce(current_setting('app.officine_id', true), '') = '', 'officine posée remise comme avant';
    BEGIN PERFORM cloud_etat_reglages('parametre_caisse'); RAISE EXCEPTION 'groupe 3 accepté';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%non mutualisable%', SQLERRM; END;
END $$;

\echo 'Tests du script central 010 : tous réussis (transaction annulée).'
ROLLBACK;
