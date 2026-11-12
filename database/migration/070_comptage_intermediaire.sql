SET client_encoding = 'UTF8';
-- =====================================================================
-- Comptage intermédiaire de la caisse : le tiroir est compté en cours de
-- journée sans clôturer (la caisse reste ouverte, les ventes continuent).
-- Le montant compté est enregistré ; l'attendu (fond + espèces nettes à
-- ce moment) et l'écart sont gardés pour qui a le droit de voir les
-- écarts, et ne sont jamais montrés avant le comptage (comptage aveugle).
-- Idempotent. A exécuter après 069_receptions_attente.sql.
-- =====================================================================

CREATE TABLE IF NOT EXISTS comptage_intermediaire (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    session_caisse_id  bigint        NOT NULL REFERENCES session_caisse(id),
    utilisateur_id     bigint        NOT NULL REFERENCES utilisateur(id),
    compte             numeric(12,2) NOT NULL CHECK (compte >= 0),
    attendu            numeric(12,2) NOT NULL,
    ecart              numeric(12,2) NOT NULL,
    comptage           jsonb         NOT NULL,
    cree_le            timestamptz   NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_comptage_intermediaire_session ON comptage_intermediaire (session_caisse_id, cree_le);
COMMENT ON TABLE comptage_intermediaire IS 'Comptages du tiroir en cours de journée, sans clôture (attendu et écart réservés au droit de voir les écarts)';
