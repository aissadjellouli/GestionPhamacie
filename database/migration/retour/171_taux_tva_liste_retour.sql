-- =====================================================================
-- Retour arrière du script 171 (taux de TVA réglables)
--   Supprime taux_tva_categorie et taux_tva et leur classement sync_table, comme avant le script 171.
--   ATTENTION : la liste des taux et les taux par défaut des catégories sont PERDUS ; les taux des produits, des prestations et des
--   factures ne changent pas. L'API doit revenir à une version sans liste des taux.
--   Rejouable sans erreur. A lancer seul, hors de la suite numérotée : ce dossier n'est jamais lu par les outils de mise à jour.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

DELETE FROM sync_table WHERE nom_table IN ('taux_tva', 'taux_tva_categorie');
DROP TABLE IF EXISTS taux_tva_categorie;
DROP TABLE IF EXISTS taux_tva;
