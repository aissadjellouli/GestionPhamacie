-- =====================================================================
-- Contenance des produits depuis la nomenclature nationale (2026-11-16)
--   Inscrit dans produit.contenance (unités par boîte) la valeur que la fiche
--   produit propose déjà en grisé, pour les seuls produits dont la contenance
--   est VIDE (NULL) : une valeur saisie par le pharmacien n'est jamais écrasée.
--
--   Règle d'extraction : identique à ContenanceProduit.DepuisNomenclature
--   (src/Pharma.Application/Catalogue/ContenanceProduit.cs, lue par
--   ProduitsModificationSql pour la fiche produit) :
--     * lignes lues : nomenclature_produit du même n° d'enregistrement que le
--       produit, dans la version la plus récente de ce n° (max(version_id)) ;
--     * motif reconnu, sans tenir compte de la casse, espaces de début et de
--       fin tolérés : « B/30 », « B/030 », « B / 30 », « BTE/30 »,
--       « BOITE DE 30 », « BOÎTE DE 30 », « B DE 30 » ; nombre de 1 à 5
--       chiffres, compris entre 1 et 10000 (contrainte ck_produit_contenance) ;
--     * toutes les lignes du n° doivent donner la même valeur reconnue ; une
--       seule ligne non reconnue ou une valeur différente : rien n'est retenu.
--   Exclusions (produit laissé vide, à saisir par le pharmacien) :
--     * texte non reconnu : plusieurs nombres (« B/1+1 », « B/30 ET B/90 »),
--       flacon, tube, volume ou masse (« FL/10ML », « T/15G »,
--       « B/2 FL DE 100 ML », « B/1FL. DE PDRE. »), unité précisée après le
--       nombre (« B/30 SACHETS ») : le nombre ne compte pas des unités de
--       délivrance sûres ;
--     * conditionnement vide ;
--     * valeur 0 (hors 1..10000, la fiche ne la propose pas non plus) ;
--     * lignes de la nomenclature en désaccord (« B/20 » et « B/30 » pour le
--       même n°) : plusieurs conditionnements possibles ;
--     * PRUDENCE PROPRE À CE SCRIPT : valeur supérieure à 1000 (la fiche la
--       propose, mais une inscription en masse sans contrôle n'est pas sûre
--       au-delà ; elle reste proposée en grisé sur la fiche).
--   Trace : une ligne « produit_modifie » par produit rempli dans
--   journal_audit (utilisateur vide : mise à jour par script), détail
--   {produit, contenance {avant, apres}, motif}.
--   Données seulement (aucun changement de schéma). Aucune suppression.
--   Idempotent : un 2e passage ne trouve plus de contenance vide à remplir
--   (0 ligne). Après 131.
-- =====================================================================

WITH lignes AS (
    SELECT p.id,
           -- motif de ContenanceProduit.Boite() : (?i)^\s*(?:B|BTE|BOITE|BOÎTE)\s*(?:/|\s+DE\s+)\s*(\d{1,5})\s*$
           substring(n.conditionnement FROM '(?i)^\s*(?:B|BTE|BOITE|BOÎTE)\s*(?:/|\s+DE\s+)\s*([0-9]{1,5})\s*$')::int AS nombre
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
                          'motif', 'contenance depuis la nomenclature, script 132'),
       m.officine_id
FROM maj m;
