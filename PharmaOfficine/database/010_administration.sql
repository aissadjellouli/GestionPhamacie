-- =====================================================================
-- Administration : identité de la pharmacie, délai de reprise des
-- grossistes, contrôles des seuils, protection du rôle titulaire.
-- A exécuter après 009_caisse.sql. Idempotent.
-- =====================================================================

-- ---------------------------------------------------------------------
-- 1. Identité de la pharmacie (une seule ligne) : en-tête du poste,
--    documents imprimés (tickets, rapports, bons de commande)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS pharmacie (
    id              int  PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    nom             text,
    titulaire       text,
    adresse         text,
    telephone       text,
    nif             text,
    chifa_officine  text,                                   -- n° officine et centre payeur Chifa (format libre)
    langue_defaut   text NOT NULL DEFAULT 'fr' CHECK (langue_defaut IN ('ar', 'fr', 'en'))
);
INSERT INTO pharmacie DEFAULT VALUES ON CONFLICT (id) DO NOTHING;

-- ---------------------------------------------------------------------
-- 2. Délai de reprise de chaque grossiste avant péremption (information
--    utile pour régler ses seuils d'alerte)
-- ---------------------------------------------------------------------
ALTER TABLE fournisseur ADD COLUMN IF NOT EXISTS delai_reprise_jours int CHECK (delai_reprise_jours > 0);

-- ---------------------------------------------------------------------
-- 3. Paramètres d'alerte : des durées positives
-- ---------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_parametre_alerte_positifs') THEN
        ALTER TABLE parametre_alerte ADD CONSTRAINT ck_parametre_alerte_positifs CHECK (
            jours_orange > 0 AND jours_rouge > 0 AND horizon_risque_jours > 0 AND fenetre_conso_jours > 0
            AND delai_fournisseur_jours >= 0 AND jours_securite >= 0 AND jours_cible_commande > 0
            AND jours_dormant > 0 AND jours_surstock > 0);
    END IF;
END $$;

-- ---------------------------------------------------------------------
-- 4. Le rôle système (titulaire) ne se modifie ni ne se supprime
--    (ses droits étaient déjà protégés par trg_proteger_role_systeme)
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION proteger_ligne_role_systeme() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.systeme THEN
        RAISE EXCEPTION 'Le rôle titulaire ne peut être ni modifié ni supprimé';
    END IF;
    RETURN COALESCE(NEW, OLD);
END;
$$;

CREATE OR REPLACE TRIGGER trg_role_systeme
    BEFORE UPDATE OR DELETE ON role
    FOR EACH ROW EXECUTE FUNCTION proteger_ligne_role_systeme();
