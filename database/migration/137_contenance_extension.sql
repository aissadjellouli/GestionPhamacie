-- =====================================================================
-- 137 — Contenance : unité de délivrance suivie de sa taille unitaire
-- (2026-11-16, suite des scripts 132, 134 et 136).
--   Constat sur dev (4 019 produits sans contenance avec une ligne de
--   nomenclature) : 246 produits ont un conditionnement de la forme
--   « B/05 AMP. DE 2ML », « B/20 SACHETS DE 15ML », « B/10 RECIPIENTS
--   UNIDOSES DE 2ML », « BOITE DE 05 AMPOULES DE 01ML » : le nombre qui suit
--   « B/ » compte les unités de délivrance (ampoules, sachets…), le complément
--   « DE 2ML » n'est que leur taille unitaire. Ces textes étaient laissés vides
--   par prudence (134) ; la lecture des 246 cas montre qu'il n'y a aucune
--   ambiguïté : seul le premier nombre est une quantité.
--
--   Extension du motif (identique à ContenanceProduit.Boite(), src/Pharma.Application/Catalogue/ContenanceProduit.cs) :
--   après l'UNIQUE unité de délivrance déjà reconnue (sachets, ampoules, comprimés,
--   gélules…), un complément facultatif « [DE] <nombre> ML | MG | G | L »
--   (virgule ou point décimal), puis la fin du texte. Le complément ne s'accepte
--   JAMAIS sans unité de délivrance (« B/10 DE 2ML » reste vide), ni quand un
--   autre texte suit (« B/5 AMPOULES EN POLYPROPYLENE », « B/1FL. DE PDRE. + 1AMP. DE 2ML »,
--   « B/5 AMP. DE 2ML ET B/50 AMP. DE 2ML », flacons, tubes, seringues, cartouches : vides).
--   Mêmes garde-fous que 134 : nombre de 1 à 10000, valeur écrite ≤ 1000, toutes les
--   lignes du n° d'enregistrement d'accord (version la plus récente), sinon vide.
--
--   Données : ne remplit QUE les produits dont produit.contenance est NULL ; une
--   valeur saisie ou déjà remplie n'est jamais touchée. Trace : une ligne
--   « produit_modifie » par produit rempli dans journal_audit (comme 134 et 136),
--   motif « contenance depuis la nomenclature, script 137 ».
--   Structure : seule la fonction contenance_depuis_conditionnement est remplacée
--   (CREATE OR REPLACE, même signature). Aucune suppression. Idempotent.
--   Rejouer un script antérieur (136) ramènerait l'ancien motif : rejouer ensuite 137.
-- =====================================================================

CREATE OR REPLACE FUNCTION contenance_depuis_conditionnement(p_conditionnement text) RETURNS int
LANGUAGE sql IMMUTABLE AS $$
    -- motif de ContenanceProduit.Boite(), recopié tel quel (test ContenanceDepuisNomenclatureTests)
    SELECT substring(p_conditionnement FROM '(?i)^\s*(?:B|BTE|BO[IÎiî]TE)\s*(?:/|\s+DE\s+)\s*([0-9]{1,5})(?:\s*(?:SACHETS?(?:(?:\s+|-)DOSES?)?|SACH|SCH|AMPOULES?|AMPS?|AMP\.?\s+BUV|CPR?S?|CP\.?\s+EFF|COMPRIM[EÉeé]S?(?:\s+EFFERVESCENTS?)?|G[EÉeé]LULES?|SUPPOSITOIRES?|SUPPO|OVULES?|CAPSULES?|PATCH(?:E?S)?|DISPOSITIFS?\s+TRANSDERMIQUES?|UNIDOSES?|R[EÉeé]CIPIENTS\s+UNIDOSES)\.?(?:\s*(?:DE\s*)?[0-9]+(?:[.,][0-9]+)?\s*(?:ML|MG|G|L))?)?\s*$')::int
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
                          'motif', 'contenance depuis la nomenclature, script 137'),
       m.officine_id
FROM maj m;
