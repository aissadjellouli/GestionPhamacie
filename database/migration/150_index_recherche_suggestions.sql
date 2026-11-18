-- =====================================================================
-- 150 — Index de la recherche pendant la frappe (suggestions, décision du titulaire 2026-11-18)
--   La recherche d'un patient et celle d'un médecin ignorent désormais les accents (fonction sans_accent) et les majuscules
--   (ILIKE) : l'expression comparée change, l'index trigrammes existant sur patient.nom (idx_patient_nom_trgm) ne sert plus.
--   1. ix_patient_nom_prenom_sans_accent : trigrammes sur sans_accent(nom || ' ' || prénom), seule expression de la recherche
--      (elle contient le nom, le prénom et « nom prénom »).
--   2. ix_medecin_nom_sans_accent : trigrammes sur sans_accent(nom) du médecin.
--   3. ix_lot_produit_numero : (produit_id, numero_lot) pour les numéros de lot proposés au rappel (tous les lots du produit,
--      épuisés compris : l'index partiel idx_lot_fefo ne couvre que les lots disponibles) ; revue du 2026-11-18.
--   Mesure (EXPLAIN ANALYZE, 2026-11-18) : sur pharmacie_test (223 patients, 46 médecins) la lecture complète prend moins de 1 ms ;
--   sur une copie temporaire de 50 000 patients, 211 ms sans index contre 12 ms avec (« ami », 4 833 réponses) et 0,06 ms pour une
--   saisie sans réponse : l'index est utile dès quelques milliers de patients.
--   Index ordinaires (pas CONCURRENTLY) : le script se rejoue dans la transaction des scripts 034 et suivants ; la création ne
--   bloque les écritures de patient, medecin et lot que le temps de lire ces trois tables.
-- Idempotent et rejouable sur une base plus récente ; aucune donnée modifiée.
-- A exécuter après 149_synchronisation_retour_arriere_nouvelle_epoque.sql.
-- =====================================================================

CREATE INDEX IF NOT EXISTS ix_patient_nom_prenom_sans_accent
    ON patient USING gin (sans_accent(nom || ' ' || COALESCE(prenom, '')) gin_trgm_ops);

CREATE INDEX IF NOT EXISTS ix_medecin_nom_sans_accent
    ON medecin USING gin (sans_accent(nom) gin_trgm_ops);

CREATE INDEX IF NOT EXISTS ix_lot_produit_numero ON lot (produit_id, numero_lot);

COMMENT ON INDEX ix_patient_nom_prenom_sans_accent IS 'Recherche d''un patient sans accents ni majuscules (nom, prénom, « nom prénom ») (script 150).';
COMMENT ON INDEX ix_medecin_nom_sans_accent IS 'Recherche d''un médecin sans accents ni majuscules (script 150).';
COMMENT ON INDEX ix_lot_produit_numero IS 'Numéros de lot d''un produit proposés au rappel, épuisés compris (script 150).';
