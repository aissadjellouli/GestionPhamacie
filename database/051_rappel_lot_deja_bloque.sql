-- =====================================================================
-- Rappel de lot : un lot déjà bloqué n'est pas rebloqué
--   * ventes_lot(produit, lot) : ventes validées du lot (patients à
--     contacter, ventes anonymes), en lecture seule : sert au rappel et à
--     la consultation d'un lot déjà rappelé ;
--   * rappel_lot ne bloque plus que les lots encore vendables de ce numéro
--     (ex. nouvelle livraison chez un autre grossiste) : le motif du
--     premier blocage est gardé et les boîtes déjà bloquées ne reçoivent
--     pas un second événement de blocage.
-- Idempotent. A exécuter après 050_ged.sql.
-- =====================================================================

CREATE OR REPLACE FUNCTION ventes_lot(p_produit_id bigint, p_numero_lot text)
RETURNS TABLE (o_patient text, o_telephone text, o_date_vente timestamptz,
               o_ticket text, o_boites bigint)
LANGUAGE sql STABLE AS $$
    -- Aussi les ventes d'avant l'étiquetage par boîte
    SELECT COALESCE(NULLIF(btrim(COALESCE(pt.nom, '') || ' ' || COALESCE(pt.prenom, '')), ''),
                    'Vente anonyme'),
           pt.telephone, v.date_vente, v.numero, SUM(lv.quantite)::bigint
    FROM ligne_vente lv
    JOIN vente v     ON v.id = lv.vente_id
    JOIN lot l       ON l.id = lv.lot_id
    LEFT JOIN patient pt ON pt.id = v.patient_id
    WHERE l.produit_id = p_produit_id AND l.numero_lot = p_numero_lot
      AND v.statut = 'validee'
    GROUP BY pt.nom, pt.prenom, pt.telephone, v.date_vente, v.numero
    ORDER BY v.date_vente DESC;
$$;

COMMENT ON FUNCTION ventes_lot(bigint, text) IS 'Ventes validées d''un lot (patients à contacter) : rappel et consultation d''un lot rappelé';

CREATE OR REPLACE FUNCTION rappel_lot(
    p_produit_id bigint, p_numero_lot text, p_motif text, p_utilisateur_id bigint)
RETURNS TABLE (o_patient text, o_telephone text, o_date_vente timestamptz,
               o_ticket text, o_boites bigint)
LANGUAGE plpgsql AS $$
BEGIN
    -- Bloquer le lot chez tous les grossistes où il est encore vendable,
    -- et tracer le blocage des boîtes en stock de ces lots
    WITH bloques AS (
        UPDATE lot SET bloque = true, motif_blocage = p_motif
        WHERE produit_id = p_produit_id AND numero_lot = p_numero_lot AND NOT bloque
        RETURNING id
    )
    INSERT INTO boite_evenement (boite_id, type, utilisateur_id, commentaire)
    SELECT b.id, 'blocage', p_utilisateur_id, p_motif
    FROM boite b JOIN bloques l ON l.id = b.lot_id
    WHERE b.statut = 'en_stock';

    RETURN QUERY SELECT * FROM ventes_lot(p_produit_id, p_numero_lot);
END;
$$;
