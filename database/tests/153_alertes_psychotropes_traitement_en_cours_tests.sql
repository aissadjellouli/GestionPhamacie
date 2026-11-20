-- =====================================================================
-- Tests du script 153 (alertes psychotropes : « traitement en cours », période « sans limite », valeurs de départ),
-- en transaction ANNULÉE, depuis la racine du dépôt :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/153_alertes_psychotropes_traitement_en_cours_tests.sql
-- Le script 153 est rejoué dans la transaction (valeurs remises à celles d'origine avant) ; rien n'est gardé (ROLLBACK).
-- =====================================================================
BEGIN;
SET LOCAL pharma.reglage_central = 'on';

-- 1. Règle présente, période vide acceptée seulement pour les deux règles prévues
DO $$
BEGIN
    ASSERT (SELECT active AND exiger_valideur AND periode_jours IS NULL FROM regle_alerte_psychotrope WHERE code = 'traitement_en_cours'),
        'traitement_en_cours : active, valideur exigé, période vide';
    UPDATE regle_alerte_psychotrope SET periode_jours = NULL WHERE code = 'ordonnance_reutilisee';
    BEGIN
        UPDATE regle_alerte_psychotrope SET periode_jours = NULL WHERE code = 'patient_repete';
        RAISE EXCEPTION 'période vide acceptée pour patient_repete';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO regle_alerte_psychotrope (code, libelle, seuil, periode_jours) VALUES ('autre_regle', 'X', 1, 1);
        RAISE EXCEPTION 'code inconnu accepté';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
END $$;

-- 2. Valeurs d'origine (117), une règle modifiée par le titulaire, puis rejeu du script 153
UPDATE regle_alerte_psychotrope SET active = true, seuil = 2, periode_jours = 30, modifie_le = NULL WHERE code = 'patient_repete';
UPDATE regle_alerte_psychotrope SET active = true, seuil = 1, periode_jours = 365, modifie_le = NULL WHERE code = 'ordonnance_reutilisee';
UPDATE regle_alerte_psychotrope SET active = true, seuil = 3, periode_jours = 28, modifie_le = NULL WHERE code = 'quantite_anormale';
UPDATE regle_alerte_psychotrope SET active = true, seuil = 1, periode_jours = 365, modifie_le = now() WHERE code = 'prescripteur_inhabituel';
DELETE FROM regle_alerte_psychotrope WHERE code = 'traitement_en_cours';
\ir ../migration/153_alertes_psychotropes_traitement_en_cours.sql
\ir ../migration/153_alertes_psychotropes_traitement_en_cours.sql

DO $$
BEGIN
    ASSERT (SELECT NOT active FROM regle_alerte_psychotrope WHERE code = 'patient_repete'), 'patient_repete jamais modifiée : désactivée';
    ASSERT (SELECT periode_jours IS NULL AND seuil = 1 FROM regle_alerte_psychotrope WHERE code = 'ordonnance_reutilisee'), 'ordonnance : sans limite';
    ASSERT (SELECT seuil = 2 AND periode_jours = 28 FROM regle_alerte_psychotrope WHERE code = 'quantite_anormale'), 'quantité : plus de 2 boîtes';
    ASSERT (SELECT active FROM regle_alerte_psychotrope WHERE code = 'prescripteur_inhabituel'), 'prescripteur modifié par le titulaire : gardé actif';
    ASSERT (SELECT count(*) FROM regle_alerte_psychotrope WHERE code = 'traitement_en_cours') = 1, 'traitement_en_cours recréée une fois';
    ASSERT (SELECT modifie_le IS NULL FROM regle_alerte_psychotrope WHERE code = 'patient_repete'), 'valeur de départ : pas notée comme modification';
END $$;

ROLLBACK;
\echo '153 : tous réussis'
