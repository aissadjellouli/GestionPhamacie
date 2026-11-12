-- =====================================================================
-- Alertes de péremption qui ont un sens
--   * Lot qui approche de sa date (seuil orange ou rouge) mais dont toutes
--     les boîtes seront vendues à temps au rythme des ventes : aucune
--     alerte, ni dans le logiciel ni par e-mail (l'alerte déjà ouverte est
--     close d'elle-même au calcul suivant).
--   * Boîtes qui ne seront pas vendues à temps : combien, pour quelle
--     valeur au prix d'achat, et quoi faire (mettre en avant, retourner au
--     grossiste, proposer à la place d'un équivalent).
--   * Lot périmé : inchangé (urgent, boîtes à retirer du rayon).
-- Même fonction qu'en 053, seuls la sélection et les messages des alertes de
-- péremption changent.
-- Idempotent. A exécuter après 065_stock_vendable_hors_bloque.sql.
-- =====================================================================

SET client_encoding = 'UTF8';

CREATE OR REPLACE FUNCTION generer_alertes() RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
    nb integer;
BEGIN
    -- Les alertes reportées dont le délai est passé redeviennent visibles
    UPDATE alerte SET statut = 'nouvelle', mise_a_jour_le = now()
    WHERE statut = 'reportee' AND reportee_jusqu_au <= current_date;

    DROP TABLE IF EXISTS _candidats;
    CREATE TEMP TABLE _candidats ON COMMIT DROP AS

    -- Péremption : une alerte seulement s'il y a quelque chose à faire (lot périmé, boîtes qui ne seront pas vendues à temps) ;
    -- le message dit pourquoi et quoi faire. Lot dont toutes les boîtes seront vendues à temps : aucune alerte
    SELECT CASE r.niveau
               WHEN 'perime'            THEN 'perime'
               WHEN 'risque_ecoulement' THEN 'risque_ecoulement'
               ELSE 'peremption'
           END AS type,
           CASE r.niveau
               WHEN 'perime' THEN 'urgent'
               WHEN 'rouge'  THEN 'urgent'
               ELSE 'attention'
           END AS niveau,
           r.produit_id,
           r.lot_id,
           CASE
               WHEN r.niveau = 'perime' THEN format('%s, lot %s : PÉRIMÉ depuis le %s. %s boîte(s) à retirer du rayon.',
                                  r.nom_commercial, r.numero_lot,
                                  to_char(r.date_peremption, 'DD/MM/YYYY'), r.quantite_disponible)
               ELSE format('%s, lot %s : expire le %s (dans %s j). Au rythme des ventes, %s boîte(s) sur %s ne seront pas vendues à temps (%s DA au prix d''achat). À faire : les mettre en avant, les retourner au grossiste ou les proposer à la place d''un équivalent.',
                           r.nom_commercial, r.numero_lot,
                           to_char(r.date_peremption, 'DD/MM/YYYY'), r.jours_restants,
                           r.qte_a_risque, r.quantite_disponible, to_char(r.valeur_a_risque, 'FM999G999G990D00'))
           END AS message,
           r.valeur_a_risque AS valeur_dzd
    FROM v_risque_peremption r
    WHERE r.niveau = 'perime' OR (r.niveau <> 'ok' AND r.qte_a_risque > 0)

    UNION ALL

    -- État du stock
    SELECT CASE e.statut
               WHEN 'rupture'  THEN 'rupture'
               WHEN 'critique' THEN 'stock_critique'
               WHEN 'dormant'  THEN 'dormant'
               ELSE 'surstock'
           END,
           CASE e.statut
               WHEN 'rupture'  THEN 'urgent'
               WHEN 'critique' THEN 'attention'
               ELSE 'info'
           END,
           e.produit_id,
           NULL::bigint,
           CASE e.statut
               WHEN 'rupture'  THEN format('%s en rupture.%s Commande suggérée : %s unités.',
                                    e.nom_commercial,
                                    CASE WHEN e.en_tension THEN ' Produit en tension nationale.' ELSE '' END,
                                    e.qte_suggeree)
               WHEN 'critique' THEN format('%s : stock %s, sous le seuil de %s. %s Commande suggérée : %s unités.',
                                    e.nom_commercial, e.stock, e.seuil_alerte,
                                    CASE WHEN e.couverture_jours IS NULL THEN 'Aucune vente récente.'
                                         ELSE format('Couverture : %s jours.', e.couverture_jours) END,
                                    e.qte_suggeree)
               WHEN 'dormant'  THEN format('%s : %s unités sans vente récente.',
                                    e.nom_commercial, e.stock)
               ELSE format('%s : %s jours de stock, surstock.',
                           e.nom_commercial, e.couverture_jours)
           END,
           NULL::numeric
    FROM v_etat_stock e
    WHERE e.statut <> 'ok'

    UNION ALL

    -- Ancien lot délaissé au profit d'un lot plus récent (alerte réservée au titulaire)
    SELECT * FROM lots_delaisses(NULL);

    -- Créer les nouvelles alertes ; mettre à jour celles qui existent.
    -- Si le niveau s'aggrave, l'alerte redevient « nouvelle » même si elle était reportée.
    INSERT INTO alerte AS a (type, niveau, produit_id, lot_id, message, valeur_dzd)
    SELECT type, niveau, produit_id, lot_id, message, valeur_dzd FROM _candidats
    ON CONFLICT (type, produit_id, COALESCE(lot_id, 0))
        WHERE statut IN ('nouvelle', 'vue', 'reportee')
    DO UPDATE SET
        message        = EXCLUDED.message,
        valeur_dzd     = EXCLUDED.valeur_dzd,
        statut         = CASE WHEN a.niveau <> EXCLUDED.niveau THEN 'nouvelle' ELSE a.statut END,
        niveau         = EXCLUDED.niveau,
        mise_a_jour_le = now();

    GET DIAGNOSTICS nb = ROW_COUNT;

    -- Clôture automatique des alertes dont la cause a disparu
    UPDATE alerte a
    SET statut = 'resolue', traitee_le = now(), mise_a_jour_le = now()
    WHERE a.statut IN ('nouvelle', 'vue', 'reportee')
      AND NOT EXISTS (
          SELECT 1 FROM _candidats c
          WHERE c.type = a.type
            AND c.produit_id = a.produit_id
            AND COALESCE(c.lot_id, 0) = COALESCE(a.lot_id, 0)
      );

    RETURN nb;
END;
$$;
