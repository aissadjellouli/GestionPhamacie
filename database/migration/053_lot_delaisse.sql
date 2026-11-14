-- =====================================================================
-- Ancien lot délaissé : alerte au titulaire et inventaire du produit
--   * ligne_vente.raison_lot : pourquoi un lot plus récent a été servi
--     alors qu'un lot plus ancien du produit est en stock :
--     peremption (vérifiée par le logiciel : le lot servi périme avant),
--     introuvable, abimee, demande_client ;
--   * situation_lot(lot) : aucune (rien à justifier), peremption (raison
--     vérifiée, à valider par le vendeur) ou obligatoire (raison à choisir) ;
--   * lots_delaisses(produit) : ancien lot A en stock, plus vendu depuis
--     l'arrivée d'un lot B plus récent, dont un même vendeur a vendu au
--     moins parametre_alerte.seuil_lot_delaisse boîtes (5 par défaut),
--     quelle que soit la raison sauf « peremption », ou dont plusieurs
--     vendeurs ont vendu ensemble au moins seuil_lot_delaisse_collectif
--     boîtes (10 par défaut) ; urgent dans ce second cas, si une vente
--     porte « introuvable » ou si le lot A approche de sa péremption ;
--     le compte repart à zéro quand le titulaire traite l'alerte ;
--   * alerte de type lot_delaisse, réservée au titulaire (elle nomme un
--     vendeur), calculée par generer_alertes et après chaque vente
--     (alerter_lots_delaisses), notifiée par SMS ou e-mail ;
--   * inventaire.produit_id : inventaire d'un seul produit (tous ses lots).
-- Idempotent. A exécuter après 052_reprise_super_admin.sql.
-- =====================================================================

-- 1. Raison du lot servi
ALTER TABLE ligne_vente ADD COLUMN IF NOT EXISTS raison_lot text;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_ligne_vente_raison_lot') THEN
        ALTER TABLE ligne_vente ADD CONSTRAINT ck_ligne_vente_raison_lot
            CHECK (raison_lot IS NULL OR raison_lot IN ('peremption', 'introuvable', 'abimee', 'demande_client'));
    END IF;
END $$;
COMMENT ON COLUMN ligne_vente.raison_lot IS 'Lot plus récent servi alors qu''un lot plus ancien est en stock : peremption (vérifiée), introuvable, abimee, demande_client';

-- 2. Seuil de l'alerte
ALTER TABLE parametre_alerte ADD COLUMN IF NOT EXISTS seuil_lot_delaisse integer NOT NULL DEFAULT 5;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_parametre_alerte_lot_delaisse') THEN
        ALTER TABLE parametre_alerte ADD CONSTRAINT ck_parametre_alerte_lot_delaisse CHECK (seuil_lot_delaisse >= 1);
    END IF;
END $$;
COMMENT ON COLUMN parametre_alerte.seuil_lot_delaisse IS 'Boîtes d''un lot plus récent vendues par un même vendeur, l''ancien lot n''étant plus vendu, avant l''alerte lot_delaisse';
ALTER TABLE parametre_alerte ADD COLUMN IF NOT EXISTS seuil_lot_delaisse_collectif integer NOT NULL DEFAULT 10;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_parametre_alerte_lot_delaisse_collectif') THEN
        ALTER TABLE parametre_alerte ADD CONSTRAINT ck_parametre_alerte_lot_delaisse_collectif CHECK (seuil_lot_delaisse_collectif >= 1);
    END IF;
END $$;
COMMENT ON COLUMN parametre_alerte.seuil_lot_delaisse_collectif IS 'Boîtes d''un lot plus récent vendues par plusieurs vendeurs ensemble, l''ancien lot n''étant plus vendu, avant l''alerte lot_delaisse urgente';

-- 3. Type d'alerte et de notification
-- Rejouable sur une base plus récente : la liste n'est refaite que si elle n'a pas encore « lot_delaisse »
DO $garde$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'alerte_type_check' AND pg_get_constraintdef(oid) LIKE '%''lot_delaisse''%') THEN
        ALTER TABLE alerte DROP CONSTRAINT IF EXISTS alerte_type_check;
        ALTER TABLE alerte ADD CONSTRAINT alerte_type_check CHECK (type = ANY (ARRAY['peremption', 'perime', 'risque_ecoulement', 'rupture',
            'stock_critique', 'dormant', 'surstock', 'lot_delaisse']));
    END IF;
END $garde$;

-- Rejouable sur une base plus récente : la liste n'est refaite que si elle n'a pas encore « lot_delaisse »
-- Les destinataires des alertes de lot reçoivent aussi celle-ci : une seule fois, quand le type est ajouté
-- (un rejeu ne recoche pas le type retiré ensuite par le titulaire)
DO $garde$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'destinataire_notification_types_check' AND pg_get_constraintdef(oid) LIKE '%''lot_delaisse''%') THEN
        ALTER TABLE destinataire_notification DROP CONSTRAINT IF EXISTS destinataire_notification_types_check;
        ALTER TABLE destinataire_notification ADD CONSTRAINT destinataire_notification_types_check
            CHECK (types <@ ARRAY['rapport_quotidien', 'ecart_caisse', 'lot_bloque', 'sauvegarde', 'ecart_inventaire', 'lot_delaisse']);
        UPDATE destinataire_notification SET types = types || 'lot_delaisse'::text
        WHERE 'lot_bloque' = ANY (types) AND NOT 'lot_delaisse' = ANY (types);
    END IF;
END $garde$;
-- Rejouable sur une base plus récente : la liste n'est refaite que si elle n'a pas encore « lot_delaisse »
DO $garde$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'message_sortant_type_check' AND pg_get_constraintdef(oid) LIKE '%''lot_delaisse''%') THEN
        ALTER TABLE message_sortant DROP CONSTRAINT IF EXISTS message_sortant_type_check;
        ALTER TABLE message_sortant ADD CONSTRAINT message_sortant_type_check
            CHECK (type = ANY (ARRAY['rapport_quotidien', 'ecart_caisse', 'lot_bloque', 'sauvegarde', 'ecart_inventaire', 'lot_delaisse', 'test']));
    END IF;
END $garde$;

-- 4. Inventaire d'un produit
ALTER TABLE inventaire ADD COLUMN IF NOT EXISTS produit_id bigint REFERENCES produit (id);
COMMENT ON COLUMN inventaire.produit_id IS 'Inventaire d''un seul produit (tous ses lots), demandé par une alerte ; NULL : inventaire d''un rayon';

-- 5. Raison à demander pour un lot servi
CREATE OR REPLACE FUNCTION situation_lot(p_lot_id bigint)
RETURNS TABLE (o_situation text, o_lot_delaisse_id bigint)
LANGUAGE sql STABLE AS $$
    WITH l AS (
        SELECT lt.id, lt.produit_id, lt.date_peremption, lt.date_a_verifier, r.date_reception
        FROM lot lt JOIN reception r ON r.id = lt.reception_id
        WHERE lt.id = p_lot_id
    ),
    -- Autres lots vendables du produit, reçus avant ou périmant avant le lot servi
    autres AS (
        SELECT a.id, a.date_peremption, a.date_a_verifier
        FROM l
        JOIN lot a ON a.produit_id = l.produit_id AND a.id <> l.id
        JOIN reception ra ON ra.id = a.reception_id
        WHERE NOT a.bloque AND a.quantite_disponible > 0 AND a.date_peremption > current_date
          AND (ra.date_reception < l.date_reception OR a.date_peremption < l.date_peremption)
    )
    SELECT CASE
               WHEN NOT EXISTS (SELECT 1 FROM autres) THEN 'aucune'
               -- Raison vérifiée sur les dates enregistrées : le lot servi périme avant tous les autres
               WHEN NOT l.date_a_verifier
                    AND NOT EXISTS (SELECT 1 FROM autres WHERE date_a_verifier OR date_peremption <= l.date_peremption) THEN 'peremption'
               ELSE 'obligatoire'
           END,
           (SELECT id FROM autres ORDER BY date_peremption, id LIMIT 1)
    FROM l;
$$;

-- 6. Anciens lots délaissés (candidats d'alerte)
CREATE OR REPLACE FUNCTION lots_delaisses(p_produit_id bigint DEFAULT NULL)
RETURNS TABLE (o_type text, o_niveau text, o_produit_id bigint, o_lot_id bigint, o_message text, o_valeur_dzd numeric)
LANGUAGE sql STABLE AS $$
    WITH p AS (SELECT seuil_lot_delaisse AS seuil, seuil_lot_delaisse_collectif AS collectif, jours_rouge FROM parametre_alerte WHERE id = 1),
    lots AS (
        SELECT l.id, l.produit_id, l.numero_lot, l.date_peremption, l.quantite_disponible, l.prix_achat_ht, l.bloque, r.date_reception
        FROM lot l JOIN reception r ON r.id = l.reception_id
        WHERE p_produit_id IS NULL OR l.produit_id = p_produit_id
    ),
    -- Ancien lot A, encore en stock et vendable
    delaisses AS (
        SELECT a.*,
               (SELECT max(t.traitee_le) FROM alerte t
                WHERE t.type = 'lot_delaisse' AND t.lot_id = a.id AND t.statut = 'traitee') AS traitee_le
        FROM lots a
        WHERE a.quantite_disponible > 0 AND a.date_peremption > current_date AND NOT a.bloque
    ),
    -- Lot B plus récent (reçu après, ou périmant après) qui ne périme pas avant A ; compté depuis l'arrivée
    -- des deux lots, ou depuis la dernière alerte traitée par le titulaire
    paires AS (
        SELECT a.id AS a_id, b.id AS b_id,
               greatest(a.date_reception, b.date_reception, a.traitee_le) AS depuis
        FROM delaisses a
        JOIN lots b ON b.produit_id = a.produit_id AND b.id <> a.id
        WHERE (b.date_reception > a.date_reception OR b.date_peremption > a.date_peremption)
          AND b.date_peremption >= a.date_peremption
    ),
    ventes AS (
        SELECT pa.a_id, v.utilisateur_id, sum(lv.quantite)::int AS boites, bool_or(lv.raison_lot = 'introuvable') AS introuvable
        FROM paires pa
        JOIN ligne_vente lv ON lv.lot_id = pa.b_id
        JOIN vente v ON v.id = lv.vente_id AND v.statut = 'validee' AND v.date_vente >= pa.depuis
        WHERE lv.raison_lot IS DISTINCT FROM 'peremption'
          -- Aucune boîte de A vendue depuis
          AND NOT EXISTS (SELECT 1 FROM ligne_vente la JOIN vente va ON va.id = la.vente_id
                          WHERE la.lot_id = pa.a_id AND va.statut = 'validee' AND va.date_vente >= pa.depuis)
        GROUP BY pa.a_id, v.utilisateur_id
    ),
    -- Par ancien lot : seuil d'un vendeur atteint, ou seuil collectif atteint par plusieurs vendeurs
    par_lot AS (
        SELECT ve.a_id, max(ve.boites) AS max_vendeur, sum(ve.boites) AS total, count(*) AS vendeurs, bool_or(ve.introuvable) AS introuvable
        FROM ventes ve GROUP BY ve.a_id
    ),
    retenus AS (
        SELECT pl.*, (pl.vendeurs >= 2 AND pl.total >= p.collectif) AS collectif
        FROM par_lot pl, p
        WHERE pl.max_vendeur >= p.seuil OR (pl.vendeurs >= 2 AND pl.total >= p.collectif)
    )
    SELECT 'lot_delaisse'::text,
           CASE WHEN rt.collectif OR rt.introuvable OR a.date_peremption <= current_date + (SELECT jours_rouge FROM p) THEN 'urgent' ELSE 'attention' END,
           a.produit_id,
           a.id,
           format('%s, lot %s : %s boîte(s) en stock, plus vendues depuis l''arrivée d''un lot plus récent. %s%s. Risque de péremption ou de perte des boîtes du lot %s : faites l''inventaire de ce produit.',
                  pr.nom_commercial, a.numero_lot, a.quantite_disponible,
                  CASE WHEN rt.collectif THEN format('%s vendeurs ont vendu ensemble %s boîte(s) d''un lot plus récent : ', rt.vendeurs, rt.total) ELSE '' END,
                  (SELECT string_agg(format('%s a vendu %s boîte(s) d''un lot plus récent%s', u.nom_complet, ve.boites,
                                            CASE WHEN ve.introuvable THEN ' (dont « boîte de l''ancien lot introuvable »)' ELSE '' END),
                                     ' ; ' ORDER BY ve.boites DESC)
                   FROM ventes ve JOIN utilisateur u ON u.id = ve.utilisateur_id WHERE ve.a_id = a.id),
                  a.numero_lot),
           round(a.quantite_disponible * a.prix_achat_ht, 2)
    FROM retenus rt
    JOIN delaisses a ON a.id = rt.a_id
    JOIN produit pr ON pr.id = a.produit_id;
$$;

-- 7. Après une vente : alertes des produits vendus (création, ou passage à « urgent ») ; renvoie celles à notifier
CREATE OR REPLACE FUNCTION alerter_lots_delaisses(p_produits bigint[])
RETURNS TABLE (o_alerte_id bigint, o_niveau text, o_message text)
LANGUAGE plpgsql AS $$
DECLARE
    c        record;
    v_id     bigint;
    v_ancien text;
BEGIN
    FOR c IN SELECT d.* FROM unnest(p_produits) AS u(pid), LATERAL lots_delaisses(u.pid) d LOOP
        v_id := NULL;
        SELECT a.id, a.niveau INTO v_id, v_ancien FROM alerte a
        WHERE a.type = c.o_type AND a.produit_id = c.o_produit_id AND a.lot_id = c.o_lot_id
          AND a.statut IN ('nouvelle', 'vue', 'reportee');
        IF v_id IS NULL THEN
            INSERT INTO alerte (type, niveau, produit_id, lot_id, message, valeur_dzd)
            VALUES (c.o_type, c.o_niveau, c.o_produit_id, c.o_lot_id, c.o_message, c.o_valeur_dzd)
            RETURNING id INTO v_id;
            o_alerte_id := v_id; o_niveau := c.o_niveau; o_message := c.o_message;
            RETURN NEXT;
        ELSE
            UPDATE alerte SET message = c.o_message, valeur_dzd = c.o_valeur_dzd, niveau = c.o_niveau,
                              statut = CASE WHEN alerte.niveau <> c.o_niveau THEN 'nouvelle' ELSE alerte.statut END,
                              mise_a_jour_le = now()
            WHERE alerte.id = v_id;
            IF v_ancien <> c.o_niveau AND c.o_niveau = 'urgent' THEN
                o_alerte_id := v_id; o_niveau := c.o_niveau; o_message := c.o_message;
                RETURN NEXT;
            END IF;
        END IF;
    END LOOP;
END;
$$;

-- 8. Calcul général des alertes : anciens lots délaissés en plus (sinon ces alertes seraient closes à chaque calcul)
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

    -- Péremption
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
           CASE r.niveau
               WHEN 'perime' THEN format('%s, lot %s : PÉRIMÉ depuis le %s, %s unités à retirer du rayon.',
                                  r.nom_commercial, r.numero_lot,
                                  to_char(r.date_peremption, 'DD/MM/YYYY'), r.quantite_disponible)
               ELSE format('%s, lot %s : expire le %s (%s j). %s unités sur %s risquent de ne pas être vendues.',
                           r.nom_commercial, r.numero_lot,
                           to_char(r.date_peremption, 'DD/MM/YYYY'), r.jours_restants,
                           r.qte_a_risque, r.quantite_disponible)
           END AS message,
           r.valeur_a_risque AS valeur_dzd
    FROM v_risque_peremption r
    WHERE r.niveau <> 'ok'

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
