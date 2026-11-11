-- =====================================================================
-- Affichage du poste choisi par chaque employé : clair, sombre, ou
-- automatique (suit le réglage de Windows). Retrouvé à chaque connexion,
-- par mot de passe ou par code comptoir.
-- Idempotent. A exécuter après 024_registre_psychotropes.sql.
-- =====================================================================

ALTER TABLE utilisateur ADD COLUMN IF NOT EXISTS affichage text NOT NULL DEFAULT 'auto';
ALTER TABLE utilisateur DROP CONSTRAINT IF EXISTS ck_utilisateur_affichage;
ALTER TABLE utilisateur ADD CONSTRAINT ck_utilisateur_affichage CHECK (affichage IN ('auto', 'clair', 'sombre'));
COMMENT ON COLUMN utilisateur.affichage IS 'Affichage du poste : auto (suit Windows), clair ou sombre';
