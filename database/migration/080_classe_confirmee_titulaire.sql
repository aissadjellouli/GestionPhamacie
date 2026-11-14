-- =====================================================================
-- Classe d'un produit confirmée par le titulaire
--   * produit.classe_confirmee_par, produit.classe_confirmee_le : le
--     titulaire a confirmé (Administration › Nomenclature nationale,
--     « Produits à vérifier ») ou enregistré à la création une classe
--     moins stricte que celle que donnent la DCI (substance_surveillee) ou
--     la nomenclature nationale (n° d'enregistrement) : hors registre pour
--     un psychotrope ou un stupéfiant, psychotrope pour un stupéfiant.
--     Une modification par un employé ne reclasse plus ce produit et ne le
--     remet pas « à vérifier » ; une DCI changée retire la confirmation
--     (le n° d'enregistrement ne se modifie pas dans le logiciel). Les
--     deux colonnes sont renseignées ensemble (contrainte
--     produit_classe_confirmee_check, posée avec garde) ;
--   * v_produit_a_verifier : un produit dont la classe est confirmée n'est
--     plus signalé « classe à corriger » (motif « classe »). Mêmes
--     colonnes, dans le même ordre ; les autres motifs (repris, absent,
--     retiré ou non renouvelé) sont inchangés.
-- Idempotent. A exécuter après 079_preteur_libre_alertes_ecarts.sql.
-- =====================================================================

ALTER TABLE produit ADD COLUMN IF NOT EXISTS classe_confirmee_par bigint REFERENCES utilisateur(id);
ALTER TABLE produit ADD COLUMN IF NOT EXISTS classe_confirmee_le timestamptz;
COMMENT ON COLUMN produit.classe_confirmee_par IS 'Titulaire qui a confirmé une classe moins stricte que celle de la DCI (substance surveillée) ou de la nomenclature : un employé ne la change plus ; retirée si la DCI change';
COMMENT ON COLUMN produit.classe_confirmee_le IS 'Date de la confirmation de classe par le titulaire (classe_confirmee_par)';

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'produit_classe_confirmee_check' AND conrelid = 'produit'::regclass) THEN
        ALTER TABLE produit ADD CONSTRAINT produit_classe_confirmee_check
            CHECK ((classe_confirmee_par IS NULL) = (classe_confirmee_le IS NULL));
    END IF;
END $$;

-- Mêmes colonnes que la version 037 ; motif « classe » seulement sans confirmation du titulaire
CREATE OR REPLACE VIEW v_produit_a_verifier AS
SELECT p.id AS produit_id, p.nom_commercial, p.dosage, p.forme, p.classe,
       d.nom AS dci, p.num_enregistrement,
       COALESCE(sd.classe, n.classe_proposee) AS classe_proposee,
       n.situation,
       CASE WHEN COALESCE(sd.classe, n.classe_proposee) IN ('psychotrope', 'stupefiant')
                 AND p.classe NOT IN ('psychotrope', 'stupefiant')
                 AND p.classe_confirmee_par IS NULL THEN 'classe'
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
        AND p.classe NOT IN ('psychotrope', 'stupefiant')
        AND p.classe_confirmee_par IS NULL)
       OR p.a_verifier
       OR n.situation IN ('retire', 'non_renouvele')
       -- N° d'enregistrement renseigné mais introuvable dans la nomenclature chargée
       OR (n.situation IS NULL AND NULLIF(trim(p.num_enregistrement), '') IS NOT NULL
           AND EXISTS (SELECT 1 FROM nomenclature_produit)));
