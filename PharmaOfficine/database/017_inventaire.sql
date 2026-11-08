-- =====================================================================
-- Inventaire tournant : un rayon à la fois, comptage à l'aveugle par lot.
-- Petit écart (sous les seuils) : ajusté tout de suite. Au-delà : en attente
-- de validation par le pharmacien titulaire, prévenu par SMS ou e-mail.
-- A exécuter après 016_emplacements.sql. Idempotent.
-- =====================================================================

-- ---------------------------------------------------------------------
-- 1. Seuils (ligne unique)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS parametre_inventaire (
    id              int PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    seuil_boites    int           NOT NULL DEFAULT 2    CHECK (seuil_boites >= 0),
    seuil_montant   numeric(12,2) NOT NULL DEFAULT 2000 CHECK (seuil_montant >= 0)   -- au prix d'achat
);
INSERT INTO parametre_inventaire DEFAULT VALUES ON CONFLICT (id) DO NOTHING;

-- ---------------------------------------------------------------------
-- 2. Inventaire d'un rayon et ses lignes (une par lot)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS inventaire (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    rayon           text        NOT NULL,
    statut          text        NOT NULL DEFAULT 'en_cours' CHECK (statut IN ('en_cours', 'termine', 'annule')),
    commence_le     timestamptz NOT NULL DEFAULT now(),
    commence_par    bigint      NOT NULL REFERENCES utilisateur(id),
    termine_le      timestamptz,
    termine_par     bigint      REFERENCES utilisateur(id)
);
-- Un seul inventaire en cours par rayon
CREATE UNIQUE INDEX IF NOT EXISTS ux_inventaire_en_cours ON inventaire (rayon) WHERE statut = 'en_cours';

CREATE TABLE IF NOT EXISTS ligne_inventaire (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    inventaire_id   bigint        NOT NULL REFERENCES inventaire(id),
    lot_id          bigint        NOT NULL REFERENCES lot(id),
    -- Stock théorique relevé au moment du comptage (jamais montré pendant le comptage)
    theorique       int,
    compte          int           CHECK (compte >= 0),
    date_verifiee   date,                                   -- péremption confirmée pour un lot « date à vérifier »
    compte_par      bigint        REFERENCES utilisateur(id),
    compte_le       timestamptz,
    ecart           int,
    valeur_ecart    numeric(12,2),
    statut          text          NOT NULL DEFAULT 'a_compter' CHECK (statut IN (
                        'a_compter', 'compte', 'conforme', 'ajuste', 'a_valider', 'valide', 'refuse')),
    decide_par      bigint        REFERENCES utilisateur(id),
    decide_le       timestamptz,
    UNIQUE (inventaire_id, lot_id)
);
CREATE INDEX IF NOT EXISTS idx_ligne_inventaire_a_valider ON ligne_inventaire (statut) WHERE statut = 'a_valider';

-- ---------------------------------------------------------------------
-- 3. Notification « écart d'inventaire » pour le titulaire
-- ---------------------------------------------------------------------
ALTER TABLE destinataire_notification DROP CONSTRAINT IF EXISTS destinataire_notification_types_check;
ALTER TABLE destinataire_notification ADD CONSTRAINT destinataire_notification_types_check
    CHECK (types <@ ARRAY['rapport_quotidien', 'ecart_caisse', 'lot_bloque', 'sauvegarde', 'ecart_inventaire']);

ALTER TABLE message_sortant DROP CONSTRAINT IF EXISTS message_sortant_type_check;
ALTER TABLE message_sortant ADD CONSTRAINT message_sortant_type_check
    CHECK (type IN ('rapport_quotidien', 'ecart_caisse', 'lot_bloque', 'sauvegarde', 'ecart_inventaire', 'test'));
