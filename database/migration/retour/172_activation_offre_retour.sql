-- =====================================================================
-- Retour arrière du script 172 (activation de l'offre signée par l'éditeur)
--   Supprime activation_offre, sa fonction et son classement sync_table, comme avant le script 172.
--   ATTENTION : les activations saisies sont PERDUES (elles se ressaisissent depuis les codes de l'éditeur). L'API doit revenir à une
--   version qui lit parametre_postes.maximum_postes (colonne jamais supprimée ; son commentaire est retiré, comme avant 172).
--   Rejouable sans erreur. A lancer seul, hors de la suite numérotée : ce dossier n'est jamais lu par les outils de mise à jour.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

DELETE FROM sync_table WHERE nom_table = 'activation_offre';
DROP FUNCTION IF EXISTS activation_offre_signaler_refus(bigint, text);
DROP TABLE IF EXISTS activation_offre;
COMMENT ON COLUMN parametre_postes.maximum_postes IS NULL;
