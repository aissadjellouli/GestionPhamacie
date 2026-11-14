-- =====================================================================
-- Don et remise réglés par personne (décision du titulaire, employé par employé)
--   * sur chaque employé : « peut faire un don » (peut_donner), remise
--     maximale en % (remise_max_pct, 0 = aucune remise) et « gère les
--     remises automatiques » (gere_remises_auto) ; le titulaire a toujours
--     tout, sans réglage ;
--   * droits « par personne » (permission.par_personne) : stock.donner,
--     caisse.remise et le nouveau caisse.remises_auto ; ils sortent de la
--     matrice des droits par rôle ;
--   * reprise : à la première exécution, chaque employé reçoit ce que son
--     rôle lui donnait (droit de don, remise maximale du rôle) : rien ne
--     change le jour de la mise à jour ;
--   * a_permission lit ces trois droits sur l'employé ; sans le droit,
--     l'autorisation sur place par le code d'un employé qui l'a reste
--     possible (la remise : jusqu'au maximum de celui qui valide).
-- role.remise_max_pct n'est plus lu (gardé pour le rejeu des scripts).
-- Idempotent. A exécuter après 070_comptage_intermediaire.sql.
-- =====================================================================

-- Colonnes de l'employé, et reprise des réglages du rôle à la première exécution seulement
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_schema = 'public' AND table_name = 'utilisateur' AND column_name = 'remise_max_pct') THEN
        ALTER TABLE utilisateur ADD COLUMN peut_donner boolean NOT NULL DEFAULT false;
        ALTER TABLE utilisateur ADD COLUMN remise_max_pct numeric(5,2) NOT NULL DEFAULT 0
            CONSTRAINT ck_utilisateur_remise_max CHECK (remise_max_pct >= 0 AND remise_max_pct <= 100 AND remise_max_pct = trunc(remise_max_pct));
        ALTER TABLE utilisateur ADD COLUMN gere_remises_auto boolean NOT NULL DEFAULT false;

        UPDATE utilisateur u
        SET remise_max_pct = CASE WHEN r.systeme THEN 100 ELSE r.remise_max_pct END,
            peut_donner = r.systeme OR EXISTS (SELECT 1 FROM role_permission rp
                                               WHERE rp.role_id = r.id AND rp.permission_code = 'stock.donner'),
            gere_remises_auto = r.systeme
        FROM role r
        WHERE r.id = u.role_id;
    END IF;
END $$;

COMMENT ON COLUMN utilisateur.peut_donner IS 'Peut faire un don (boîtes données, don partiel du reste à charge) sans autorisation ; réglé par le titulaire';
COMMENT ON COLUMN utilisateur.remise_max_pct IS 'Remise maximale (%, entier) accordée seul ; au-delà, code d''un employé dont le maximum le permet ; réglé par le titulaire';
COMMENT ON COLUMN utilisateur.gere_remises_auto IS 'Peut créer, modifier et arrêter les remises automatiques ; réglé par le titulaire';

-- Droits réglés par personne
ALTER TABLE permission ADD COLUMN IF NOT EXISTS par_personne boolean NOT NULL DEFAULT false;
COMMENT ON COLUMN permission.par_personne IS 'Droit réglé sur chaque employé (utilisateur), pas dans la matrice des rôles';

INSERT INTO permission (code, module, libelle, sensible)
VALUES ('caisse.remises_auto', 'Caisse', 'Gérer les remises automatiques', true)
ON CONFLICT (code) DO NOTHING;

UPDATE permission SET par_personne = true
WHERE code IN ('caisse.remise', 'stock.donner', 'caisse.remises_auto') AND NOT par_personne;

-- Ces droits ne sont plus attribués par rôle (le titulaire les a toujours : rôle système)
DELETE FROM role_permission rp
USING role r
WHERE r.id = rp.role_id AND NOT r.systeme
  AND rp.permission_code IN ('caisse.remise', 'stock.donner', 'caisse.remises_auto');

-- Rôle système : le nouveau droit est aussi inscrit (matrice et listes complètes) ; déclencheur levé le temps de l'insertion
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM role_permission rp JOIN role r ON r.id = rp.role_id
                   WHERE r.systeme AND rp.permission_code = 'caisse.remises_auto') THEN
        ALTER TABLE role_permission DISABLE TRIGGER trg_proteger_role_systeme;
        INSERT INTO role_permission (role_id, permission_code)
        SELECT r.id, 'caisse.remises_auto' FROM role r WHERE r.systeme
        ON CONFLICT DO NOTHING;
        ALTER TABLE role_permission ENABLE TRIGGER trg_proteger_role_systeme;
    END IF;
END $$;

-- Vérification d'un droit : les droits « par personne » se lisent sur l'employé, les autres sur son rôle
CREATE OR REPLACE FUNCTION a_permission(p_utilisateur_id bigint, p_code text)
RETURNS boolean
LANGUAGE sql STABLE
AS $$
    SELECT CASE
        WHEN p_code IN ('stock.donner', 'caisse.remise', 'caisse.remises_auto') THEN EXISTS (
            SELECT 1
            FROM utilisateur u
            JOIN role r ON r.id = u.role_id
            WHERE u.id = p_utilisateur_id
              AND u.actif
              AND (r.systeme
                   OR (p_code = 'stock.donner' AND u.peut_donner)
                   OR (p_code = 'caisse.remise' AND u.remise_max_pct > 0)
                   OR (p_code = 'caisse.remises_auto' AND u.gere_remises_auto)))
        ELSE EXISTS (
            SELECT 1
            FROM utilisateur u
            JOIN role_permission rp ON rp.role_id = u.role_id
            WHERE u.id = p_utilisateur_id
              AND u.actif
              AND rp.permission_code = p_code)
    END;
$$;
