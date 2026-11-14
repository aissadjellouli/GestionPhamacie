-- =====================================================================
-- Affichage du poste choisi par chaque employé : clair, sombre, ou
-- automatique (suit le réglage de Windows). Retrouvé à chaque connexion,
-- par mot de passe ou par code comptoir.
-- Idempotent. A exécuter après 024_registre_psychotropes.sql.
-- =====================================================================

ALTER TABLE utilisateur ADD COLUMN IF NOT EXISTS affichage text NOT NULL DEFAULT 'auto';
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_utilisateur_affichage') THEN
        ALTER TABLE utilisateur ADD CONSTRAINT ck_utilisateur_affichage CHECK (affichage IN ('auto', 'clair', 'sombre'));
    END IF;
END $$;
COMMENT ON COLUMN utilisateur.affichage IS 'Affichage du poste : auto (suit Windows), clair ou sombre';
