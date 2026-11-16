-- =====================================================================
-- Contenance des produits depuis la nomenclature : sachets, ampoules,
-- comprimés… (2026-11-16, suite du script 132)
--   Même principe que 132 : inscrit dans produit.contenance la valeur que la
--   fiche produit propose en grisé, pour les seuls produits dont la
--   contenance est encore VIDE (NULL) ; une valeur saisie par le pharmacien
--   ou déjà remplie par 132 n'est jamais touchée.
--
--   Règle d'extraction : identique à ContenanceProduit.DepuisNomenclature
--   (src/Pharma.Application/Catalogue/ContenanceProduit.cs, même motif) :
--     * lignes lues : nomenclature_produit du même n° d'enregistrement, dans
--       la version la plus récente de ce n° (max(version_id)) ;
--     * « B/30 », « B/030 », « BTE/30 », « BOITE DE 30 » (casse ignorée),
--       ÉVENTUELLEMENT SUIVI d'une seule unité de délivrance comptée par le
--       nombre (point final toléré) : SACHET(S), SACH, SCH, SACHETS-DOSE(S),
--       SACHETS DOSES ; AMPOULE(S), AMP, AMPS, AMP BUV ; CP, CPR, CPRS,
--       CP EFF, COMPRIMÉ(S) [EFFERVESCENT(S)] ; GÉLULE(S) ; SUPPOSITOIRE(S),
--       SUPPO ; OVULE(S) ; CAPSULE(S) ; PATCH(S), PATCHES ; DISPOSITIF(S)
--       TRANSDERMIQUE(S) ; UNIDOSE(S), RÉCIPIENTS UNIDOSES ;
--     * nombre de 1 à 10000, toutes les lignes du n° d'accord.
--   Exclusions (produit laissé vide) : flacon, tube, seringue, cartouche,
--   volume ou masse (« FL », « T/15G », « AMP. DE 2ML »), plusieurs nombres
--   (« B/1+1 », « B/30 ET B/90 », « B/30 SACHETS DE 5 G »), unité suivie
--   d'un complément (« AMPOULES EN POLYPROPYLENE », « AMP.AUTOCASSABLES ») :
--   rien n'est deviné ; conditionnement vide ; valeur 0 ; lignes en
--   désaccord ; et, prudence propre au script, valeur supérieure à 1000.
--   Trace : une ligne « produit_modifie » par produit rempli dans
--   journal_audit (utilisateur vide), détail {produit, contenance {avant,
--   apres}, motif « contenance depuis la nomenclature, script 134 »}.
--   Données seulement (aucun changement de schéma). Aucune suppression.
--   Idempotent : un 2e passage ne modifie rien. Après 133.
-- =====================================================================

WITH lignes AS (
    SELECT p.id,
           -- motif de ContenanceProduit.Boite(), recopié tel quel (test ContenanceDepuisNomenclatureTests)
           substring(n.conditionnement FROM '(?i)^\s*(?:B|BTE|BOITE|BOÎTE)\s*(?:/|\s+DE\s+)\s*([0-9]{1,5})(?:\s*(?:SACHETS?(?:(?:\s+|-)DOSES?)?|SACH|SCH|AMPOULES?|AMPS?|AMP\.?\s+BUV|CPR?S?|CP\.?\s+EFF|COMPRIM[EÉ]S?(?:\s+EFFERVESCENTS?)?|G[EÉ]LULES?|SUPPOSITOIRES?|SUPPO|OVULES?|CAPSULES?|PATCH(?:E?S)?|DISPOSITIFS?\s+TRANSDERMIQUES?|UNIDOSES?|R[EÉ]CIPIENTS\s+UNIDOSES)\.?)?\s*$')::int AS nombre
    FROM produit p
    JOIN nomenclature_produit n ON n.num_enregistrement = p.num_enregistrement
     AND n.version_id = (SELECT max(x.version_id) FROM nomenclature_produit x WHERE x.num_enregistrement = p.num_enregistrement)
    WHERE p.contenance IS NULL
), sures AS (
    SELECT id, min(nombre) AS valeur
    FROM lignes
    GROUP BY id
    HAVING bool_and(coalesce(nombre BETWEEN 1 AND 10000, false))   -- non reconnu (NULL) ou 0 : exclu
       AND count(DISTINCT nombre) = 1
       AND min(nombre) <= 1000
), maj AS (
    UPDATE produit p SET contenance = s.valeur
    FROM sures s
    WHERE p.id = s.id AND p.contenance IS NULL
    RETURNING p.id, p.nom_commercial, p.contenance, p.officine_id
)
INSERT INTO journal_audit (utilisateur_id, action, table_cible, enregistrement_id, details, officine_id)
SELECT NULL, 'produit_modifie', 'produit', m.id,
       jsonb_build_object('produit', m.nom_commercial,
                          'contenance', jsonb_build_object('avant', NULL, 'apres', m.contenance),
                          'motif', 'contenance depuis la nomenclature, script 134'),
       m.officine_id
FROM maj m;
