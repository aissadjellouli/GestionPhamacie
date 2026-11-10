-- =====================================================================
-- Gestion des droits : rôles personnalisables et permissions par action
-- A exécuter après schema_pharmacie.sql
-- =====================================================================

-- ---------------------------------------------------------------------
-- 1. Catalogue des permissions (fixé par le logiciel)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS permission (
    code        text PRIMARY KEY,
    module      text    NOT NULL,
    libelle     text    NOT NULL,
    sensible    boolean NOT NULL DEFAULT false   -- inscrite au journal, code du titulaire si non accordée
);

INSERT INTO permission (code, module, libelle, sensible) VALUES
    ('caisse.vendre',               'Caisse',      'Encaisser une vente',                        false),
    ('caisse.supprimer_ligne',      'Caisse',      'Supprimer une ligne après scan',             true),
    ('caisse.annuler_vente',        'Caisse',      'Annuler une vente validée',                  true),
    ('caisse.remise',               'Caisse',      'Accorder une remise',                        true),
    ('caisse.retour_especes',       'Caisse',      'Rembourser un retour en espèces',            true),
    ('caisse.ouvrir_tiroir',        'Caisse',      'Ouvrir le tiroir sans vente',                true),
    ('caisse.voir_ecart',           'Caisse',      'Voir le montant attendu et les écarts',      true),
    ('credit.accorder',             'Crédits',     'Mettre une vente en crédit',                 false),
    ('credit.depasser_plafond',     'Crédits',     'Dépasser le plafond de crédit d''un client', true),
    ('psychotropes.delivrer',       'Psychotropes','Délivrer un psychotrope',                    true),
    ('psychotropes.registre',       'Psychotropes','Consulter le registre',                      false),
    ('stock.consulter',             'Stock',       'Consulter le stock',                         false),
    ('stock.ajuster',               'Stock',       'Corriger le stock (inventaire)',             true),
    ('stock.declarer_perime',       'Stock',       'Déclarer un produit périmé ou cassé',        true),
    ('prix.modifier',               'Stock',       'Modifier un prix de vente',                  true),
    ('achats.commander',            'Achats',      'Passer une commande',                        false),
    ('achats.receptionner',         'Achats',      'Réceptionner une livraison',                 false),
    ('achats.retour_fournisseur',   'Achats',      'Faire un retour fournisseur',                false),
    ('chifa.importer',              'Chifa',       'Importer les factures Chifa',                false),
    ('chifa.bordereaux',            'Chifa',       'Gérer les bordereaux et rejets',             false),
    ('rapports.ventes',             'Rapports',    'Voir les rapports de ventes',                false),
    ('rapports.marges',             'Rapports',    'Voir les prix d''achat et les marges',       true),
    ('admin.parametres',            'Administration','Modifier les paramètres',                  true),
    ('admin.utilisateurs',          'Administration','Gérer les utilisateurs et les droits',     true),
    ('admin.sauvegardes',           'Administration','Gérer les sauvegardes',                    true)
-- Catalogue fixé par le logiciel : une réexécution remet les libellés à jour
ON CONFLICT (code) DO UPDATE
    SET module = EXCLUDED.module, libelle = EXCLUDED.libelle, sensible = EXCLUDED.sensible;

-- ---------------------------------------------------------------------
-- 2. Rôles, modifiables par le titulaire
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS role (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code                text    NOT NULL UNIQUE,
    nom                 text    NOT NULL,
    systeme             boolean NOT NULL DEFAULT false,   -- le rôle titulaire ne peut être ni modifié ni supprimé
    remise_max_pct      numeric(5,2) NOT NULL DEFAULT 0 CHECK (remise_max_pct BETWEEN 0 AND 100)
);

CREATE TABLE IF NOT EXISTS role_permission (
    role_id         bigint NOT NULL REFERENCES role(id) ON DELETE CASCADE,
    permission_code text   NOT NULL REFERENCES permission(code),
    PRIMARY KEY (role_id, permission_code)
);

INSERT INTO role (code, nom, systeme, remise_max_pct) VALUES
    ('titulaire',  'Pharmacien titulaire', true,  100),
    ('assistant',  'Pharmacien assistant', false, 10),
    ('vendeur',    'Vendeur',              false, 0),
    ('stagiaire',  'Stagiaire',            false, 0)
-- Les rôles existants gardent les réglages du titulaire
ON CONFLICT (code) DO NOTHING;

-- Droits de départ : posés uniquement pour un rôle qui n'a encore aucun droit,
-- pour qu'une réexécution n'écrase jamais les choix du titulaire.

-- Titulaire : tout
INSERT INTO role_permission
SELECT r.id, p.code FROM role r CROSS JOIN permission p
WHERE r.code = 'titulaire'
  AND NOT EXISTS (SELECT 1 FROM role_permission x WHERE x.role_id = r.id);

-- Pharmacien assistant : tout sauf administration et marges
INSERT INTO role_permission
SELECT r.id, p.code FROM role r CROSS JOIN permission p
WHERE r.code = 'assistant'
  AND p.module <> 'Administration'
  AND p.code NOT IN ('rapports.marges', 'caisse.voir_ecart', 'prix.modifier')
  AND NOT EXISTS (SELECT 1 FROM role_permission x WHERE x.role_id = r.id);

-- Vendeur : comptoir et réception
INSERT INTO role_permission
SELECT r.id, p.code FROM role r CROSS JOIN permission p
WHERE r.code = 'vendeur'
  AND p.code IN ('caisse.vendre', 'credit.accorder', 'stock.consulter',
                 'achats.receptionner', 'chifa.importer')
  AND NOT EXISTS (SELECT 1 FROM role_permission x WHERE x.role_id = r.id);

-- Stagiaire : vente simple uniquement
INSERT INTO role_permission
SELECT r.id, p.code FROM role r CROSS JOIN permission p
WHERE r.code = 'stagiaire'
  AND p.code IN ('caisse.vendre', 'stock.consulter')
  AND NOT EXISTS (SELECT 1 FROM role_permission x WHERE x.role_id = r.id);

-- ---------------------------------------------------------------------
-- 3. Rattacher les utilisateurs aux rôles (remplace l'ancienne colonne role)
-- ---------------------------------------------------------------------
ALTER TABLE utilisateur ADD COLUMN IF NOT EXISTS role_id  bigint REFERENCES role(id);
ALTER TABLE utilisateur ADD COLUMN IF NOT EXISTS pin_hash text;     -- code court pour la caisse (haché par l'application)
ALTER TABLE utilisateur ADD COLUMN IF NOT EXISTS derniere_connexion timestamptz;

-- Migration de l'ancienne colonne role, seulement si elle existe encore (première exécution)
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_schema = current_schema() AND table_name = 'utilisateur' AND column_name = 'role') THEN
        EXECUTE $migration$
            UPDATE utilisateur u SET role_id = r.id
            FROM role r
            WHERE u.role_id IS NULL
              AND r.code = CASE u.role
                               WHEN 'admin'      THEN 'titulaire'
                               WHEN 'pharmacien' THEN 'assistant'
                               ELSE 'vendeur'
                           END
        $migration$;
        ALTER TABLE utilisateur DROP COLUMN role;
    END IF;
END $$;

ALTER TABLE utilisateur ALTER COLUMN role_id SET NOT NULL;

-- Le rôle titulaire est intouchable
CREATE OR REPLACE FUNCTION proteger_role_systeme() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    rid bigint := COALESCE(OLD.role_id, NEW.role_id);
BEGIN
    IF (SELECT systeme FROM role WHERE id = rid) THEN
        RAISE EXCEPTION 'Les droits du titulaire ne peuvent pas être modifiés';
    END IF;
    RETURN COALESCE(NEW, OLD);
END;
$$;

CREATE OR REPLACE TRIGGER trg_proteger_role_systeme
    BEFORE INSERT OR DELETE ON role_permission
    FOR EACH ROW EXECUTE FUNCTION proteger_role_systeme();

-- Il doit toujours rester au moins un titulaire actif
CREATE OR REPLACE FUNCTION garder_un_titulaire() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM utilisateur u JOIN role r ON r.id = u.role_id
        WHERE r.code = 'titulaire' AND u.actif
    ) THEN
        RAISE EXCEPTION 'Il doit rester au moins un titulaire actif';
    END IF;
    RETURN NULL;
END;
$$;

-- OR REPLACE n'existe pas pour les déclencheurs de contrainte
DROP TRIGGER IF EXISTS trg_garder_un_titulaire ON utilisateur;
CREATE CONSTRAINT TRIGGER trg_garder_un_titulaire
    AFTER UPDATE OR DELETE ON utilisateur
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION garder_un_titulaire();

-- ---------------------------------------------------------------------
-- 4. Vérification d'un droit (appelée par l'API avant chaque action)
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION a_permission(p_utilisateur_id bigint, p_code text)
RETURNS boolean LANGUAGE sql STABLE AS $$
    SELECT EXISTS (
        SELECT 1
        FROM utilisateur u
        JOIN role_permission rp ON rp.role_id = u.role_id
        WHERE u.id = p_utilisateur_id
          AND u.actif
          AND rp.permission_code = p_code
    );
$$;

-- ---------------------------------------------------------------------
-- 5. Journal des actions sensibles, avec qui a autorisé
-- ---------------------------------------------------------------------
ALTER TABLE journal_audit ADD COLUMN IF NOT EXISTS permission_code text REFERENCES permission(code);
ALTER TABLE journal_audit ADD COLUMN IF NOT EXISTS autorise_par    bigint REFERENCES utilisateur(id);

CREATE OR REPLACE FUNCTION interdire_modification_journal() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Le journal des actions ne peut être ni modifié ni supprimé';
END;
$$;

CREATE OR REPLACE TRIGGER trg_journal_immuable
    BEFORE UPDATE OR DELETE ON journal_audit
    FOR EACH ROW EXECUTE FUNCTION interdire_modification_journal();
