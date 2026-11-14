-- =====================================================================
-- Rappel de lot, lot délaissé et produit à vérifier
--   * ventes_lot(produit, lot) : boîtes vendues moins les boîtes rendues
--     par le patient (retour client, remises en stock ou détruites) ; une
--     vente entièrement rendue n'est plus dans la liste des patients à
--     contacter. Mêmes paramètres et mêmes colonnes ;
--   * lots_delaisses(produit) : les ventes du lot récent se comptent à
--     partir de la dernière vente validée d'une boîte de l'ancien lot (au
--     lieu d'écarter le lot dès qu'une de ses boîtes a été vendue après
--     l'arrivée du lot récent : jamais détecté ensuite). Mêmes colonnes ;
--   * produit.a_verifier : aussi posé par le logiciel quand un employé
--     (pas le titulaire) crée un produit, ou lui donne une DCI, de
--     substance surveillée en classe hors registre : la classe de la
--     substance est imposée et le produit attend la confirmation du
--     titulaire (commentaire de la colonne mis à jour).
-- La balance mensuelle du registre des psychotropes (solde reporté, lignes
-- antérieures à l'ouverture exclues) est calculée par le logiciel :
-- v_registre_psychotrope est inchangée.
-- Idempotent. A exécuter après 074_tresorerie_traites.sql.
-- =====================================================================

-- 1. Patients à contacter d'un lot rappelé : boîtes rendues déduites
CREATE OR REPLACE FUNCTION ventes_lot(p_produit_id bigint, p_numero_lot text)
RETURNS TABLE (o_patient text, o_telephone text, o_date_vente timestamptz,
               o_ticket text, o_boites bigint)
LANGUAGE sql STABLE AS $$
    -- Aussi les ventes d'avant l'étiquetage par boîte ; une vente dont toutes les boîtes du lot ont été rendues est masquée
    SELECT COALESCE(NULLIF(btrim(COALESCE(pt.nom, '') || ' ' || COALESCE(pt.prenom, '')), ''),
                    'Vente anonyme'),
           pt.telephone, v.date_vente, v.numero, SUM(greatest(lv.quantite - COALESCE(rd.rendues, 0), 0))::bigint
    FROM ligne_vente lv
    JOIN vente v     ON v.id = lv.vente_id
    JOIN lot l       ON l.id = lv.lot_id
    LEFT JOIN patient pt ON pt.id = v.patient_id
    LEFT JOIN LATERAL (SELECT sum(rl.quantite) AS rendues
                       FROM retour_client_ligne rl WHERE rl.ligne_vente_id = lv.id) rd ON true
    WHERE l.produit_id = p_produit_id AND l.numero_lot = p_numero_lot
      AND v.statut = 'validee'
    GROUP BY pt.nom, pt.prenom, pt.telephone, v.date_vente, v.numero
    HAVING SUM(greatest(lv.quantite - COALESCE(rd.rendues, 0), 0)) > 0
    ORDER BY v.date_vente DESC;
$$;

COMMENT ON FUNCTION ventes_lot(bigint, text) IS 'Ventes validées d''un lot (patients à contacter), boîtes rendues déduites : rappel et consultation d''un lot rappelé';

-- 2. Anciens lots délaissés : ventes du lot récent comptées depuis la dernière vente de l'ancien lot
CREATE OR REPLACE FUNCTION lots_delaisses(p_produit_id bigint DEFAULT NULL)
RETURNS TABLE (o_type text, o_niveau text, o_produit_id bigint, o_lot_id bigint, o_message text, o_valeur_dzd numeric)
LANGUAGE sql STABLE AS $$
    WITH p AS (SELECT seuil_lot_delaisse AS seuil, seuil_lot_delaisse_collectif AS collectif, jours_rouge FROM parametre_alerte WHERE id = 1),
    lots AS (
        SELECT l.id, l.produit_id, l.numero_lot, l.date_peremption, l.quantite_disponible, l.prix_achat_ht, l.bloque, r.date_reception
        FROM lot l JOIN reception r ON r.id = l.reception_id
        WHERE p_produit_id IS NULL OR l.produit_id = p_produit_id
    ),
    -- Ancien lot A, encore en stock et vendable ; dernière vente validée d'une de ses boîtes
    delaisses AS (
        SELECT a.*,
               (SELECT max(t.traitee_le) FROM alerte t
                WHERE t.type = 'lot_delaisse' AND t.lot_id = a.id AND t.statut = 'traitee') AS traitee_le,
               (SELECT max(va.date_vente) FROM ligne_vente la JOIN vente va ON va.id = la.vente_id
                WHERE la.lot_id = a.id AND va.statut = 'validee') AS derniere_vente
        FROM lots a
        WHERE a.quantite_disponible > 0 AND a.date_peremption > current_date AND NOT a.bloque
    ),
    -- Lot B plus récent (reçu après, ou périmant après) qui ne périme pas avant A ; compté depuis l'arrivée
    -- des deux lots, la dernière alerte traitée par le titulaire ou la dernière vente d'une boîte de A
    paires AS (
        SELECT a.id AS a_id, b.id AS b_id,
               greatest(a.date_reception, b.date_reception, a.traitee_le, a.derniere_vente) AS depuis,
               a.derniere_vente
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
          -- Strictement après la dernière vente de A (une vente qui contient aussi une boîte de A ne compte pas)
          AND (pa.derniere_vente IS NULL OR v.date_vente > pa.derniere_vente)
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
           format('%s, lot %s : %s boîte(s) en stock, délaissées : un lot plus récent est vendu à leur place. %s%s. Risque de péremption ou de perte des boîtes du lot %s : faites l''inventaire de ce produit.',
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

-- 3. Produit à vérifier : aussi la classe imposée par le logiciel
COMMENT ON COLUMN produit.a_verifier IS 'Classe à confirmer par le titulaire, vente refusée d''ici là : produit repris de l''ancien logiciel sans n° d''enregistrement, ou créé (ou passé à une DCI de substance surveillée) par un autre que le titulaire en classe hors registre, classé d''office psychotrope ou stupéfiant';
