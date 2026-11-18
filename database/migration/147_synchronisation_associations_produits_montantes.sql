-- =====================================================================
-- 147 : synchronisation, reclassement des associations faites dans
-- l'officine sur ses produits (2026-11-18, « go reclassement » ;
-- docs/conception-synchronisation.md § 29, décisions RC-D1 à RC-D3).
--   * chifa_code_produit (code CHIFA relié à un produit local, qui l'a
--     associé, quand) et produit_indication (produit local relié à une
--     indication) étaient classées « descendantes » à tort : elles
--     désignent des produits par leur n° local, le central ne peut pas les
--     fournir. Elles deviennent MONTANTES : copie au central (restauration
--     d'une officine, vues multi-officines). Aucune donnée de patient ;
--     associe_par (n° d'employé) envoyé comme les autres n° d'employés.
--   * Si la synchronisation est active : déclencheurs posés, puis les
--     lignes déjà présentes sont inscrites UNE fois dans la file d'envoi
--     (« creation », minimisées comme par le déclencheur), au moment du
--     changement de sens seulement : le rejeu du script n'inscrit rien.
--   * PRÉREQUIS : le script 019 du serveur central (tables miroir) doit
--     être passé AVANT (un lot contenant une table inconnue du central est
--     refusé en entier).
-- Idempotent. Aucune donnée existante modifiée.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

DO $reclassement$
DECLARE
    v_tables text[];
    v_actif  boolean;
    t        sync_table;
    p        sync_parametre;
    n        bigint;
BEGIN
    -- 1. Sens : tables pas encore montantes (lues AVANT l'écriture : elles seules auront leurs lignes existantes inscrites)
    SELECT coalesce(array_agg(nom_table), '{}') INTO v_tables
    FROM sync_table WHERE nom_table IN ('chifa_code_produit', 'produit_indication') AND sens <> 'montante';
    INSERT INTO sync_table (nom_table, sens, colonnes_exclues, colonnes_patient, porteur, motif, cle_colonnes) VALUES
        ('chifa_code_produit', 'montante', '{}', '{}', false, 'Code CHIFA associé à un produit dans l''officine (n° de produit local), script 147', '{code}'),
        ('produit_indication', 'montante', '{}', '{}', false, 'Indications associées à un produit dans l''officine (n° de produit local), script 147', '{produit_id,indication_id}')
    ON CONFLICT (nom_table) DO UPDATE SET sens = EXCLUDED.sens, motif = EXCLUDED.motif, cle_colonnes = EXCLUDED.cle_colonnes
    WHERE (sync_table.sens, sync_table.motif, sync_table.cle_colonnes) IS DISTINCT FROM (EXCLUDED.sens, EXCLUDED.motif, EXCLUDED.cle_colonnes);
    SELECT * INTO p FROM sync_parametre WHERE id = 1;
    v_actif := coalesce(p.active, false);
    IF cardinality(v_tables) = 0 OR NOT v_actif THEN RETURN; END IF;

    -- 2. Synchronisation active : déclencheurs posés, puis lignes existantes inscrites une fois (comme le ferait le déclencheur)
    PERFORM sync_installer_declencheurs();
    FOREACH t.nom_table IN ARRAY v_tables LOOP
        SELECT * INTO t FROM sync_table WHERE nom_table = t.nom_table;
        EXECUTE format($f$
            INSERT INTO sync_sortie (nom_table, id_local, operation, donnees, version_contrat, epoque)
            SELECT %L,
                   (SELECT string_agg(coalesce(m.j ->> k.col, ''), '|' ORDER BY k.n) FROM unnest($1::text[]) WITH ORDINALITY AS k(col, n)),
                   'creation', m.j, $2, $3
            FROM (SELECT sync_minimiser($4, to_jsonb(x)) AS j FROM public.%I x) m$f$, t.nom_table, t.nom_table)
        USING t.cle_colonnes, p.version_contrat, p.epoque, t;
        GET DIAGNOSTICS n = ROW_COUNT;
        RAISE NOTICE 'Synchronisation : % ligne(s) de % inscrites pour le serveur central', n, t.nom_table;
    END LOOP;
END $reclassement$;
