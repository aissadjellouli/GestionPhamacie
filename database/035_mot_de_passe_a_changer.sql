-- =====================================================================
-- Mot de passe à changer à la première connexion : le compte titulaire
-- créé par la mise en production (database/init/seed_referentiel.sql)
-- reçoit un mot de passe provisoire ; tant qu'il n'est pas changé, le
-- serveur refuse toute action protégée par un droit.
-- Idempotent. A exécuter après 034_nomenclature_absents.sql.
-- =====================================================================

ALTER TABLE utilisateur ADD COLUMN IF NOT EXISTS doit_changer_mot_de_passe boolean NOT NULL DEFAULT false;
