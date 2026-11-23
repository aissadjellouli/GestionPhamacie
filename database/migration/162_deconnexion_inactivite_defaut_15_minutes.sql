-- =====================================================================
-- 162 -- Déconnexion après inactivité : 15 minutes par défaut pour les nouvelles installations (décision du titulaire, 2026-11-23)
--   Avant : parametre_caisse.deconnexion_minutes valait 0 par défaut (déconnexion désactivée).
--   Maintenant : la valeur par défaut de la colonne est 15 ; la ligne unique de parametre_caisse créée par une installation neuve
--   (seed_referentiel.sql : INSERT ... DEFAULT VALUES) reprend donc 15.
--   Aucune donnée modifiée : la ligne existante des bases déjà installées garde sa valeur. La base ne dit pas si le titulaire a
--   réglé cette durée lui-même (0 voulu ou 0 jamais touché) ; elle n'est donc pas changée d'office : le titulaire la règle dans
--   Administration › Caisse (0 = désactivée, 1 à 240 minutes).
--   Retour arrière : database/migration/retour/162_deconnexion_inactivite_defaut_15_minutes_retour.sql (remet le défaut à 0).
-- Idempotent et rejouable sur une base plus récente.
-- A exécuter après 161_index_cles_etrangeres_manquantes.sql.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

ALTER TABLE parametre_caisse ALTER COLUMN deconnexion_minutes SET DEFAULT 15;
