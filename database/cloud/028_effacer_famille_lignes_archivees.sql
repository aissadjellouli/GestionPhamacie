-- =====================================================================
-- Base centrale, 2026-11-20 (décisions du titulaire, docs/taches/
-- central-decisions-2026-11-19) : effacement d'une famille nominative
-- aussi dans les lignes archivées (ligne_remplacee). Script de la base
-- CENTRALE, passé par le propriétaire, idempotent ; à passer après 027.
--   1. ligne_remplacee : colonnes expurgee_le et colonnes_effacees (comme
--      lot_recu). Rien d'autre ne change : la table reste en ajout seul.
--   2. cloud_ligne_remplacee_garde redéfinie (dernière définition : 021) :
--      DELETE inchangé (effacement d'une officine partie) ; UPDATE permis
--      SEULEMENT si l'opération « expurger » est posée par la fonction
--      d'effacement de famille, que toutes les autres colonnes restent
--      identiques, que la copie archivée perde exactement les colonnes
--      déclarées effacées et que ces colonnes appartiennent à une famille
--      nominative de cette table (cloud_famille_colonne).
--   3. cloud_effacer_famille redéfinie (dernière définition : 004), même
--      signature. En plus des tables miroir et des lots bruts : les lignes
--      archivées de l'officine qui contiennent encore la colonne (simulation :
--      compte ; confirmation : colonne retirée de la copie, date et colonnes
--      gardées). Lignes de sortie supplémentaires : nom_table = 'ligne_remplacee',
--      colonne = '<table>.<colonne>'. Journal de sécurité « famille_effacee »
--      complété. Refus inchangé si la famille est encore envoyée.
--   Gardes de rejeu dans 004 et 021 (marqueur : colonne
--   ligne_remplacee.colonnes_effacees).
-- Aucune ligne archivée n'est supprimée ; seules des valeurs de famille
-- décochée sont retirées, sur confirmation.
-- =====================================================================

SET client_encoding = 'UTF8';

DO $version$ BEGIN
    IF to_regclass('public.ligne_remplacee') IS NULL OR to_regprocedure('cloud_effacement_tables_techniques()') IS NULL THEN
        RAISE EXCEPTION 'Script central 028 : passer d''abord les scripts 020 à 027 (ligne_remplacee ou cloud_effacement_tables_techniques() absent). Rien n''a été modifié.';
    END IF;
END $version$;

-- ---- 1. Colonnes ----
ALTER TABLE ligne_remplacee ADD COLUMN IF NOT EXISTS expurgee_le timestamptz;
ALTER TABLE ligne_remplacee ADD COLUMN IF NOT EXISTS colonnes_effacees text[];
COMMENT ON COLUMN ligne_remplacee.expurgee_le IS 'Valeurs de familles nominatives décochées retirées de la copie archivée (script 028).';
COMMENT ON COLUMN ligne_remplacee.colonnes_effacees IS 'Colonnes retirées de la copie archivée (famille effacée), noms de colonnes de la table copiée (script 028).';

-- ---- 2. Garde de la table : ajout seul, sauf effacement de départ (DELETE) et expurgation d'une famille (UPDATE) ----
CREATE OR REPLACE FUNCTION cloud_ligne_remplacee_garde() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE v_mobiles text[] := ARRAY['ancienne', 'colonnes_effacees', 'expurgee_le'];
BEGIN
    IF TG_OP = 'DELETE' AND cloud_effacement_officine_en_cours(OLD.officine_id) THEN
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE'
       AND coalesce(current_setting('cloud.ligne_remplacee_operation', true), '') = 'expurger'
       AND (to_jsonb(NEW) - v_mobiles) = (to_jsonb(OLD) - v_mobiles)
       AND NEW.expurgee_le IS NOT NULL
       AND coalesce(cardinality(NEW.colonnes_effacees), 0) > 0
       AND coalesce(OLD.colonnes_effacees, '{}') <@ NEW.colonnes_effacees
       AND NEW.ancienne = OLD.ancienne - NEW.colonnes_effacees
       AND NOT EXISTS (SELECT 1 FROM unnest(NEW.colonnes_effacees) c
                       WHERE NOT EXISTS (SELECT 1 FROM cloud_famille_colonne f WHERE f.nom_table = OLD.nom_table AND f.colonne = c)) THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION '% : ajout seul', TG_TABLE_NAME;
END $$;

-- ---- 3. Effacement d'une famille (remplace la version de 004) : lignes archivées comprises ----
CREATE OR REPLACE FUNCTION cloud_effacer_famille(p_famille text, p_officine uuid DEFAULT NULL, p_confirmer boolean DEFAULT false)
RETURNS TABLE (officine_id uuid, nom_table text, colonne text, lignes bigint, lots bigint)
LANGUAGE plpgsql AS $$
DECLARE o record; c record; v_cols text[]; v_n bigint; v_lots bigint; v_total bigint; v_total_lots bigint; v_archives bigint;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM cloud_famille WHERE famille = p_famille) THEN RAISE EXCEPTION 'Famille inconnue : %', p_famille; END IF;
    IF p_officine IS NOT NULL THEN
        IF NOT EXISTS (SELECT 1 FROM officine WHERE id = p_officine) THEN RAISE EXCEPTION 'Officine inconnue : %', p_officine; END IF;
        IF p_famille = ANY (cloud_familles_envoyees(p_officine)) THEN
            RAISE EXCEPTION 'La famille % est encore envoyée par cette officine : décochez-la d''abord', p_famille USING ERRCODE = 'P0001';
        END IF;
    END IF;
    SELECT array_agg(f.nom_table || '.' || f.colonne ORDER BY 1) INTO v_cols FROM cloud_famille_colonne f WHERE f.famille = p_famille;
    FOR o IN SELECT x.id FROM officine x
             WHERE (p_officine IS NULL OR x.id = p_officine) AND NOT p_famille = ANY (cloud_familles_envoyees(x.id)) ORDER BY x.id LOOP
        PERFORM set_config('app.officine_id', o.id::text, true);
        v_total := 0;
        v_archives := 0;
        FOR c IN SELECT f.nom_table, f.colonne FROM cloud_famille_colonne f
                 JOIN cloud_table_miroir m ON m.nom_table = f.nom_table AND f.colonne = ANY (m.colonnes)
                 WHERE f.famille = p_famille ORDER BY 1, 2 LOOP
            IF p_confirmer THEN
                EXECUTE format('WITH u AS (UPDATE %I SET %I = NULL WHERE officine_id = $1 AND %I IS NOT NULL RETURNING 1) SELECT count(*) FROM u',
                               c.nom_table, c.colonne, c.colonne) INTO v_n USING o.id;
            ELSE
                EXECUTE format('SELECT count(*) FROM %I WHERE officine_id = $1 AND %I IS NOT NULL', c.nom_table, c.colonne) INTO v_n USING o.id;
            END IF;
            v_total := v_total + v_n;
            RETURN QUERY SELECT o.id, c.nom_table, c.colonne, v_n, NULL::bigint;
        END LOOP;
        -- Lignes archivées (ligne_remplacee) : colonnes de la famille encore présentes dans la copie. Compte avant retrait.
        FOR c IN SELECT f.nom_table, f.colonne FROM cloud_famille_colonne f
                 JOIN cloud_table_miroir m ON m.nom_table = f.nom_table AND f.colonne = ANY (m.colonnes)
                 WHERE f.famille = p_famille ORDER BY 1, 2 LOOP
            SELECT count(*) INTO v_n FROM ligne_remplacee l
            WHERE l.officine_id = o.id AND l.nom_table = c.nom_table AND l.ancienne ? c.colonne;
            v_archives := v_archives + v_n;
            IF v_n > 0 THEN
                RETURN QUERY SELECT o.id, 'ligne_remplacee'::text, c.nom_table || '.' || c.colonne, v_n, NULL::bigint;
            END IF;
        END LOOP;
        IF p_confirmer THEN
            PERFORM set_config('cloud.ligne_remplacee_operation', 'expurger', true);
            UPDATE ligne_remplacee l
            SET ancienne = l.ancienne - x.cols,
                colonnes_effacees = ARRAY(SELECT DISTINCT y FROM unnest(coalesce(l.colonnes_effacees, '{}') || x.cols) y ORDER BY 1),
                expurgee_le = now()
            FROM (SELECT f.nom_table, array_agg(f.colonne ORDER BY f.colonne) AS cols
                  FROM cloud_famille_colonne f JOIN cloud_table_miroir m ON m.nom_table = f.nom_table AND f.colonne = ANY (m.colonnes)
                  WHERE f.famille = p_famille GROUP BY f.nom_table) x
            WHERE l.officine_id = o.id AND l.nom_table = x.nom_table AND l.ancienne ?| x.cols;
            PERFORM set_config('cloud.ligne_remplacee_operation', '', true);
            PERFORM set_config('cloud.lot_recu_operation', 'expurger', true);
            WITH u AS (
                UPDATE lot_recu l
                SET contenu = convert_to(cloud_lot_sans_colonnes(convert_from(l.contenu, 'UTF8')::jsonb, v_cols)::text, 'UTF8'),
                    colonnes_effacees = ARRAY(SELECT DISTINCT x FROM unnest(coalesce(l.colonnes_effacees, '{}') || v_cols) x ORDER BY 1),
                    expurge_le = now()
                WHERE l.officine_id = o.id AND cardinality(cloud_colonnes_presentes(convert_from(l.contenu, 'UTF8')::jsonb, v_cols)) > 0
                RETURNING 1)
            SELECT count(*) INTO v_lots FROM u;
            PERFORM set_config('cloud.lot_recu_operation', '', true);
            INSERT INTO journal_securite (officine_id, evenement, detail)
            VALUES (o.id, 'famille_effacee', left(format('famille %s : %s valeur(s) effacée(s) des tables, %s valeur(s) des lignes archivées (ligne_remplacee), %s lot(s) brut(s) expurgé(s)',
                                                         p_famille, v_total, v_archives, v_lots), 500));
        ELSE
            SELECT count(*) INTO v_lots FROM lot_recu l
            WHERE l.officine_id = o.id AND cardinality(cloud_colonnes_presentes(convert_from(l.contenu, 'UTF8')::jsonb, v_cols)) > 0;
        END IF;
        RETURN QUERY SELECT o.id, NULL::text, NULL::text, NULL::bigint, v_lots;
    END LOOP;
    PERFORM set_config('app.officine_id', '', true);
END $$;
REVOKE EXECUTE ON FUNCTION cloud_effacer_famille(text, uuid, boolean) FROM PUBLIC;
