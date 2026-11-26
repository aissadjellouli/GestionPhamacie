-- =====================================================================
-- 169 -- Contenance : contenant seul (flacon, tube) et pilulier
--   Décision du titulaire du 2026-11-26 (lot 2, point 5, « 5 OK »), suite du script 139 (« B/N FL » et « B/N SERINGUE » donnent N,
--   déjà en place). Constat sur dev le 2026-11-26 : 3 253 produits sans contenance, surtout un contenant seul sans nombre de
--   contenants (« FL/120ML » 597, « T/30G » 317, « FL./120ML » 140, « FL/120 ML » 115…).
--   Règle ajoutée (identique à ContenanceProduit, src/Pharma.Application/Catalogue/ContenanceProduit.cs) :
--   * contenant seul suivi de sa taille : 1 (« FL/120ML », « FL./20DOSES », « F/30ML », « FLACON DE 100ML », « T/30G », « TUBE/30G »,
--     « B/FL 15 ML », « B/TUBE DE 30G ») ;
--   * pilulier : ses unités (« PILULIER/30 », « PILULIER DE 30 » = 30).
--   Restent VIDES : plusieurs présentations (« B/30 ET B/60 », « B/28+7 », « B/1 - B/3 »), flacon de poudre, tout autre texte à la
--   suite. Mêmes garde-fous que 139 : nombre de 1 à 10000, valeur écrite ≤ 1000, toutes les lignes du n° d'enregistrement d'accord
--   (version la plus récente), sinon vide.
--   Données : ne remplit QUE les produits dont produit.contenance est NULL ; une valeur saisie ou déjà remplie n'est jamais touchée.
--   Trace : une ligne « produit_modifie » par produit rempli dans journal_audit, motif « contenance depuis la nomenclature, script 169 ».
--   Structure : seule la fonction contenance_depuis_conditionnement est remplacée (même signature). Idempotent.
--   Rejouer un script antérieur (136, 137, 139) ramènerait l'ancien motif : rejouer ensuite 169 (la suite complète le fait).
--   Retour arrière : database/migration/retour/169_contenance_contenant_seul_pilulier_retour.sql (fonction du 139 ; contenances
--   remplies par ce script et jamais changées depuis remises à vide).
-- A exécuter après 168_controle_reception_abandonnee.sql.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

CREATE OR REPLACE FUNCTION contenance_depuis_conditionnement(p_conditionnement text) RETURNS int
LANGUAGE sql IMMUTABLE AS $$
    -- motifs de ContenanceProduit (Boite, Pilulier, ContenantSeul), recopiés tels quels (test ContenanceDepuisNomenclatureTests)
    SELECT COALESCE(
        substring(p_conditionnement FROM '(?i)^\s*(?:B|BTE|BO[IÎiî]TE)\s*(?:/|\s+DE\s+)\s*([0-9]{1,5})(?:\s*(?:SACHETS?(?:(?:\s+|-)DOSES?)?|SACH|SCH|AMPOULES?|AMPS?|AMP\.?\s+BUV|CPR?S?|CP\.?\s+EFF|COMPRIM[EÉeé]S?(?:\s+EFFERVESCENTS?)?|G[EÉeé]LULES?|SUPPOSITOIRES?|SUPPO|OVULES?|CAPSULES?|PATCH(?:E?S)?|DISPOSITIFS?\s+TRANSDERMIQUES?|UNIDOSES?|R[EÉeé]CIPIENTS\s+UNIDOSES|FLACONS?|FLC|FL|(?:SERINGUES?|SERING\.?|SERIN|SER)(?:\s*PR[EÉeé][\s-]*REMPL[A-Za-zÉéÈè]*\.?)?|STYLOS?(?:\s*PR[EÉeé][\s-]*REMPL[A-Za-zÉéÈè]*\.?)?|CART(?:OUCHES?)?|POCHES?|TUBES?)\.?(?:\s*(?:DE\s*|/\s*)?[0-9]+(?:[.,][0-9]+)?\s*(?:ML|MG|G|L)|\s*\(\s*[0-9]+(?:[.,][0-9]+)?\s*(?:ML|MG|G|L)\s*\))?)?\s*$')::int,
        substring(p_conditionnement FROM '(?i)^\s*PILULIER\s*(?:/|\s+DE\s+)\s*([0-9]{1,5})\s*$')::int,
        CASE WHEN p_conditionnement ~* '^\s*(?:B\s*/\s*)?(?:FL\.?|FLC|FLACON|F|T|TUBE)\s*(?:/|\s+DE\s+|\s)\s*[0-9]+(?:[.,][0-9]+)?\s*(?:ML|MG|G|L|DOSES?)\.?\s*$' THEN 1 END)
$$;

WITH lignes AS (
    SELECT p.id, contenance_depuis_conditionnement(n.conditionnement) AS nombre
    FROM produit p
    JOIN nomenclature_produit n ON n.num_enregistrement = p.num_enregistrement
     AND n.version_id = (SELECT max(x.version_id) FROM nomenclature_produit x WHERE x.num_enregistrement = p.num_enregistrement)
    WHERE p.contenance IS NULL
), sures AS (
    SELECT id, min(nombre) AS valeur
    FROM lignes
    GROUP BY id
    HAVING bool_and(coalesce(nombre BETWEEN 1 AND 10000, false))
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
                          'motif', 'contenance depuis la nomenclature, script 169'),
       m.officine_id
FROM maj m;
