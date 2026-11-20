-- =====================================================================
-- 153 : alertes du registre des psychotropes, décisions du titulaire du
-- 2026-11-19 (précisées le 2026-11-20).
--   1. Nouvelle règle « traitement_en_cours » : alerte quand le patient
--      revient alors qu'un psychotrope (quel qu'il soit) déjà délivré est
--      encore en cours de traitement (jour de délivrance + durée prescrite
--      après aujourd'hui). Active, valideur exigé ; seuil et période sans
--      objet (période vide). La durée du traitement devient obligatoire à
--      la caisse pour un psychotrope ou stupéfiant (code de l'application).
--   2. Période vide acceptée pour « ordonnance_reutilisee » (« sans
--      limite ») et « traitement_en_cours » (sans objet) ; obligatoire pour
--      les autres règles.
--   3. Nouvelles valeurs de départ, posées SEULEMENT sur une règle jamais
--      modifiée (modifie_le vide, valeurs d'origine des scripts 117/118) et
--      si la table n'est pas imposée par le serveur central :
--        patient_repete           : désactivée (remplacée par la règle 1)
--        ordonnance_reutilisee    : 1 fois, sans limite de durée
--        quantite_anormale        : plus de 2 boîtes (28 jours inchangé)
--        prescripteur_inhabituel  : désactivée
--      Un réglage déjà changé par le titulaire (ou reçu du central) est gardé.
-- Écritures sous le marqueur pharma.reglage_central (script, non noté
-- comme modification locale). Idempotent. Aucune donnée supprimée.
-- Après 117, 118 et 141.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

DO $$
BEGIN
    -- ---- 1. Codes : ajout de « traitement_en_cours » (contrainte remplacée, mêmes codes plus celui-ci)
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_regle_alerte_psy_code'
                   AND pg_get_constraintdef(oid) LIKE '%traitement_en_cours%') THEN
        ALTER TABLE regle_alerte_psychotrope DROP CONSTRAINT IF EXISTS ck_regle_alerte_psy_code;
        ALTER TABLE regle_alerte_psychotrope ADD CONSTRAINT ck_regle_alerte_psy_code
            CHECK (code IN ('patient_repete', 'ordonnance_reutilisee', 'quantite_anormale', 'prescripteur_inhabituel',
                            'prescripteur_hors_zone', 'traitement_en_cours'));
    END IF;

    -- ---- 2. Période vide seulement pour les deux règles qui l'acceptent
    ALTER TABLE regle_alerte_psychotrope ALTER COLUMN periode_jours DROP NOT NULL;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_regle_alerte_psy_periode_vide') THEN
        ALTER TABLE regle_alerte_psychotrope ADD CONSTRAINT ck_regle_alerte_psy_periode_vide
            CHECK (periode_jours IS NOT NULL OR code IN ('ordonnance_reutilisee', 'traitement_en_cours'));
    END IF;
END;
$$;

DO $$
DECLARE
    v_impose boolean := reglage_mode_effectif('regle_alerte_psychotrope') = 'impose';   -- script 141
BEGIN
    -- Script : pas une modification locale du titulaire (garde reglage_garde, scripts 141 et 142)
    PERFORM set_config('pharma.reglage_central', 'on', true);

    -- ---- 1. Nouvelle règle (rang 6, après « hors zone »)
    INSERT INTO regle_alerte_psychotrope (code, libelle, active, seuil, periode_jours, exiger_valideur, ordre)
    VALUES ('traitement_en_cours', 'Patient encore sous traitement (psychotrope déjà délivré, durée prescrite non écoulée)',
            true, 1, NULL, true, 6)
    ON CONFLICT (code) DO NOTHING;

    -- ---- 3. Valeurs de départ, sur les règles jamais modifiées seulement (table non imposée par le central)
    IF NOT v_impose THEN
        UPDATE regle_alerte_psychotrope SET active = false
         WHERE code = 'patient_repete' AND modifie_le IS NULL AND active AND seuil = 2 AND periode_jours = 30;
        UPDATE regle_alerte_psychotrope SET periode_jours = NULL
         WHERE code = 'ordonnance_reutilisee' AND modifie_le IS NULL AND seuil = 1 AND periode_jours = 365;
        UPDATE regle_alerte_psychotrope SET seuil = 2
         WHERE code = 'quantite_anormale' AND modifie_le IS NULL AND seuil = 3 AND periode_jours = 28;
        UPDATE regle_alerte_psychotrope SET active = false
         WHERE code = 'prescripteur_inhabituel' AND modifie_le IS NULL AND active AND seuil = 1 AND periode_jours = 365;
    ELSE
        RAISE NOTICE '153 : règles des psychotropes imposées par le serveur central, valeurs de départ inchangées.';
    END IF;
END;
$$;

COMMENT ON COLUMN regle_alerte_psychotrope.periode_jours IS
    'Période en jours (1 à 3650) ; vide : sans limite (ordonnance_reutilisee) ou sans objet (traitement_en_cours), script 153.';
