-- =====================================================================
-- Sauvegardes automatiques de la base (pg_dump) vers un disque externe.
-- A exécuter après 013_notifications.sql. Idempotent.
-- =====================================================================

CREATE TABLE IF NOT EXISTS parametre_sauvegarde (
    id                  int PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    actif               boolean NOT NULL DEFAULT false,      -- activé une fois le disque choisi
    dossier             text,                                -- ex. E:\SauvegardesPharmacie (disque externe)
    heure               time    NOT NULL DEFAULT '22:00',
    conservation        int     NOT NULL DEFAULT 14 CHECK (conservation BETWEEN 1 AND 365),  -- fichiers gardés
    derniere_execution  date                                 -- évite deux sauvegardes automatiques le même jour
);
INSERT INTO parametre_sauvegarde DEFAULT VALUES ON CONFLICT (id) DO NOTHING;

-- Journal des sauvegardes (non modifiable)
CREATE TABLE IF NOT EXISTS journal_sauvegarde (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    debut           timestamptz NOT NULL DEFAULT now(),
    fin             timestamptz,
    statut          text        NOT NULL CHECK (statut IN ('reussie', 'echouee', 'disque_absent')),
    declenchement   text        NOT NULL CHECK (declenchement IN ('automatique', 'manuel')),
    fichier         text,
    taille_octets   bigint,
    message         text,
    utilisateur_id  bigint      REFERENCES utilisateur(id)
);
CREATE INDEX IF NOT EXISTS idx_journal_sauvegarde ON journal_sauvegarde (debut DESC);

CREATE OR REPLACE TRIGGER trg_journal_sauvegarde_immuable
    BEFORE UPDATE OR DELETE ON journal_sauvegarde
    FOR EACH ROW EXECUTE FUNCTION interdire_modification_historique();
