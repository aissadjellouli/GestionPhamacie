-- =====================================================================
-- Retour arrière du script 162 (déconnexion après inactivité : 15 minutes par défaut)
--   Remet la valeur par défaut de parametre_caisse.deconnexion_minutes à 0 (déconnexion désactivée), comme avant le script 162.
--   Aucune donnée modifiée (le script 162 n'en avait modifié aucune). Rejouable sans erreur.
--   A lancer seul, hors de la suite numérotée : ce dossier n'est jamais lu par les outils de mise à jour.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

ALTER TABLE parametre_caisse ALTER COLUMN deconnexion_minutes SET DEFAULT 0;
