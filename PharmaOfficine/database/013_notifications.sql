-- =====================================================================
-- Notifications du titulaire : e-mail (serveur SMTP) et SMS (modem GSM).
-- Aucun service cloud. Aucune donnée de patient ni d'ordonnance dans
-- les messages : chiffres et noms de produits seulement.
-- A exécuter après 012_achats.sql. Idempotent.
-- =====================================================================

-- ---------------------------------------------------------------------
-- 1. Réglages (ligne unique)
--    Le mot de passe SMTP est chiffré par l'API avec DPAPI (portée machine) :
--    il n'est lisible que sur le PC serveur qui l'a enregistré.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS parametre_notification (
    id                      int PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    smtp_serveur            text,
    smtp_port               int     NOT NULL DEFAULT 587 CHECK (smtp_port BETWEEN 1 AND 65535),
    smtp_tls                boolean NOT NULL DEFAULT true,
    smtp_expediteur         text,
    smtp_identifiant        text,
    smtp_mot_de_passe       bytea,                     -- chiffré (DPAPI), jamais en clair
    modem_port              text,                      -- ex. COM3 ; vide = SMS désactivés
    heure_rapport           time    NOT NULL DEFAULT '21:00',
    dernier_rapport         date,                      -- évite d'envoyer deux fois le même jour
    sms_seuil_ecart         numeric(12,2) CHECK (sms_seuil_ecart >= 0),  -- vide = seuil de la caisse
    sms_lot_bloque          boolean NOT NULL DEFAULT true,
    sms_sauvegarde          boolean NOT NULL DEFAULT true
);
INSERT INTO parametre_notification DEFAULT VALUES ON CONFLICT (id) DO NOTHING;

-- ---------------------------------------------------------------------
-- 2. Destinataires : seules ces adresses, vérifiées par le titulaire,
--    reçoivent des messages.
--    Types : rapport_quotidien, ecart_caisse, lot_bloque, sauvegarde
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS destinataire_notification (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    nom             text        NOT NULL,
    email           text,
    telephone       text,
    canal_email     boolean     NOT NULL DEFAULT false,
    canal_sms       boolean     NOT NULL DEFAULT false,
    types           text[]      NOT NULL DEFAULT ARRAY['rapport_quotidien', 'ecart_caisse', 'lot_bloque', 'sauvegarde'],
    valide          boolean     NOT NULL DEFAULT false,   -- adresse vérifiée (message de test reçu)
    actif           boolean     NOT NULL DEFAULT true,
    cree_le         timestamptz NOT NULL DEFAULT now(),
    CHECK (NOT canal_email OR email IS NOT NULL),
    CHECK (NOT canal_sms OR telephone IS NOT NULL),
    CHECK (types <@ ARRAY['rapport_quotidien', 'ecart_caisse', 'lot_bloque', 'sauvegarde'])
);

-- ---------------------------------------------------------------------
-- 3. File d'attente des messages sortants
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS message_sortant (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    canal               text        NOT NULL CHECK (canal IN ('email', 'sms')),
    destinataire_id     bigint      NOT NULL REFERENCES destinataire_notification(id),
    adresse             text        NOT NULL,          -- e-mail ou téléphone au moment de l'envoi
    type                text        NOT NULL CHECK (type IN ('rapport_quotidien', 'ecart_caisse', 'lot_bloque', 'sauvegarde', 'test')),
    objet               text,
    corps               text        NOT NULL,
    corps_html          text,
    statut              text        NOT NULL DEFAULT 'en_attente' CHECK (statut IN ('en_attente', 'envoye', 'echec')),
    tentatives          int         NOT NULL DEFAULT 0,
    prochaine_tentative timestamptz,
    derniere_erreur     text,
    cree_le             timestamptz NOT NULL DEFAULT now(),
    envoye_le           timestamptz
);
CREATE INDEX IF NOT EXISTS idx_message_a_envoyer ON message_sortant (prochaine_tentative) WHERE statut = 'en_attente';

-- Filet : un message ne part que vers un destinataire actif, à l'adresse enregistrée,
-- et vérifié (sauf le message de test qui sert justement à le vérifier)
CREATE OR REPLACE FUNCTION verifier_destinataire_message() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    d destinataire_notification;
BEGIN
    SELECT * INTO d FROM destinataire_notification WHERE id = NEW.destinataire_id;
    IF NOT d.actif THEN
        RAISE EXCEPTION 'Destinataire désactivé : message refusé';
    END IF;
    IF NOT d.valide AND NEW.type <> 'test' THEN
        RAISE EXCEPTION 'Destinataire non vérifié : message refusé';
    END IF;
    IF (NEW.canal = 'email' AND NEW.adresse IS DISTINCT FROM d.email)
       OR (NEW.canal = 'sms' AND NEW.adresse IS DISTINCT FROM d.telephone) THEN
        RAISE EXCEPTION 'Adresse différente de celle du destinataire : message refusé';
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE TRIGGER trg_message_destinataire
    BEFORE INSERT ON message_sortant
    FOR EACH ROW EXECUTE FUNCTION verifier_destinataire_message();
