-- =====================================================================
-- Retour arrière du script 167 (liste des postes autorisés)
--   Supprime la colonne presence_poste.poste_id, les tables poste_autorise et parametre_postes et leur classement sync_table,
--   comme avant le script 167.
--   ATTENTION : les enrôlements sont PERDUS (clés publiques, noms, révocations) ; chaque poste devra être réautorisé si le script
--   est rejoué. À ne lancer qu'avec la validation explicite du titulaire, et avec une API revenue à une version sans liste des postes.
--   Rejouable sans erreur. A lancer seul, hors de la suite numérotée : ce dossier n'est jamais lu par les outils de mise à jour.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

DROP INDEX IF EXISTS ix_presence_poste_poste_id;
ALTER TABLE IF EXISTS presence_poste DROP CONSTRAINT IF EXISTS fk_presence_poste_poste_autorise;
ALTER TABLE IF EXISTS presence_poste DROP COLUMN IF EXISTS poste_id;
DELETE FROM sync_table WHERE nom_table IN ('poste_autorise', 'parametre_postes');
DROP TABLE IF EXISTS parametre_postes;
DROP TABLE IF EXISTS poste_autorise;
