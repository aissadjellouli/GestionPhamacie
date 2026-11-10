-- =====================================================================
-- Nomenclature : signaler aussi les produits du catalogue dont le n°
-- d'enregistrement n'est plus dans la nomenclature chargée (motif
-- « absent »). L'import remplace la liste officielle sans jamais toucher
-- au catalogue : sans ce signalement, un produit disparu de la liste
-- passait inaperçu. Rien n'est modifié automatiquement : le pharmacien
-- vérifie et décide.
-- Pas de signalement tant qu'aucune nomenclature n'est chargée, ni pour
-- un produit sans n° d'enregistrement (parapharmacie, saisie libre).
-- Mêmes colonnes que la version 023. Idempotent. Après 033.
-- =====================================================================

CREATE OR REPLACE VIEW v_produit_a_verifier AS
SELECT p.id AS produit_id, p.nom_commercial, p.dosage, p.forme, p.classe,
       d.nom AS dci, p.num_enregistrement,
       COALESCE(sd.classe, n.classe_proposee) AS classe_proposee,
       n.situation,
       CASE WHEN COALESCE(sd.classe, n.classe_proposee) IN ('psychotrope', 'stupefiant')
                 AND p.classe NOT IN ('psychotrope', 'stupefiant') THEN 'classe'
            WHEN n.situation IS NULL THEN 'absent'
            ELSE 'situation' END AS motif
FROM produit p
LEFT JOIN dci d ON d.id = p.dci_id
LEFT JOIN LATERAL (SELECT s.classe FROM substance_surveillee s
                   WHERE majuscule_sans_accent(d.nom) LIKE '%' || s.motif || '%'
                   ORDER BY s.classe DESC LIMIT 1) sd ON true
LEFT JOIN LATERAL (SELECT v.classe_proposee, v.situation FROM v_nomenclature v
                   WHERE v.num_enregistrement = p.num_enregistrement
                   ORDER BY v.situation = 'en_vigueur' DESC LIMIT 1) n ON true
WHERE p.actif
  AND ((COALESCE(sd.classe, n.classe_proposee) IN ('psychotrope', 'stupefiant')
        AND p.classe NOT IN ('psychotrope', 'stupefiant'))
       OR n.situation IN ('retire', 'non_renouvele')
       -- N° d'enregistrement renseigné mais introuvable dans la nomenclature chargée
       OR (n.situation IS NULL AND NULLIF(trim(p.num_enregistrement), '') IS NOT NULL
           AND EXISTS (SELECT 1 FROM nomenclature_produit)));
