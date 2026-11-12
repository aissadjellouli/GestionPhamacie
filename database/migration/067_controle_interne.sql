-- =====================================================================
-- « Surveillance » devient « Contrôle interne » dans les messages
--   * Les messages d'anomalie qui renvoyaient à l'écran « Surveillance »
--     (remises répétées au même patient, patient récent avec une dette,
--     dette jamais remboursée) renvoient à l'écran « Contrôle interne ».
--     Ils restent sans accents : ils partent aussi par SMS.
--   * Mêmes fonctions qu'en 059 (surveillance_candidats) et 058
--     (surveillance_candidats_comptes) : seul ce texte change. Les noms
--     techniques (tables, fonctions, types d'alerte) ne changent pas.
-- Idempotent. A exécuter après 066_alertes_peremption_claires.sql.
-- =====================================================================

SET client_encoding = 'UTF8';

-- Mêmes calculs que 059_corrections_surveillance.sql
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
          -- Plage normale (08:00-20:00) ou passant minuit (08:00-00:30)
          AND CASE WHEN p.heure_ouverture < p.heure_fermeture
                   THEN v.date_vente::time < p.heure_ouverture OR v.date_vente::time >= p.heure_fermeture
                   ELSE v.date_vente::time >= p.heure_fermeture AND v.date_vente::time < p.heure_ouverture END
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

-- Mêmes calculs que 058_surveillance_comptes.sql
CREATE OR REPLACE FUNCTION surveillance_candidats_comptes(p_jour date)
RETURNS TABLE (o_type text, o_niveau text, o_utilisateur_id bigint, o_jour date, o_cle text, o_message text, o_notification text, o_valeur numeric)
LANGUAGE sql STABLE AS $$
    WITH p AS (SELECT * FROM parametre_surveillance WHERE id = 1),
    -- Crédits accordés (ventes à crédit), par patient et par employé
    credits AS (
        SELECT m.patient_id, m.utilisateur_id, m.montant, m.cree_le FROM credit_mouvement m WHERE m.type = 'vente_credit'
    ),
    -- Patient récent dont la dette atteint le seuil, crédit accordé ce jour
    nouveaux AS (
        SELECT pt.id, pt.nom, pt.prenom, pt.solde_credit, pt.cree_le, pt.cree_par,
               (SELECT string_agg(DISTINCT u.nom_complet, ', ') FROM credits c JOIN utilisateur u ON u.id = c.utilisateur_id WHERE c.patient_id = pt.id) AS vendeurs
        FROM patient pt, p
        WHERE pt.cree_le >= p_jour - p.jours_patient_recent + 1 AND pt.solde_credit >= p.seuil_credit_nouveau_patient
          AND EXISTS (SELECT 1 FROM credits c WHERE c.patient_id = pt.id AND c.cree_le >= p_jour AND c.cree_le < p_jour + 1)
    ),
    -- Crédits accordés par un employé ce jour
    par_employe AS (
        SELECT c.utilisateur_id, count(*)::int AS n, sum(c.montant) AS montant, count(DISTINCT c.patient_id)::int AS patients
        FROM credits c WHERE c.utilisateur_id IS NOT NULL AND c.cree_le >= p_jour AND c.cree_le < p_jour + 1
        GROUP BY c.utilisateur_id
    ),
    -- Patient endetté qui n'a jamais rien remboursé depuis son premier crédit
    jamais AS (
        SELECT pt.id, pt.nom, pt.prenom, pt.solde_credit, min(c.cree_le) AS premier,
               string_agg(DISTINCT u.nom_complet, ', ') AS vendeurs
        FROM patient pt JOIN credits c ON c.patient_id = pt.id LEFT JOIN utilisateur u ON u.id = c.utilisateur_id, p
        WHERE pt.solde_credit > 0
          AND NOT EXISTS (SELECT 1 FROM credit_mouvement r WHERE r.patient_id = pt.id AND r.type = 'remboursement')
        GROUP BY pt.id, pt.nom, pt.prenom, pt.solde_credit, p.jours_credit_sans_remboursement
        HAVING min(c.cree_le)::date <= p_jour - p.jours_credit_sans_remboursement
    ),
    -- Autorisations sur place par code comptoir ce jour
    autorisations AS (
        SELECT j.utilisateur_id, j.autorise_par, j.details->>'droit' AS droit
        FROM journal_audit j
        WHERE j.action = 'autorisation_code' AND j.autorise_par IS NOT NULL AND j.cree_le >= p_jour AND j.cree_le < p_jour + 1
    ),
    par_valideur AS (
        SELECT a.autorise_par AS utilisateur_id, count(*)::int AS n, string_agg(DISTINCT u.nom_complet, ', ') AS employes
        FROM autorisations a JOIN utilisateur u ON u.id = a.utilisateur_id GROUP BY a.autorise_par
    ),
    par_beneficiaire AS (
        SELECT a.utilisateur_id, count(*)::int AS n, string_agg(DISTINCT v.nom_complet, ', ') AS valideurs, string_agg(DISTINCT a.droit, ', ') AS droits
        FROM autorisations a JOIN utilisateur v ON v.id = a.autorise_par GROUP BY a.utilisateur_id
    ),
    -- Activité par poste ce jour (connexions et ventes) : changements de poste rapprochés
    activite AS (
        SELECT c.utilisateur_id, c.poste, c.cree_le AS t FROM connexion_employe c
        WHERE c.poste IS NOT NULL AND c.cree_le >= p_jour AND c.cree_le < p_jour + 1
        UNION ALL
        SELECT v.utilisateur_id, s.poste, v.date_vente FROM vente v JOIN session_caisse s ON s.id = v.session_caisse_id
        WHERE v.date_vente >= p_jour AND v.date_vente < p_jour + 1
    ),
    suite AS (
        SELECT a.utilisateur_id, a.poste, a.t,
               lag(a.poste) OVER w AS poste_avant, lag(a.t) OVER w AS t_avant
        FROM activite a WINDOW w AS (PARTITION BY a.utilisateur_id ORDER BY a.t)
    ),
    postes AS (
        SELECT s.utilisateur_id, count(*)::int AS n, string_agg(DISTINCT s.poste_avant || ' / ' || s.poste, ', ') AS paires
        FROM suite s, p
        WHERE s.poste_avant IS NOT NULL AND s.poste_avant <> s.poste AND s.t - s.t_avant <= make_interval(mins => p.minutes_postes_simultanes)
        GROUP BY s.utilisateur_id
    ),
    -- Baisses de prix par un autre que le titulaire
    baisses AS (
        SELECT j.utilisateur_id, count(*)::int AS n,
               string_agg(format('%s : %s → %s DA', j.details->>'produit', j.details->>'avant', j.details->>'apres'), ', ' ORDER BY j.id) AS detail,
               sum((j.details->>'avant')::numeric - (j.details->>'apres')::numeric) AS baisse
        FROM journal_audit j JOIN utilisateur u ON u.id = j.utilisateur_id JOIN role r ON r.id = u.role_id
        WHERE j.action = 'prix_modifie' AND j.cree_le >= p_jour AND j.cree_le < p_jour + 1 AND r.code <> 'titulaire'
          AND (j.details->>'apres')::numeric < (j.details->>'avant')::numeric
        GROUP BY j.utilisateur_id
    )
    SELECT 'credit_nouveau_patient', CASE WHEN n.solde_credit >= 2 * p.seuil_credit_nouveau_patient THEN 'urgent' ELSE 'attention' END,
           n.cree_par, n.cree_le::date, 'patient:' || n.id,
           format('%s %s, patient créé le %s%s : dette de %s DA dès ses premiers jours (crédits accordés par %s).', n.nom, COALESCE(n.prenom, ''),
                  to_char(n.cree_le, 'DD/MM/YYYY'), COALESCE(' par ' || (SELECT nom_complet FROM utilisateur WHERE id = n.cree_par), ''),
                  to_char(n.solde_credit, 'FM999G999G990D00'), COALESCE(n.vendeurs, '?')),
           format('Patient cree recemment avec une dette de %s DA (detail dans Controle interne).', to_char(n.solde_credit, 'FM999G999G990')),
           n.solde_credit
    FROM nouveaux n, p
    UNION ALL
    SELECT 'credits_employe', CASE WHEN e.montant >= 2 * p.seuil_credits_jour THEN 'urgent' ELSE 'attention' END, e.utilisateur_id, p_jour, '',
           format('%s a accordé %s vente(s) à crédit le %s à %s patient(s), pour %s DA.', u.nom_complet, e.n, to_char(p_jour, 'DD/MM/YYYY'),
                  e.patients, to_char(e.montant, 'FM999G999G990D00')),
           format('%s : %s DA de credits accordes le %s.', u.nom_complet, to_char(e.montant, 'FM999G999G990'), to_char(p_jour, 'DD/MM')),
           e.montant
    FROM par_employe e JOIN utilisateur u ON u.id = e.utilisateur_id, p WHERE e.montant >= p.seuil_credits_jour
    UNION ALL
    SELECT 'credit_sans_remboursement', 'attention', NULL::bigint, j.premier::date, 'patient:' || j.id,
           format('%s %s doit %s DA et n''a jamais rien remboursé depuis son premier crédit du %s (crédits accordés par %s).',
                  j.nom, COALESCE(j.prenom, ''), to_char(j.solde_credit, 'FM999G999G990D00'), to_char(j.premier, 'DD/MM/YYYY'), COALESCE(j.vendeurs, '?')),
           format('Patient endette sans aucun remboursement depuis le %s (detail dans Controle interne).', to_char(j.premier, 'DD/MM/YYYY')),
           j.solde_credit
    FROM jamais j
    UNION ALL
    SELECT 'autorisations_valideur', CASE WHEN v.n >= 2 * p.seuil_autorisations_valideur THEN 'urgent' ELSE 'attention' END, v.utilisateur_id, p_jour, '',
           format('%s a autorisé %s action(s) par son code comptoir le %s (pour %s).', u.nom_complet, v.n, to_char(p_jour, 'DD/MM/YYYY'), v.employes),
           format('%s : %s autorisations par code comptoir le %s.', u.nom_complet, v.n, to_char(p_jour, 'DD/MM')),
           v.n
    FROM par_valideur v JOIN utilisateur u ON u.id = v.utilisateur_id, p WHERE v.n >= p.seuil_autorisations_valideur
    UNION ALL
    SELECT 'autorisations_employe', CASE WHEN b.n >= 2 * p.seuil_autorisations_employe THEN 'urgent' ELSE 'attention' END, b.utilisateur_id, p_jour, '',
           format('%s a eu besoin de %s autorisation(s) par code comptoir le %s (droits : %s ; autorisé par %s).', u.nom_complet, b.n,
                  to_char(p_jour, 'DD/MM/YYYY'), b.droits, b.valideurs),
           format('%s : %s autorisations par code demandees le %s.', u.nom_complet, b.n, to_char(p_jour, 'DD/MM')),
           b.n
    FROM par_beneficiaire b JOIN utilisateur u ON u.id = b.utilisateur_id, p WHERE b.n >= p.seuil_autorisations_employe
    UNION ALL
    SELECT 'postes_simultanes', CASE WHEN s.n >= 2 * p.seuil_changements_poste THEN 'urgent' ELSE 'attention' END, s.utilisateur_id, p_jour, '',
           format('%s a travaillé sur deux postes à la fois le %s : %s passage(s) d''un poste à l''autre en moins de %s minutes (%s). Son identifiant ou son code est peut-être utilisé par quelqu''un d''autre.',
                  u.nom_complet, to_char(p_jour, 'DD/MM/YYYY'), s.n, p.minutes_postes_simultanes, s.paires),
           format('%s : actif sur deux postes a la fois le %s.', u.nom_complet, to_char(p_jour, 'DD/MM')),
           s.n
    FROM postes s JOIN utilisateur u ON u.id = s.utilisateur_id, p WHERE s.n >= p.seuil_changements_poste
    UNION ALL
    SELECT 'baisses_prix', CASE WHEN b.n >= 2 * p.seuil_baisses_prix THEN 'urgent' ELSE 'attention' END, b.utilisateur_id, p_jour, '',
           format('%s a baissé le prix de vente de %s produit(s) le %s : %s.', u.nom_complet, b.n, to_char(p_jour, 'DD/MM/YYYY'), b.detail),
           format('%s : %s baisse(s) de prix le %s.', u.nom_complet, b.n, to_char(p_jour, 'DD/MM')),
           b.baisse
    FROM baisses b JOIN utilisateur u ON u.id = b.utilisateur_id, p WHERE b.n >= p.seuil_baisses_prix;
$$;
