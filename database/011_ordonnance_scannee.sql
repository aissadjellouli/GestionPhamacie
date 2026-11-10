-- =====================================================================
-- Ordonnance scannée en caisse : image conservée sur le serveur
-- (ordonnance.chemin_scan, déjà prévu en 001) et texte reconnu par l'OCR.
-- A exécuter après 010_administration.sql. Idempotent.
-- =====================================================================

-- Date d'enregistrement de l'ordonnance au comptoir
ALTER TABLE ordonnance ADD COLUMN IF NOT EXISTS cree_le timestamptz NOT NULL DEFAULT now();

-- Texte lu par la reconnaissance de caractères (aide à la recherche ; l'image fait foi)
ALTER TABLE ordonnance ADD COLUMN IF NOT EXISTS texte_reconnu text;

-- Un nom de fichier d'image ne sert qu'à une seule ordonnance
CREATE UNIQUE INDEX IF NOT EXISTS ux_ordonnance_chemin_scan ON ordonnance (chemin_scan) WHERE chemin_scan IS NOT NULL;
