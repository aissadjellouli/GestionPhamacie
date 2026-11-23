-- =====================================================================
-- Retour arrière du script 165 (présence des postes)
--   Supprime la table presence_poste et son classement sync_table, comme avant le script 165.
--   Les lignes de la table sont perdues : elles ne sont pas reprises d'ailleurs, et se reconstituent en une minute quand le serveur
--   principal revoit les postes (si le script est rejoué).
--   Rejouable sans erreur. A lancer seul, hors de la suite numérotée : ce dossier n'est jamais lu par les outils de mise à jour.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

DELETE FROM sync_table WHERE nom_table = 'presence_poste';
DROP TABLE IF EXISTS presence_poste;
