-- =====================================================================
-- Base centrale : correctif du défaut D-M8-1 (2026-11-17, lot M8 de la mutualisation,
-- docs/conception-synchronisation.md § 16 bis.12).
-- Script de la base CENTRALE. Idempotent ; à passer après 010.
--   * Défaut : cloud_descendants (script 010) écrivait « ajouts_locaux » dans l'élément mutu-v1 de TOUTES les tables ;
--     le format mutu-v1 (Pharma.Shared, ValidationModele) l'interdit hors motif_reglable et remise_automatique, donc l'officine
--     rejetait les 9 autres tables.
--   * Correctif : cloud_descendants redéfinie à l'identique, sauf « ajouts_locaux » ajouté seulement pour ces deux tables.
--     Un élément déjà publié avec l'ancien format change : il est republié une fois (nouvelle version descendante) au prochain échange.
--   * Le script 010 a une garde : rejoué après 011, il ne ramène pas l'ancienne version.
--   * Droits inchangés (sans EXECUTE pour PUBLIC, EXECUTE pour pharma_cloud_api).
-- =====================================================================

CREATE OR REPLACE FUNCTION cloud_descendants(p_apres bigint)
RETURNS TABLE (nom_table text, cle text, version bigint, donnees jsonb, version_max bigint)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v_off uuid := cloud_officine_courante(); v_max bigint; v_n bigint; v_liberee boolean; m record;
BEGIN
    IF v_off IS NULL THEN RAISE EXCEPTION 'Officine courante non posée (app.officine_id)'; END IF;
    PERFORM cloud_publier_descendant('reglage_central', 'familles_envoyees',
        jsonb_build_object('familles', to_jsonb(cloud_familles_envoyees(v_off))));
    -- Modèles de réglages publiés (D12 : rien tant que l'administrateur n'a pas publié) ; officine libérée : mode « local »
    SELECT x.mutualisation_liberee_le IS NOT NULL INTO v_liberee FROM officine x WHERE x.id = v_off;
    FOR m IN SELECT r.* FROM cloud_reglage_modele r WHERE r.perimetre = 'tous' AND r.valeur IS NOT NULL ORDER BY r.nom_table LOOP
        PERFORM cloud_publier_descendant('reglage_central', 'mutu_' || m.nom_table,
            CASE WHEN coalesce(v_liberee, false)
                 THEN jsonb_build_object('format', 'mutu-v1', 'mode', 'local', 'version_modele', m.version_modele)
                 ELSE jsonb_build_object('format', 'mutu-v1', 'mode', m.mode, 'version_modele', m.version_modele,
                                         'publie_le', to_char(m.modifie_le AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
                                         'auteur', m.modifie_par, 'valeur', m.valeur)
                      -- « ajouts_locaux » seulement pour les motifs et les remises : le format mutu-v1 l'interdit ailleurs
                      || CASE WHEN m.nom_table IN ('motif_reglable', 'remise_automatique')
                              THEN jsonb_build_object('ajouts_locaux', m.ajouts_locaux) ELSE '{}'::jsonb END END);
    END LOOP;
    SELECT o.version_max INTO v_max FROM officine_descendant o WHERE o.officine_id = v_off;
    IF p_apres IS NOT NULL AND p_apres > v_max AND p_apres <= 1000000000000 THEN
        -- Serveur central restauré : l'officine a vu des versions que ce serveur ne connaît plus. Tout est republié au-dessus.
        WITH o AS (SELECT x.nom_table AS t, x.cle AS c, row_number() OVER (ORDER BY x.version, x.nom_table, x.cle) AS rn
                   FROM descendant x WHERE x.officine_id = v_off)
        UPDATE descendant d SET version = p_apres + o.rn, modifie_le = now() FROM o
        WHERE d.officine_id = v_off AND d.nom_table = o.t AND d.cle = o.c;
        GET DIAGNOSTICS v_n = ROW_COUNT;
        UPDATE officine_descendant o SET version_max = p_apres + v_n WHERE o.officine_id = v_off;
        PERFORM cloud_journaliser(v_off, NULL, 'descendant_remonte', 'version descendante de l''officine supérieure à celle du serveur central : versions remontées');
        v_max := p_apres + v_n;
    END IF;
    IF p_apres IS NOT NULL AND p_apres BETWEEN 0 AND v_max THEN
        UPDATE officine_descendant o SET version_acquittee = greatest(o.version_acquittee, p_apres), acquittee_le = now()
        WHERE o.officine_id = v_off AND o.version_acquittee < p_apres;
    END IF;
    RETURN QUERY SELECT d.nom_table, d.cle, d.version, d.donnees, v_max FROM descendant d
                 WHERE d.officine_id = v_off AND d.version > coalesce(p_apres, 0) ORDER BY d.version;
END $$;
REVOKE EXECUTE ON FUNCTION cloud_descendants(bigint) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_descendants(bigint) TO pharma_cloud_api;
