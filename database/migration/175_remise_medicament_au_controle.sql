-- =====================================================================
-- 175 -- Contrôle interne : la remise sur un médicament compte de nouveau
--   Constat de revue du 2026-11-28 : depuis le script 173, les comptages « remises » (par employé et par jour) et « remises_patient »
--   (par patient sur 30 jours) exigeaient « montant_remise > 0 ». Or la réduction accordée sur un médicament est rangée dans
--   vente.montant_don (montant_remise ne porte que les produits hors médicaments) : une vente de médicaments avec remise n'était plus
--   comptée, ni l'anomalie enregistrée, ni le titulaire prévenu.
--   * surveillance_candidats(date) redéfinie à l'identique, sauf : les deux comptages exigent « montant_remise + montant_don > 0 » et
--     additionnent ces deux montants (le montant affiché dans l'anomalie est toute la réduction accordée).
--   La décision du 2026-11-27 reste tenue : une vente à taux de remise positif mais sans aucune réduction (0 DA) ne compte pas.
--   Données : aucune anomalie déjà enregistrée n'est modifiée ni supprimée ; les journées passées ne sont pas recalculées.
--   Retour arrière : database/migration/retour/175_remise_medicament_au_controle_retour.sql (redéfinition du script 173).
-- Idempotent et rejouable sur une base plus récente.
-- A exécuter après 174_postes_preuve_exigee.sql.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

CREATE OR REPLACE FUNCTION public.surveillance_candidats(p_jour date) RETURNS TABLE(o_type text, o_niveau text, o_utilisateur_id bigint, o_jour date, o_cle text, o_message text, o_notification text, o_valeur numeric)
    LANGUAGE sql STABLE
    AS $$
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
          -- Plage normale (08:00-20:00) ou passant minuit (08:00-00:30)
          AND CASE WHEN p.heure_ouverture < p.heure_fermeture
                   THEN v.date_vente::time < p.heure_ouverture OR v.date_vente::time >= p.heure_fermeture
                   ELSE v.date_vente::time >= p.heure_fermeture AND v.date_vente::time < p.heure_ouverture END
        GROUP BY v.utilisateur_id
    ),
    remises AS (
        SELECT v.utilisateur_id, count(*)::int AS n, sum(v.montant_remise + v.montant_don) AS montant
        FROM ventes v WHERE v.remise_pct > 0 AND v.montant_remise + v.montant_don > 0 GROUP BY v.utilisateur_id
    ),
    -- Remises au même patient sur les 30 jours qui précèdent (une anomalie par patient et par mois)
    remises_patient AS (
        SELECT v.patient_id, count(*)::int AS n, sum(v.montant_remise + v.montant_don) AS montant,
               string_agg(DISTINCT u.nom_complet, ', ') AS vendeurs
        FROM vente v JOIN utilisateur u ON u.id = v.utilisateur_id
        WHERE v.statut = 'validee' AND v.remise_pct > 0 AND v.montant_remise + v.montant_don > 0 AND v.patient_id IS NOT NULL
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
    -- Lot compté uniquement en scannant ses boîtes identifiées (boîtes vues = boîtes comptées) : un lot compté à la main,
    -- ou par un code qui ne désigne pas une boîte, n'a pas de boîtes fantômes
    lots_scannes AS (
        SELECT e.inventaire_id, b.lot_id
        FROM boite_evenement e JOIN boite b ON b.id = e.boite_id
        JOIN ligne_inventaire li ON li.inventaire_id = e.inventaire_id AND li.lot_id = b.lot_id
        WHERE e.type = 'inventaire' AND e.inventaire_id IN (SELECT id FROM inv)
        GROUP BY e.inventaire_id, b.lot_id, li.compte
        HAVING count(*) = COALESCE(li.compte, 0)
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
           format('Remises repetees au meme patient : %s ventes en 30 jours (detail dans Controle interne).', rp.n),
           rp.montant
    FROM remises_patient rp JOIN patient pt ON pt.id = rp.patient_id, p WHERE rp.n >= p.seuil_remises_patient
      -- Une série de remises à cheval sur deux mois ne crée pas une seconde anomalie
      AND NOT EXISTS (SELECT 1 FROM alerte_surveillance a WHERE a.type = 'remises_patient' AND a.cle = 'patient:' || rp.patient_id
                      AND a.jour < date_trunc('month', p_jour)::date AND a.cree_le >= p_jour - 29)
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

