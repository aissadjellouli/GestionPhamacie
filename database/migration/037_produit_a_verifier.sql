-- =====================================================================
-- Produits repris de l'ancien logiciel, à vérifier avant la vente
--   * produit.a_verifier : posé par la reprise (import.charger, script
--     008) pour un produit créé sans n° d'enregistrement. La nomenclature
--     ne peut alors pas proposer sa classe : un psychotrope serait repris
--     « libre » et vendu sans ordonnance ni registre ;
--   * tant que le titulaire n'a pas confirmé sa classe (Administration,
--     Nomenclature nationale), le produit n'est pas vendable : la caisse
--     le refuse ;
--   * v_produit_a_verifier le signale (motif « repris »). Mêmes colonnes
--     que la version 034.
-- import.charger est défini dans 008_reprise_donnees.sql : les scripts se rejouent
-- tous, dans l'ordre, sur une base déjà installée.
-- Idempotent. A exécuter après 036_solde_tresorerie_echeance.sql.
-- =====================================================================

ALTER TABLE produit ADD COLUMN IF NOT EXISTS a_verifier boolean NOT NULL DEFAULT false;
COMMENT ON COLUMN produit.a_verifier IS 'Repris de l''ancien logiciel sans n° d''enregistrement : classe à confirmer par le titulaire, vente refusée d''ici là';

CREATE OR REPLACE VIEW v_produit_a_verifier AS
SELECT p.id AS produit_id, p.nom_commercial, p.dosage, p.forme, p.classe,
       d.nom AS dci, p.num_enregistrement,
       COALESCE(sd.classe, n.classe_proposee) AS classe_proposee,
       n.situation,
       CASE WHEN COALESCE(sd.classe, n.classe_proposee) IN ('psychotrope', 'stupefiant')
                 AND p.classe NOT IN ('psychotrope', 'stupefiant') THEN 'classe'
            WHEN p.a_verifier THEN 'repris'
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
       OR p.a_verifier
       OR n.situation IN ('retire', 'non_renouvele')
       -- N° d'enregistrement renseigné mais introuvable dans la nomenclature chargée
       OR (n.situation IS NULL AND NULLIF(trim(p.num_enregistrement), '') IS NOT NULL
           AND EXISTS (SELECT 1 FROM nomenclature_produit)));
