-- =====================================================================
-- 139 — Contenance : flacons, seringues, stylos, cartouches, poches, tubes
-- (2026-11-16, suite des scripts 132, 134, 136 et 137 ; décision du titulaire :
-- « remplis-les »).
--   Constat sur dev : environ 850 produits ont un conditionnement de la forme
--   « B/01 FL », « B/10FL. DE 20ML », « B/01 FLACON DE 125ML », « B/1 SERINGUE »,
--   « B/05 STYLOS PREREMPLIS DE 3ML », « B/50CART. DE 1,8ML », « B/04 POCHES DE 2L »,
--   « B/20AMP./5ML », « B/1AMP. (5ML) » : le nombre qui suit « B/ » compte les
--   contenants de la boîte ; la taille qui suit n'est que celle d'un contenant.
--   Ces textes étaient laissés vides par prudence (134, 137).
--
--   Extension du motif (identique à ContenanceProduit.Boite(), src/Pharma.Application/Catalogue/ContenanceProduit.cs) :
--   * nouveaux contenants après « B/ <nombre> » : FL, FL., FLC, FLACON(S), SERINGUE(S),
--     SERING., SERIN, SER (éventuellement suivis de PRE-REMPLI(E)(S), PREREMPL.),
--     STYLO(S) (idem), CART., CARTOUCHE(S), POCHE(S), TUBE(S) ;
--   * taille du contenant : « [DE] <nombre> ML | MG | G | L » comme au 137, ou après
--     une barre « /5ML » (« B/20AMP./5ML »), ou entre parenthèses « (5ML) » ; ces deux
--     formes valent aussi pour les unités déjà reconnues (ampoules, sachets, comprimés…).
--   Restent VIDES : plusieurs nombres ou conditionnements (« B/1+1 », « B/30 ET B/90 »),
--   contenant en tête sans « B/ » (« FL/05ML », « T/15G »), « B/FL 15 ML », flacon de poudre
--   (« B/1FL. DE PDRE. »), taille sans unité (« B/5 AMP. DE 2 »), tout autre texte à la suite
--   (« + PIPETTE », « CONTENANT 500 MG »…). Mêmes garde-fous que 134 et 137 : nombre de 1 à
--   10000, valeur écrite ≤ 1000, toutes les lignes du n° d'enregistrement d'accord (version
--   la plus récente), sinon vide.
--
--   Données : ne remplit QUE les produits dont produit.contenance est NULL ; une
--   valeur saisie ou déjà remplie n'est jamais touchée. Trace : une ligne
--   « produit_modifie » par produit rempli dans journal_audit (comme 134 et 137),
--   motif « contenance depuis la nomenclature, script 139 ».
--   Structure : seule la fonction contenance_depuis_conditionnement est remplacée
--   (CREATE OR REPLACE, même signature). Aucune suppression. Idempotent.
--   Rejouer un script antérieur (136, 137) ramènerait l'ancien motif : rejouer ensuite 139.
-- =====================================================================

CREATE OR REPLACE FUNCTION contenance_depuis_conditionnement(p_conditionnement text) RETURNS int
LANGUAGE sql IMMUTABLE AS $$
    -- motif de ContenanceProduit.Boite(), recopié tel quel (test ContenanceDepuisNomenclatureTests)
    SELECT substring(p_conditionnement FROM '(?i)^\s*(?:B|BTE|BO[IÎiî]TE)\s*(?:/|\s+DE\s+)\s*([0-9]{1,5})(?:\s*(?:SACHETS?(?:(?:\s+|-)DOSES?)?|SACH|SCH|AMPOULES?|AMPS?|AMP\.?\s+BUV|CPR?S?|CP\.?\s+EFF|COMPRIM[EÉeé]S?(?:\s+EFFERVESCENTS?)?|G[EÉeé]LULES?|SUPPOSITOIRES?|SUPPO|OVULES?|CAPSULES?|PATCH(?:E?S)?|DISPOSITIFS?\s+TRANSDERMIQUES?|UNIDOSES?|R[EÉeé]CIPIENTS\s+UNIDOSES|FLACONS?|FLC|FL|(?:SERINGUES?|SERING\.?|SERIN|SER)(?:\s*PR[EÉeé][\s-]*REMPL[A-Za-zÉéÈè]*\.?)?|STYLOS?(?:\s*PR[EÉeé][\s-]*REMPL[A-Za-zÉéÈè]*\.?)?|CART(?:OUCHES?)?|POCHES?|TUBES?)\.?(?:\s*(?:DE\s*|/\s*)?[0-9]+(?:[.,][0-9]+)?\s*(?:ML|MG|G|L)|\s*\(\s*[0-9]+(?:[.,][0-9]+)?\s*(?:ML|MG|G|L)\s*\))?)?\s*$')::int
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
                          'motif', 'contenance depuis la nomenclature, script 139'),
       m.officine_id
FROM maj m;
