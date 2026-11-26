-- =====================================================================
-- Retour arrière du script 169 (contenance : contenant seul et pilulier)
--   Remet contenance_depuis_conditionnement dans sa version du script 139 et vide les contenances écrites par le script 169
--   qui n'ont pas changé depuis (journal_audit, motif « contenance depuis la nomenclature, script 169 ») ; une contenance saisie
--   ensuite par le pharmacien n'est jamais touchée. Le vidage est inscrit au journal (motif « retour arrière du script 169 »).
--   Rejouable sans erreur. A lancer seul, hors de la suite numérotée : ce dossier n'est jamais lu par les outils de mise à jour.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

CREATE OR REPLACE FUNCTION contenance_depuis_conditionnement(p_conditionnement text) RETURNS int
LANGUAGE sql IMMUTABLE AS $$
    -- motif de ContenanceProduit.Boite(), recopié tel quel (test ContenanceDepuisNomenclatureTests)
    SELECT substring(p_conditionnement FROM '(?i)^\s*(?:B|BTE|BO[IÎiî]TE)\s*(?:/|\s+DE\s+)\s*([0-9]{1,5})(?:\s*(?:SACHETS?(?:(?:\s+|-)DOSES?)?|SACH|SCH|AMPOULES?|AMPS?|AMP\.?\s+BUV|CPR?S?|CP\.?\s+EFF|COMPRIM[EÉeé]S?(?:\s+EFFERVESCENTS?)?|G[EÉeé]LULES?|SUPPOSITOIRES?|SUPPO|OVULES?|CAPSULES?|PATCH(?:E?S)?|DISPOSITIFS?\s+TRANSDERMIQUES?|UNIDOSES?|R[EÉeé]CIPIENTS\s+UNIDOSES|FLACONS?|FLC|FL|(?:SERINGUES?|SERING\.?|SERIN|SER)(?:\s*PR[EÉeé][\s-]*REMPL[A-Za-zÉéÈè]*\.?)?|STYLOS?(?:\s*PR[EÉeé][\s-]*REMPL[A-Za-zÉéÈè]*\.?)?|CART(?:OUCHES?)?|POCHES?|TUBES?)\.?(?:\s*(?:DE\s*|/\s*)?[0-9]+(?:[.,][0-9]+)?\s*(?:ML|MG|G|L)|\s*\(\s*[0-9]+(?:[.,][0-9]+)?\s*(?:ML|MG|G|L)\s*\))?)?\s*$')::int
$$;

WITH ecrites AS (
    SELECT DISTINCT ON (j.enregistrement_id) j.enregistrement_id AS id, (j.details->'contenance'->>'apres')::int AS valeur
    FROM journal_audit j
    WHERE j.action = 'produit_modifie' AND j.details->>'motif' = 'contenance depuis la nomenclature, script 169'
    ORDER BY j.enregistrement_id, j.id DESC
), videes AS (
    UPDATE produit p SET contenance = NULL
    FROM ecrites e
    WHERE p.id = e.id AND p.contenance = e.valeur
      -- aucune autre modification de la contenance depuis
      AND NOT EXISTS (SELECT 1 FROM journal_audit k WHERE k.action = 'produit_modifie' AND k.enregistrement_id = p.id
                        AND k.details ? 'contenance' AND coalesce(k.details->>'motif', '') <> 'contenance depuis la nomenclature, script 169'
                        AND k.id > (SELECT max(x.id) FROM journal_audit x WHERE x.action = 'produit_modifie' AND x.enregistrement_id = p.id
                                     AND x.details->>'motif' = 'contenance depuis la nomenclature, script 169'))
    RETURNING p.id, p.nom_commercial, e.valeur, p.officine_id
)
INSERT INTO journal_audit (utilisateur_id, action, table_cible, enregistrement_id, details, officine_id)
SELECT NULL, 'produit_modifie', 'produit', v.id,
       jsonb_build_object('produit', v.nom_commercial,
                          'contenance', jsonb_build_object('avant', v.valeur, 'apres', NULL),
                          'motif', 'retour arrière du script 169'),
       v.officine_id
FROM videes v;
