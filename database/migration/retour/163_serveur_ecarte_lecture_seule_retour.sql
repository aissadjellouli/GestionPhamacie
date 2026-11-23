-- =====================================================================
-- Retour arrière du script 163 (état « serveur écarté »)
--   Supprime la table serveur_ecarte, sa fonction de protection et son classement sync_table, comme avant le script 163.
--   Les lignes de la table (écarts notés depuis le script 163) sont perdues : le script 163 ne les avait pas copiées d'ailleurs.
--   Rejouable sans erreur. A lancer seul, hors de la suite numérotée : ce dossier n'est jamais lu par les outils de mise à jour.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

DELETE FROM sync_table WHERE nom_table = 'serveur_ecarte';
DROP TABLE IF EXISTS serveur_ecarte;
DROP FUNCTION IF EXISTS serveur_ecarte_proteger();
