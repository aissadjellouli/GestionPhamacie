-- =====================================================================
-- Retour arrière du script 174 (postes autorisés : preuve toujours exigée)
--   Remet la valeur par défaut de parametre_postes.mode à 'transition' (comme le script 167) et retire le commentaire de la colonne.
--   La valeur de la ligne n'est PAS changée : elle reste 'exige' (repasser en « transition » est un choix du titulaire, à faire dans
--   Administration › Postes autorisés avec une version de l'API d'avant 174). Aucune autre donnée touchée.
--   Rejouable sans erreur. A lancer seul, hors de la suite numérotée : ce dossier n'est jamais lu par les outils de mise à jour.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

ALTER TABLE parametre_postes ALTER COLUMN mode SET DEFAULT 'transition';
COMMENT ON COLUMN parametre_postes.mode IS NULL;
