-- =====================================================================
-- Surveillance du stock (lutte contre le vol), réservée au titulaire
--   * boîte vue à l'inventaire : boite_evenement « inventaire » (avec
--     inventaire_id) quand une boîte identifiée est scannée au comptage ;
--     une même boîte n'est comptée qu'une fois par inventaire ;
--   * boîtes fantômes : à la fin d'un inventaire, boîtes identifiées en
--     stock d'un lot scanné, jamais scannées pendant ce comptage ;
--   * produits sensibles (produit.sensible) : comptage « Produits
--     sensibles » proposé tous les parametre_surveillance.jours_comptage_sensibles
--     jours (7 par défaut), anomalie s'il est en retard ;
--   * échantillons et gratuités : sortie de stock tracée et nominative
--     (mouvement « echantillon », boîte « donnee »), jamais pour un produit
--     sous registre ;
--   * anomalies par employé et par jour : boîtes manquantes ajustées en
--     inventaire, casses, échantillons, au-delà des seuils.
-- Idempotent. A exécuter après 054_surveillance_caisse.sql.
-- =====================================================================

-- 1. Boîte vue à l'inventaire, boîte donnée (échantillon)
ALTER TABLE boite_evenement ADD COLUMN IF NOT EXISTS inventaire_id bigint REFERENCES inventaire (id);
-- Rejouable sur une base plus récente : la liste n'est refaite que si elle n'a pas encore « echantillon »
DO $garde$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'boite_evenement_type_check' AND pg_get_constraintdef(oid) LIKE '%''echantillon''%') THEN
        ALTER TABLE boite_evenement DROP CONSTRAINT IF EXISTS boite_evenement_type_check;
        ALTER TABLE boite_evenement ADD CONSTRAINT boite_evenement_type_check CHECK (type = ANY (ARRAY['reception', 'vente', 'retour_client',
            'retour_fournisseur', 'destruction', 'blocage', 'correction', 'inventaire', 'echantillon']));
    END IF;
END $garde$;
CREATE UNIQUE INDEX IF NOT EXISTS ux_boite_vue_inventaire ON boite_evenement (boite_id, inventaire_id) WHERE type = 'inventaire';
COMMENT ON COLUMN boite_evenement.inventaire_id IS 'Inventaire où la boîte a été scannée (type inventaire) : boîtes fantômes';

-- Rejouable sur une base plus récente : la liste n'est refaite que si elle n'a pas encore « donnee »
DO $garde$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'boite_statut_check' AND pg_get_constraintdef(oid) LIKE '%''donnee''%') THEN
        ALTER TABLE boite DROP CONSTRAINT IF EXISTS boite_statut_check;
        ALTER TABLE boite ADD CONSTRAINT boite_statut_check CHECK (statut = ANY (ARRAY['en_stock', 'vendue', 'retournee_fournisseur', 'detruite',
            'retour_client', 'donnee']));
    END IF;
END $garde$;

-- Rejouable sur une base plus récente : la liste n'est refaite que si elle n'a pas encore « echantillon »
DO $garde$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'mouvement_stock_type_check' AND pg_get_constraintdef(oid) LIKE '%''echantillon''%') THEN
        ALTER TABLE mouvement_stock DROP CONSTRAINT IF EXISTS mouvement_stock_type_check;
        ALTER TABLE mouvement_stock ADD CONSTRAINT mouvement_stock_type_check CHECK (type = ANY (ARRAY['entree', 'vente', 'retour_client',
            'retour_fournisseur', 'perime', 'casse', 'ajustement_inventaire', 'destruction', 'echantillon']));
    END IF;
END $garde$;

-- 2. Produits sensibles
ALTER TABLE produit ADD COLUMN IF NOT EXISTS sensible boolean NOT NULL DEFAULT false;
COMMENT ON COLUMN produit.sensible IS 'Produit sensible (cher, très demandé, souvent volé) : compté à part, régulièrement (Surveillance)';

-- 3. Seuils
ALTER TABLE parametre_surveillance ADD COLUMN IF NOT EXISTS seuil_ajustements integer NOT NULL DEFAULT 5;
ALTER TABLE parametre_surveillance ADD COLUMN IF NOT EXISTS seuil_casses integer NOT NULL DEFAULT 3;
ALTER TABLE parametre_surveillance ADD COLUMN IF NOT EXISTS seuil_echantillons integer NOT NULL DEFAULT 3;
ALTER TABLE parametre_surveillance ADD COLUMN IF NOT EXISTS jours_comptage_sensibles integer NOT NULL DEFAULT 7;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_parametre_surveillance_stock') THEN
        ALTER TABLE parametre_surveillance ADD CONSTRAINT ck_parametre_surveillance_stock
            CHECK (seuil_ajustements >= 1 AND seuil_casses >= 1 AND seuil_echantillons >= 1 AND jours_comptage_sensibles BETWEEN 1 AND 90);
    END IF;
END $$;

-- 4. Types d'anomalie
-- Rejouable sur une base plus récente : la liste n'est refaite que si elle n'a pas encore « boites_fantomes »
DO $garde$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_alerte_surveillance_type' AND pg_get_constraintdef(oid) LIKE '%''boites_fantomes''%') THEN
        ALTER TABLE alerte_surveillance DROP CONSTRAINT IF EXISTS ck_alerte_surveillance_type;
        ALTER TABLE alerte_surveillance ADD CONSTRAINT ck_alerte_surveillance_type CHECK (type IN ('lignes_retirees', 'tiroir', 'attentes_abandonnees',
            'hors_horaires', 'remises', 'remises_patient', 'ajustements', 'casses', 'echantillons', 'boites_fantomes', 'sensibles_en_retard'));
    END IF;
END $garde$;

-- 5. Anomalies d'un jour (remplace la version de 054)
CREATE OR REPLACE FUNCTION surveillance_candidats(p_jour date)
RETURNS TABLE (o_type text, o_niveau text, o_utilisateur_id bigint, o_jour date, o_cle text, o_message text, o_notification text, o_valeur numeric)
LANGUAGE sql STABLE AS $$
    WITH p AS (SELECT * FROM parametre_surveillance WHERE id = 1),
    ev AS (
        SELECT e.* FROM evenement_caisse e
        WHERE e.cree_le >= p_jour AND e.cree_le < p_jour + 1
    ),
    ventes AS (
        SELECT v.* FROM vente v
        WHERE v.statut = 'validee' AND v.date_vente >= p_jour AND v.date_vente < p_jour + 1
    ),
    -- Boîtes retirées du panier avant le paiement
    retraits AS (
        SELECT ev.utilisateur_id, sum(COALESCE(ev.quantite, 1))::int AS boites, sum(COALESCE(ev.montant, 0)) AS montant, count(*) AS lignes
        FROM ev WHERE ev.type = 'ligne_retiree' GROUP BY ev.utilisateur_id
    ),
    tiroirs AS (
        SELECT ev.utilisateur_id, count(*)::int AS n FROM ev WHERE ev.type = 'tiroir_ouvert' GROUP BY ev.utilisateur_id
    ),
    attentes AS (
        SELECT ev.utilisateur_id, count(*)::int AS n, sum(COALESCE(ev.montant, 0)) AS montant
        FROM ev WHERE ev.type = 'attente_abandonnee' GROUP BY ev.utilisateur_id
    ),
    hors AS (
        SELECT v.utilisateur_id, count(*)::int AS n, sum(v.montant_total) AS montant,
               min(v.date_vente)::time(0) AS premiere, max(v.date_vente)::time(0) AS derniere
        FROM ventes v, p
        WHERE p.heure_ouverture IS NOT NULL
          AND (v.date_vente::time < p.heure_ouverture OR v.date_vente::time >= p.heure_fermeture)
        GROUP BY v.utilisateur_id
    ),
    remises AS (
        SELECT v.utilisateur_id, count(*)::int AS n, sum(v.montant_remise) AS montant
        FROM ventes v WHERE v.remise_pct > 0 GROUP BY v.utilisateur_id
    ),
    -- Remises au même patient sur les 30 jours qui précèdent (une anomalie par patient et par mois)
    remises_patient AS (
        SELECT v.patient_id, count(*)::int AS n, sum(v.montant_remise) AS montant,
               string_agg(DISTINCT u.nom_complet, ', ') AS vendeurs
        FROM vente v JOIN utilisateur u ON u.id = v.utilisateur_id
        WHERE v.statut = 'validee' AND v.remise_pct > 0 AND v.patient_id IS NOT NULL
          AND v.date_vente >= p_jour - 29 AND v.date_vente < p_jour + 1
        GROUP BY v.patient_id
    )
    ,
    -- Inventaire : boîtes manquantes ajustées (stock diminué), par employé qui a terminé ou validé
    ajustements AS (
        SELECT m.utilisateur_id, sum(-m.quantite)::int AS boites, sum(-m.quantite * l.prix_achat_ht) AS valeur
        FROM mouvement_stock m JOIN lot l ON l.id = m.lot_id
        WHERE m.type = 'ajustement_inventaire' AND m.quantite < 0 AND m.cree_le >= p_jour AND m.cree_le < p_jour + 1
        GROUP BY m.utilisateur_id
    ),
    -- Boîtes déclarées cassées, et sorties en échantillon ou gratuité
    sorties AS (
        SELECT m.utilisateur_id, m.type, sum(-m.quantite)::int AS boites, sum(-m.quantite * l.prix_achat_ht) AS valeur
        FROM mouvement_stock m JOIN lot l ON l.id = m.lot_id
        WHERE m.type IN ('casse', 'echantillon') AND m.cree_le >= p_jour AND m.cree_le < p_jour + 1
        GROUP BY m.utilisateur_id, m.type
    ),
    -- Boîtes fantômes : inventaire terminé ce jour, lot dont au moins une boîte a été scannée, boîtes du lot en stock
    -- (entrées avant le comptage) jamais scannées pendant cet inventaire
    inv AS (
        SELECT i.id, i.rayon, i.commence_le FROM inventaire i
        WHERE i.statut = 'termine' AND i.termine_le >= p_jour AND i.termine_le < p_jour + 1
    ),
    lots_scannes AS (
        SELECT DISTINCT e.inventaire_id, b.lot_id
        FROM boite_evenement e JOIN boite b ON b.id = e.boite_id
        WHERE e.type = 'inventaire' AND e.inventaire_id IN (SELECT id FROM inv)
    ),
    fantomes AS (
        SELECT ls.inventaire_id, b.lot_id, count(*)::int AS n
        FROM lots_scannes ls
        JOIN inv ON inv.id = ls.inventaire_id
        JOIN boite b ON b.lot_id = ls.lot_id AND b.statut = 'en_stock' AND b.scannee_le < inv.commence_le
        WHERE NOT EXISTS (SELECT 1 FROM boite_evenement e WHERE e.boite_id = b.id AND e.type = 'inventaire' AND e.inventaire_id = ls.inventaire_id)
        GROUP BY ls.inventaire_id, b.lot_id
    ),
    fantomes_inv AS (
        SELECT f.inventaire_id, sum(f.n)::int AS n, sum(f.n * l.prix_achat_ht) AS valeur,
               string_agg(format('%s lot %s (%s)', pr.nom_commercial, l.numero_lot, f.n), ', ' ORDER BY f.n DESC) AS detail
        FROM fantomes f JOIN lot l ON l.id = f.lot_id JOIN produit pr ON pr.id = l.produit_id
        GROUP BY f.inventaire_id
    ),
    -- Produits sensibles : dernier comptage dépassé
    sensibles AS (
        SELECT (SELECT max(i.termine_le) FROM inventaire i WHERE i.rayon = 'Produits sensibles' AND i.statut = 'termine') AS dernier,
               (SELECT count(*) FROM produit pr WHERE pr.sensible AND pr.actif) AS produits
    )
    SELECT 'lignes_retirees', CASE WHEN r.boites >= 2 * p.seuil_lignes_retirees THEN 'urgent' ELSE 'attention' END,
           r.utilisateur_id, p_jour, '',
           format('%s a retiré du panier %s boîte(s) (%s ligne(s), %s DA) avant le paiement le %s.', u.nom_complet, r.boites, r.lignes,
                  to_char(r.montant, 'FM999G999G990D00'), to_char(p_jour, 'DD/MM/YYYY')),
           format('%s : %s boite(s) retiree(s) du panier avant paiement le %s.', u.nom_complet, r.boites, to_char(p_jour, 'DD/MM')),
           r.montant
    FROM retraits r JOIN utilisateur u ON u.id = r.utilisateur_id, p WHERE r.boites >= p.seuil_lignes_retirees
    UNION ALL
    SELECT 'tiroir', CASE WHEN t.n >= 2 * p.seuil_tiroir THEN 'urgent' ELSE 'attention' END, t.utilisateur_id, p_jour, '',
           format('%s a ouvert le tiroir-caisse sans vente %s fois le %s.', u.nom_complet, t.n, to_char(p_jour, 'DD/MM/YYYY')),
           format('%s : tiroir ouvert sans vente %s fois le %s.', u.nom_complet, t.n, to_char(p_jour, 'DD/MM')),
           t.n
    FROM tiroirs t JOIN utilisateur u ON u.id = t.utilisateur_id, p WHERE t.n >= p.seuil_tiroir
    UNION ALL
    SELECT 'attentes_abandonnees', CASE WHEN a.n >= 2 * p.seuil_attentes_abandonnees THEN 'urgent' ELSE 'attention' END, a.utilisateur_id, p_jour, '',
           format('%s a abandonné %s vente(s) en attente (%s DA) le %s.', u.nom_complet, a.n, to_char(a.montant, 'FM999G999G990D00'), to_char(p_jour, 'DD/MM/YYYY')),
           format('%s : %s vente(s) en attente abandonnee(s) le %s.', u.nom_complet, a.n, to_char(p_jour, 'DD/MM')),
           a.montant
    FROM attentes a JOIN utilisateur u ON u.id = a.utilisateur_id, p WHERE a.n >= p.seuil_attentes_abandonnees
    UNION ALL
    SELECT 'hors_horaires', 'urgent', h.utilisateur_id, p_jour, '',
           format('%s a encaissé %s vente(s) (%s DA) hors des horaires d''ouverture le %s (de %s à %s).', u.nom_complet, h.n,
                  to_char(h.montant, 'FM999G999G990D00'), to_char(p_jour, 'DD/MM/YYYY'), h.premiere, h.derniere),
           format('%s : %s vente(s) hors horaires le %s.', u.nom_complet, h.n, to_char(p_jour, 'DD/MM')),
           h.montant
    FROM hors h JOIN utilisateur u ON u.id = h.utilisateur_id
    UNION ALL
    SELECT 'remises', CASE WHEN r.n >= 2 * p.seuil_remises THEN 'urgent' ELSE 'attention' END, r.utilisateur_id, p_jour, '',
           format('%s a accordé une remise sur %s vente(s) (%s DA de remise) le %s.', u.nom_complet, r.n, to_char(r.montant, 'FM999G999G990D00'), to_char(p_jour, 'DD/MM/YYYY')),
           format('%s : remise sur %s vente(s) le %s.', u.nom_complet, r.n, to_char(p_jour, 'DD/MM')),
           r.montant
    FROM remises r JOIN utilisateur u ON u.id = r.utilisateur_id, p WHERE r.n >= p.seuil_remises
    UNION ALL
    SELECT 'remises_patient', CASE WHEN rp.n >= 2 * p.seuil_remises_patient THEN 'urgent' ELSE 'attention' END, NULL::bigint,
           date_trunc('month', p_jour)::date, 'patient:' || rp.patient_id,
           format('%s %s : %s vente(s) avec remise en 30 jours (%s DA de remise), accordées par %s.',
                  pt.nom, COALESCE(pt.prenom, ''), rp.n, to_char(rp.montant, 'FM999G999G990D00'), rp.vendeurs),
           format('Remises repetees au meme patient : %s ventes en 30 jours (detail dans Surveillance).', rp.n),
           rp.montant
    FROM remises_patient rp JOIN patient pt ON pt.id = rp.patient_id, p WHERE rp.n >= p.seuil_remises_patient
    UNION ALL
    SELECT 'ajustements', CASE WHEN a.boites >= 2 * p.seuil_ajustements THEN 'urgent' ELSE 'attention' END, a.utilisateur_id, p_jour, '',
           format('Inventaires terminés ou validés par %s le %s : %s boîte(s) manquante(s) ajustée(s) (%s DA au prix d''achat).', u.nom_complet,
                  to_char(p_jour, 'DD/MM/YYYY'), a.boites, to_char(a.valeur, 'FM999G999G990D00')),
           format('%s : %s boite(s) manquante(s) ajustee(s) en inventaire le %s.', u.nom_complet, a.boites, to_char(p_jour, 'DD/MM')),
           a.valeur
    FROM ajustements a JOIN utilisateur u ON u.id = a.utilisateur_id, p WHERE a.boites >= p.seuil_ajustements
    UNION ALL
    SELECT CASE s.type WHEN 'casse' THEN 'casses' ELSE 'echantillons' END,
           CASE WHEN s.boites >= 2 * CASE s.type WHEN 'casse' THEN p.seuil_casses ELSE p.seuil_echantillons END THEN 'urgent' ELSE 'attention' END,
           s.utilisateur_id, p_jour, '',
           format('%s a déclaré %s boîte(s) %s le %s (%s DA au prix d''achat).', u.nom_complet, s.boites,
                  CASE s.type WHEN 'casse' THEN 'cassée(s)' ELSE 'sortie(s) en échantillon ou gratuité' END,
                  to_char(p_jour, 'DD/MM/YYYY'), to_char(s.valeur, 'FM999G999G990D00')),
           format('%s : %s boite(s) %s le %s.', u.nom_complet, s.boites, CASE s.type WHEN 'casse' THEN 'cassee(s)' ELSE 'en echantillon' END, to_char(p_jour, 'DD/MM')),
           s.valeur
    FROM sorties s JOIN utilisateur u ON u.id = s.utilisateur_id, p
    WHERE s.boites >= CASE s.type WHEN 'casse' THEN p.seuil_casses ELSE p.seuil_echantillons END
    UNION ALL
    SELECT 'boites_fantomes', CASE WHEN fi.n >= 3 THEN 'urgent' ELSE 'attention' END, NULL::bigint, p_jour, 'inventaire:' || fi.inventaire_id,
           format('Inventaire « %s » : %s boîte(s) identifiée(s) en stock non retrouvée(s) au comptage : %s. Elles ont peut-être quitté la pharmacie : recherchez-les, puis faites leur sortie.',
                  inv.rayon, fi.n, fi.detail),
           format('Inventaire %s : %s boite(s) identifiee(s) non retrouvee(s) au comptage.', inv.rayon, fi.n),
           fi.valeur
    FROM fantomes_inv fi JOIN inv ON inv.id = fi.inventaire_id
    UNION ALL
    SELECT 'sensibles_en_retard', 'attention', NULL::bigint, COALESCE(se.dernier::date, DATE '2000-01-01'), 'sensibles',
           format('Comptage des produits sensibles (%s produit(s)) %s : à faire dans Stock, Inventaire, « Produits sensibles ».', se.produits,
                  CASE WHEN se.dernier IS NULL THEN 'jamais fait' ELSE 'pas fait depuis le ' || to_char(se.dernier, 'DD/MM/YYYY') END),
           'Comptage des produits sensibles en retard.',
           NULL::numeric
    FROM sensibles se, p
    WHERE se.produits > 0 AND (se.dernier IS NULL OR se.dernier < p_jour - p.jours_comptage_sensibles + 1);
$$;
